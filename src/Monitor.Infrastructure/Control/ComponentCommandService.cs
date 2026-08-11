using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Monitor.Domain;
using Monitor.Infrastructure.Auditing;

namespace Monitor.Infrastructure.Control;

public sealed class ComponentCommandOptions
{
    public const string SectionName = "ComponentCommands";

    public bool Enabled { get; set; } = true;
    public int LeaseSeconds { get; set; } = 30;
    public int SweepIntervalSeconds { get; set; } = 15;
    public int DefaultExpiryMinutes { get; set; } = 15;
    public int MaxDeliveryAttempts { get; set; } = 10;
}

public sealed class ComponentCommandService(
    MonitorDbContext db,
    AuditTrailWriter audit,
    IOptions<ComponentCommandOptions> options,
    ILogger<ComponentCommandService> logger)
{
    private const string ExpiryLockResource = "Monitor.ComponentCommands.Expiry";
    private readonly ComponentCommandOptions _options = options.Value;

    public async Task<ClaimedComponentCommand?> ClaimNextAsync(
        Guid componentId,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return null;
        }

        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            var connection = db.Database.GetDbConnection();
            var resource = $"Monitor.ComponentCommands.{componentId:N}";
            if (!await TryAcquireLockAsync(connection, resource, cancellationToken))
            {
                return null;
            }

            try
            {
                var now = DateTimeOffset.UtcNow;
                var maxAttempts = Math.Clamp(_options.MaxDeliveryAttempts, 1, 100);
                var leaseDuration = TimeSpan.FromSeconds(Math.Clamp(_options.LeaseSeconds, 5, 3600));

                await ExpireDueCommandsAsync(componentId, now, maxAttempts, cancellationToken);

                var command = await db.ComponentCommands
                    .Include(x => x.Component)
                    .Where(x =>
                        x.ComponentId == componentId &&
                        x.AvailableAt <= now &&
                        x.ExpiresAt > now &&
                        x.DeliveryAttempts < maxAttempts &&
                        (x.Status == ComponentCommandStatus.Pending ||
                         (x.Status == ComponentCommandStatus.Leased && x.LeaseExpiresAt <= now)))
                    .OrderBy(x => x.CreatedAt)
                    .ThenBy(x => x.Id)
                    .FirstOrDefaultAsync(cancellationToken);

                if (command is null)
                {
                    await db.SaveChangesAsync(cancellationToken);
                    return null;
                }

                var leaseToken = command.Lease(now, leaseDuration, maxAttempts);
                await db.SaveChangesAsync(cancellationToken);

                logger.LogDebug(
                    "Leased component command {CommandId} ({CommandType}) to {ComponentId}; delivery attempt {Attempt}.",
                    command.Id,
                    command.Type,
                    componentId,
                    command.DeliveryAttempts);

                return new ClaimedComponentCommand(
                    command.Id,
                    command.ComponentId,
                    command.Type,
                    command.TargetRunId,
                    command.PayloadJson,
                    command.CreatedAt,
                    command.ExpiresAt,
                    leaseToken,
                    command.LeaseExpiresAt!.Value,
                    command.DeliveryAttempts);
            }
            finally
            {
                await ReleaseLockAsync(connection, resource, cancellationToken);
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    public async Task<ComponentCommandCompletionResult> CompleteAsync(
        Guid componentId,
        Guid commandId,
        Guid leaseToken,
        ComponentCommandOutcome outcome,
        string? resultJson,
        string? error,
        CancellationToken cancellationToken = default)
    {
        var command = await db.ComponentCommands
            .Include(x => x.Component)
            .SingleOrDefaultAsync(
                x => x.Id == commandId && x.ComponentId == componentId,
                cancellationToken);

        if (command is null)
        {
            return ComponentCommandCompletionResult.NotFound;
        }

        if (command.IsTerminal)
        {
            return new ComponentCommandCompletionResult(true, true, false, command.Status);
        }

        if (command.Status != ComponentCommandStatus.Leased || command.LeaseToken != leaseToken)
        {
            return new ComponentCommandCompletionResult(true, false, true, command.Status);
        }

        var before = Snapshot(command);
        var now = DateTimeOffset.UtcNow;
        command.Complete(leaseToken, outcome, resultJson, error, now);

        if (outcome == ComponentCommandOutcome.Succeeded)
        {
            command.Component.ApplySuccessfulControlCommand(command.Type, now);
        }

        audit.RecordComponent(
            command.ComponentId,
            command.Component.Name,
            ActionFor(command.Status),
            AuditTargetTypes.ComponentCommand,
            command.Id.ToString("D"),
            command.Type.ToString(),
            before,
            Snapshot(command),
            new
            {
                command.TargetRunId,
                command.DeliveryAttempts,
                componentControlState = command.Component.ControlState,
                componentEnabled = command.Component.Enabled
            },
            now);

        await db.SaveChangesAsync(cancellationToken);
        return new ComponentCommandCompletionResult(true, false, false, command.Status);
    }

    public async Task<int> ExpireOutstandingAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return 0;
        }

        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            var connection = db.Database.GetDbConnection();
            if (!await TryAcquireLockAsync(connection, ExpiryLockResource, cancellationToken))
            {
                return 0;
            }

            try
            {
                var now = DateTimeOffset.UtcNow;
                var maxAttempts = Math.Clamp(_options.MaxDeliveryAttempts, 1, 100);
                var commands = await db.ComponentCommands
                    .Include(x => x.Component)
                    .Where(x =>
                        (x.Status == ComponentCommandStatus.Pending || x.Status == ComponentCommandStatus.Leased) &&
                        (x.ExpiresAt <= now ||
                         (x.Status == ComponentCommandStatus.Leased &&
                          x.LeaseExpiresAt <= now &&
                          x.DeliveryAttempts >= maxAttempts)))
                    .OrderBy(x => x.CreatedAt)
                    .Take(500)
                    .ToListAsync(cancellationToken);

                foreach (var command in commands)
                {
                    Expire(command, now, maxAttempts);
                }

                if (commands.Count > 0)
                {
                    await db.SaveChangesAsync(cancellationToken);
                    logger.LogInformation("Expired {CommandCount} component command(s).", commands.Count);
                }

                return commands.Count;
            }
            finally
            {
                await ReleaseLockAsync(connection, ExpiryLockResource, cancellationToken);
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private async Task ExpireDueCommandsAsync(
        Guid componentId,
        DateTimeOffset now,
        int maxAttempts,
        CancellationToken cancellationToken)
    {
        var commands = await db.ComponentCommands
            .Include(x => x.Component)
            .Where(x =>
                x.ComponentId == componentId &&
                (x.Status == ComponentCommandStatus.Pending || x.Status == ComponentCommandStatus.Leased) &&
                (x.ExpiresAt <= now ||
                 (x.Status == ComponentCommandStatus.Leased &&
                  x.LeaseExpiresAt <= now &&
                  x.DeliveryAttempts >= maxAttempts)))
            .ToListAsync(cancellationToken);

        foreach (var command in commands)
        {
            Expire(command, now, maxAttempts);
        }
    }

    private void Expire(ComponentCommand command, DateTimeOffset now, int maxAttempts)
    {
        var before = Snapshot(command);
        var reason = command.ExpiresAt <= now
            ? "Command expired before acknowledgement."
            : $"Command exceeded the maximum of {maxAttempts} delivery attempts.";

        command.Expire(reason, now);
        audit.RecordSystem(
            "Component command expiry",
            AuditActions.ComponentCommandExpired,
            AuditTargetTypes.ComponentCommand,
            command.Id.ToString("D"),
            command.Type.ToString(),
            before,
            Snapshot(command),
            new { command.ComponentId, command.TargetRunId, command.DeliveryAttempts },
            now);
    }

    private static string ActionFor(ComponentCommandStatus status) => status switch
    {
        ComponentCommandStatus.Succeeded => AuditActions.ComponentCommandSucceeded,
        ComponentCommandStatus.Failed => AuditActions.ComponentCommandFailed,
        ComponentCommandStatus.Rejected => AuditActions.ComponentCommandRejected,
        _ => throw new InvalidOperationException($"No component audit action exists for {status}.")
    };

    public static object Snapshot(ComponentCommand command) => new
    {
        command.ComponentId,
        command.Type,
        command.Status,
        command.TargetRunId,
        command.CreatedAt,
        command.AvailableAt,
        command.ExpiresAt,
        command.LeasedAt,
        command.LeaseExpiresAt,
        command.DeliveryAttempts,
        command.CompletedAt,
        command.ResultJson,
        command.Error,
        command.CancelledAt,
        command.CancelledBy
    };

    private static async Task<bool> TryAcquireLockAsync(
        DbConnection connection,
        string resource,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock
                @Resource = @resource,
                @LockMode = 'Exclusive',
                @LockOwner = 'Session',
                @LockTimeout = 0;
            SELECT @result;
            """;

        var parameter = command.CreateParameter();
        parameter.ParameterName = "@resource";
        parameter.Value = resource;
        command.Parameters.Add(parameter);

        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) >= 0;
    }

    private static async Task ReleaseLockAsync(
        DbConnection connection,
        string resource,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "EXEC sys.sp_releaseapplock @Resource = @resource, @LockOwner = 'Session';";

        var parameter = command.CreateParameter();
        parameter.ParameterName = "@resource";
        parameter.Value = resource;
        command.Parameters.Add(parameter);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

public sealed record ClaimedComponentCommand(
    Guid Id,
    Guid ComponentId,
    ComponentCommandType Type,
    Guid? TargetRunId,
    string? PayloadJson,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    Guid LeaseToken,
    DateTimeOffset LeaseExpiresAt,
    int DeliveryAttempt);

public sealed record ComponentCommandCompletionResult(
    bool Found,
    bool AlreadyTerminal,
    bool LeaseConflict,
    ComponentCommandStatus? Status)
{
    public static ComponentCommandCompletionResult NotFound { get; } = new(false, false, false, null);
}

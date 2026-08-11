using System.Data.Common;
using System.Security.Claims;
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
            var resource = LockResource(componentId);
            if (!await TryAcquireLockAsync(connection, resource, cancellationToken))
            {
                return null;
            }

            try
            {
                var now = DateTimeOffset.UtcNow;
                var maxAttempts = MaxDeliveryAttempts();
                var leaseDuration = LeaseDuration();

                await ExpireDueCommandsAsync(componentId, now, maxAttempts, cancellationToken);

                var command = await db.ComponentCommands
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
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            var connection = db.Database.GetDbConnection();
            var resource = LockResource(componentId);
            if (!await TryAcquireLockAsync(connection, resource, cancellationToken))
            {
                return new ComponentCommandCompletionResult(true, false, true, null);
            }

            try
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

    public async Task<ComponentCommandCancelResult> CancelAsync(
        Guid componentId,
        Guid commandId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            var connection = db.Database.GetDbConnection();
            var resource = LockResource(componentId);
            if (!await TryAcquireLockAsync(connection, resource, cancellationToken))
            {
                return new ComponentCommandCancelResult(true, false, true, null);
            }

            try
            {
                var command = await db.ComponentCommands
                    .Include(x => x.Component)
                    .SingleOrDefaultAsync(
                        x => x.Id == commandId && x.ComponentId == componentId,
                        cancellationToken);

                if (command is null)
                {
                    return ComponentCommandCancelResult.NotFound;
                }

                if (command.IsTerminal)
                {
                    return new ComponentCommandCancelResult(true, true, false, command.Status);
                }

                var before = Snapshot(command);
                var now = DateTimeOffset.UtcNow;
                command.Cancel(user.Identity?.Name, now);
                audit.RecordOperator(
                    user,
                    AuditActions.ComponentCommandCancelled,
                    AuditTargetTypes.ComponentCommand,
                    command.Id.ToString("D"),
                    command.Type.ToString(),
                    before,
                    Snapshot(command),
                    new { command.ComponentId, command.TargetRunId },
                    now);

                await db.SaveChangesAsync(cancellationToken);
                return new ComponentCommandCancelResult(true, false, false, command.Status);
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

    public async Task<int> ExpireOutstandingAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return 0;
        }

        var now = DateTimeOffset.UtcNow;
        var maxAttempts = MaxDeliveryAttempts();
        var componentIds = await db.ComponentCommands
            .AsNoTracking()
            .Where(x =>
                (x.Status == ComponentCommandStatus.Pending && x.ExpiresAt <= now) ||
                (x.Status == ComponentCommandStatus.Leased &&
                 x.LeaseExpiresAt <= now &&
                 (x.ExpiresAt <= now || x.DeliveryAttempts >= maxAttempts)))
            .Select(x => x.ComponentId)
            .Distinct()
            .Take(100)
            .ToListAsync(cancellationToken);

        var expired = 0;
        foreach (var componentId in componentIds)
        {
            db.ChangeTracker.Clear();
            await db.Database.OpenConnectionAsync(cancellationToken);
            try
            {
                var connection = db.Database.GetDbConnection();
                var resource = LockResource(componentId);
                if (!await TryAcquireLockAsync(connection, resource, cancellationToken))
                {
                    continue;
                }

                try
                {
                    await ExpireDueCommandsAsync(componentId, DateTimeOffset.UtcNow, maxAttempts, cancellationToken);
                    var changed = db.ChangeTracker.Entries<ComponentCommand>()
                        .Count(x => x.State == EntityState.Modified);
                    if (changed > 0)
                    {
                        await db.SaveChangesAsync(cancellationToken);
                        expired += changed;
                    }
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

        if (expired > 0)
        {
            logger.LogInformation("Expired {CommandCount} component command(s).", expired);
        }

        return expired;
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
                ((x.Status == ComponentCommandStatus.Pending && x.ExpiresAt <= now) ||
                 (x.Status == ComponentCommandStatus.Leased &&
                  x.LeaseExpiresAt <= now &&
                  (x.ExpiresAt <= now || x.DeliveryAttempts >= maxAttempts))))
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

    private int MaxDeliveryAttempts() => Math.Clamp(_options.MaxDeliveryAttempts, 1, 100);
    private TimeSpan LeaseDuration() => TimeSpan.FromSeconds(Math.Clamp(_options.LeaseSeconds, 5, 3600));
    private static string LockResource(Guid componentId) => $"Monitor.ComponentCommands.{componentId:N}";

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

public sealed record ComponentCommandCancelResult(
    bool Found,
    bool AlreadyTerminal,
    bool LockUnavailable,
    ComponentCommandStatus? Status)
{
    public static ComponentCommandCancelResult NotFound { get; } = new(false, false, false, null);
}

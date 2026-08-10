using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Monitor.Domain;

namespace Monitor.Infrastructure.Failures;

public sealed class FailureAlertEvaluationService(
    MonitorDbContext db,
    IOptions<FailureAlertingOptions> options,
    ILogger<FailureAlertEvaluationService> logger)
{
    private const string LockResource = "Monitor.FailureAlerting";
    private readonly FailureAlertingOptions _options = options.Value;

    public async Task<FailureAlertSweepResult> SweepAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return FailureAlertSweepResult.Disabled;
        }

        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            var connection = db.Database.GetDbConnection();
            if (!await TryAcquireLockAsync(connection, cancellationToken))
            {
                logger.LogDebug("Failure alert sweep skipped because another Monitor node owns the alerting lock.");
                return FailureAlertSweepResult.Locked;
            }

            try
            {
                var now = DateTimeOffset.UtcNow;
                var rules = await db.Set<FailureAlertRule>()
                    .Where(x => x.Enabled)
                    .Include(x => x.DestinationAssignments)
                    .OrderBy(x => x.CreatedAt)
                    .ToListAsync(cancellationToken);

                if (rules.Count == 0)
                {
                    return new FailureAlertSweepResult(true, false, 0, 0, []);
                }

                var enabledDestinations = await db.AlertDeliveryDestinations
                    .Where(x => x.Enabled)
                    .OrderBy(x => x.CreatedAt)
                    .ToDictionaryAsync(x => x.Id, cancellationToken);

                var createdEvents = new List<Guid>();
                var enqueuedDeliveries = 0;

                foreach (var rule in rules)
                {
                    var windowStart = now.AddMinutes(-rule.WindowMinutes);
                    var summary = await db.Runs
                        .Where(x =>
                            x.FailureGroupId == rule.FailureGroupId &&
                            x.CompletedAt != null &&
                            x.CompletedAt >= windowStart &&
                            x.CompletedAt <= now &&
                            (x.Status == RunStatus.Failed || x.Status == RunStatus.Cancelled))
                        .GroupBy(_ => 1)
                        .Select(group => new WindowSummary(
                            group.LongCount(),
                            group.Max(x => x.Sequence)))
                        .SingleOrDefaultAsync(cancellationToken);

                    rule.MarkEvaluated(now);

                    if (summary is null || summary.Occurrences < rule.Threshold)
                    {
                        continue;
                    }

                    var cooldownElapsed = rule.LastTriggeredAt is null ||
                        now >= rule.LastTriggeredAt.Value.AddMinutes(rule.CooldownMinutes);
                    var hasNewOccurrence = rule.LastTriggeredRunSequence is null ||
                        summary.LatestRunSequence > rule.LastTriggeredRunSequence.Value;

                    if (!cooldownElapsed || !hasNewOccurrence)
                    {
                        continue;
                    }

                    var alertEvent = FailureAlertEvent.Create(
                        rule,
                        windowStart,
                        now,
                        summary.Occurrences,
                        summary.LatestRunSequence);

                    db.FailureAlertEvents.Add(alertEvent);

                    IEnumerable<AlertDeliveryDestination> destinationsForRule;
                    if (rule.DeliverToAllEnabledDestinations)
                    {
                        destinationsForRule = enabledDestinations.Values;
                    }
                    else
                    {
                        destinationsForRule = rule.DestinationAssignments
                            .Select(x => x.DestinationId)
                            .Distinct()
                            .Where(enabledDestinations.ContainsKey)
                            .Select(destinationId => enabledDestinations[destinationId]);
                    }

                    foreach (var destination in destinationsForRule)
                    {
                        db.AlertDeliveries.Add(AlertDelivery.Create(alertEvent, destination, now));
                        enqueuedDeliveries++;
                    }

                    rule.MarkTriggered(now, summary.LatestRunSequence);
                    createdEvents.Add(alertEvent.Id);
                }

                await db.SaveChangesAsync(cancellationToken);

                if (createdEvents.Count > 0)
                {
                    logger.LogWarning(
                        "Failure alert sweep triggered {AlertCount} alert event(s) from {RuleCount} enabled rule(s) and enqueued {DeliveryCount} delivery item(s).",
                        createdEvents.Count,
                        rules.Count,
                        enqueuedDeliveries);
                }

                return new FailureAlertSweepResult(true, false, rules.Count, createdEvents.Count, createdEvents);
            }
            finally
            {
                await ReleaseLockAsync(db.Database.GetDbConnection(), cancellationToken);
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private static async Task<bool> TryAcquireLockAsync(DbConnection connection, CancellationToken cancellationToken)
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
        parameter.Value = LockResource;
        command.Parameters.Add(parameter);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result) >= 0;
    }

    private static async Task ReleaseLockAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "EXEC sys.sp_releaseapplock @Resource = @resource, @LockOwner = 'Session';";

        var parameter = command.CreateParameter();
        parameter.ParameterName = "@resource";
        parameter.Value = LockResource;
        command.Parameters.Add(parameter);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed record WindowSummary(long Occurrences, long LatestRunSequence);
}

public sealed record FailureAlertSweepResult(
    bool Executed,
    bool LockUnavailable,
    int EvaluatedRules,
    int TriggeredAlerts,
    IReadOnlyList<Guid> AlertEventIds)
{
    public static FailureAlertSweepResult Disabled { get; } = new(false, false, 0, 0, []);
    public static FailureAlertSweepResult Locked { get; } = new(false, true, 0, 0, []);
}

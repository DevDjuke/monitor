using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Monitor.Domain;
using Monitor.Infrastructure.Auditing;

namespace Monitor.Infrastructure.Usage;

public sealed class UsageBudgetOptions
{
    public const string SectionName = "UsageBudgets";
    public bool Enabled { get; set; } = true;
    public int InitialDelaySeconds { get; set; } = 10;
    public int SweepIntervalSeconds { get; set; } = 60;
}

public sealed class UsageBudgetEvaluationService(
    MonitorDbContext db,
    AuditTrailWriter audit,
    IOptions<UsageBudgetOptions> options,
    ILogger<UsageBudgetEvaluationService> logger)
{
    private const string LockResource = "Monitor.UsageBudgets";
    private readonly UsageBudgetOptions _options = options.Value;

    public async Task<UsageBudgetSweepResult> SweepAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled) return UsageBudgetSweepResult.Disabled;

        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            var connection = db.Database.GetDbConnection();
            if (!await TryAcquireLockAsync(connection, cancellationToken))
            {
                return UsageBudgetSweepResult.Locked;
            }

            try
            {
                var now = DateTimeOffset.UtcNow;
                var budgets = await db.UsageBudgets
                    .Where(x => x.Enabled && !x.IsDeleted)
                    .Include(x => x.DestinationAssignments)
                    .OrderBy(x => x.CreatedAt)
                    .ToListAsync(cancellationToken);

                if (budgets.Count == 0)
                {
                    return new UsageBudgetSweepResult(true, false, 0, 0, 0, []);
                }

                var enabledDestinations = await db.AlertDeliveryDestinations
                    .Where(x => x.Enabled)
                    .OrderBy(x => x.CreatedAt)
                    .ToDictionaryAsync(x => x.Id, cancellationToken);

                var triggeredIds = new List<Guid>();
                var enqueuedDeliveries = 0;

                foreach (var budget in budgets)
                {
                    var (periodStart, periodEnd) = ResolvePeriod(now, budget.Period);
                    var usage = await CalculateUsageAsync(budget, periodStart, periodEnd, cancellationToken);
                    budget.MarkEvaluated(periodStart, usage.CostUsd, usage.Tokens, now);

                    var utilization = budget.GetUtilizationPercent(usage.CostUsd, usage.Tokens);
                    var level = budget.GetAlertLevel(utilization);
                    if (level is null || (budget.LastTriggeredLevel is not null && level <= budget.LastTriggeredLevel))
                    {
                        continue;
                    }

                    var alertEvent = UsageBudgetAlertEvent.Create(
                        budget,
                        level.Value,
                        periodStart,
                        periodEnd,
                        usage.CostUsd,
                        usage.Tokens,
                        utilization,
                        now);
                    db.UsageBudgetAlertEvents.Add(alertEvent);

                    IEnumerable<AlertDeliveryDestination> destinations = budget.DeliverToAllEnabledDestinations
                        ? enabledDestinations.Values
                        : budget.DestinationAssignments
                            .Select(x => x.DestinationId)
                            .Distinct()
                            .Where(enabledDestinations.ContainsKey)
                            .Select(id => enabledDestinations[id]);

                    foreach (var destination in destinations)
                    {
                        db.UsageBudgetAlertDeliveries.Add(UsageBudgetAlertDelivery.Create(alertEvent, destination, now));
                        enqueuedDeliveries++;
                    }

                    budget.MarkTriggered(level.Value, now);
                    triggeredIds.Add(alertEvent.Id);
                    audit.RecordSystem(
                        "UsageBudgetEvaluator",
                        $"usage-budget.{level.Value.ToString().ToLowerInvariant()}",
                        "UsageBudget",
                        budget.Id.ToString(),
                        budget.Name,
                        after: new
                        {
                            level = level.Value,
                            utilizationPercent = utilization,
                            observedCostUsd = usage.CostUsd,
                            observedTokens = usage.Tokens,
                            periodStart,
                            periodEnd
                        },
                        occurredAt: now);
                }

                await db.SaveChangesAsync(cancellationToken);

                if (triggeredIds.Count > 0)
                {
                    logger.LogWarning(
                        "Usage budget sweep triggered {AlertCount} budget alert(s) and enqueued {DeliveryCount} delivery item(s).",
                        triggeredIds.Count,
                        enqueuedDeliveries);
                }

                return new UsageBudgetSweepResult(true, false, budgets.Count, triggeredIds.Count, enqueuedDeliveries, triggeredIds);
            }
            finally
            {
                await ReleaseLockAsync(connection, cancellationToken);
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private async Task<UsageSummary> CalculateUsageAsync(
        UsageBudget budget,
        DateTimeOffset periodStart,
        DateTimeOffset periodEnd,
        CancellationToken cancellationToken)
    {
        var aggregateQuery = db.RunAggregates.AsNoTracking()
            .Where(x => x.BucketStart >= periodStart && x.BucketStart < periodEnd);
        if (budget.ComponentId is not null) aggregateQuery = aggregateQuery.Where(x => x.ComponentId == budget.ComponentId.Value);
        if (budget.Environment is not null) aggregateQuery = aggregateQuery.Where(x => x.Environment == budget.Environment);
        if (budget.Model is not null) aggregateQuery = aggregateQuery.Where(x => x.Model == budget.Model);

        var aggregate = await aggregateQuery
            .GroupBy(_ => 1)
            .Select(g => new UsageSummary(g.Sum(x => x.CostUsd), g.Sum(x => x.InputTokens + x.OutputTokens)))
            .SingleOrDefaultAsync(cancellationToken) ?? UsageSummary.Empty;

        var rawQuery = db.Runs.AsNoTracking()
            .Where(x =>
                x.Status != RunStatus.Running &&
                x.CompletedAt != null &&
                x.CompletedAt >= periodStart &&
                x.CompletedAt < periodEnd &&
                x.AggregatedAt == null);
        if (budget.ComponentId is not null) rawQuery = rawQuery.Where(x => x.ComponentId == budget.ComponentId.Value);
        if (budget.Environment is not null) rawQuery = rawQuery.Where(x => x.Component.Environment == budget.Environment);
        if (budget.Model is not null) rawQuery = rawQuery.Where(x => x.Model == budget.Model);

        var raw = await rawQuery
            .GroupBy(_ => 1)
            .Select(g => new UsageSummary(g.Sum(x => x.CostUsd), g.Sum(x => x.InputTokens + x.OutputTokens)))
            .SingleOrDefaultAsync(cancellationToken) ?? UsageSummary.Empty;

        return new UsageSummary(aggregate.CostUsd + raw.CostUsd, aggregate.Tokens + raw.Tokens);
    }

    public static (DateTimeOffset Start, DateTimeOffset End) ResolvePeriod(DateTimeOffset now, UsageBudgetPeriod period)
    {
        var utc = now.ToUniversalTime();
        var start = period == UsageBudgetPeriod.Daily
            ? new DateTimeOffset(utc.Year, utc.Month, utc.Day, 0, 0, 0, TimeSpan.Zero)
            : new DateTimeOffset(utc.Year, utc.Month, 1, 0, 0, 0, TimeSpan.Zero);
        return (start, period == UsageBudgetPeriod.Daily ? start.AddDays(1) : start.AddMonths(1));
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
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) >= 0;
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

    private sealed record UsageSummary(double CostUsd, long Tokens)
    {
        public static UsageSummary Empty { get; } = new(0, 0);
    }
}

public sealed record UsageBudgetSweepResult(
    bool Executed,
    bool LockUnavailable,
    int EvaluatedBudgets,
    int TriggeredAlerts,
    int EnqueuedDeliveries,
    IReadOnlyList<Guid> AlertEventIds)
{
    public static UsageBudgetSweepResult Disabled { get; } = new(false, false, 0, 0, 0, []);
    public static UsageBudgetSweepResult Locked { get; } = new(false, true, 0, 0, 0, []);
}

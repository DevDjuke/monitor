using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Monitor.Domain;

namespace Monitor.Infrastructure.Retention;

public sealed class RetentionAggregationService(
    MonitorDbContext db,
    IOptions<RetentionOptions> options,
    ILogger<RetentionAggregationService> logger)
{
    private const string LockResource = "Monitor.RetentionAggregation";
    private readonly RetentionOptions _options = options.Value;

    public async Task<RetentionSweepResult> SweepAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return RetentionSweepResult.Disabled;
        }

        var now = DateTimeOffset.UtcNow;
        await db.Database.OpenConnectionAsync(cancellationToken);

        try
        {
            if (!await TryAcquireLockAsync(db.Database.GetDbConnection(), cancellationToken))
            {
                logger.LogDebug("Retention sweep skipped because another Monitor node owns the retention lock.");
                return RetentionSweepResult.Locked;
            }

            try
            {
                var aggregatedRuns = await AggregatePendingRunsAsync(now, cancellationToken);
                var purgedSuccessfulRuns = await PurgeSuccessfulRunsAsync(now, cancellationToken);

                if (aggregatedRuns > 0 || purgedSuccessfulRuns > 0)
                {
                    logger.LogInformation(
                        "Retention sweep aggregated {AggregatedRuns} terminal runs and purged {PurgedSuccessfulRuns} successful runs.",
                        aggregatedRuns,
                        purgedSuccessfulRuns);
                }

                return new RetentionSweepResult(true, false, aggregatedRuns, purgedSuccessfulRuns);
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

    private async Task<int> AggregatePendingRunsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var aggregationCutoff = now - _options.AggregationDelay;
        var totalAggregated = 0;

        for (var batchNumber = 0; batchNumber < _options.SafeMaxBatchesPerSweep; batchNumber++)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

            var runs = await db.Runs
                .Include(x => x.Component)
                .Where(x =>
                    x.Status != RunStatus.Running &&
                    x.CompletedAt != null &&
                    x.CompletedAt <= aggregationCutoff &&
                    x.AggregatedAt == null)
                .OrderBy(x => x.Sequence)
                .Take(_options.SafeBatchSize)
                .ToListAsync(cancellationToken);

            if (runs.Count == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                break;
            }

            var grouped = runs
                .GroupBy(x => new AggregateKey(
                    ToHourBucket(x.StartedAt),
                    x.ComponentId,
                    NormalizeModelKey(x.Model)))
                .Select(group => new PendingAggregate(
                    group.Key,
                    group.First().Component.Name,
                    group.First().Component.Environment,
                    NormalizeModel(group.First().Model),
                    CreateDelta(group)))
                .ToList();

            var minBucket = grouped.Min(x => x.Key.BucketStart);
            var maxBucket = grouped.Max(x => x.Key.BucketStart);
            var componentIds = grouped.Select(x => x.Key.ComponentId).Distinct().ToArray();

            var existingAggregates = await db.RunAggregates
                .Where(x =>
                    x.BucketStart >= minBucket &&
                    x.BucketStart <= maxBucket &&
                    componentIds.Contains(x.ComponentId))
                .ToListAsync(cancellationToken);

            var existingByKey = existingAggregates.ToDictionary(
                x => new AggregateKey(x.BucketStart, x.ComponentId, NormalizeModelKey(x.Model)));

            foreach (var pending in grouped)
            {
                if (existingByKey.TryGetValue(pending.Key, out var aggregate))
                {
                    aggregate.Apply(pending.Delta, pending.ComponentName, pending.Environment, now);
                }
                else
                {
                    aggregate = RunAggregate.Create(
                        pending.Key.BucketStart,
                        pending.Key.ComponentId,
                        pending.ComponentName,
                        pending.Environment,
                        pending.Model,
                        pending.Delta,
                        now);
                    db.RunAggregates.Add(aggregate);
                    existingByKey.Add(pending.Key, aggregate);
                }
            }

            foreach (var run in runs)
            {
                run.MarkAggregated(now);
            }

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            totalAggregated += runs.Count;
            db.ChangeTracker.Clear();

            if (runs.Count < _options.SafeBatchSize)
            {
                break;
            }
        }

        return totalAggregated;
    }

    private async Task<int> PurgeSuccessfulRunsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var cutoff = now - _options.SuccessfulRunDetailRetention;
        var totalPurged = 0;

        for (var batchNumber = 0; batchNumber < _options.SafeMaxBatchesPerSweep; batchNumber++)
        {
            var ids = await db.Runs
                .AsNoTracking()
                .Where(x =>
                    x.Status == RunStatus.Success &&
                    x.CompletedAt != null &&
                    x.CompletedAt <= cutoff &&
                    x.AggregatedAt != null)
                .OrderBy(x => x.Sequence)
                .Select(x => x.Id)
                .Take(_options.SafeBatchSize)
                .ToListAsync(cancellationToken);

            if (ids.Count == 0)
            {
                break;
            }

            var deleted = await db.Runs
                .Where(x => ids.Contains(x.Id))
                .ExecuteDeleteAsync(cancellationToken);

            totalPurged += deleted;

            if (ids.Count < _options.SafeBatchSize)
            {
                break;
            }
        }

        return totalPurged;
    }

    private static RunAggregateDelta CreateDelta(IEnumerable<AgentRun> runs)
    {
        var materialized = runs.ToList();
        var durations = materialized
            .Select(GetDurationMilliseconds)
            .ToArray();

        return new RunAggregateDelta(
            materialized.Count,
            materialized.LongCount(x => x.Status == RunStatus.Success),
            materialized.LongCount(x => x.Status == RunStatus.Failed),
            materialized.LongCount(x => x.Status == RunStatus.Cancelled),
            materialized.Sum(x => x.InputTokens),
            materialized.Sum(x => x.OutputTokens),
            materialized.Sum(x => x.CostUsd),
            durations.Sum(),
            durations.Min(),
            durations.Max(),
            materialized.Min(x => x.StartedAt),
            materialized.Max(x => x.StartedAt));
    }

    private static long GetDurationMilliseconds(AgentRun run)
    {
        if (run.CompletedAt is null || run.CompletedAt <= run.StartedAt)
        {
            return 0;
        }

        return (long)Math.Round((run.CompletedAt.Value - run.StartedAt).TotalMilliseconds);
    }

    private static DateTimeOffset ToHourBucket(DateTimeOffset timestamp)
    {
        var utc = timestamp.ToUniversalTime();
        return new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, TimeSpan.Zero);
    }

    private static string NormalizeModel(string? model) => model?.Trim() ?? string.Empty;
    private static string NormalizeModelKey(string? model) => NormalizeModel(model).ToUpperInvariant();

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

    private sealed record AggregateKey(DateTimeOffset BucketStart, Guid ComponentId, string ModelKey);
    private sealed record PendingAggregate(
        AggregateKey Key,
        string ComponentName,
        string Environment,
        string Model,
        RunAggregateDelta Delta);
}

public sealed record RetentionSweepResult(
    bool Executed,
    bool LockUnavailable,
    int AggregatedRuns,
    int PurgedSuccessfulRuns)
{
    public static RetentionSweepResult Disabled { get; } = new(false, false, 0, 0);
    public static RetentionSweepResult Locked { get; } = new(false, true, 0, 0);
}

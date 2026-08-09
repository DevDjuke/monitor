using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Monitor.Domain;
using Monitor.Infrastructure;
using Monitor.Infrastructure.Retention;

namespace Monitor.Web.Pages;

public sealed class UsageModel(
    MonitorDbContext db,
    IOptions<RetentionOptions> retentionOptions) : PageModel
{
    private readonly RetentionOptions _retention = retentionOptions.Value;

    public long TotalRuns { get; private set; }
    public long SuccessRuns { get; private set; }
    public long FailedRuns { get; private set; }
    public long CancelledRuns { get; private set; }
    public long InputTokens { get; private set; }
    public long OutputTokens { get; private set; }
    public double CostUsd { get; private set; }
    public long StoredRawRuns { get; private set; }
    public long PendingAggregation { get; private set; }
    public long RetainedSuccessfulRuns { get; private set; }
    public long ForensicRuns { get; private set; }
    public IReadOnlyList<BucketRow> RecentBuckets { get; private set; } = [];

    public bool RetentionEnabled => _retention.Enabled;
    public int SuccessfulRunDetailDays => _retention.SuccessfulRunDetailDays;
    public int AggregationDelayMinutes => _retention.AggregationDelayMinutes;
    public int SweepIntervalMinutes => _retention.SweepIntervalMinutes;

    public double SuccessRate => TotalRuns == 0 ? 0 : SuccessRuns * 100d / TotalRuns;

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var aggregateSummary = await db.RunAggregates
            .AsNoTracking()
            .GroupBy(_ => 1)
            .Select(group => new Summary(
                group.Sum(x => x.TotalRuns),
                group.Sum(x => x.SuccessRuns),
                group.Sum(x => x.FailedRuns),
                group.Sum(x => x.CancelledRuns),
                group.Sum(x => x.InputTokens),
                group.Sum(x => x.OutputTokens),
                group.Sum(x => x.CostUsd)))
            .SingleOrDefaultAsync(cancellationToken)
            ?? Summary.Empty;

        var pendingSummary = await db.Runs
            .AsNoTracking()
            .Where(x =>
                x.Status != RunStatus.Running &&
                x.CompletedAt != null &&
                x.AggregatedAt == null)
            .GroupBy(_ => 1)
            .Select(group => new Summary(
                group.LongCount(),
                group.Sum(x => x.Status == RunStatus.Success ? 1L : 0L),
                group.Sum(x => x.Status == RunStatus.Failed ? 1L : 0L),
                group.Sum(x => x.Status == RunStatus.Cancelled ? 1L : 0L),
                group.Sum(x => x.InputTokens),
                group.Sum(x => x.OutputTokens),
                group.Sum(x => x.CostUsd)))
            .SingleOrDefaultAsync(cancellationToken)
            ?? Summary.Empty;

        TotalRuns = aggregateSummary.TotalRuns + pendingSummary.TotalRuns;
        SuccessRuns = aggregateSummary.SuccessRuns + pendingSummary.SuccessRuns;
        FailedRuns = aggregateSummary.FailedRuns + pendingSummary.FailedRuns;
        CancelledRuns = aggregateSummary.CancelledRuns + pendingSummary.CancelledRuns;
        InputTokens = aggregateSummary.InputTokens + pendingSummary.InputTokens;
        OutputTokens = aggregateSummary.OutputTokens + pendingSummary.OutputTokens;
        CostUsd = aggregateSummary.CostUsd + pendingSummary.CostUsd;
        PendingAggregation = pendingSummary.TotalRuns;

        StoredRawRuns = await db.Runs.LongCountAsync(cancellationToken);
        RetainedSuccessfulRuns = await db.Runs.LongCountAsync(x => x.Status == RunStatus.Success, cancellationToken);
        ForensicRuns = await db.Runs.LongCountAsync(
            x => x.Status == RunStatus.Failed || x.Status == RunStatus.Cancelled,
            cancellationToken);

        var since = DateTimeOffset.UtcNow.AddHours(-48);
        RecentBuckets = await db.RunAggregates
            .AsNoTracking()
            .Where(x => x.BucketStart >= since)
            .GroupBy(x => x.BucketStart)
            .Select(group => new BucketRow(
                group.Key,
                group.Sum(x => x.TotalRuns),
                group.Sum(x => x.SuccessRuns),
                group.Sum(x => x.FailedRuns),
                group.Sum(x => x.CancelledRuns),
                group.Sum(x => x.InputTokens + x.OutputTokens),
                group.Sum(x => x.CostUsd),
                group.Sum(x => x.TotalDurationMs)))
            .OrderByDescending(x => x.BucketStart)
            .Take(48)
            .ToListAsync(cancellationToken);
    }

    private sealed record Summary(
        long TotalRuns,
        long SuccessRuns,
        long FailedRuns,
        long CancelledRuns,
        long InputTokens,
        long OutputTokens,
        double CostUsd)
    {
        public static Summary Empty { get; } = new(0, 0, 0, 0, 0, 0, 0);
    }

    public sealed record BucketRow(
        DateTimeOffset BucketStart,
        long TotalRuns,
        long SuccessRuns,
        long FailedRuns,
        long CancelledRuns,
        long Tokens,
        double CostUsd,
        long TotalDurationMs)
    {
        public double AverageDurationMs => TotalRuns == 0 ? 0 : (double)TotalDurationMs / TotalRuns;
    }
}

using Microsoft.AspNetCore.Mvc;
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

    [BindProperty(SupportsGet = true)]
    public string Window { get; set; } = "48h";

    [BindProperty(SupportsGet = true)]
    public Guid? ComponentId { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Environment { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Model { get; set; }

    [BindProperty(SupportsGet = true)]
    public FailureCategory? FailureCategory { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? FailureSearch { get; set; }

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

    public IReadOnlyList<ComponentOption> ComponentOptions { get; private set; } = [];
    public IReadOnlyList<string> EnvironmentOptions { get; private set; } = [];
    public IReadOnlyList<string> ModelOptions { get; private set; } = [];
    public IReadOnlyList<FailureCategory> FailureCategories { get; } = Enum.GetValues<FailureCategory>();
    public IReadOnlyList<BucketRow> RecentBuckets { get; private set; } = [];
    public IReadOnlyList<FailureGroupRow> TopFailureGroups { get; private set; } = [];

    public bool RetentionEnabled => _retention.Enabled;
    public int SuccessfulRunDetailDays => _retention.SuccessfulRunDetailDays;
    public int AggregationDelayMinutes => _retention.AggregationDelayMinutes;
    public int SweepIntervalMinutes => _retention.SweepIntervalMinutes;
    public double SuccessRate => TotalRuns == 0 ? 0 : SuccessRuns * 100d / TotalRuns;
    public string ScopeLabel { get; private set; } = "Last 48 hours";
    public string RecentBucketLabel { get; private set; } = "Last 48 hours";

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        NormalizeFilters();
        await LoadFilterOptionsAsync(cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var since = ResolveWindowStart(now, Window);
        ScopeLabel = BuildScopeLabel();
        RecentBucketLabel = BuildBucketLabel(Window);

        var aggregateScope = ApplyAggregateScope(
            db.RunAggregates.AsNoTracking(),
            since);

        var aggregateSummary = await aggregateScope
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

        var pendingScope = ApplyRunScope(
            db.Runs
                .AsNoTracking()
                .Where(x =>
                    x.Status != RunStatus.Running &&
                    x.CompletedAt != null &&
                    x.AggregatedAt == null),
            since,
            useCompletedAt: true);

        var pendingSummary = await pendingScope
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

        var rawScope = ApplyRunScope(db.Runs.AsNoTracking(), since, useCompletedAt: false);
        StoredRawRuns = await rawScope.LongCountAsync(cancellationToken);
        RetainedSuccessfulRuns = await rawScope.LongCountAsync(
            x => x.Status == RunStatus.Success,
            cancellationToken);
        ForensicRuns = await rawScope.LongCountAsync(
            x => x.Status == RunStatus.Failed || x.Status == RunStatus.Cancelled,
            cancellationToken);

        await LoadFailureGroupsAsync(since, cancellationToken);
        await LoadBucketsAsync(aggregateScope, cancellationToken);
    }

    private async Task LoadFilterOptionsAsync(CancellationToken cancellationToken)
    {
        ComponentOptions = await db.Components
            .AsNoTracking()
            .OrderBy(x => x.Name)
            .ThenBy(x => x.Environment)
            .Select(x => new ComponentOption(x.Id, x.Name, x.Environment))
            .ToListAsync(cancellationToken);

        EnvironmentOptions = await db.Components
            .AsNoTracking()
            .Select(x => x.Environment)
            .Where(x => x != "")
            .Distinct()
            .OrderBy(x => x)
            .ToListAsync(cancellationToken);

        var rawModels = await db.Runs
            .AsNoTracking()
            .Where(x => x.Model != null && x.Model != "")
            .Select(x => x.Model!)
            .Distinct()
            .ToListAsync(cancellationToken);

        var aggregateModels = await db.RunAggregates
            .AsNoTracking()
            .Where(x => x.Model != null && x.Model != "")
            .Select(x => x.Model!)
            .Distinct()
            .ToListAsync(cancellationToken);

        ModelOptions = rawModels
            .Concat(aggregateModels)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task LoadFailureGroupsAsync(DateTimeOffset? since, CancellationToken cancellationToken)
    {
        var failureRuns = ApplyRunScope(
            db.Runs
                .AsNoTracking()
                .Where(x =>
                    x.FailureGroupId != null &&
                    x.CompletedAt != null &&
                    (x.Status == RunStatus.Failed || x.Status == RunStatus.Cancelled)),
            since,
            useCompletedAt: true);

        var failureStats = failureRuns
            .GroupBy(x => x.FailureGroupId!.Value)
            .Select(group => new
            {
                FailureGroupId = group.Key,
                Occurrences = group.LongCount(),
                FirstSeenAt = group.Min(x => x.CompletedAt),
                LastSeenAt = group.Max(x => x.CompletedAt)
            });

        var groups = db.FailureGroups.AsNoTracking().AsQueryable();
        if (FailureCategory is not null)
        {
            groups = groups.Where(x => x.Category == FailureCategory.Value);
        }

        if (!string.IsNullOrWhiteSpace(FailureSearch))
        {
            var search = FailureSearch;
            groups = groups.Where(x =>
                x.Operation.Contains(search) ||
                (x.FailureType != null && x.FailureType.Contains(search)) ||
                (x.Dependency != null && x.Dependency.Contains(search)) ||
                (x.MessageTemplate != null && x.MessageTemplate.Contains(search)) ||
                x.Fingerprint.Contains(search));
        }

        TopFailureGroups = await (
                from group in groups
                join stats in failureStats on group.Id equals stats.FailureGroupId
                orderby stats.Occurrences descending, stats.LastSeenAt descending
                select new FailureGroupRow(
                    group.Id,
                    group.Category,
                    group.Operation,
                    group.FailureType,
                    group.Dependency,
                    group.HttpStatusCode,
                    group.MessageTemplate,
                    stats.Occurrences,
                    stats.FirstSeenAt,
                    stats.LastSeenAt))
            .Take(25)
            .ToListAsync(cancellationToken);
    }

    private async Task LoadBucketsAsync(
        IQueryable<RunAggregate> aggregateScope,
        CancellationToken cancellationToken)
    {
        var bucketLimit = Window switch
        {
            "24h" => 24,
            "48h" => 48,
            _ => 168
        };

        var bucketData = await aggregateScope
            .GroupBy(x => x.BucketStart)
            .Select(group => new
            {
                BucketStart = group.Key,
                TotalRuns = group.Sum(x => x.TotalRuns),
                SuccessRuns = group.Sum(x => x.SuccessRuns),
                FailedRuns = group.Sum(x => x.FailedRuns),
                CancelledRuns = group.Sum(x => x.CancelledRuns),
                InputTokens = group.Sum(x => x.InputTokens),
                OutputTokens = group.Sum(x => x.OutputTokens),
                CostUsd = group.Sum(x => x.CostUsd),
                TotalDurationMs = group.Sum(x => x.TotalDurationMs)
            })
            .OrderByDescending(x => x.BucketStart)
            .Take(bucketLimit)
            .ToListAsync(cancellationToken);

        RecentBuckets = bucketData
            .Select(x => new BucketRow(
                x.BucketStart,
                x.TotalRuns,
                x.SuccessRuns,
                x.FailedRuns,
                x.CancelledRuns,
                x.InputTokens + x.OutputTokens,
                x.CostUsd,
                x.TotalDurationMs))
            .ToList();
    }

    private IQueryable<RunAggregate> ApplyAggregateScope(
        IQueryable<RunAggregate> query,
        DateTimeOffset? since)
    {
        if (ComponentId is not null)
        {
            query = query.Where(x => x.ComponentId == ComponentId.Value);
        }

        if (!string.IsNullOrWhiteSpace(Environment))
        {
            query = query.Where(x => x.Environment == Environment);
        }

        if (!string.IsNullOrWhiteSpace(Model))
        {
            query = query.Where(x => x.Model == Model);
        }

        if (since is not null)
        {
            query = query.Where(x => x.BucketStart >= since.Value);
        }

        return query;
    }

    private IQueryable<AgentRun> ApplyRunScope(
        IQueryable<AgentRun> query,
        DateTimeOffset? since,
        bool useCompletedAt)
    {
        if (ComponentId is not null)
        {
            query = query.Where(x => x.ComponentId == ComponentId.Value);
        }

        if (!string.IsNullOrWhiteSpace(Environment))
        {
            query = query.Where(x => x.Component.Environment == Environment);
        }

        if (!string.IsNullOrWhiteSpace(Model))
        {
            query = query.Where(x => x.Model == Model);
        }

        if (since is not null)
        {
            query = useCompletedAt
                ? query.Where(x => x.CompletedAt >= since.Value)
                : query.Where(x => x.StartedAt >= since.Value);
        }

        return query;
    }

    private void NormalizeFilters()
    {
        Window = Window?.Trim().ToLowerInvariant() switch
        {
            "24h" => "24h",
            "48h" => "48h",
            "7d" => "7d",
            "30d" => "30d",
            "all" => "all",
            _ => "48h"
        };

        Environment = NormalizeOptional(Environment);
        Model = NormalizeOptional(Model);
        FailureSearch = NormalizeOptional(FailureSearch);
    }

    private string BuildScopeLabel()
    {
        var parts = new List<string>
        {
            Window switch
            {
                "24h" => "Last 24 hours",
                "48h" => "Last 48 hours",
                "7d" => "Last 7 days",
                "30d" => "Last 30 days",
                _ => "All retained history"
            }
        };

        if (ComponentId is not null)
        {
            var component = ComponentOptions.FirstOrDefault(x => x.Id == ComponentId.Value);
            parts.Add(component is null ? "selected component" : component.Name);
        }

        if (!string.IsNullOrWhiteSpace(Environment))
        {
            parts.Add(Environment);
        }

        if (!string.IsNullOrWhiteSpace(Model))
        {
            parts.Add(Model);
        }

        return string.Join(" · ", parts);
    }

    private static string BuildBucketLabel(string window) => window switch
    {
        "24h" => "Last 24 hourly buckets",
        "48h" => "Last 48 hourly buckets",
        "7d" => "Last 7 days · hourly",
        "30d" => "Latest 168 hourly buckets in 30-day scope",
        _ => "Latest 168 hourly buckets in retained history"
    };

    private static DateTimeOffset? ResolveWindowStart(DateTimeOffset now, string window)
    {
        DateTimeOffset? start = window switch
        {
            "24h" => now.AddHours(-24),
            "48h" => now.AddHours(-48),
            "7d" => now.AddDays(-7),
            "30d" => now.AddDays(-30),
            _ => null
        };

        if (start is null)
        {
            return null;
        }

        return new DateTimeOffset(
            start.Value.Year,
            start.Value.Month,
            start.Value.Day,
            start.Value.Hour,
            0,
            0,
            TimeSpan.Zero);
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

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

    public sealed record ComponentOption(Guid Id, string Name, string Environment);

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

    public sealed record FailureGroupRow(
        Guid Id,
        FailureCategory Category,
        string Operation,
        string? FailureType,
        string? Dependency,
        int? HttpStatusCode,
        string? MessageTemplate,
        long Occurrences,
        DateTimeOffset? FirstSeenAt,
        DateTimeOffset? LastSeenAt);
}

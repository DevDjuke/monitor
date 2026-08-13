using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Monitor.Domain;
using Monitor.Infrastructure;

namespace Monitor.Web.Pages;

public sealed class MetricsModel(MonitorDbContext db) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string Window { get; set; } = "24h";

    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    [BindProperty(SupportsGet = true)]
    public Guid? ComponentId { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Environment { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Name { get; set; }

    [BindProperty(SupportsGet = true)]
    public MetricKind? Kind { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Scope { get; set; }

    [BindProperty(SupportsGet = true)]
    public int Take { get; set; } = 200;

    public IReadOnlyList<ComponentOption> Components { get; private set; } = [];
    public IReadOnlyList<string> Environments { get; private set; } = [];
    public IReadOnlyList<string> Names { get; private set; } = [];
    public IReadOnlyList<string> Scopes { get; private set; } = [];
    public IReadOnlyList<MetricKind> Kinds { get; } = Enum.GetValues<MetricKind>();
    public IReadOnlyList<Row> Rows { get; private set; } = [];
    public long MatchingCount { get; private set; }
    public int UniqueMetricCount { get; private set; }
    public long DistributionCount { get; private set; }
    public long MissingValueCount { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        NormalizeFilters();

        Components = await db.Components
            .AsNoTracking()
            .OrderBy(x => x.Name)
            .ThenBy(x => x.Environment)
            .Select(x => new ComponentOption(x.Id, x.Name, x.Environment))
            .ToListAsync(cancellationToken);
        Environments = Components
            .Select(x => x.Environment)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x)
            .ToArray();

        var metrics = db.Set<MetricPoint>().AsNoTracking();
        Names = await metrics
            .Where(x => x.Name != "")
            .Select(x => x.Name)
            .Distinct()
            .OrderBy(x => x)
            .Take(200)
            .ToListAsync(cancellationToken);
        Scopes = await metrics
            .Where(x => x.ScopeName != null && x.ScopeName != "")
            .Select(x => x.ScopeName!)
            .Distinct()
            .OrderBy(x => x)
            .Take(200)
            .ToListAsync(cancellationToken);

        var query = ApplyFilters(metrics);
        MatchingCount = await query.LongCountAsync(cancellationToken);
        UniqueMetricCount = await query.Select(x => x.Name).Distinct().CountAsync(cancellationToken);
        DistributionCount = await query.LongCountAsync(
            x => x.Kind == MetricKind.Histogram ||
                 x.Kind == MetricKind.ExponentialHistogram ||
                 x.Kind == MetricKind.Summary,
            cancellationToken);
        MissingValueCount = await query.LongCountAsync(x => !x.HasRecordedValue, cancellationToken);

        Rows = await query
            .OrderByDescending(x => x.Timestamp)
            .ThenByDescending(x => x.CreatedAt)
            .Take(Take)
            .Select(x => new Row(
                x.Id,
                x.ComponentId,
                x.Component.Name,
                x.Component.Environment,
                x.Name,
                x.Description,
                x.Unit,
                x.Kind,
                x.Temporality,
                x.IsMonotonic,
                x.HasRecordedValue,
                x.StartTimestamp,
                x.Timestamp,
                x.NumericValue,
                x.Count,
                x.Sum,
                x.Min,
                x.Max,
                x.Scale,
                x.ZeroCount,
                x.ZeroThreshold,
                x.BucketCountsJson,
                x.ExplicitBoundsJson,
                x.PositiveBucketsJson,
                x.NegativeBucketsJson,
                x.QuantilesJson,
                x.AttributesJson,
                x.ResourceAttributesJson,
                x.MetricMetadataJson,
                x.ExemplarsJson,
                x.ScopeName,
                x.ScopeVersion,
                x.ResourceSchemaUrl,
                x.ScopeSchemaUrl,
                x.Flags,
                x.Source))
            .ToListAsync(cancellationToken);
    }

    private IQueryable<MetricPoint> ApplyFilters(IQueryable<MetricPoint> query)
    {
        var cutoff = GetCutoff();
        if (cutoff.HasValue)
        {
            query = query.Where(x => x.Timestamp >= cutoff.Value);
        }

        if (ComponentId.HasValue)
        {
            query = query.Where(x => x.ComponentId == ComponentId.Value);
        }

        if (!string.IsNullOrWhiteSpace(Environment))
        {
            var environment = Environment;
            query = query.Where(x => x.Component.Environment == environment);
        }

        if (!string.IsNullOrWhiteSpace(Name))
        {
            var name = Name;
            query = query.Where(x => x.Name == name);
        }

        if (Kind.HasValue)
        {
            query = query.Where(x => x.Kind == Kind.Value);
        }

        if (!string.IsNullOrWhiteSpace(Scope))
        {
            var scope = Scope;
            query = query.Where(x => x.ScopeName == scope);
        }

        if (!string.IsNullOrWhiteSpace(Search))
        {
            var search = Search;
            query = query.Where(x =>
                x.Name.Contains(search) ||
                (x.Description != null && x.Description.Contains(search)) ||
                (x.Unit != null && x.Unit.Contains(search)) ||
                (x.ScopeName != null && x.ScopeName.Contains(search)) ||
                (x.AttributesJson != null && x.AttributesJson.Contains(search)) ||
                (x.ResourceAttributesJson != null && x.ResourceAttributesJson.Contains(search)) ||
                (x.MetricMetadataJson != null && x.MetricMetadataJson.Contains(search)));
        }

        return query;
    }

    private DateTimeOffset? GetCutoff()
    {
        var now = DateTimeOffset.UtcNow;
        return Window switch
        {
            "1h" => now.AddHours(-1),
            "6h" => now.AddHours(-6),
            "7d" => now.AddDays(-7),
            "30d" => now.AddDays(-30),
            "all" => null,
            _ => now.AddHours(-24)
        };
    }

    private void NormalizeFilters()
    {
        Window = Window?.Trim().ToLowerInvariant() switch
        {
            "1h" => "1h",
            "6h" => "6h",
            "7d" => "7d",
            "30d" => "30d",
            "all" => "all",
            _ => "24h"
        };
        Search = string.IsNullOrWhiteSpace(Search) ? null : Search.Trim();
        Environment = string.IsNullOrWhiteSpace(Environment) ? null : Environment.Trim().ToLowerInvariant();
        Name = string.IsNullOrWhiteSpace(Name) ? null : Name.Trim();
        Scope = string.IsNullOrWhiteSpace(Scope) ? null : Scope.Trim();
        Take = Take is 50 or 100 or 200 or 500 ? Take : 200;
    }

    public static string FormatValue(Row row)
    {
        if (!row.HasRecordedValue)
        {
            return "No recorded value";
        }

        var suffix = string.IsNullOrWhiteSpace(row.Unit) ? string.Empty : $" {row.Unit}";
        return row.Kind switch
        {
            MetricKind.Gauge or MetricKind.Sum when row.NumericValue.HasValue => $"{row.NumericValue.Value:G6}{suffix}",
            MetricKind.Histogram or MetricKind.ExponentialHistogram =>
                $"count {row.Count?.ToString("0") ?? "—"} · sum {row.Sum?.ToString("G6") ?? "—"}{suffix}",
            MetricKind.Summary => $"count {row.Count?.ToString("0") ?? "—"} · sum {row.Sum?.ToString("G6") ?? "—"}{suffix}",
            _ => "—"
        };
    }

    public sealed record ComponentOption(Guid Id, string Name, string Environment);

    public sealed record Row(
        Guid Id,
        Guid ComponentId,
        string ComponentName,
        string Environment,
        string Name,
        string? Description,
        string? Unit,
        MetricKind Kind,
        MetricAggregationTemporality Temporality,
        bool IsMonotonic,
        bool HasRecordedValue,
        DateTimeOffset? StartTimestamp,
        DateTimeOffset Timestamp,
        double? NumericValue,
        decimal? Count,
        double? Sum,
        double? Min,
        double? Max,
        int? Scale,
        decimal? ZeroCount,
        double? ZeroThreshold,
        string? BucketCountsJson,
        string? ExplicitBoundsJson,
        string? PositiveBucketsJson,
        string? NegativeBucketsJson,
        string? QuantilesJson,
        string? AttributesJson,
        string? ResourceAttributesJson,
        string? MetricMetadataJson,
        string? ExemplarsJson,
        string? ScopeName,
        string? ScopeVersion,
        string? ResourceSchemaUrl,
        string? ScopeSchemaUrl,
        long Flags,
        string Source);
}

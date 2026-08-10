using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Monitor.Domain;
using Monitor.Infrastructure;

namespace Monitor.Web.Pages;

public sealed class LogsModel(MonitorDbContext db) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string Window { get; set; } = "24h";

    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    [BindProperty(SupportsGet = true)]
    public Guid? ComponentId { get; set; }

    [BindProperty(SupportsGet = true)]
    public LogEventLevel? MinimumLevel { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Environment { get; set; }

    [BindProperty(SupportsGet = true)]
    public Guid? RunId { get; set; }

    [BindProperty(SupportsGet = true)]
    public Guid? SpanId { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Source { get; set; }

    [BindProperty(SupportsGet = true)]
    public int Take { get; set; } = 200;

    public IReadOnlyList<ComponentOption> Components { get; private set; } = [];
    public IReadOnlyList<string> Environments { get; private set; } = [];
    public IReadOnlyList<string> Sources { get; private set; } = [];
    public IReadOnlyList<LogEventLevel> Levels { get; } = Enum.GetValues<LogEventLevel>();
    public IReadOnlyList<Row> Rows { get; private set; } = [];
    public long MatchingCount { get; private set; }
    public long ErrorCount { get; private set; }
    public long WarningCount { get; private set; }
    public long UnlinkedCount { get; private set; }

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

        var query = ApplyFilters(db.LogEvents.AsNoTracking());

        MatchingCount = await query.LongCountAsync(cancellationToken);
        ErrorCount = await query.LongCountAsync(x => x.Level >= LogEventLevel.Error, cancellationToken);
        WarningCount = await query.LongCountAsync(x => x.Level == LogEventLevel.Warning, cancellationToken);
        UnlinkedCount = await query.LongCountAsync(x => x.RunId == null, cancellationToken);

        Sources = await db.LogEvents
            .AsNoTracking()
            .Where(x => x.Source != null && x.Source != "")
            .Select(x => x.Source!)
            .Distinct()
            .OrderBy(x => x)
            .Take(100)
            .ToListAsync(cancellationToken);

        Rows = await query
            .OrderByDescending(x => x.Timestamp)
            .ThenByDescending(x => x.CreatedAt)
            .Take(Take)
            .Select(x => new Row(
                x.Id,
                x.ComponentId,
                x.Component.Name,
                x.Component.Environment,
                x.RunId,
                x.SpanId,
                x.Timestamp,
                x.ObservedAt,
                x.Level,
                x.SeverityText,
                x.EventName,
                x.Message,
                x.MessageTemplate,
                x.Source,
                x.PropertiesJson,
                x.ExceptionType,
                x.ExceptionMessage,
                x.ExceptionStackTrace,
                x.ExternalTraceId,
                x.ExternalSpanId))
            .ToListAsync(cancellationToken);
    }

    private IQueryable<LogEvent> ApplyFilters(IQueryable<LogEvent> query)
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

        if (MinimumLevel.HasValue)
        {
            query = query.Where(x => x.Level >= MinimumLevel.Value);
        }

        if (!string.IsNullOrWhiteSpace(Environment))
        {
            var environment = Environment;
            query = query.Where(x => x.Component.Environment == environment);
        }

        if (RunId.HasValue)
        {
            query = query.Where(x => x.RunId == RunId.Value);
        }

        if (SpanId.HasValue)
        {
            query = query.Where(x => x.SpanId == SpanId.Value);
        }

        if (!string.IsNullOrWhiteSpace(Source))
        {
            var source = Source;
            query = query.Where(x => x.Source == source);
        }

        if (!string.IsNullOrWhiteSpace(Search))
        {
            var search = Search;
            query = query.Where(x =>
                x.Message.Contains(search) ||
                (x.MessageTemplate != null && x.MessageTemplate.Contains(search)) ||
                (x.EventName != null && x.EventName.Contains(search)) ||
                (x.Source != null && x.Source.Contains(search)) ||
                (x.ExceptionType != null && x.ExceptionType.Contains(search)) ||
                (x.ExceptionMessage != null && x.ExceptionMessage.Contains(search)) ||
                (x.PropertiesJson != null && x.PropertiesJson.Contains(search)));
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
        Source = string.IsNullOrWhiteSpace(Source) ? null : Source.Trim();
        Take = Take is 50 or 100 or 200 or 500 ? Take : 200;
    }

    public sealed record ComponentOption(Guid Id, string Name, string Environment);

    public sealed record Row(
        Guid Id,
        Guid ComponentId,
        string ComponentName,
        string Environment,
        Guid? RunId,
        Guid? SpanId,
        DateTimeOffset Timestamp,
        DateTimeOffset ObservedAt,
        LogEventLevel Level,
        string? SeverityText,
        string? EventName,
        string Message,
        string? MessageTemplate,
        string? Source,
        string? PropertiesJson,
        string? ExceptionType,
        string? ExceptionMessage,
        string? ExceptionStackTrace,
        string? ExternalTraceId,
        string? ExternalSpanId);
}

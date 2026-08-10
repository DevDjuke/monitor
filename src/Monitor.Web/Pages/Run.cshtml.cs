using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Monitor.Domain;
using Monitor.Infrastructure;

namespace Monitor.Web.Pages;

public sealed class RunModel(MonitorDbContext db) : PageModel
{
    public AgentRun Run { get; private set; } = null!;
    public IReadOnlyList<TraceSpan> Spans { get; private set; } = [];
    public IReadOnlyList<LogEvent> LogEvents { get; private set; } = [];
    public IReadOnlyList<TimelineRow> Timeline { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        var run = await db.Runs
            .AsNoTracking()
            .Include(x => x.Component)
            .Include(x => x.FailureGroup)
            .Include(x => x.Spans)
            .Include(x => x.LogEvents)
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (run is null)
        {
            return NotFound();
        }

        Run = run;
        Spans = run.Spans.OrderBy(x => x.StartedAt).ToList();
        LogEvents = run.LogEvents.OrderBy(x => x.Timestamp).ThenBy(x => x.CreatedAt).ToList();
        Timeline = BuildTimeline(Spans, LogEvents);
        return Page();
    }

    private static IReadOnlyList<TimelineRow> BuildTimeline(
        IEnumerable<TraceSpan> spans,
        IEnumerable<LogEvent> logEvents)
    {
        var spanRows = spans.Select(span =>
        {
            TimeSpan? duration = span.CompletedAt is null
                ? null
                : span.CompletedAt.Value - span.StartedAt;
            return new TimelineRow(
                span.StartedAt,
                "SPAN",
                span.Name,
                span.Kind.ToString(),
                duration is null ? "running" : $"{duration.Value.TotalMilliseconds:0} ms",
                $"span-{span.Status.ToString().ToLowerInvariant()}",
                span.Id,
                null);
        });

        var logRows = logEvents.Select(logEvent => new TimelineRow(
            logEvent.Timestamp,
            logEvent.Level.ToString().ToUpperInvariant(),
            logEvent.Message,
            logEvent.EventName ?? logEvent.Source ?? "log event",
            logEvent.ExceptionType,
            $"log-{logEvent.Level.ToString().ToLowerInvariant()}",
            logEvent.SpanId,
            logEvent.PropertiesJson));

        return spanRows
            .Concat(logRows)
            .OrderBy(x => x.Timestamp)
            .ThenBy(x => x.Kind == "SPAN" ? 0 : 1)
            .ToList();
    }

    public sealed record TimelineRow(
        DateTimeOffset Timestamp,
        string Kind,
        string Title,
        string? Subtitle,
        string? Detail,
        string CssClass,
        Guid? SpanId,
        string? PropertiesJson);
}

using Microsoft.EntityFrameworkCore;

namespace Monitor.Infrastructure.Logs;

public sealed class LogCorrelationService(MonitorDbContext db)
{
    public async Task<int> CorrelatePendingAsync(
        int batchSize = 500,
        CancellationToken cancellationToken = default)
    {
        var pending = await db.LogEvents
            .Where(x => x.RunId == null && x.ExternalTraceId != null)
            .OrderBy(x => x.CreatedAt)
            .Take(Math.Clamp(batchSize, 1, 2000))
            .ToListAsync(cancellationToken);

        if (pending.Count == 0)
        {
            return 0;
        }

        var componentIds = pending.Select(x => x.ComponentId).Distinct().ToArray();
        var traceIds = pending.Select(x => x.ExternalTraceId!).Distinct().ToArray();
        var runs = await db.Runs
            .AsNoTracking()
            .Where(x => componentIds.Contains(x.ComponentId) && x.TraceId != null && traceIds.Contains(x.TraceId))
            .Select(x => new { x.Id, x.ComponentId, x.TraceId })
            .ToListAsync(cancellationToken);

        if (runs.Count == 0)
        {
            return 0;
        }

        var runIds = runs.Select(x => x.Id).ToArray();
        var spans = await db.Spans
            .AsNoTracking()
            .Where(x => runIds.Contains(x.RunId) && x.ExternalSpanId != null)
            .Select(x => new { x.Id, x.RunId, x.ExternalSpanId })
            .ToListAsync(cancellationToken);

        var runLookup = runs.ToDictionary(
            x => (x.ComponentId, x.TraceId!),
            x => x.Id);
        var spanLookup = spans.ToDictionary(
            x => (x.RunId, x.ExternalSpanId!),
            x => x.Id);

        var correlated = 0;
        foreach (var logEvent in pending)
        {
            if (!runLookup.TryGetValue((logEvent.ComponentId, logEvent.ExternalTraceId!), out var runId))
            {
                continue;
            }

            Guid? spanId = null;
            if (!string.IsNullOrWhiteSpace(logEvent.ExternalSpanId) &&
                spanLookup.TryGetValue((runId, logEvent.ExternalSpanId), out var resolvedSpanId))
            {
                spanId = resolvedSpanId;
            }

            logEvent.Correlate(runId, spanId);
            correlated++;
        }

        if (correlated > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return correlated;
    }
}

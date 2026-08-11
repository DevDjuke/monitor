using Microsoft.EntityFrameworkCore;
using Monitor.Domain;
using Monitor.Infrastructure;
using Monitor.Web.Auth;
using Monitor.Web.Realtime;

namespace Monitor.Web.Api;

public static class LogEndpoints
{
    public static IEndpointRouteBuilder MapLogApi(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api");
        api.AddEndpointFilter(async (context, next) =>
        {
            var httpContext = context.HttpContext;
            var authenticator = httpContext.RequestServices.GetRequiredService<IngestionCredentialAuthenticator>();
            var identity = await authenticator.AuthenticateAsync(
                httpContext,
                allowOperator: true,
                httpContext.RequestAborted);

            if (identity is null)
            {
                return Results.Unauthorized();
            }

            IngestionCredentialAuthenticator.SetIdentity(httpContext, identity);
            return await next(context);
        });

        api.MapPost("/runs/{runId:guid}/events", CreateEvent);
        api.MapGet("/logs/query", QueryLogs);
        return endpoints;
    }

    private static async Task<IResult> CreateEvent(
        Guid runId,
        CreateLogEventRequest request,
        HttpContext httpContext,
        MonitorDbContext db,
        MonitorRealtimePublisher realtime,
        CancellationToken cancellationToken)
    {
        var run = await db.Runs
            .AsNoTracking()
            .Where(x => x.Id == runId)
            .Select(x => new { x.Id, x.ComponentId })
            .SingleOrDefaultAsync(cancellationToken);
        if (run is null)
        {
            return Results.NotFound();
        }

        var identity = IngestionCredentialAuthenticator.GetIdentity(httpContext);
        if (!identity.CanAccess(run.ComponentId))
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        if (request.SpanId is Guid spanId &&
            !await db.Spans.AnyAsync(x => x.Id == spanId && x.RunId == runId, cancellationToken))
        {
            return Results.BadRequest(new { error = "span does not belong to this run" });
        }

        var now = DateTimeOffset.UtcNow;
        var logEvent = LogEvent.Create(
            run.ComponentId,
            run.Id,
            request.SpanId,
            request.Level,
            request.Timestamp ?? now,
            request.ObservedAt ?? now,
            request.Message ?? string.Empty,
            request.MessageTemplate,
            request.PropertiesJson,
            request.ExceptionType,
            request.ExceptionMessage,
            request.ExceptionStackTrace,
            request.Source,
            request.EventName);

        db.LogEvents.Add(logEvent);
        await db.SaveChangesAsync(cancellationToken);
        await realtime.PublishLogAppendedAsync(logEvent, cancellationToken);
        return Results.Created($"/api/logs/{logEvent.Id}", new { logEvent.Id, logEvent.Timestamp });
    }

    private static async Task<IResult> QueryLogs(
        Guid? componentId,
        LogEventLevel? level,
        string? environment,
        Guid? runId,
        Guid? spanId,
        string? source,
        string? search,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int? take,
        HttpContext httpContext,
        MonitorDbContext db,
        CancellationToken cancellationToken)
    {
        var identity = IngestionCredentialAuthenticator.GetIdentity(httpContext);
        if (identity.ComponentId is Guid scopedComponentId &&
            componentId.HasValue &&
            componentId.Value != scopedComponentId)
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        var query = db.LogEvents.AsNoTracking().AsQueryable();
        if (identity.ComponentId is Guid authorizedComponentId)
        {
            query = query.Where(x => x.ComponentId == authorizedComponentId);
        }
        else if (componentId.HasValue)
        {
            query = query.Where(x => x.ComponentId == componentId.Value);
        }

        if (level.HasValue)
        {
            query = query.Where(x => x.Level == level.Value);
        }

        if (!string.IsNullOrWhiteSpace(environment))
        {
            var normalizedEnvironment = environment.Trim().ToLowerInvariant();
            query = query.Where(x => x.Component.Environment == normalizedEnvironment);
        }

        if (runId.HasValue)
        {
            query = query.Where(x => x.RunId == runId.Value);
        }

        if (spanId.HasValue)
        {
            query = query.Where(x => x.SpanId == spanId.Value);
        }

        if (!string.IsNullOrWhiteSpace(source))
        {
            var normalizedSource = source.Trim();
            query = query.Where(x => x.Source == normalizedSource);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(x =>
                x.Message.Contains(term) ||
                (x.MessageTemplate != null && x.MessageTemplate.Contains(term)) ||
                (x.EventName != null && x.EventName.Contains(term)) ||
                (x.Source != null && x.Source.Contains(term)) ||
                (x.ExceptionType != null && x.ExceptionType.Contains(term)) ||
                (x.ExceptionMessage != null && x.ExceptionMessage.Contains(term)) ||
                (x.PropertiesJson != null && x.PropertiesJson.Contains(term)));
        }

        if (from.HasValue)
        {
            query = query.Where(x => x.Timestamp >= from.Value);
        }

        if (to.HasValue)
        {
            query = query.Where(x => x.Timestamp < to.Value);
        }

        var limit = Math.Clamp(take ?? 100, 1, 500);
        var rows = await query
            .OrderByDescending(x => x.Timestamp)
            .ThenByDescending(x => x.CreatedAt)
            .Take(limit)
            .Select(x => new
            {
                x.Id,
                x.ComponentId,
                Component = x.Component.Name,
                Environment = x.Component.Environment,
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
                x.ExternalSpanId
            })
            .ToListAsync(cancellationToken);

        return Results.Ok(new { items = rows, count = rows.Count, limit });
    }

    public sealed record CreateLogEventRequest(
        LogEventLevel Level,
        string? Message,
        DateTimeOffset? Timestamp = null,
        DateTimeOffset? ObservedAt = null,
        Guid? SpanId = null,
        string? MessageTemplate = null,
        string? PropertiesJson = null,
        string? ExceptionType = null,
        string? ExceptionMessage = null,
        string? ExceptionStackTrace = null,
        string? Source = null,
        string? EventName = null);
}

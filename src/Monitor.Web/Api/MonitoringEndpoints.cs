using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Monitor.Domain;
using Monitor.Infrastructure;
using Monitor.Infrastructure.Failures;
using Monitor.Web.Realtime;

namespace Monitor.Web.Api;

public static class MonitoringEndpoints
{
    public static IEndpointRouteBuilder MapMonitoringApi(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/health", () => Results.Ok(new { status = "ok", now = DateTimeOffset.UtcNow }));

        var expectedApiKey = endpoints.ServiceProvider
            .GetRequiredService<IConfiguration>()["Monitor:IngestionApiKey"];

        var api = endpoints.MapGroup("/api");
        api.AddEndpointFilter(async (context, next) =>
        {
            var httpContext = context.HttpContext;
            if (httpContext.User.Identity?.IsAuthenticated == true)
            {
                return await next(context);
            }

            if (string.IsNullOrWhiteSpace(expectedApiKey))
            {
                return Results.Json(
                    new { error = "The ingestion API key is not configured." },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var suppliedApiKey = httpContext.Request.Headers["X-Monitor-Key"].ToString();
            if (!ApiKeysMatch(expectedApiKey, suppliedApiKey))
            {
                return Results.Unauthorized();
            }

            return await next(context);
        });

        api.MapGet("/components", GetComponents);
        api.MapPost("/components/register", RegisterComponent);
        api.MapPost("/components/{id:guid}/heartbeat", Heartbeat);

        api.MapGet("/runs", GetRuns);
        api.MapGet("/runs/query", QueryRuns);
        api.MapGet("/runs/options", GetRunOptions);
        api.MapGet("/runs/{id:guid}", GetRun);
        api.MapPost("/runs", StartRun);
        api.MapPost("/runs/{id:guid}/complete", CompleteRun);
        api.MapPost("/runs/{runId:guid}/spans", CreateSpan);

        return endpoints;
    }

    private static bool ApiKeysMatch(string expected, string supplied)
    {
        if (string.IsNullOrEmpty(supplied))
        {
            return false;
        }

        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
        return CryptographicOperations.FixedTimeEquals(expectedHash, suppliedHash);
    }

    private static async Task<IResult> GetComponents(MonitorDbContext db, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var components = await db.Components
            .AsNoTracking()
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);

        return Results.Ok(components.Select(x => new
        {
            x.Id,
            x.Name,
            x.Slug,
            x.Type,
            x.Environment,
            x.Version,
            x.Enabled,
            Status = x.GetEffectiveStatus(now, TimeSpan.FromMinutes(2)),
            x.LastHeartbeatAt,
            x.LastRunAt
        }));
    }

    private static async Task<IResult> RegisterComponent(
        RegisterComponentRequest request,
        MonitorDbContext db,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name) ||
            string.IsNullOrWhiteSpace(request.Slug) ||
            string.IsNullOrWhiteSpace(request.Environment))
        {
            return Results.BadRequest(new { error = "name, slug and environment are required" });
        }

        var slug = request.Slug.Trim().ToLowerInvariant();
        var environment = request.Environment.Trim().ToLowerInvariant();
        var now = DateTimeOffset.UtcNow;

        var component = await db.Components.SingleOrDefaultAsync(
            x => x.Slug == slug && x.Environment == environment,
            cancellationToken);

        if (component is null)
        {
            component = MonitoredComponent.Create(
                request.Name.Trim(),
                slug,
                request.Type,
                environment,
                request.Version?.Trim(),
                now);
            db.Components.Add(component);
        }
        else
        {
            component.UpdateRegistration(request.Name.Trim(), request.Type, request.Version?.Trim(), now);
        }

        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(new { component.Id, component.Slug, component.Environment });
    }

    private static async Task<IResult> Heartbeat(
        Guid id,
        MonitorDbContext db,
        CancellationToken cancellationToken)
    {
        var component = await db.Components.FindAsync([id], cancellationToken);
        if (component is null)
        {
            return Results.NotFound();
        }

        component.Heartbeat(DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> GetRuns(
        int? take,
        MonitorDbContext db,
        CancellationToken cancellationToken)
    {
        var limit = Math.Clamp(take ?? 50, 1, 250);
        var runs = await db.Runs
            .AsNoTracking()
            .OrderByDescending(x => x.Sequence)
            .Take(limit)
            .Select(x => new
            {
                x.Id,
                x.Sequence,
                x.ComponentId,
                Component = x.Component.Name,
                x.Name,
                x.Status,
                x.StartedAt,
                x.CompletedAt,
                x.Model,
                x.InputTokens,
                x.OutputTokens,
                x.CostUsd
            })
            .ToListAsync(cancellationToken);

        return Results.Ok(runs);
    }

    private static async Task<IResult> QueryRuns(
        int? pageSize,
        long? before,
        Guid? componentId,
        RunStatus? status,
        string? environment,
        string? model,
        string? search,
        DateTimeOffset? from,
        DateTimeOffset? to,
        MonitorDbContext db,
        CancellationToken cancellationToken)
    {
        var limit = pageSize is 25 or 50 or 100 ? pageSize.Value : 50;
        var query = db.Runs.AsNoTracking().AsQueryable();

        if (before is > 0)
        {
            query = query.Where(x => x.Sequence < before.Value);
        }

        if (componentId.HasValue)
        {
            query = query.Where(x => x.ComponentId == componentId.Value);
        }

        if (status.HasValue)
        {
            query = query.Where(x => x.Status == status.Value);
        }

        if (!string.IsNullOrWhiteSpace(environment))
        {
            var normalizedEnvironment = environment.Trim().ToLowerInvariant();
            query = query.Where(x => x.Component.Environment == normalizedEnvironment);
        }

        if (!string.IsNullOrWhiteSpace(model))
        {
            var normalizedModel = model.Trim();
            query = query.Where(x => x.Model == normalizedModel);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(x =>
                x.Name.Contains(term) ||
                x.Component.Name.Contains(term) ||
                (x.Model != null && x.Model.Contains(term)) ||
                (x.ExternalId != null && x.ExternalId.Contains(term)));
        }

        if (from.HasValue)
        {
            query = query.Where(x => x.StartedAt >= from.Value);
        }

        if (to.HasValue)
        {
            query = query.Where(x => x.StartedAt < to.Value);
        }

        var rows = await query
            .OrderByDescending(x => x.Sequence)
            .Take(limit + 1)
            .Select(x => new RunQueryRow(
                x.Id,
                x.Sequence,
                x.ComponentId,
                x.Component.Name,
                x.Component.Environment,
                x.Name,
                x.Status,
                x.Model,
                x.StartedAt,
                x.CompletedAt,
                x.InputTokens + x.OutputTokens,
                x.CostUsd))
            .ToListAsync(cancellationToken);

        var hasMore = rows.Count > limit;
        if (hasMore)
        {
            rows.RemoveAt(limit);
        }

        long? nextCursor = hasMore && rows.Count > 0 ? rows[^1].Sequence : null;
        return Results.Ok(new { items = rows, nextCursor, pageSize = limit });
    }

    private static async Task<IResult> GetRunOptions(
        MonitorDbContext db,
        CancellationToken cancellationToken)
    {
        var components = await db.Components
            .AsNoTracking()
            .OrderBy(x => x.Name)
            .ThenBy(x => x.Environment)
            .Select(x => new { x.Id, x.Name, x.Environment })
            .ToListAsync(cancellationToken);

        var environments = components
            .Select(x => x.Environment)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x)
            .ToArray();

        var models = await db.Runs
            .AsNoTracking()
            .Where(x => x.Model != null && x.Model != "")
            .Select(x => x.Model!)
            .Distinct()
            .OrderBy(x => x)
            .Take(100)
            .ToListAsync(cancellationToken);

        return Results.Ok(new { components, environments, models });
    }

    private static async Task<IResult> GetRun(
        Guid id,
        MonitorDbContext db,
        CancellationToken cancellationToken)
    {
        var run = await db.Runs
            .AsNoTracking()
            .Include(x => x.Component)
            .Include(x => x.Spans)
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (run is null)
        {
            return Results.NotFound();
        }

        return Results.Ok(new
        {
            run.Id,
            run.Sequence,
            run.ComponentId,
            Component = run.Component.Name,
            run.ExternalId,
            run.Name,
            run.Trigger,
            run.Model,
            run.Status,
            run.StartedAt,
            run.CompletedAt,
            run.InputTokens,
            run.OutputTokens,
            run.CostUsd,
            run.InputJson,
            run.OutputJson,
            run.Error,
            Spans = run.Spans.OrderBy(x => x.StartedAt).Select(x => new
            {
                x.Id,
                x.ParentSpanId,
                x.Name,
                x.Kind,
                x.Status,
                x.StartedAt,
                x.CompletedAt,
                x.AttributesJson,
                x.Error
            })
        });
    }

    private static async Task<IResult> StartRun(
        StartRunRequest request,
        MonitorDbContext db,
        IHubContext<MonitorHub> hub,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return Results.BadRequest(new { error = "name is required" });
        }

        var component = await db.Components.FindAsync([request.ComponentId], cancellationToken);
        if (component is null)
        {
            return Results.BadRequest(new { error = "unknown component" });
        }

        var now = DateTimeOffset.UtcNow;
        var run = AgentRun.Start(
            component.Id,
            request.Name.Trim(),
            request.ExternalId,
            request.Trigger,
            request.Model,
            request.InputJson,
            now);

        component.MarkRunStarted(now);
        db.Runs.Add(run);
        await db.SaveChangesAsync(cancellationToken);
        await BroadcastRunChangedAsync(hub, run, component, "Started", cancellationToken);

        return Results.Created($"/api/runs/{run.Id}", new { run.Id, run.StartedAt });
    }

    private static async Task<IResult> CompleteRun(
        Guid id,
        CompleteRunRequest request,
        MonitorDbContext db,
        FailureGroupingService failureGrouping,
        IHubContext<MonitorHub> hub,
        CancellationToken cancellationToken)
    {
        if (request.Status == RunStatus.Running)
        {
            return Results.BadRequest(new { error = "status must be Success, Failed or Cancelled" });
        }

        var run = await db.Runs
            .Include(x => x.Component)
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (run is null)
        {
            return Results.NotFound();
        }

        run.Complete(
            request.Status,
            request.InputTokens,
            request.OutputTokens,
            request.CostUsd,
            request.OutputJson,
            request.Error,
            DateTimeOffset.UtcNow);

        await db.SaveChangesAsync(cancellationToken);
        if (request.Status is RunStatus.Failed or RunStatus.Cancelled)
        {
            await failureGrouping.GroupPendingAsync(cancellationToken);
        }

        await BroadcastRunChangedAsync(hub, run, run.Component, "Completed", cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> CreateSpan(
        Guid runId,
        CreateSpanRequest request,
        MonitorDbContext db,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return Results.BadRequest(new { error = "name is required" });
        }

        if (!await db.Runs.AnyAsync(x => x.Id == runId, cancellationToken))
        {
            return Results.NotFound();
        }

        if (request.ParentSpanId is not null &&
            !await db.Spans.AnyAsync(x => x.Id == request.ParentSpanId && x.RunId == runId, cancellationToken))
        {
            return Results.BadRequest(new { error = "parent span does not belong to this run" });
        }

        var span = TraceSpan.Create(
            runId,
            request.ParentSpanId,
            request.Name.Trim(),
            request.Kind,
            request.Status,
            request.StartedAt ?? DateTimeOffset.UtcNow,
            request.CompletedAt,
            request.AttributesJson,
            request.Error);

        db.Spans.Add(span);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/runs/{runId}/spans/{span.Id}", new { span.Id });
    }

    private static Task BroadcastRunChangedAsync(
        IHubContext<MonitorHub> hub,
        AgentRun run,
        MonitoredComponent component,
        string change,
        CancellationToken cancellationToken)
    {
        return hub.Clients.All.SendAsync(
            "RunChanged",
            new RunRealtimeEvent(
                run.Id,
                run.Sequence,
                run.ComponentId,
                component.Name,
                component.Environment,
                run.Name,
                run.Model,
                run.Status.ToString(),
                run.StartedAt,
                change),
            cancellationToken);
    }
}

public sealed record RegisterComponentRequest(
    string Name,
    string Slug,
    ComponentType Type,
    string Environment,
    string? Version);

public sealed record StartRunRequest(
    Guid ComponentId,
    string Name,
    string? ExternalId,
    string? Trigger,
    string? Model,
    string? InputJson);

public sealed record CompleteRunRequest(
    RunStatus Status,
    long InputTokens,
    long OutputTokens,
    double CostUsd,
    string? OutputJson,
    string? Error);

public sealed record CreateSpanRequest(
    Guid? ParentSpanId,
    string Name,
    SpanKind Kind,
    SpanStatus Status,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? AttributesJson,
    string? Error);

public sealed record RunQueryRow(
    Guid Id,
    long Sequence,
    Guid ComponentId,
    string Component,
    string Environment,
    string Name,
    RunStatus Status,
    string? Model,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    long Tokens,
    double CostUsd);

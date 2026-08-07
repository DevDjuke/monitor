using Microsoft.EntityFrameworkCore;
using Monitor.Domain;
using Monitor.Infrastructure;

namespace Monitor.Web.Api;

public static class MonitoringEndpoints
{
    public static IEndpointRouteBuilder MapMonitoringApi(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api");

        api.MapGet("/components", GetComponents);
        api.MapPost("/components/register", RegisterComponent);
        api.MapPost("/components/{id:guid}/heartbeat", Heartbeat);

        api.MapGet("/runs", GetRuns);
        api.MapPost("/runs", StartRun);
        api.MapPost("/runs/{id:guid}/complete", CompleteRun);
        api.MapPost("/runs/{runId:guid}/spans", CreateSpan);

        api.MapGet("/health", () => Results.Ok(new { status = "ok", now = DateTimeOffset.UtcNow }));

        return endpoints;
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
            .Include(x => x.Component)
            .OrderByDescending(x => x.StartedAt)
            .Take(limit)
            .Select(x => new
            {
                x.Id,
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

    private static async Task<IResult> StartRun(
        StartRunRequest request,
        MonitorDbContext db,
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

        return Results.Created($"/api/runs/{run.Id}", new { run.Id, run.StartedAt });
    }

    private static async Task<IResult> CompleteRun(
        Guid id,
        CompleteRunRequest request,
        MonitorDbContext db,
        CancellationToken cancellationToken)
    {
        if (request.Status == RunStatus.Running)
        {
            return Results.BadRequest(new { error = "status must be Success, Failed or Cancelled" });
        }

        var run = await db.Runs.FindAsync([id], cancellationToken);
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

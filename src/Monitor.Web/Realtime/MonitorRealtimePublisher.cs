using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Monitor.Domain;
using Monitor.Infrastructure;

namespace Monitor.Web.Realtime;

public sealed class MonitorRealtimePublisher(
    MonitorDbContext db,
    IHubContext<MonitorHub> hub)
{
    public Task PublishRunChangedAsync(
        AgentRun run,
        MonitoredComponent component,
        string change,
        CancellationToken cancellationToken = default)
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

    public Task PublishSpanChangedAsync(
        TraceSpan span,
        CancellationToken cancellationToken = default) =>
        PublishRunDetailChangedAsync(
            span.RunId,
            "Span",
            span.Id,
            cancellationToken);

    public Task PublishLogAppendedAsync(
        LogEvent logEvent,
        CancellationToken cancellationToken = default) =>
        logEvent.RunId is Guid runId
            ? PublishRunDetailChangedAsync(
                runId,
                "Log",
                logEvent.Id,
                cancellationToken)
            : Task.CompletedTask;

    public Task PublishRunDetailChangedAsync(
        Guid runId,
        string kind,
        Guid? entityId,
        CancellationToken cancellationToken = default)
    {
        if (runId == Guid.Empty)
        {
            return Task.CompletedTask;
        }

        return hub.Clients.Group(MonitorHub.RunGroup(runId)).SendAsync(
            "RunDetailChanged",
            new RunDetailRealtimeEvent(
                runId,
                kind,
                entityId,
                DateTimeOffset.UtcNow),
            cancellationToken);
    }

    public async Task PublishCommandChangedAsync(
        Guid commandId,
        CancellationToken cancellationToken = default)
    {
        var command = await db.ComponentCommands
            .AsNoTracking()
            .Where(x => x.Id == commandId)
            .Select(x => new CommandRealtimeEvent(
                x.Id,
                x.ComponentId,
                x.Component.Name,
                x.Component.Environment,
                x.Type.ToString(),
                x.Status.ToString(),
                x.TargetRunId,
                x.RequestedBy,
                x.CreatedAt,
                x.ExpiresAt,
                x.LeasedAt,
                x.LeaseExpiresAt,
                x.DeliveryAttempts,
                x.CompletedAt,
                x.ResultJson,
                x.Error))
            .SingleOrDefaultAsync(cancellationToken);

        if (command is null)
        {
            return;
        }

        await hub.Clients.Group(MonitorHub.CommandsGroup).SendAsync(
            "CommandChanged",
            command,
            cancellationToken);
    }
}

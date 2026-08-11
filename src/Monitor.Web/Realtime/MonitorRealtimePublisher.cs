using Microsoft.AspNetCore.SignalR;
using Monitor.Domain;

namespace Monitor.Web.Realtime;

public sealed class MonitorRealtimePublisher(
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
}

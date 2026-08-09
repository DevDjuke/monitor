using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Monitor.Web.Realtime;

[Authorize]
public sealed class MonitorHub : Hub
{
}

public sealed record RunRealtimeEvent(
    Guid RunId,
    long Sequence,
    Guid ComponentId,
    string Component,
    string Environment,
    string Name,
    string? Model,
    string Status,
    DateTimeOffset StartedAt,
    string Change);

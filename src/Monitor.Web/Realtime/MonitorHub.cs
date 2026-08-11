using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Monitor.Web.Realtime;

[Authorize]
public sealed class MonitorHub : Hub
{
    public const string CommandsGroup = "commands";

    public Task WatchRun(Guid runId)
    {
        if (runId == Guid.Empty)
        {
            throw new HubException("A run id is required.");
        }

        return Groups.AddToGroupAsync(Context.ConnectionId, RunGroup(runId));
    }

    public Task UnwatchRun(Guid runId)
    {
        if (runId == Guid.Empty)
        {
            return Task.CompletedTask;
        }

        return Groups.RemoveFromGroupAsync(Context.ConnectionId, RunGroup(runId));
    }

    public Task WatchCommands() =>
        Groups.AddToGroupAsync(Context.ConnectionId, CommandsGroup);

    public Task UnwatchCommands() =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, CommandsGroup);

    public static string RunGroup(Guid runId) => $"run:{runId:N}";
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

public sealed record RunDetailRealtimeEvent(
    Guid RunId,
    string Kind,
    Guid? EntityId,
    DateTimeOffset OccurredAt);

public sealed record CommandRealtimeEvent(
    Guid CommandId,
    Guid ComponentId,
    string Component,
    string Environment,
    string Type,
    string Status,
    Guid? TargetRunId,
    string? RequestedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? LeasedAt,
    DateTimeOffset? LeaseExpiresAt,
    int DeliveryAttempts,
    DateTimeOffset? CompletedAt,
    string? ResultJson,
    string? Error);

using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Monitor.Domain;

namespace Monitor.Web.Realtime;

public sealed class MonitorRealtimeSaveChangesInterceptor(
    IHubContext<MonitorHub> hub,
    ILogger<MonitorRealtimeSaveChangesInterceptor> logger) : SaveChangesInterceptor
{
    private readonly List<RunDetailRealtimeEvent> _runChanges = [];
    private readonly Dictionary<Guid, CommandRealtimeEvent> _commandChanges = [];

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Capture(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        Capture(eventData.Context);
        return result;
    }

    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        await PublishBestEffortAsync();
        return result;
    }

    public override int SavedChanges(
        SaveChangesCompletedEventData eventData,
        int result)
    {
        PublishBestEffortAsync().GetAwaiter().GetResult();
        return result;
    }

    public override Task SaveChangesFailedAsync(
        DbContextErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        Clear();
        return Task.CompletedTask;
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData) => Clear();

    private void Capture(DbContext? context)
    {
        Clear();
        if (context is null)
        {
            return;
        }

        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified))
            {
                continue;
            }

            switch (entry.Entity)
            {
                case AgentRun run when IsVisibleRunChange(entry):
                    AddRunChange(run.Id, "Run", null);
                    break;
                case TraceSpan span:
                    AddRunChange(span.RunId, "Span", span.Id);
                    break;
                case LogEvent logEvent when logEvent.RunId is Guid runId:
                    AddRunChange(runId, "Log", logEvent.Id);
                    break;
                case ComponentCommand command:
                    CaptureCommand(command);
                    break;
            }
        }
    }

    private static bool IsVisibleRunChange(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry)
    {
        if (entry.State == EntityState.Added)
        {
            return true;
        }

        return entry.Properties.Any(property =>
            property.IsModified &&
            !string.Equals(
                property.Metadata.Name,
                nameof(AgentRun.AggregatedAt),
                StringComparison.Ordinal));
    }

    private void CaptureCommand(ComponentCommand command)
    {
        var component = command.Component;
        _commandChanges[command.Id] = new CommandRealtimeEvent(
            command.Id,
            command.ComponentId,
            component?.Name ?? string.Empty,
            component?.Environment ?? string.Empty,
            command.Type.ToString(),
            command.Status.ToString(),
            command.TargetRunId,
            command.RequestedBy,
            command.CreatedAt,
            command.ExpiresAt,
            command.LeasedAt,
            command.LeaseExpiresAt,
            command.DeliveryAttempts,
            command.CompletedAt,
            command.ResultJson,
            command.Error);
    }

    private void AddRunChange(Guid runId, string kind, Guid? entityId)
    {
        if (runId == Guid.Empty)
        {
            return;
        }

        if (_runChanges.Any(x =>
                x.RunId == runId &&
                string.Equals(x.Kind, kind, StringComparison.Ordinal) &&
                x.EntityId == entityId))
        {
            return;
        }

        _runChanges.Add(new RunDetailRealtimeEvent(
            runId,
            kind,
            entityId,
            DateTimeOffset.UtcNow));
    }

    private async Task PublishBestEffortAsync()
    {
        var runChanges = _runChanges.ToArray();
        var commandChanges = _commandChanges.Values.ToArray();
        Clear();

        try
        {
            foreach (var change in runChanges)
            {
                await hub.Clients.Group(MonitorHub.RunGroup(change.RunId)).SendAsync(
                    "RunDetailChanged",
                    change,
                    CancellationToken.None);
            }

            foreach (var command in commandChanges)
            {
                await hub.Clients.Group(MonitorHub.CommandsGroup).SendAsync(
                    "CommandChanged",
                    command,
                    CancellationToken.None);
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Persisted Monitor state successfully, but realtime publication failed. Clients will reconcile from authoritative persisted state.");
        }
    }

    private void Clear()
    {
        _runChanges.Clear();
        _commandChanges.Clear();
    }
}

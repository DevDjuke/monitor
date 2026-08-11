using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Monitor.Domain;

namespace Monitor.Web.Realtime;

public sealed class MonitorRealtimeSaveChangesInterceptor(
    IHubContext<MonitorHub> hub) : SaveChangesInterceptor
{
    private readonly List<RunDetailRealtimeEvent> _runChanges = [];
    private readonly HashSet<Guid> _commandIds = [];

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
        await PublishAsync(eventData.Context, cancellationToken);
        return result;
    }

    public override int SavedChanges(
        SaveChangesCompletedEventData eventData,
        int result)
    {
        PublishAsync(eventData.Context, CancellationToken.None).GetAwaiter().GetResult();
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
                case AgentRun run:
                    AddRunChange(run.Id, "Run", null);
                    break;
                case TraceSpan span:
                    AddRunChange(span.RunId, "Span", span.Id);
                    break;
                case LogEvent logEvent when logEvent.RunId is Guid runId:
                    AddRunChange(runId, "Log", logEvent.Id);
                    break;
                case ComponentCommand command:
                    _commandIds.Add(command.Id);
                    break;
            }
        }
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

    private async Task PublishAsync(
        DbContext? context,
        CancellationToken cancellationToken)
    {
        if (context is null)
        {
            Clear();
            return;
        }

        var runChanges = _runChanges.ToArray();
        var commandIds = _commandIds.ToArray();
        Clear();

        foreach (var change in runChanges)
        {
            await hub.Clients.Group(MonitorHub.RunGroup(change.RunId)).SendAsync(
                "RunDetailChanged",
                change,
                cancellationToken);
        }

        if (commandIds.Length == 0)
        {
            return;
        }

        var commands = await context.Set<ComponentCommand>()
            .AsNoTracking()
            .Where(x => commandIds.Contains(x.Id))
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
            .ToListAsync(cancellationToken);

        foreach (var command in commands)
        {
            await hub.Clients.Group(MonitorHub.CommandsGroup).SendAsync(
                "CommandChanged",
                command,
                cancellationToken);
        }
    }

    private void Clear()
    {
        _runChanges.Clear();
        _commandIds.Clear();
    }
}

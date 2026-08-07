using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Monitor.Domain;
using Monitor.Infrastructure;

namespace Monitor.Web.Pages;

public sealed class IndexModel(MonitorDbContext db) : PageModel
{
    public int ComponentCount { get; private set; }
    public int HealthyCount { get; private set; }
    public int RunsToday { get; private set; }
    public double CostTodayUsd { get; private set; }
    public IReadOnlyList<ComponentRow> Components { get; private set; } = [];
    public IReadOnlyList<RunRow> RecentRuns { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var today = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);

        var components = await db.Components
            .AsNoTracking()
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);

        Components = components.Select(x => new ComponentRow(
            x.Id,
            x.Name,
            x.Type,
            x.Environment,
            x.GetEffectiveStatus(now, TimeSpan.FromMinutes(2)),
            x.LastHeartbeatAt)).ToList();

        ComponentCount = Components.Count;
        HealthyCount = Components.Count(x => x.Status == ComponentStatus.Healthy);
        RunsToday = await db.Runs.CountAsync(x => x.StartedAt >= today, cancellationToken);
        CostTodayUsd = await db.Runs.Where(x => x.StartedAt >= today).SumAsync(x => x.CostUsd, cancellationToken);

        var runs = await db.Runs
            .AsNoTracking()
            .Include(x => x.Component)
            .OrderByDescending(x => x.StartedAt)
            .Take(12)
            .ToListAsync(cancellationToken);

        RecentRuns = runs.Select(x => new RunRow(
            x.Id,
            x.Component.Name,
            x.Name,
            x.Status,
            x.StartedAt,
            x.CompletedAt,
            x.CostUsd)).ToList();
    }

    public sealed record ComponentRow(
        Guid Id,
        string Name,
        ComponentType Type,
        string Environment,
        ComponentStatus Status,
        DateTimeOffset? LastHeartbeatAt);

    public sealed record RunRow(
        Guid Id,
        string Component,
        string Name,
        RunStatus Status,
        DateTimeOffset StartedAt,
        DateTimeOffset? CompletedAt,
        double CostUsd)
    {
        public double? DurationMs => CompletedAt is null
            ? null
            : (CompletedAt.Value - StartedAt).TotalMilliseconds;
    }
}

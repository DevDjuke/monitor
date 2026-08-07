using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Monitor.Domain;
using Monitor.Infrastructure;

namespace Monitor.Web.Pages;

public sealed class ComponentsModel(MonitorDbContext db) : PageModel
{
    public IReadOnlyList<Row> Components { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var components = await db.Components.AsNoTracking().OrderBy(x => x.Name).ToListAsync(cancellationToken);
        Components = components.Select(x => new Row(
            x.Id,
            x.Name,
            x.Slug,
            x.Type,
            x.Environment,
            x.Version,
            x.GetEffectiveStatus(now, TimeSpan.FromMinutes(2)),
            x.LastHeartbeatAt,
            x.LastRunAt)).ToList();
    }

    public sealed record Row(
        Guid Id,
        string Name,
        string Slug,
        ComponentType Type,
        string Environment,
        string? Version,
        ComponentStatus Status,
        DateTimeOffset? LastHeartbeatAt,
        DateTimeOffset? LastRunAt);
}

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
        var components = await db.Components
            .AsNoTracking()
            .Include(x => x.IngestionCredentials)
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);

        Components = components.Select(x => new Row(
            x.Id,
            x.Name,
            x.Slug,
            x.Type,
            x.Environment,
            x.Version,
            x.ControlState,
            x.GetEffectiveStatus(now, TimeSpan.FromMinutes(2)),
            x.LastHeartbeatAt,
            x.LastRunAt,
            x.IngestionCredentials.LongCount(credential => !credential.IsRevoked))).ToList();
    }

    public sealed record Row(
        Guid Id,
        string Name,
        string Slug,
        ComponentType Type,
        string Environment,
        string? Version,
        ComponentControlState ControlState,
        ComponentStatus Status,
        DateTimeOffset? LastHeartbeatAt,
        DateTimeOffset? LastRunAt,
        long ActiveCredentials);
}

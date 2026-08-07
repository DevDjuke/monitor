using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Monitor.Domain;
using Monitor.Infrastructure;

namespace Monitor.Web.Pages;

public sealed class RunsModel(MonitorDbContext db) : PageModel
{
    public IReadOnlyList<Row> Runs { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var runs = await db.Runs
            .AsNoTracking()
            .Include(x => x.Component)
            .OrderByDescending(x => x.StartedAt)
            .Take(250)
            .ToListAsync(cancellationToken);

        Runs = runs.Select(x => new Row(
            x.Id,
            x.Component.Name,
            x.Name,
            x.Status,
            x.Model,
            x.StartedAt,
            x.CompletedAt,
            x.InputTokens + x.OutputTokens,
            x.CostUsd)).ToList();
    }

    public sealed record Row(
        Guid Id,
        string Component,
        string Name,
        RunStatus Status,
        string? Model,
        DateTimeOffset StartedAt,
        DateTimeOffset? CompletedAt,
        long Tokens,
        double CostUsd)
    {
        public TimeSpan? Duration => CompletedAt - StartedAt;
    }
}

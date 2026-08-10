using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Monitor.Domain;
using Monitor.Infrastructure;

namespace Monitor.Web.Pages;

public sealed class RunModel(MonitorDbContext db) : PageModel
{
    public AgentRun Run { get; private set; } = null!;
    public IReadOnlyList<TraceSpan> Spans { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        var run = await db.Runs
            .AsNoTracking()
            .Include(x => x.Component)
            .Include(x => x.FailureGroup)
            .Include(x => x.Spans)
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (run is null)
        {
            return NotFound();
        }

        Run = run;
        Spans = run.Spans.OrderBy(x => x.StartedAt).ToList();
        return Page();
    }
}

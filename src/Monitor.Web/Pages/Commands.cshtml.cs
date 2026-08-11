using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Monitor.Domain;
using Monitor.Infrastructure;
using Monitor.Infrastructure.Auditing;
using Monitor.Infrastructure.Control;

namespace Monitor.Web.Pages;

public sealed class CommandsModel(MonitorDbContext db, AuditTrailWriter audit) : PageModel
{
    [BindProperty(SupportsGet = true)] public string Window { get; set; } = "7d";
    [BindProperty(SupportsGet = true)] public Guid? ComponentId { get; set; }
    [BindProperty(SupportsGet = true)] public ComponentCommandType? Type { get; set; }
    [BindProperty(SupportsGet = true)] public ComponentCommandStatus? Status { get; set; }
    [BindProperty(SupportsGet = true)] public string? Search { get; set; }

    public IReadOnlyList<ComponentOption> Components { get; private set; } = [];
    public IReadOnlyList<CommandRow> Commands { get; private set; } = [];
    public IReadOnlyList<ComponentCommandType> CommandTypes { get; } = Enum.GetValues<ComponentCommandType>();
    public IReadOnlyList<ComponentCommandStatus> CommandStatuses { get; } = Enum.GetValues<ComponentCommandStatus>();
    public long PendingCount { get; private set; }
    public long LeasedCount { get; private set; }
    public long FailedCount { get; private set; }
    public long CompletedCount { get; private set; }
    public string ReturnUrl => $"{Request.Path}{Request.QueryString}";

    public async Task OnGetAsync(CancellationToken cancellationToken) => await LoadAsync(cancellationToken);

    public async Task<IActionResult> OnPostCancelAsync(
        Guid commandId,
        string? returnUrl,
        CancellationToken cancellationToken)
    {
        var command = await db.ComponentCommands
            .Include(x => x.Component)
            .SingleOrDefaultAsync(x => x.Id == commandId, cancellationToken);
        if (command is null)
        {
            return NotFound();
        }

        if (!command.IsTerminal)
        {
            var before = ComponentCommandService.Snapshot(command);
            var now = DateTimeOffset.UtcNow;
            command.Cancel(User.Identity?.Name, now);
            audit.RecordOperator(
                User,
                AuditActions.ComponentCommandCancelled,
                AuditTargetTypes.ComponentCommand,
                command.Id.ToString("D"),
                command.Type.ToString(),
                before,
                ComponentCommandService.Snapshot(command),
                new { command.ComponentId, command.TargetRunId },
                now);
            await db.SaveChangesAsync(cancellationToken);
        }

        TempData["StatusMessage"] = "Component command cancelled.";
        return !string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl)
            ? LocalRedirect(returnUrl)
            : RedirectToPage();
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        NormalizeFilters();
        Components = await db.Components
            .AsNoTracking()
            .OrderBy(x => x.Name)
            .ThenBy(x => x.Environment)
            .Select(x => new ComponentOption(x.Id, x.Name, x.Environment))
            .ToListAsync(cancellationToken);

        var since = ResolveSince(DateTimeOffset.UtcNow, Window);
        var query = db.ComponentCommands.AsNoTracking().AsQueryable();
        if (since is not null) query = query.Where(x => x.CreatedAt >= since.Value);
        if (ComponentId is not null) query = query.Where(x => x.ComponentId == ComponentId.Value);
        if (Type is not null) query = query.Where(x => x.Type == Type.Value);
        if (Status is not null) query = query.Where(x => x.Status == Status.Value);
        if (Search is not null)
        {
            var term = Search;
            query = query.Where(x =>
                x.Component.Name.Contains(term) ||
                (x.RequestedBy != null && x.RequestedBy.Contains(term)) ||
                (x.Error != null && x.Error.Contains(term)) ||
                (x.ResultJson != null && x.ResultJson.Contains(term)));
        }

        Commands = await query
            .OrderByDescending(x => x.CreatedAt)
            .Take(250)
            .Select(x => new CommandRow(
                x.Id,
                x.ComponentId,
                x.Component.Name,
                x.Component.Environment,
                x.Type,
                x.Status,
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

        PendingCount = await db.ComponentCommands.LongCountAsync(x => x.Status == ComponentCommandStatus.Pending, cancellationToken);
        LeasedCount = await db.ComponentCommands.LongCountAsync(x => x.Status == ComponentCommandStatus.Leased, cancellationToken);
        FailedCount = await db.ComponentCommands.LongCountAsync(
            x => x.Status == ComponentCommandStatus.Failed || x.Status == ComponentCommandStatus.Rejected || x.Status == ComponentCommandStatus.Expired,
            cancellationToken);
        CompletedCount = await db.ComponentCommands.LongCountAsync(x => x.Status == ComponentCommandStatus.Succeeded, cancellationToken);
    }

    private void NormalizeFilters()
    {
        Window = Window?.Trim().ToLowerInvariant() switch
        {
            "24h" => "24h",
            "7d" => "7d",
            "30d" => "30d",
            "all" => "all",
            _ => "7d"
        };
        Search = string.IsNullOrWhiteSpace(Search) ? null : Search.Trim();
    }

    private static DateTimeOffset? ResolveSince(DateTimeOffset now, string window) => window switch
    {
        "24h" => now.AddHours(-24),
        "7d" => now.AddDays(-7),
        "30d" => now.AddDays(-30),
        _ => null
    };

    public sealed record ComponentOption(Guid Id, string Name, string Environment);

    public sealed record CommandRow(
        Guid Id,
        Guid ComponentId,
        string ComponentName,
        string Environment,
        ComponentCommandType Type,
        ComponentCommandStatus Status,
        Guid? TargetRunId,
        string? RequestedBy,
        DateTimeOffset CreatedAt,
        DateTimeOffset ExpiresAt,
        DateTimeOffset? LeasedAt,
        DateTimeOffset? LeaseExpiresAt,
        int DeliveryAttempts,
        DateTimeOffset? CompletedAt,
        string? ResultJson,
        string? Error)
    {
        public bool CanCancel => Status is ComponentCommandStatus.Pending or ComponentCommandStatus.Leased;
    }
}

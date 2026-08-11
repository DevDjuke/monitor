using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Monitor.Domain;
using Monitor.Infrastructure;
using Monitor.Infrastructure.Auditing;

namespace Monitor.Web.Pages;

public sealed class AlertRulesModel(MonitorDbContext db, AuditTrailWriter audit) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    [BindProperty(SupportsGet = true)]
    public string State { get; set; } = "all";

    [BindProperty(SupportsGet = true)]
    public FailureCategory? Category { get; set; }

    public IReadOnlyList<FailureCategory> FailureCategories { get; } = Enum.GetValues<FailureCategory>();
    public IReadOnlyList<RuleRow> Rules { get; private set; } = [];
    public long MatchingRules { get; private set; }
    public long EnabledRules { get; private set; }
    public long DisabledRules { get; private set; }
    public long DeletedRules { get; private set; }
    public string ReturnUrl => $"{Request.Path}{Request.QueryString}";

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostToggleAsync(Guid id, string? returnUrl, CancellationToken cancellationToken)
    {
        var rule = await db.FailureAlertRules.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (rule is null)
        {
            return NotFound();
        }

        if (rule.IsDeleted)
        {
            TempData["StatusMessage"] = "Deleted alert rules cannot be re-enabled.";
            return RedirectBack(returnUrl);
        }

        var beforeEnabled = rule.Enabled;
        var now = DateTimeOffset.UtcNow;
        rule.SetEnabled(!rule.Enabled, now);
        audit.RecordOperator(
            User,
            rule.Enabled ? AuditActions.AlertRuleEnabled : AuditActions.AlertRuleDisabled,
            AuditTargetTypes.AlertRule,
            rule.Id.ToString("D"),
            rule.Name,
            new { enabled = beforeEnabled },
            new { rule.Enabled },
            occurredAt: now);

        await db.SaveChangesAsync(cancellationToken);
        TempData["StatusMessage"] = rule.Enabled ? "Alert rule enabled." : "Alert rule disabled.";
        return RedirectBack(returnUrl);
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id, string? returnUrl, CancellationToken cancellationToken)
    {
        var rule = await db.FailureAlertRules.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (rule is null)
        {
            return NotFound();
        }

        var now = DateTimeOffset.UtcNow;
        var before = new { rule.Enabled, rule.IsDeleted, rule.DeletedAt };
        rule.Delete(now);
        audit.RecordOperator(
            User,
            AuditActions.AlertRuleDeleted,
            AuditTargetTypes.AlertRule,
            rule.Id.ToString("D"),
            rule.Name,
            before,
            new { rule.Enabled, rule.IsDeleted, rule.DeletedAt },
            occurredAt: now);

        await db.SaveChangesAsync(cancellationToken);
        TempData["StatusMessage"] = "Alert rule deleted. Historical alert events remain intact for audit and forensic review.";
        return RedirectBack(returnUrl);
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        NormalizeFilters();

        var query = db.FailureAlertRules
            .AsNoTracking()
            .Include(x => x.FailureGroup)
            .Include(x => x.DestinationAssignments)
                .ThenInclude(x => x.Destination)
            .AsQueryable();

        query = State switch
        {
            "enabled" => query.Where(x => !x.IsDeleted && x.Enabled),
            "disabled" => query.Where(x => !x.IsDeleted && !x.Enabled),
            "deleted" => query.Where(x => x.IsDeleted),
            _ => query.Where(x => !x.IsDeleted)
        };

        if (Category is not null)
        {
            query = query.Where(x => x.FailureGroup.Category == Category.Value);
        }

        if (!string.IsNullOrWhiteSpace(Search))
        {
            var search = Search;
            query = query.Where(x =>
                x.Name.Contains(search) ||
                x.FailureGroup.Operation.Contains(search) ||
                x.FailureGroup.Fingerprint.Contains(search) ||
                (x.FailureGroup.FailureType != null && x.FailureGroup.FailureType.Contains(search)) ||
                (x.FailureGroup.Dependency != null && x.FailureGroup.Dependency.Contains(search)) ||
                x.DestinationAssignments.Any(a => a.Destination.Name.Contains(search)));
        }

        MatchingRules = await query.LongCountAsync(cancellationToken);
        EnabledRules = await db.FailureAlertRules.LongCountAsync(x => !x.IsDeleted && x.Enabled, cancellationToken);
        DisabledRules = await db.FailureAlertRules.LongCountAsync(x => !x.IsDeleted && !x.Enabled, cancellationToken);
        DeletedRules = await db.FailureAlertRules.LongCountAsync(x => x.IsDeleted, cancellationToken);

        var rules = await query
            .OrderByDescending(x => x.Enabled)
            .ThenByDescending(x => x.LastTriggeredAt)
            .ThenBy(x => x.Name)
            .Take(200)
            .ToListAsync(cancellationToken);

        var ruleIds = rules.Select(x => x.Id).ToList();
        var openAlertCounts = ruleIds.Count == 0
            ? new Dictionary<Guid, long>()
            : await db.FailureAlertEvents
                .AsNoTracking()
                .Where(x => ruleIds.Contains(x.AlertRuleId) && x.AcknowledgedAt == null)
                .GroupBy(x => x.AlertRuleId)
                .Select(group => new { RuleId = group.Key, Count = group.LongCount() })
                .ToDictionaryAsync(x => x.RuleId, x => x.Count, cancellationToken);

        Rules = rules.Select(rule => new RuleRow(
            rule.Id,
            rule.FailureGroupId,
            rule.Name,
            rule.FailureGroup.Category,
            rule.FailureGroup.Operation,
            rule.FailureGroup.FailureType,
            rule.FailureGroup.Dependency,
            rule.Threshold,
            rule.WindowMinutes,
            rule.CooldownMinutes,
            rule.Enabled,
            rule.IsDeleted,
            rule.DeletedAt,
            rule.LastEvaluatedAt,
            rule.LastTriggeredAt,
            openAlertCounts.GetValueOrDefault(rule.Id),
            BuildDeliveryScope(rule)))
            .ToList();
    }

    private static string BuildDeliveryScope(FailureAlertRule rule)
    {
        if (rule.DeliverToAllEnabledDestinations)
        {
            return "All enabled destinations";
        }

        var names = rule.DestinationAssignments
            .Select(x => x.Destination.Name)
            .OrderBy(x => x)
            .ToList();

        return names.Count switch
        {
            0 => "No destinations selected",
            1 => names[0],
            2 => string.Join(", ", names),
            _ => $"{names[0]}, {names[1]} +{names.Count - 2} more"
        };
    }

    private void NormalizeFilters()
    {
        Search = string.IsNullOrWhiteSpace(Search) ? null : Search.Trim();
        State = State?.Trim().ToLowerInvariant() switch
        {
            "enabled" => "enabled",
            "disabled" => "disabled",
            "deleted" => "deleted",
            _ => "all"
        };
    }

    private IActionResult RedirectBack(string? returnUrl) =>
        !string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl)
            ? LocalRedirect(returnUrl)
            : RedirectToPage();

    public sealed record RuleRow(
        Guid Id,
        Guid FailureGroupId,
        string Name,
        FailureCategory Category,
        string Operation,
        string? FailureType,
        string? Dependency,
        int Threshold,
        int WindowMinutes,
        int CooldownMinutes,
        bool Enabled,
        bool IsDeleted,
        DateTimeOffset? DeletedAt,
        DateTimeOffset? LastEvaluatedAt,
        DateTimeOffset? LastTriggeredAt,
        long OpenAlerts,
        string DeliveryScope);
}

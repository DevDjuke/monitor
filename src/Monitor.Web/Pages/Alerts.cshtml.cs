using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Monitor.Infrastructure;

namespace Monitor.Web.Pages;

public sealed class AlertsModel(MonitorDbContext db) : PageModel
{
    public long ActiveRules { get; private set; }
    public long OpenAlerts { get; private set; }
    public long TriggeredLast24Hours { get; private set; }
    public long AffectedGroups { get; private set; }

    public IReadOnlyList<AlertEventRow> RecentAlerts { get; private set; } = [];
    public IReadOnlyList<AlertRuleRow> Rules { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostAcknowledgeAsync(Guid eventId, CancellationToken cancellationToken)
    {
        var alertEvent = await db.FailureAlertEvents.SingleOrDefaultAsync(x => x.Id == eventId, cancellationToken);
        if (alertEvent is null)
        {
            return NotFound();
        }

        alertEvent.Acknowledge(User.Identity?.Name, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        TempData["StatusMessage"] = "Alert acknowledged.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostToggleRuleAsync(Guid ruleId, CancellationToken cancellationToken)
    {
        var rule = await db.FailureAlertRules.SingleOrDefaultAsync(x => x.Id == ruleId, cancellationToken);
        if (rule is null)
        {
            return NotFound();
        }

        rule.SetEnabled(!rule.Enabled, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        TempData["StatusMessage"] = rule.Enabled ? "Alert rule enabled." : "Alert rule disabled.";
        return RedirectToPage();
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        var since = DateTimeOffset.UtcNow.AddHours(-24);

        ActiveRules = await db.FailureAlertRules.LongCountAsync(x => x.Enabled, cancellationToken);
        OpenAlerts = await db.FailureAlertEvents.LongCountAsync(x => x.AcknowledgedAt == null, cancellationToken);
        TriggeredLast24Hours = await db.FailureAlertEvents.LongCountAsync(x => x.TriggeredAt >= since, cancellationToken);
        AffectedGroups = await db.FailureAlertEvents
            .Where(x => x.TriggeredAt >= since)
            .Select(x => x.FailureGroupId)
            .Distinct()
            .LongCountAsync(cancellationToken);

        RecentAlerts = await db.FailureAlertEvents
            .AsNoTracking()
            .OrderBy(x => x.AcknowledgedAt != null)
            .ThenByDescending(x => x.TriggeredAt)
            .Take(50)
            .Select(x => new AlertEventRow(
                x.Id,
                x.FailureGroupId,
                x.AlertRule.Name,
                x.FailureGroup.Category.ToString(),
                x.FailureGroup.Operation,
                x.FailureGroup.FailureType,
                x.FailureGroup.Dependency,
                x.TriggeredAt,
                x.OccurrencesInWindow,
                x.Threshold,
                x.WindowStart,
                x.WindowEnd,
                x.AcknowledgedAt,
                x.AcknowledgedBy))
            .ToListAsync(cancellationToken);

        Rules = await db.FailureAlertRules
            .AsNoTracking()
            .OrderByDescending(x => x.Enabled)
            .ThenByDescending(x => x.LastTriggeredAt)
            .ThenBy(x => x.Name)
            .Take(100)
            .Select(x => new AlertRuleRow(
                x.Id,
                x.FailureGroupId,
                x.Name,
                x.FailureGroup.Category.ToString(),
                x.FailureGroup.Operation,
                x.Threshold,
                x.WindowMinutes,
                x.CooldownMinutes,
                x.Enabled,
                x.LastEvaluatedAt,
                x.LastTriggeredAt,
                x.Events.LongCount(e => e.AcknowledgedAt == null)))
            .ToListAsync(cancellationToken);
    }

    public sealed record AlertEventRow(
        Guid Id,
        Guid FailureGroupId,
        string RuleName,
        string Category,
        string Operation,
        string? FailureType,
        string? Dependency,
        DateTimeOffset TriggeredAt,
        long OccurrencesInWindow,
        int Threshold,
        DateTimeOffset WindowStart,
        DateTimeOffset WindowEnd,
        DateTimeOffset? AcknowledgedAt,
        string? AcknowledgedBy);

    public sealed record AlertRuleRow(
        Guid Id,
        Guid FailureGroupId,
        string Name,
        string Category,
        string Operation,
        int Threshold,
        int WindowMinutes,
        int CooldownMinutes,
        bool Enabled,
        DateTimeOffset? LastEvaluatedAt,
        DateTimeOffset? LastTriggeredAt,
        long OpenAlerts);
}

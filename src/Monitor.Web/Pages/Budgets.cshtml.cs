using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Monitor.Domain;
using Monitor.Infrastructure;
using Monitor.Infrastructure.Auditing;

namespace Monitor.Web.Pages;

public sealed class BudgetsModel(MonitorDbContext db, AuditTrailWriter audit) : PageModel
{
    [BindProperty(SupportsGet = true)] public string? Search { get; set; }
    [BindProperty(SupportsGet = true)] public string State { get; set; } = "all";
    [BindProperty(SupportsGet = true)] public UsageBudgetPeriod? Period { get; set; }

    public IReadOnlyList<BudgetRow> Budgets { get; private set; } = [];
    public IReadOnlyList<BudgetAlertRow> RecentAlerts { get; private set; } = [];
    public IReadOnlyList<BudgetDeliveryRow> RecentDeliveries { get; private set; } = [];
    public long EnabledBudgets { get; private set; }
    public long WarningBudgets { get; private set; }
    public long CriticalBudgets { get; private set; }
    public long OpenAlerts { get; private set; }
    public string ReturnUrl => $"{Request.Path}{Request.QueryString}";

    public async Task OnGetAsync(CancellationToken cancellationToken) => await LoadAsync(cancellationToken);

    public async Task<IActionResult> OnPostToggleAsync(Guid id, string? returnUrl, CancellationToken cancellationToken)
    {
        var budget = await db.UsageBudgets.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (budget is null) return NotFound();
        if (budget.IsDeleted)
        {
            TempData["StatusMessage"] = "Deleted budgets cannot be re-enabled.";
            return RedirectBack(returnUrl);
        }

        var before = Snapshot(budget);
        var now = DateTimeOffset.UtcNow;
        budget.SetEnabled(!budget.Enabled, now);
        audit.RecordOperator(User, budget.Enabled ? "usage-budget.enabled" : "usage-budget.disabled", "UsageBudget", budget.Id.ToString(), budget.Name, before, Snapshot(budget), occurredAt: now);
        await db.SaveChangesAsync(cancellationToken);
        TempData["StatusMessage"] = budget.Enabled ? "Usage budget enabled." : "Usage budget disabled.";
        return RedirectBack(returnUrl);
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id, string? returnUrl, CancellationToken cancellationToken)
    {
        var budget = await db.UsageBudgets.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (budget is null) return NotFound();
        var before = Snapshot(budget);
        var now = DateTimeOffset.UtcNow;
        budget.Delete(now);
        audit.RecordOperator(User, "usage-budget.deleted", "UsageBudget", budget.Id.ToString(), budget.Name, before, Snapshot(budget), occurredAt: now);
        await db.SaveChangesAsync(cancellationToken);
        TempData["StatusMessage"] = "Usage budget deleted. Historical budget alerts remain available.";
        return RedirectBack(returnUrl);
    }

    public async Task<IActionResult> OnPostAcknowledgeAsync(Guid eventId, string? returnUrl, CancellationToken cancellationToken)
    {
        var alertEvent = await db.UsageBudgetAlertEvents
            .Include(x => x.UsageBudget)
            .SingleOrDefaultAsync(x => x.Id == eventId, cancellationToken);
        if (alertEvent is null) return NotFound();

        var before = new { alertEvent.AcknowledgedAt, alertEvent.AcknowledgedBy };
        var now = DateTimeOffset.UtcNow;
        alertEvent.Acknowledge(User.Identity?.Name, now);
        audit.RecordOperator(User, "usage-budget.alert-acknowledged", "UsageBudgetAlertEvent", alertEvent.Id.ToString(), alertEvent.UsageBudget.Name, before, new { alertEvent.AcknowledgedAt, alertEvent.AcknowledgedBy }, occurredAt: now);
        await db.SaveChangesAsync(cancellationToken);
        TempData["StatusMessage"] = "Budget alert acknowledged.";
        return RedirectBack(returnUrl);
    }

    public async Task<IActionResult> OnPostRequeueDeliveryAsync(Guid deliveryId, string? returnUrl, CancellationToken cancellationToken)
    {
        var delivery = await db.UsageBudgetAlertDeliveries
            .Include(x => x.BudgetAlertEvent).ThenInclude(x => x.UsageBudget)
            .SingleOrDefaultAsync(x => x.Id == deliveryId, cancellationToken);
        if (delivery is null) return NotFound();
        if (delivery.Status == AlertDeliveryStatus.Delivered)
        {
            TempData["StatusMessage"] = "Delivered notifications cannot be requeued.";
            return RedirectBack(returnUrl);
        }

        var before = new { delivery.Status, delivery.NextAttemptAt, delivery.AttemptCount, delivery.LastError };
        var now = DateTimeOffset.UtcNow;
        delivery.Requeue(now);
        audit.RecordOperator(User, "usage-budget.delivery-requeued", "UsageBudgetAlertDelivery", delivery.Id.ToString(), delivery.BudgetAlertEvent.UsageBudget.Name, before, new { delivery.Status, delivery.NextAttemptAt, delivery.AttemptCount, delivery.LastError }, occurredAt: now);
        await db.SaveChangesAsync(cancellationToken);
        TempData["StatusMessage"] = "Budget alert delivery requeued.";
        return RedirectBack(returnUrl);
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        Search = string.IsNullOrWhiteSpace(Search) ? null : Search.Trim();
        State = State?.Trim().ToLowerInvariant() switch
        {
            "enabled" => "enabled",
            "disabled" => "disabled",
            "deleted" => "deleted",
            "warning" => "warning",
            "critical" => "critical",
            _ => "all"
        };

        var query = db.UsageBudgets.AsNoTracking().Include(x => x.Component).AsQueryable();
        query = State switch
        {
            "enabled" => query.Where(x => !x.IsDeleted && x.Enabled),
            "disabled" => query.Where(x => !x.IsDeleted && !x.Enabled),
            "deleted" => query.Where(x => x.IsDeleted),
            "warning" => query.Where(x => !x.IsDeleted && x.LastTriggeredLevel == UsageBudgetAlertLevel.Warning),
            "critical" => query.Where(x => !x.IsDeleted && x.LastTriggeredLevel == UsageBudgetAlertLevel.Critical),
            _ => query.Where(x => !x.IsDeleted)
        };
        if (Period is not null) query = query.Where(x => x.Period == Period.Value);
        if (Search is not null)
        {
            var search = Search;
            query = query.Where(x => x.Name.Contains(search) ||
                (x.Environment != null && x.Environment.Contains(search)) ||
                (x.Model != null && x.Model.Contains(search)) ||
                (x.Component != null && x.Component.Name.Contains(search)));
        }

        var rows = await query
            .OrderByDescending(x => x.Enabled)
            .ThenByDescending(x => x.LastTriggeredLevel)
            .ThenBy(x => x.Name)
            .Take(200)
            .ToListAsync(cancellationToken);

        Budgets = rows.Select(x => new BudgetRow(
            x.Id,
            x.Name,
            x.Component?.Name,
            x.Environment,
            x.Model,
            x.Period,
            x.CostLimitUsd,
            x.TokenLimit,
            x.WarningPercent,
            x.CriticalPercent,
            x.Enabled,
            x.IsDeleted,
            x.LastObservedCostUsd,
            x.LastObservedTokens,
            x.GetUtilizationPercent(x.LastObservedCostUsd, x.LastObservedTokens),
            x.LastTriggeredLevel,
            x.LastEvaluatedAt)).ToList();

        EnabledBudgets = await db.UsageBudgets.LongCountAsync(x => !x.IsDeleted && x.Enabled, cancellationToken);
        WarningBudgets = await db.UsageBudgets.LongCountAsync(x => !x.IsDeleted && x.LastTriggeredLevel == UsageBudgetAlertLevel.Warning, cancellationToken);
        CriticalBudgets = await db.UsageBudgets.LongCountAsync(x => !x.IsDeleted && x.LastTriggeredLevel == UsageBudgetAlertLevel.Critical, cancellationToken);
        OpenAlerts = await db.UsageBudgetAlertEvents.LongCountAsync(x => x.AcknowledgedAt == null, cancellationToken);

        RecentAlerts = await db.UsageBudgetAlertEvents
            .AsNoTracking()
            .OrderBy(x => x.AcknowledgedAt != null)
            .ThenByDescending(x => x.TriggeredAt)
            .Take(50)
            .Select(x => new BudgetAlertRow(
                x.Id,
                x.UsageBudgetId,
                x.UsageBudget.Name,
                x.Level,
                x.TriggeredAt,
                x.PeriodStart,
                x.PeriodEnd,
                x.ObservedCostUsd,
                x.ObservedTokens,
                x.UtilizationPercent,
                x.AcknowledgedAt,
                x.AcknowledgedBy,
                x.Deliveries.LongCount(d => d.Status == AlertDeliveryStatus.Delivered),
                x.Deliveries.LongCount(d => d.Status == AlertDeliveryStatus.Pending || d.Status == AlertDeliveryStatus.RetryScheduled),
                x.Deliveries.LongCount(d => d.Status == AlertDeliveryStatus.DeadLetter)))
            .ToListAsync(cancellationToken);

        RecentDeliveries = await db.UsageBudgetAlertDeliveries
            .AsNoTracking()
            .OrderByDescending(x => x.CreatedAt)
            .Take(50)
            .Select(x => new BudgetDeliveryRow(
                x.Id,
                x.BudgetAlertEvent.UsageBudget.Name,
                x.BudgetAlertEvent.Level,
                x.Destination.Name,
                x.Status,
                x.AttemptCount,
                x.CreatedAt,
                x.NextAttemptAt,
                x.LastAttemptAt,
                x.DeliveredAt,
                x.ResponseStatusCode,
                x.LastError))
            .ToListAsync(cancellationToken);
    }

    private static object Snapshot(UsageBudget x) => new
    {
        x.Name,
        x.ComponentId,
        x.Environment,
        x.Model,
        x.Period,
        x.CostLimitUsd,
        x.TokenLimit,
        x.WarningPercent,
        x.CriticalPercent,
        x.Enabled,
        x.IsDeleted,
        x.DeliverToAllEnabledDestinations
    };

    private IActionResult RedirectBack(string? returnUrl) =>
        !string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl)
            ? LocalRedirect(returnUrl)
            : RedirectToPage();

    public sealed record BudgetRow(Guid Id, string Name, string? ComponentName, string? Environment, string? Model, UsageBudgetPeriod Period, double? CostLimitUsd, long? TokenLimit, int WarningPercent, int CriticalPercent, bool Enabled, bool IsDeleted, double ObservedCostUsd, long ObservedTokens, double UtilizationPercent, UsageBudgetAlertLevel? LastLevel, DateTimeOffset? LastEvaluatedAt);
    public sealed record BudgetAlertRow(Guid Id, Guid UsageBudgetId, string BudgetName, UsageBudgetAlertLevel Level, DateTimeOffset TriggeredAt, DateTimeOffset PeriodStart, DateTimeOffset PeriodEnd, double ObservedCostUsd, long ObservedTokens, double UtilizationPercent, DateTimeOffset? AcknowledgedAt, string? AcknowledgedBy, long Delivered, long Pending, long DeadLetter);
    public sealed record BudgetDeliveryRow(Guid Id, string BudgetName, UsageBudgetAlertLevel Level, string DestinationName, AlertDeliveryStatus Status, int AttemptCount, DateTimeOffset CreatedAt, DateTimeOffset NextAttemptAt, DateTimeOffset? LastAttemptAt, DateTimeOffset? DeliveredAt, int? ResponseStatusCode, string? LastError);
}

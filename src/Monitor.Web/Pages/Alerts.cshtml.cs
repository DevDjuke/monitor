using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Monitor.Domain;
using Monitor.Infrastructure;
using Monitor.Web.Services;

namespace Monitor.Web.Pages;

public sealed class AlertsModel(MonitorDbContext db, WebhookAlertSender webhookSender) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string Window { get; set; } = "24h";

    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    [BindProperty(SupportsGet = true)]
    public FailureCategory? Category { get; set; }

    [BindProperty(SupportsGet = true)]
    public string AlertState { get; set; } = "all";

    [BindProperty(SupportsGet = true)]
    public string RuleState { get; set; } = "all";

    [BindProperty(SupportsGet = true)]
    public AlertDeliveryStatus? DeliveryStatus { get; set; }

    [BindProperty(SupportsGet = true)]
    public Guid? DestinationId { get; set; }

    public long MatchingAlerts { get; private set; }
    public long OpenAlerts { get; private set; }
    public long MatchingRules { get; private set; }
    public long AffectedGroups { get; private set; }
    public long EnabledDestinations { get; private set; }
    public long MatchingDeliveries { get; private set; }
    public long PendingDeliveries { get; private set; }
    public long DeadLetterDeliveries { get; private set; }

    public IReadOnlyList<FailureCategory> FailureCategories { get; } = Enum.GetValues<FailureCategory>();
    public IReadOnlyList<AlertDeliveryStatus> DeliveryStatuses { get; } = Enum.GetValues<AlertDeliveryStatus>();
    public IReadOnlyList<AlertEventRow> RecentAlerts { get; private set; } = [];
    public IReadOnlyList<AlertRuleRow> Rules { get; private set; } = [];
    public IReadOnlyList<DestinationRow> Destinations { get; private set; } = [];
    public IReadOnlyList<DeliveryRow> RecentDeliveries { get; private set; } = [];
    public string ScopeLabel { get; private set; } = "Last 24 hours";
    public string ReturnUrl => $"{Request.Path}{Request.QueryString}";

    [BindProperty]
    public CreateDestinationInput DestinationInput { get; set; } = new();

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostAcknowledgeAsync(Guid eventId, string? returnUrl, CancellationToken cancellationToken)
    {
        var alertEvent = await db.FailureAlertEvents.SingleOrDefaultAsync(x => x.Id == eventId, cancellationToken);
        if (alertEvent is null)
        {
            return NotFound();
        }

        alertEvent.Acknowledge(User.Identity?.Name, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        TempData["StatusMessage"] = "Alert acknowledged.";
        return RedirectBack(returnUrl);
    }

    public async Task<IActionResult> OnPostToggleRuleAsync(Guid ruleId, string? returnUrl, CancellationToken cancellationToken)
    {
        var rule = await db.FailureAlertRules
            .SingleOrDefaultAsync(x => x.Id == ruleId && !x.IsDeleted, cancellationToken);
        if (rule is null)
        {
            return NotFound();
        }

        rule.SetEnabled(!rule.Enabled, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        TempData["StatusMessage"] = rule.Enabled ? "Alert rule enabled." : "Alert rule disabled.";
        return RedirectBack(returnUrl);
    }

    public async Task<IActionResult> OnPostCreateDestinationAsync(string? returnUrl, CancellationToken cancellationToken)
    {
        // GET filter properties are also bound on POST by Razor Pages. Validate only the
        // destination payload so an omitted query filter can never veto this operator action.
        ModelState.Clear();
        if (!TryValidateModel(DestinationInput, nameof(DestinationInput)))
        {
            await LoadAsync(cancellationToken);
            return Page();
        }

        try
        {
            var protectedSecret = webhookSender.ProtectSecret(DestinationInput.Secret);
            var destination = AlertDeliveryDestination.CreateWebhook(
                DestinationInput.Name,
                DestinationInput.EndpointUrl,
                protectedSecret,
                DateTimeOffset.UtcNow);

            db.AlertDeliveryDestinations.Add(destination);
            await db.SaveChangesAsync(cancellationToken);
            TempData["StatusMessage"] = "Webhook destination created. Future alert events will be queued for delivery.";
            return RedirectBack(returnUrl);
        }
        catch (ArgumentException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            await LoadAsync(cancellationToken);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostToggleDestinationAsync(Guid destinationId, string? returnUrl, CancellationToken cancellationToken)
    {
        var destination = await db.AlertDeliveryDestinations
            .SingleOrDefaultAsync(x => x.Id == destinationId, cancellationToken);
        if (destination is null)
        {
            return NotFound();
        }

        destination.SetEnabled(!destination.Enabled, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        TempData["StatusMessage"] = destination.Enabled
            ? "Webhook destination enabled."
            : "Webhook destination disabled. Existing queued deliveries are retained and will resume if it is enabled again.";
        return RedirectBack(returnUrl);
    }

    public async Task<IActionResult> OnPostTestDestinationAsync(Guid destinationId, string? returnUrl, CancellationToken cancellationToken)
    {
        var destination = await db.AlertDeliveryDestinations
            .SingleOrDefaultAsync(x => x.Id == destinationId, cancellationToken);
        if (destination is null)
        {
            return NotFound();
        }

        var now = DateTimeOffset.UtcNow;
        var result = await webhookSender.SendTestAsync(destination, cancellationToken);
        if (result.Succeeded)
        {
            destination.RecordSuccess(now);
            TempData["StatusMessage"] = $"Test webhook delivered successfully (HTTP {result.StatusCode}).";
        }
        else
        {
            destination.RecordFailure(result.Error ?? "Test webhook failed.", now);
            TempData["StatusMessage"] = $"Test webhook failed: {result.Error}";
        }

        await db.SaveChangesAsync(cancellationToken);
        return RedirectBack(returnUrl);
    }

    public async Task<IActionResult> OnPostRequeueDeliveryAsync(Guid deliveryId, string? returnUrl, CancellationToken cancellationToken)
    {
        var delivery = await db.AlertDeliveries.SingleOrDefaultAsync(x => x.Id == deliveryId, cancellationToken);
        if (delivery is null)
        {
            return NotFound();
        }

        if (delivery.Status == AlertDeliveryStatus.Delivered)
        {
            TempData["StatusMessage"] = "Delivered notifications cannot be requeued.";
            return RedirectBack(returnUrl);
        }

        delivery.Requeue(DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        TempData["StatusMessage"] = "Alert delivery requeued for immediate retry.";
        return RedirectBack(returnUrl);
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        NormalizeFilters();
        var since = ResolveWindowStart(DateTimeOffset.UtcNow, Window);
        ScopeLabel = BuildScopeLabel();

        var alertQuery = ApplyAlertFilters(db.FailureAlertEvents.AsNoTracking(), since);
        MatchingAlerts = await alertQuery.LongCountAsync(cancellationToken);
        OpenAlerts = await alertQuery.LongCountAsync(x => x.AcknowledgedAt == null, cancellationToken);
        AffectedGroups = await alertQuery
            .Select(x => x.FailureGroupId)
            .Distinct()
            .LongCountAsync(cancellationToken);

        RecentAlerts = await alertQuery
            .OrderBy(x => x.AcknowledgedAt != null)
            .ThenByDescending(x => x.TriggeredAt)
            .Take(100)
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
                x.AcknowledgedBy,
                x.Deliveries.LongCount(d => d.Status == AlertDeliveryStatus.Delivered),
                x.Deliveries.LongCount(d => d.Status == AlertDeliveryStatus.DeadLetter),
                x.Deliveries.LongCount(d => d.Status == AlertDeliveryStatus.Pending || d.Status == AlertDeliveryStatus.RetryScheduled)))
            .ToListAsync(cancellationToken);

        var ruleQuery = ApplyRuleFilters(db.FailureAlertRules.AsNoTracking());
        MatchingRules = await ruleQuery.LongCountAsync(cancellationToken);
        Rules = await ruleQuery
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

        Destinations = await db.AlertDeliveryDestinations
            .AsNoTracking()
            .OrderByDescending(x => x.Enabled)
            .ThenBy(x => x.Name)
            .Select(x => new DestinationRow(
                x.Id,
                x.Name,
                x.Kind,
                x.EndpointUrl,
                x.Enabled,
                x.LastSuccessAt,
                x.LastFailureAt,
                x.LastFailure,
                x.Deliveries.LongCount(d => d.Status == AlertDeliveryStatus.Delivered),
                x.Deliveries.LongCount(d => d.Status == AlertDeliveryStatus.DeadLetter)))
            .ToListAsync(cancellationToken);

        EnabledDestinations = Destinations.LongCount(x => x.Enabled);

        var deliveryQuery = ApplyDeliveryFilters(db.AlertDeliveries.AsNoTracking(), since);
        MatchingDeliveries = await deliveryQuery.LongCountAsync(cancellationToken);
        PendingDeliveries = await deliveryQuery.LongCountAsync(
            x => x.Status == AlertDeliveryStatus.Pending || x.Status == AlertDeliveryStatus.RetryScheduled,
            cancellationToken);
        DeadLetterDeliveries = await deliveryQuery.LongCountAsync(
            x => x.Status == AlertDeliveryStatus.DeadLetter,
            cancellationToken);

        RecentDeliveries = await deliveryQuery
            .OrderByDescending(x => x.CreatedAt)
            .Take(100)
            .Select(x => new DeliveryRow(
                x.Id,
                x.AlertEventId,
                x.AlertEvent.FailureGroupId,
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

    private IQueryable<FailureAlertEvent> ApplyAlertFilters(
        IQueryable<FailureAlertEvent> query,
        DateTimeOffset? since)
    {
        if (since is not null)
        {
            query = query.Where(x => x.TriggeredAt >= since.Value);
        }

        if (Category is not null)
        {
            query = query.Where(x => x.FailureGroup.Category == Category.Value);
        }

        query = AlertState switch
        {
            "open" => query.Where(x => x.AcknowledgedAt == null),
            "acknowledged" => query.Where(x => x.AcknowledgedAt != null),
            _ => query
        };

        if (!string.IsNullOrWhiteSpace(Search))
        {
            var search = Search;
            query = query.Where(x =>
                x.AlertRule.Name.Contains(search) ||
                x.FailureGroup.Operation.Contains(search) ||
                (x.FailureGroup.FailureType != null && x.FailureGroup.FailureType.Contains(search)) ||
                (x.FailureGroup.Dependency != null && x.FailureGroup.Dependency.Contains(search)) ||
                x.FailureGroup.Fingerprint.Contains(search));
        }

        return query;
    }

    private IQueryable<FailureAlertRule> ApplyRuleFilters(IQueryable<FailureAlertRule> query)
    {
        query = query.Where(x => !x.IsDeleted);

        if (Category is not null)
        {
            query = query.Where(x => x.FailureGroup.Category == Category.Value);
        }

        query = RuleState switch
        {
            "enabled" => query.Where(x => x.Enabled),
            "disabled" => query.Where(x => !x.Enabled),
            _ => query
        };

        if (!string.IsNullOrWhiteSpace(Search))
        {
            var search = Search;
            query = query.Where(x =>
                x.Name.Contains(search) ||
                x.FailureGroup.Operation.Contains(search) ||
                (x.FailureGroup.FailureType != null && x.FailureGroup.FailureType.Contains(search)) ||
                (x.FailureGroup.Dependency != null && x.FailureGroup.Dependency.Contains(search)) ||
                x.FailureGroup.Fingerprint.Contains(search));
        }

        return query;
    }

    private IQueryable<AlertDelivery> ApplyDeliveryFilters(
        IQueryable<AlertDelivery> query,
        DateTimeOffset? since)
    {
        if (since is not null)
        {
            query = query.Where(x => x.CreatedAt >= since.Value);
        }

        if (Category is not null)
        {
            query = query.Where(x => x.AlertEvent.FailureGroup.Category == Category.Value);
        }

        if (DeliveryStatus is not null)
        {
            query = query.Where(x => x.Status == DeliveryStatus.Value);
        }

        if (DestinationId is not null)
        {
            query = query.Where(x => x.DestinationId == DestinationId.Value);
        }

        if (!string.IsNullOrWhiteSpace(Search))
        {
            var search = Search;
            query = query.Where(x =>
                x.Destination.Name.Contains(search) ||
                x.AlertEvent.AlertRule.Name.Contains(search) ||
                x.AlertEvent.FailureGroup.Operation.Contains(search) ||
                (x.AlertEvent.FailureGroup.FailureType != null && x.AlertEvent.FailureGroup.FailureType.Contains(search)) ||
                (x.AlertEvent.FailureGroup.Dependency != null && x.AlertEvent.FailureGroup.Dependency.Contains(search)) ||
                (x.LastError != null && x.LastError.Contains(search)));
        }

        return query;
    }

    private void NormalizeFilters()
    {
        Window = Window?.Trim().ToLowerInvariant() switch
        {
            "24h" => "24h",
            "7d" => "7d",
            "30d" => "30d",
            "all" => "all",
            _ => "24h"
        };

        AlertState = AlertState?.Trim().ToLowerInvariant() switch
        {
            "open" => "open",
            "acknowledged" => "acknowledged",
            _ => "all"
        };

        RuleState = RuleState?.Trim().ToLowerInvariant() switch
        {
            "enabled" => "enabled",
            "disabled" => "disabled",
            _ => "all"
        };

        Search = string.IsNullOrWhiteSpace(Search) ? null : Search.Trim();
    }

    private string BuildScopeLabel()
    {
        var parts = new List<string>
        {
            Window switch
            {
                "24h" => "Last 24 hours",
                "7d" => "Last 7 days",
                "30d" => "Last 30 days",
                _ => "All retained history"
            }
        };

        if (Category is not null)
        {
            parts.Add(Category.Value.ToString());
        }

        if (!string.IsNullOrWhiteSpace(Search))
        {
            parts.Add($"search: {Search}");
        }

        return string.Join(" · ", parts);
    }

    private static DateTimeOffset? ResolveWindowStart(DateTimeOffset now, string window) => window switch
    {
        "24h" => now.AddHours(-24),
        "7d" => now.AddDays(-7),
        "30d" => now.AddDays(-30),
        _ => null
    };

    private IActionResult RedirectBack(string? returnUrl) =>
        !string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl)
            ? LocalRedirect(returnUrl)
            : RedirectToPage();

    public sealed class CreateDestinationInput
    {
        [Required, StringLength(200)]
        public string Name { get; set; } = string.Empty;

        [Required, StringLength(2000), Url]
        public string EndpointUrl { get; set; } = string.Empty;

        [Required, StringLength(512, MinimumLength = 16)]
        public string Secret { get; set; } = string.Empty;
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
        string? AcknowledgedBy,
        long DeliveredNotifications,
        long DeadLetterNotifications,
        long PendingNotifications);

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

    public sealed record DestinationRow(
        Guid Id,
        string Name,
        AlertDeliveryKind Kind,
        string EndpointUrl,
        bool Enabled,
        DateTimeOffset? LastSuccessAt,
        DateTimeOffset? LastFailureAt,
        string? LastFailure,
        long Delivered,
        long DeadLetters);

    public sealed record DeliveryRow(
        Guid Id,
        Guid AlertEventId,
        Guid FailureGroupId,
        string DestinationName,
        AlertDeliveryStatus Status,
        int AttemptCount,
        DateTimeOffset CreatedAt,
        DateTimeOffset NextAttemptAt,
        DateTimeOffset? LastAttemptAt,
        DateTimeOffset? DeliveredAt,
        int? ResponseStatusCode,
        string? LastError);
}

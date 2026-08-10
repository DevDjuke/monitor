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
    public long ActiveRules { get; private set; }
    public long OpenAlerts { get; private set; }
    public long TriggeredLast24Hours { get; private set; }
    public long AffectedGroups { get; private set; }
    public long EnabledDestinations { get; private set; }
    public long PendingDeliveries { get; private set; }
    public long DeadLetterDeliveries { get; private set; }

    public IReadOnlyList<AlertEventRow> RecentAlerts { get; private set; } = [];
    public IReadOnlyList<AlertRuleRow> Rules { get; private set; } = [];
    public IReadOnlyList<DestinationRow> Destinations { get; private set; } = [];
    public IReadOnlyList<DeliveryRow> RecentDeliveries { get; private set; } = [];

    [BindProperty]
    public CreateDestinationInput DestinationInput { get; set; } = new();

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

    public async Task<IActionResult> OnPostCreateDestinationAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
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
            return RedirectToPage();
        }
        catch (ArgumentException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            await LoadAsync(cancellationToken);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostToggleDestinationAsync(Guid destinationId, CancellationToken cancellationToken)
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
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostTestDestinationAsync(Guid destinationId, CancellationToken cancellationToken)
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
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRequeueDeliveryAsync(Guid deliveryId, CancellationToken cancellationToken)
    {
        var delivery = await db.AlertDeliveries.SingleOrDefaultAsync(x => x.Id == deliveryId, cancellationToken);
        if (delivery is null)
        {
            return NotFound();
        }

        if (delivery.Status == AlertDeliveryStatus.Delivered)
        {
            TempData["StatusMessage"] = "Delivered notifications cannot be requeued.";
            return RedirectToPage();
        }

        delivery.Requeue(DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        TempData["StatusMessage"] = "Alert delivery requeued for immediate retry.";
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
        EnabledDestinations = await db.AlertDeliveryDestinations.LongCountAsync(x => x.Enabled, cancellationToken);
        PendingDeliveries = await db.AlertDeliveries.LongCountAsync(
            x => x.Status == AlertDeliveryStatus.Pending || x.Status == AlertDeliveryStatus.RetryScheduled,
            cancellationToken);
        DeadLetterDeliveries = await db.AlertDeliveries.LongCountAsync(
            x => x.Status == AlertDeliveryStatus.DeadLetter,
            cancellationToken);

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
                x.AcknowledgedBy,
                x.Deliveries.LongCount(d => d.Status == AlertDeliveryStatus.Delivered),
                x.Deliveries.LongCount(d => d.Status == AlertDeliveryStatus.DeadLetter),
                x.Deliveries.LongCount(d => d.Status == AlertDeliveryStatus.Pending || d.Status == AlertDeliveryStatus.RetryScheduled)))
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

        RecentDeliveries = await db.AlertDeliveries
            .AsNoTracking()
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

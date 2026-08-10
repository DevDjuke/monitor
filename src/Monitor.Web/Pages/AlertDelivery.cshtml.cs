using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Monitor.Domain;
using Monitor.Infrastructure;
using Monitor.Web.Services;

namespace Monitor.Web.Pages;

public sealed class AlertDeliveryModel(
    MonitorDbContext db,
    WebhookSecretProtector secretProtector) : PageModel
{
    public long EnabledDestinations { get; private set; }
    public long ActiveRoutes { get; private set; }
    public long QueuedDeliveries { get; private set; }
    public long DeadLetters { get; private set; }

    public IReadOnlyList<DestinationRow> Destinations { get; private set; } = [];
    public IReadOnlyList<RouteRow> Routes { get; private set; } = [];
    public IReadOnlyList<DeliveryRow> RecentDeliveries { get; private set; } = [];
    public IReadOnlyList<RuleOption> RuleOptions { get; private set; } = [];
    public IReadOnlyList<DestinationOption> DestinationOptions { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostCreateDestinationAsync(
        string? name,
        string? endpoint,
        string? signingSecret,
        CancellationToken cancellationToken)
    {
        var generatedSecret = string.IsNullOrWhiteSpace(signingSecret);
        signingSecret = generatedSecret
            ? WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32))
            : signingSecret;

        try
        {
            var protectedSecret = secretProtector.Protect(signingSecret!);
            var destination = AlertDestination.CreateWebhook(
                name ?? string.Empty,
                endpoint ?? string.Empty,
                protectedSecret,
                DateTimeOffset.UtcNow);

            db.AlertDestinations.Add(destination);
            await db.SaveChangesAsync(cancellationToken);

            TempData["StatusMessage"] = $"Webhook destination '{destination.Name}' created.";
            if (generatedSecret)
            {
                TempData["GeneratedWebhookSecret"] = signingSecret;
                TempData["GeneratedWebhookSecretDestination"] = destination.Name;
            }

            return RedirectToPage();
        }
        catch (ArgumentException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            await LoadAsync(cancellationToken);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostToggleDestinationAsync(
        Guid destinationId,
        CancellationToken cancellationToken)
    {
        var destination = await db.AlertDestinations
            .SingleOrDefaultAsync(x => x.Id == destinationId, cancellationToken);

        if (destination is null)
        {
            return NotFound();
        }

        destination.SetEnabled(!destination.Enabled, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        TempData["StatusMessage"] = destination.Enabled
            ? $"Destination '{destination.Name}' enabled."
            : $"Destination '{destination.Name}' disabled.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRotateSecretAsync(
        Guid destinationId,
        CancellationToken cancellationToken)
    {
        var destination = await db.AlertDestinations
            .SingleOrDefaultAsync(x => x.Id == destinationId, cancellationToken);

        if (destination is null)
        {
            return NotFound();
        }

        var signingSecret = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        destination.RotateSigningSecret(
            secretProtector.Protect(signingSecret),
            DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(cancellationToken);

        TempData["StatusMessage"] = $"Signing secret rotated for '{destination.Name}'.";
        TempData["GeneratedWebhookSecret"] = signingSecret;
        TempData["GeneratedWebhookSecretDestination"] = destination.Name;
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostAddRouteAsync(
        Guid ruleId,
        Guid destinationId,
        CancellationToken cancellationToken)
    {
        if (ruleId == Guid.Empty || destinationId == Guid.Empty)
        {
            ModelState.AddModelError(string.Empty, "Choose both an alert rule and a destination.");
            await LoadAsync(cancellationToken);
            return Page();
        }

        var rule = await db.FailureAlertRules
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == ruleId, cancellationToken);
        var destination = await db.AlertDestinations
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == destinationId, cancellationToken);

        if (rule is null || destination is null)
        {
            return NotFound();
        }

        var exists = await db.FailureAlertRoutes.AnyAsync(
            x => x.AlertRuleId == ruleId && x.DestinationId == destinationId,
            cancellationToken);

        if (exists)
        {
            TempData["StatusMessage"] = "That delivery route already exists.";
            return RedirectToPage();
        }

        db.FailureAlertRoutes.Add(FailureAlertRoute.Create(
            ruleId,
            destinationId,
            DateTimeOffset.UtcNow));
        await db.SaveChangesAsync(cancellationToken);
        TempData["StatusMessage"] = $"'{rule.Name}' will deliver to '{destination.Name}'.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRemoveRouteAsync(
        Guid routeId,
        CancellationToken cancellationToken)
    {
        var route = await db.FailureAlertRoutes
            .Include(x => x.AlertRule)
            .Include(x => x.Destination)
            .SingleOrDefaultAsync(x => x.Id == routeId, cancellationToken);

        if (route is null)
        {
            return NotFound();
        }

        var message = $"Delivery route '{route.AlertRule.Name}' → '{route.Destination.Name}' removed.";
        db.FailureAlertRoutes.Remove(route);
        await db.SaveChangesAsync(cancellationToken);
        TempData["StatusMessage"] = message;
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRetryDeliveryAsync(
        Guid deliveryId,
        CancellationToken cancellationToken)
    {
        var delivery = await db.AlertDeliveries
            .SingleOrDefaultAsync(x => x.Id == deliveryId, cancellationToken);

        if (delivery is null)
        {
            return NotFound();
        }

        if (delivery.Status is AlertDeliveryStatus.DeadLetter or AlertDeliveryStatus.RetryScheduled)
        {
            delivery.Requeue(DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(cancellationToken);
            TempData["StatusMessage"] = "Delivery requeued for immediate retry.";
        }

        return RedirectToPage();
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        EnabledDestinations = await db.AlertDestinations
            .LongCountAsync(x => x.Enabled, cancellationToken);
        ActiveRoutes = await db.FailureAlertRoutes
            .LongCountAsync(x => x.AlertRule.Enabled && x.Destination.Enabled, cancellationToken);
        QueuedDeliveries = await db.AlertDeliveries
            .LongCountAsync(
                x => x.Status == AlertDeliveryStatus.Pending || x.Status == AlertDeliveryStatus.RetryScheduled,
                cancellationToken);
        DeadLetters = await db.AlertDeliveries
            .LongCountAsync(x => x.Status == AlertDeliveryStatus.DeadLetter, cancellationToken);

        Destinations = await db.AlertDestinations
            .AsNoTracking()
            .OrderByDescending(x => x.Enabled)
            .ThenBy(x => x.Name)
            .Take(100)
            .Select(x => new DestinationRow(
                x.Id,
                x.Name,
                x.Kind,
                x.Endpoint,
                x.Enabled,
                x.AlertRoutes.LongCount(),
                x.Deliveries.LongCount(d => d.Status == AlertDeliveryStatus.Delivered),
                x.Deliveries.LongCount(d => d.Status == AlertDeliveryStatus.DeadLetter),
                x.Deliveries
                    .Where(d => d.DeliveredAt != null)
                    .OrderByDescending(d => d.DeliveredAt)
                    .Select(d => d.DeliveredAt)
                    .FirstOrDefault()))
            .ToListAsync(cancellationToken);

        Routes = await db.FailureAlertRoutes
            .AsNoTracking()
            .OrderBy(x => x.AlertRule.Name)
            .ThenBy(x => x.Destination.Name)
            .Take(200)
            .Select(x => new RouteRow(
                x.Id,
                x.AlertRuleId,
                x.AlertRule.Name,
                x.AlertRule.FailureGroupId,
                x.AlertRule.FailureGroup.Category.ToString(),
                x.AlertRule.FailureGroup.Operation,
                x.AlertRule.Enabled,
                x.DestinationId,
                x.Destination.Name,
                x.Destination.Enabled,
                x.CreatedAt))
            .ToListAsync(cancellationToken);

        RecentDeliveries = await db.AlertDeliveries
            .AsNoTracking()
            .OrderByDescending(x => x.CreatedAt)
            .Take(100)
            .Select(x => new DeliveryRow(
                x.Id,
                x.AlertEventId,
                x.AlertEvent.FailureGroupId,
                x.AlertEvent.AlertRule.Name,
                x.AlertEvent.FailureGroup.Category.ToString(),
                x.AlertEvent.FailureGroup.Operation,
                x.Destination.Name,
                x.Status,
                x.AttemptCount,
                x.CreatedAt,
                x.NextAttemptAt,
                x.LastAttemptAt,
                x.DeliveredAt,
                x.LastStatusCode,
                x.LastError))
            .ToListAsync(cancellationToken);

        RuleOptions = await db.FailureAlertRules
            .AsNoTracking()
            .OrderByDescending(x => x.Enabled)
            .ThenBy(x => x.Name)
            .Take(250)
            .Select(x => new RuleOption(
                x.Id,
                x.Name,
                x.FailureGroup.Category.ToString(),
                x.FailureGroup.Operation,
                x.Enabled))
            .ToListAsync(cancellationToken);

        DestinationOptions = Destinations
            .Where(x => x.Enabled)
            .Select(x => new DestinationOption(x.Id, x.Name))
            .ToList();
    }

    public sealed record DestinationRow(
        Guid Id,
        string Name,
        AlertDestinationKind Kind,
        string Endpoint,
        bool Enabled,
        long RouteCount,
        long DeliveredCount,
        long DeadLetterCount,
        DateTimeOffset? LastDeliveredAt);

    public sealed record RouteRow(
        Guid Id,
        Guid RuleId,
        string RuleName,
        Guid FailureGroupId,
        string Category,
        string Operation,
        bool RuleEnabled,
        Guid DestinationId,
        string DestinationName,
        bool DestinationEnabled,
        DateTimeOffset CreatedAt);

    public sealed record DeliveryRow(
        Guid Id,
        Guid AlertEventId,
        Guid FailureGroupId,
        string RuleName,
        string Category,
        string Operation,
        string DestinationName,
        AlertDeliveryStatus Status,
        int AttemptCount,
        DateTimeOffset CreatedAt,
        DateTimeOffset NextAttemptAt,
        DateTimeOffset? LastAttemptAt,
        DateTimeOffset? DeliveredAt,
        int? LastStatusCode,
        string? LastError);

    public sealed record RuleOption(
        Guid Id,
        string Name,
        string Category,
        string Operation,
        bool Enabled);

    public sealed record DestinationOption(Guid Id, string Name);
}

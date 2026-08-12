using System.ComponentModel.DataAnnotations;
using System.Net.Mail;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Monitor.Domain;
using Monitor.Infrastructure;
using Monitor.Infrastructure.Auditing;
using Monitor.Web.Services;

namespace Monitor.Web.Pages;

public sealed class AlertsModel(
    MonitorDbContext db,
    WebhookAlertSender webhookSender,
    AlertDestinationSecretProtector destinationSecretProtector,
    AlertDeliverySender deliverySender,
    AuditTrailWriter audit) : PageModel
{
    private const string PagerDutyEventsEndpoint = "https://events.pagerduty.com/v2/enqueue";

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
    public IReadOnlyList<AlertDeliveryKind> DestinationKinds { get; } = Enum.GetValues<AlertDeliveryKind>();
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

        if (alertEvent.AcknowledgedAt is null)
        {
            var now = DateTimeOffset.UtcNow;
            alertEvent.Acknowledge(User.Identity?.Name, now);
            audit.RecordOperator(
                User,
                AuditActions.AlertAcknowledged,
                AuditTargetTypes.Alert,
                alertEvent.Id.ToString("D"),
                before: new { acknowledgedAt = (DateTimeOffset?)null, acknowledgedBy = (string?)null },
                after: new { alertEvent.AcknowledgedAt, alertEvent.AcknowledgedBy },
                metadata: new { alertEvent.FailureGroupId, alertEvent.AlertRuleId },
                occurredAt: now);

            await db.SaveChangesAsync(cancellationToken);
        }

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
            new { rule.FailureGroupId },
            now);

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
            var now = DateTimeOffset.UtcNow;
            var destination = BuildDestination(now);

            db.AlertDeliveryDestinations.Add(destination);
            audit.RecordOperator(
                User,
                AuditActions.AlertDestinationCreated,
                AuditTargetTypes.AlertDestination,
                destination.Id.ToString("D"),
                destination.Name,
                after: SnapshotDestination(destination),
                occurredAt: now);

            await db.SaveChangesAsync(cancellationToken);
            TempData["StatusMessage"] = $"{DisplayKind(destination.Kind)} destination created. Future matching alerts will use the durable delivery outbox.";
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

        var before = SnapshotDestination(destination);
        var now = DateTimeOffset.UtcNow;
        destination.SetEnabled(!destination.Enabled, now);
        audit.RecordOperator(
            User,
            destination.Enabled ? AuditActions.AlertDestinationEnabled : AuditActions.AlertDestinationDisabled,
            AuditTargetTypes.AlertDestination,
            destination.Id.ToString("D"),
            destination.Name,
            before,
            SnapshotDestination(destination),
            occurredAt: now);

        await db.SaveChangesAsync(cancellationToken);
        TempData["StatusMessage"] = destination.Enabled
            ? $"{DisplayKind(destination.Kind)} destination enabled."
            : $"{DisplayKind(destination.Kind)} destination disabled. Existing queued deliveries are retained and will resume if it is enabled again.";
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

        var before = SnapshotDestination(destination);
        var now = DateTimeOffset.UtcNow;
        var result = await deliverySender.SendTestAsync(destination, cancellationToken);
        if (result.Succeeded)
        {
            destination.RecordSuccess(now);
            TempData["StatusMessage"] = result.StatusCode is null
                ? $"Test {DisplayKind(destination.Kind)} notification delivered successfully."
                : $"Test {DisplayKind(destination.Kind)} notification delivered successfully (status {result.StatusCode}).";
        }
        else
        {
            destination.RecordFailure(result.Error ?? "Test delivery failed.", now);
            TempData["StatusMessage"] = $"Test {DisplayKind(destination.Kind)} notification failed: {result.Error}";
        }

        audit.RecordOperator(
            User,
            AuditActions.AlertDestinationTested,
            AuditTargetTypes.AlertDestination,
            destination.Id.ToString("D"),
            destination.Name,
            before,
            SnapshotDestination(destination),
            new { result.Succeeded, result.StatusCode, destination.Kind },
            now);

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

        var before = new
        {
            delivery.Status,
            delivery.AttemptCount,
            delivery.NextAttemptAt,
            delivery.LastAttemptAt,
            delivery.DeliveredAt,
            delivery.ResponseStatusCode,
            delivery.LastError
        };
        var now = DateTimeOffset.UtcNow;
        delivery.Requeue(now);
        audit.RecordOperator(
            User,
            AuditActions.AlertDeliveryRequeued,
            AuditTargetTypes.AlertDelivery,
            delivery.Id.ToString("D"),
            before: before,
            after: new
            {
                delivery.Status,
                delivery.AttemptCount,
                delivery.NextAttemptAt,
                delivery.LastAttemptAt,
                delivery.DeliveredAt,
                delivery.ResponseStatusCode,
                delivery.LastError
            },
            metadata: new { delivery.AlertEventId, delivery.DestinationId },
            occurredAt: now);

        await db.SaveChangesAsync(cancellationToken);
        TempData["StatusMessage"] = "Alert delivery requeued for immediate retry.";
        return RedirectBack(returnUrl);
    }

    private AlertDeliveryDestination BuildDestination(DateTimeOffset now)
    {
        var name = DestinationInput.Name.Trim();

        return DestinationInput.Kind switch
        {
            AlertDeliveryKind.Webhook => BuildSignedWebhook(name, now),
            AlertDeliveryKind.Slack => BuildProtectedWebhook(name, AlertDeliveryKind.Slack, "Slack", now),
            AlertDeliveryKind.MicrosoftTeams => BuildProtectedWebhook(name, AlertDeliveryKind.MicrosoftTeams, "Microsoft Teams", now),
            AlertDeliveryKind.Discord => BuildProtectedWebhook(name, AlertDeliveryKind.Discord, "Discord", now),
            AlertDeliveryKind.PagerDuty => BuildPagerDuty(name, now),
            AlertDeliveryKind.Email => BuildEmail(name, now),
            _ => throw new ArgumentOutOfRangeException(nameof(DestinationInput.Kind), "Unsupported delivery destination kind.")
        };
    }

    private AlertDeliveryDestination BuildSignedWebhook(string name, DateTimeOffset now)
    {
        var endpoint = RequireHttpEndpoint(DestinationInput.EndpointUrl, "Webhook URL", requireHttps: false);
        var secret = RequireValue(DestinationInput.Secret, "Webhook signing secret");
        if (secret.Length < 16)
        {
            throw new ArgumentException("Webhook signing secret must be at least 16 characters.");
        }

        return AlertDeliveryDestination.CreateWebhook(
            name,
            endpoint,
            webhookSender.ProtectSecret(secret),
            now);
    }

    private AlertDeliveryDestination BuildProtectedWebhook(
        string name,
        AlertDeliveryKind kind,
        string providerName,
        DateTimeOffset now)
    {
        var endpoint = RequireHttpEndpoint(DestinationInput.EndpointUrl, $"{providerName} webhook URL", requireHttps: true);
        var protectedEndpoint = destinationSecretProtector.Protect(endpoint);
        return AlertDeliveryDestination.CreateAdapter(
            name,
            kind,
            RedactEndpoint(endpoint),
            protectedEndpoint,
            now);
    }

    private AlertDeliveryDestination BuildPagerDuty(string name, DateTimeOffset now)
    {
        var routingKey = RequireValue(DestinationInput.Secret, "PagerDuty routing key");
        return AlertDeliveryDestination.CreateAdapter(
            name,
            AlertDeliveryKind.PagerDuty,
            PagerDutyEventsEndpoint,
            destinationSecretProtector.Protect(routingKey),
            now);
    }

    private AlertDeliveryDestination BuildEmail(string name, DateTimeOffset now)
    {
        var recipient = NormalizeEmail(DestinationInput.EmailRecipient, "Recipient email");
        var fromAddress = NormalizeEmail(DestinationInput.SmtpFromAddress, "SMTP from address");
        var host = RequireValue(DestinationInput.SmtpHost, "SMTP host");

        if (DestinationInput.SmtpPort is < 1 or > 65535)
        {
            throw new ArgumentException("SMTP port must be between 1 and 65535.");
        }

        var userName = string.IsNullOrWhiteSpace(DestinationInput.SmtpUserName)
            ? null
            : DestinationInput.SmtpUserName.Trim();
        var password = string.IsNullOrWhiteSpace(DestinationInput.SmtpPassword)
            ? null
            : DestinationInput.SmtpPassword;

        if (userName is not null && password is null)
        {
            throw new ArgumentException("SMTP password is required when an SMTP username is configured.");
        }

        var configuration = new EmailDestinationConfiguration(
            host,
            DestinationInput.SmtpPort,
            fromAddress,
            userName,
            password,
            DestinationInput.SmtpEnableSsl);

        return AlertDeliveryDestination.CreateAdapter(
            name,
            AlertDeliveryKind.Email,
            $"mailto:{recipient}",
            destinationSecretProtector.Protect(JsonSerializer.Serialize(configuration)),
            now);
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

    private static object SnapshotDestination(AlertDeliveryDestination destination) => new
    {
        destination.Name,
        destination.Kind,
        destination.EndpointUrl,
        destination.Enabled,
        destination.LastSuccessAt,
        destination.LastFailureAt,
        destination.LastFailure
    };

    private static string RequireValue(string? value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{fieldName} is required.");
        }

        return value.Trim();
    }

    private static string RequireHttpEndpoint(string? value, string fieldName, bool requireHttps)
    {
        var endpoint = RequireValue(value, fieldName);
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException($"{fieldName} must be an absolute HTTP or HTTPS URL.");
        }

        if (requireHttps && uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException($"{fieldName} must use HTTPS.");
        }

        return uri.AbsoluteUri;
    }

    private static string RedactEndpoint(string endpoint)
    {
        var uri = new Uri(endpoint, UriKind.Absolute);
        var authority = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
        return $"{uri.Scheme}://{authority}/***";
    }

    private static string NormalizeEmail(string? value, string fieldName)
    {
        var email = RequireValue(value, fieldName);
        try
        {
            return new MailAddress(email).Address;
        }
        catch (FormatException)
        {
            throw new ArgumentException($"{fieldName} is not a valid email address.");
        }
    }

    public static string DisplayKind(AlertDeliveryKind kind) => kind switch
    {
        AlertDeliveryKind.MicrosoftTeams => "Microsoft Teams",
        AlertDeliveryKind.PagerDuty => "PagerDuty",
        _ => kind.ToString()
    };

    public sealed class CreateDestinationInput
    {
        [Required, StringLength(200)]
        public string Name { get; set; } = string.Empty;

        public AlertDeliveryKind Kind { get; set; } = AlertDeliveryKind.Webhook;

        [StringLength(2000)]
        public string? EndpointUrl { get; set; }

        [StringLength(2000)]
        public string? Secret { get; set; }

        [StringLength(320), EmailAddress]
        public string? EmailRecipient { get; set; }

        [StringLength(500)]
        public string? SmtpHost { get; set; }

        [Range(1, 65535)]
        public int SmtpPort { get; set; } = 587;

        [StringLength(320), EmailAddress]
        public string? SmtpFromAddress { get; set; }

        [StringLength(500)]
        public string? SmtpUserName { get; set; }

        [StringLength(2000)]
        public string? SmtpPassword { get; set; }

        public bool SmtpEnableSsl { get; set; } = true;
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

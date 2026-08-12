using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Monitor.Domain;

namespace Monitor.Web.Services;

public sealed class AlertDeliverySender(
    IHttpClientFactory httpClientFactory,
    AlertDestinationSecretProtector secretProtector,
    WebhookAlertSender webhookSender,
    IOptions<AlertDeliveryOptions> options)
{
    private readonly AlertDeliveryOptions _options = options.Value;

    public async Task<AlertSendResult> SendAlertAsync(AlertDelivery delivery, CancellationToken cancellationToken)
    {
        if (delivery.Destination.Kind == AlertDeliveryKind.Webhook)
            return Convert(await webhookSender.SendAlertAsync(delivery, cancellationToken));

        var alertEvent = delivery.AlertEvent;
        var rule = alertEvent.AlertRule;
        var group = alertEvent.FailureGroup;
        var notification = new AlertNotification(
            alertEvent.Id,
            "failure.alert.triggered",
            $"Monitor failure alert: {rule.Name}",
            $"{alertEvent.OccurrencesInWindow:N0} occurrence(s) in {rule.WindowMinutes} min · {group.Category} · {group.Operation}",
            "error",
            alertEvent.TriggeredAt,
            $"monitor:failure:{alertEvent.Id:D}",
            new Dictionary<string, object?>
            {
                ["alertEventId"] = alertEvent.Id, ["ruleId"] = rule.Id, ["rule"] = rule.Name,
                ["failureGroupId"] = group.Id, ["fingerprint"] = group.Fingerprint, ["category"] = group.Category.ToString(),
                ["operation"] = group.Operation, ["failureType"] = group.FailureType, ["dependency"] = group.Dependency,
                ["httpStatusCode"] = group.HttpStatusCode, ["threshold"] = alertEvent.Threshold,
                ["occurrences"] = alertEvent.OccurrencesInWindow, ["windowStart"] = alertEvent.WindowStart, ["windowEnd"] = alertEvent.WindowEnd
            });
        return await SendAsync(delivery.Destination, notification, cancellationToken);
    }

    public async Task<AlertSendResult> SendBudgetAlertAsync(UsageBudgetAlertDelivery delivery, CancellationToken cancellationToken)
    {
        if (delivery.Destination.Kind == AlertDeliveryKind.Webhook)
            return Convert(await webhookSender.SendBudgetAlertAsync(delivery, cancellationToken));

        var alertEvent = delivery.BudgetAlertEvent;
        var budget = alertEvent.UsageBudget;
        var level = alertEvent.Level == UsageBudgetAlertLevel.Critical ? "critical" : "warning";
        var notification = new AlertNotification(
            alertEvent.Id,
            alertEvent.Level == UsageBudgetAlertLevel.Critical ? "usage.budget.critical" : "usage.budget.warning",
            $"Monitor budget {level}: {budget.Name}",
            $"{alertEvent.UtilizationPercent:N1}% utilized · {budget.Period} budget",
            alertEvent.Level == UsageBudgetAlertLevel.Critical ? "critical" : "warning",
            alertEvent.TriggeredAt,
            $"monitor:budget:{alertEvent.Id:D}",
            new Dictionary<string, object?>
            {
                ["alertEventId"] = alertEvent.Id, ["budgetId"] = budget.Id, ["budget"] = budget.Name,
                ["period"] = budget.Period.ToString(), ["periodStart"] = alertEvent.PeriodStart, ["periodEnd"] = alertEvent.PeriodEnd,
                ["componentId"] = budget.ComponentId, ["environment"] = budget.Environment, ["model"] = budget.Model,
                ["costLimitUsd"] = alertEvent.CostLimitUsd, ["tokenLimit"] = alertEvent.TokenLimit,
                ["observedCostUsd"] = alertEvent.ObservedCostUsd, ["observedTokens"] = alertEvent.ObservedTokens,
                ["utilizationPercent"] = alertEvent.UtilizationPercent
            });
        return await SendAsync(delivery.Destination, notification, cancellationToken);
    }

    public async Task<AlertSendResult> SendTestAsync(AlertDeliveryDestination destination, CancellationToken cancellationToken)
    {
        if (destination.Kind == AlertDeliveryKind.Webhook)
            return Convert(await webhookSender.SendTestAsync(destination, cancellationToken));

        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid();
        var notification = new AlertNotification(id, "delivery.test", "Monitor delivery test",
            $"Test notification for destination ‘{destination.Name}’. If you can read this, the adapter is configured correctly.",
            "info", now, $"monitor:test:{id:D}",
            new Dictionary<string, object?> { ["destinationId"] = destination.Id, ["destination"] = destination.Name, ["kind"] = destination.Kind.ToString(), ["sentAt"] = now });
        return await SendAsync(destination, notification, cancellationToken);
    }

    private Task<AlertSendResult> SendAsync(AlertDeliveryDestination destination, AlertNotification notification, CancellationToken cancellationToken) =>
        destination.Kind switch
        {
            AlertDeliveryKind.Slack => SendSlackAsync(destination, notification, cancellationToken),
            AlertDeliveryKind.MicrosoftTeams => SendTeamsAsync(destination, notification, cancellationToken),
            AlertDeliveryKind.Discord => SendDiscordAsync(destination, notification, cancellationToken),
            AlertDeliveryKind.PagerDuty => SendPagerDutyAsync(destination, notification, cancellationToken),
            AlertDeliveryKind.Email => SendEmailAsync(destination, notification, cancellationToken),
            _ => Task.FromResult(AlertSendResult.PermanentFailure(null, $"Unsupported alert delivery kind: {destination.Kind}."))
        };

    private async Task<AlertSendResult> SendSlackAsync(AlertDeliveryDestination destination, AlertNotification notification, CancellationToken cancellationToken)
    {
        if (!TryReadProtectedUri(destination, out var endpoint, out var failure)) return failure!;
        var payload = new { text = $"{notification.Title}\n{notification.Summary}", blocks = new object[] {
            new { type = "header", text = new { type = "plain_text", text = Truncate(notification.Title, 150), emoji = true } },
            new { type = "section", text = new { type = "mrkdwn", text = notification.Summary } },
            new { type = "context", elements = new[] { new { type = "mrkdwn", text = $"*Event:* `{notification.EventType}` · *ID:* `{notification.Id:D}`" } } }
        } };
        return await PostJsonAsync(endpoint!, notification.EventType, payload, cancellationToken);
    }

    private async Task<AlertSendResult> SendTeamsAsync(AlertDeliveryDestination destination, AlertNotification notification, CancellationToken cancellationToken)
    {
        if (!TryReadProtectedUri(destination, out var endpoint, out var failure)) return failure!;
        var payload = new { type = "message", attachments = new[] { new {
            contentType = "application/vnd.microsoft.card.adaptive", contentUrl = (string?)null,
            content = new { type = "AdaptiveCard", version = "1.4", body = new object[] {
                new { type = "TextBlock", size = "Medium", weight = "Bolder", text = notification.Title, wrap = true },
                new { type = "TextBlock", text = notification.Summary, wrap = true },
                new { type = "TextBlock", spacing = "Small", isSubtle = true, text = $"{notification.EventType} · {notification.Id:D}", wrap = true }
            } }
        } } };
        return await PostJsonAsync(endpoint!, notification.EventType, payload, cancellationToken);
    }

    private async Task<AlertSendResult> SendDiscordAsync(AlertDeliveryDestination destination, AlertNotification notification, CancellationToken cancellationToken)
    {
        if (!TryReadProtectedUri(destination, out var endpoint, out var failure)) return failure!;
        var payload = new { username = "Monitor", content = notification.Title,
            embeds = new[] { new { description = notification.Summary, timestamp = notification.OccurredAt, footer = new { text = $"{notification.EventType} · {notification.Id:D}" } } },
            allowed_mentions = new { parse = Array.Empty<string>() } };
        return await PostJsonAsync(endpoint!, notification.EventType, payload, cancellationToken);
    }

    private async Task<AlertSendResult> SendPagerDutyAsync(AlertDeliveryDestination destination, AlertNotification notification, CancellationToken cancellationToken)
    {
        if (!TryUnprotect(destination, out var routingKey, out var failure)) return failure!;
        if (!Uri.TryCreate(destination.EndpointUrl, UriKind.Absolute, out var endpoint))
            return AlertSendResult.PermanentFailure(null, "PagerDuty endpoint is invalid.");
        var payload = new { routing_key = routingKey, event_action = "trigger", dedup_key = notification.DedupKey,
            payload = new { summary = Truncate($"{notification.Title} — {notification.Summary}", 1024), source = "Monitor",
                severity = notification.Severity, timestamp = notification.OccurredAt, custom_details = notification.Details } };
        return await PostJsonAsync(endpoint, notification.EventType, payload, cancellationToken);
    }

    private async Task<AlertSendResult> SendEmailAsync(AlertDeliveryDestination destination, AlertNotification notification, CancellationToken cancellationToken)
    {
        if (!TryUnprotect(destination, out var protectedJson, out var failure)) return failure!;
        EmailDestinationConfiguration? configuration;
        try { configuration = JsonSerializer.Deserialize<EmailDestinationConfiguration>(protectedJson!); }
        catch (JsonException exception) { return AlertSendResult.PermanentFailure(null, $"Email destination configuration is invalid: {exception.Message}"); }
        if (configuration is null || string.IsNullOrWhiteSpace(configuration.Host))
            return AlertSendResult.PermanentFailure(null, "Email destination configuration is incomplete.");

        var recipientValue = destination.EndpointUrl.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ? destination.EndpointUrl["mailto:".Length..] : destination.EndpointUrl;
        try
        {
            using var message = new MailMessage { From = new MailAddress(configuration.FromAddress), Subject = Truncate(notification.Title, 200), Body = BuildEmailBody(notification), IsBodyHtml = false };
            message.To.Add(new MailAddress(recipientValue));
            message.Headers.Add("X-Monitor-Event", notification.EventType);
            message.Headers.Add("X-Monitor-Notification-Id", notification.Id.ToString("D"));
            using var client = new SmtpClient(configuration.Host, configuration.Port) { EnableSsl = configuration.EnableSsl, Timeout = Math.Clamp(_options.RequestTimeoutSeconds, 1, 120) * 1000 };
            if (!string.IsNullOrWhiteSpace(configuration.UserName)) { client.UseDefaultCredentials = false; client.Credentials = new NetworkCredential(configuration.UserName, configuration.Password ?? string.Empty); }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.RequestTimeoutSeconds, 1, 120)));
            await client.SendMailAsync(message).WaitAsync(timeout.Token);
            return AlertSendResult.Success(250);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return AlertSendResult.RetryableFailure(null, "SMTP delivery timed out."); }
        catch (SmtpException exception)
        {
            var statusCode = (int)exception.StatusCode;
            var retryable = statusCode == 0 || (statusCode >= 400 && statusCode < 500);
            return retryable ? AlertSendResult.RetryableFailure(statusCode == 0 ? null : statusCode, exception.Message) : AlertSendResult.PermanentFailure(statusCode == 0 ? null : statusCode, exception.Message);
        }
        catch (FormatException exception) { return AlertSendResult.PermanentFailure(null, exception.Message); }
        catch (InvalidOperationException exception) { return AlertSendResult.PermanentFailure(null, exception.Message); }
    }

    private async Task<AlertSendResult> PostJsonAsync(Uri endpoint, string eventType, object payload, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(payload);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.TryAddWithoutValidation("X-Monitor-Event", eventType);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.RequestTimeoutSeconds, 1, 120)));
        try
        {
            var client = httpClientFactory.CreateClient("alert-adapters");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.IsSuccessStatusCode) return AlertSendResult.Success((int)response.StatusCode);
            var responseBody = await ReadErrorBodyAsync(response, timeout.Token);
            var error = string.IsNullOrWhiteSpace(responseBody) ? $"Destination returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase})." : $"Destination returned HTTP {(int)response.StatusCode}: {responseBody}";
            return IsRetryable(response.StatusCode) ? AlertSendResult.RetryableFailure((int)response.StatusCode, error) : AlertSendResult.PermanentFailure((int)response.StatusCode, error);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return AlertSendResult.RetryableFailure(null, "Destination request timed out."); }
        catch (HttpRequestException exception) { return AlertSendResult.RetryableFailure(null, exception.Message); }
    }

    private bool TryReadProtectedUri(AlertDeliveryDestination destination, out Uri? endpoint, out AlertSendResult? failure)
    {
        endpoint = null;
        if (!TryUnprotect(destination, out var value, out failure)) return false;
        if (!Uri.TryCreate(value, UriKind.Absolute, out endpoint) || (endpoint.Scheme != Uri.UriSchemeHttps && !(endpoint.Scheme == Uri.UriSchemeHttp && endpoint.IsLoopback)))
        {
            failure = AlertSendResult.PermanentFailure(null, $"{destination.Kind} webhook URL must use HTTPS (HTTP is accepted only for loopback development endpoints).");
            endpoint = null;
            return false;
        }
        return true;
    }

    private bool TryUnprotect(AlertDeliveryDestination destination, out string? value, out AlertSendResult? failure)
    {
        try { value = secretProtector.Unprotect(destination.ProtectedSecret); failure = null; return true; }
        catch (CryptographicException exception) { value = null; failure = AlertSendResult.PermanentFailure(null, $"Destination secret could not be decrypted: {exception.Message}"); return false; }
    }

    private static AlertSendResult Convert(WebhookSendResult result) => new(result.Succeeded, result.Retryable, result.StatusCode, result.Error);
    private static bool IsRetryable(HttpStatusCode statusCode) => statusCode == HttpStatusCode.RequestTimeout || (int)statusCode == 425 || (int)statusCode == 429 || (int)statusCode >= 500;
    private static async Task<string?> ReadErrorBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    { var body = await response.Content.ReadAsStringAsync(cancellationToken); if (string.IsNullOrWhiteSpace(body)) return null; const int maxLength = 1000; return body.Length <= maxLength ? body.Trim() : body[..maxLength].Trim(); }

    private static string BuildEmailBody(AlertNotification notification)
    {
        var builder = new StringBuilder(); builder.AppendLine(notification.Title); builder.AppendLine(); builder.AppendLine(notification.Summary); builder.AppendLine();
        builder.AppendLine($"Event: {notification.EventType}"); builder.AppendLine($"ID: {notification.Id:D}"); builder.AppendLine($"Occurred: {notification.OccurredAt:O}");
        builder.AppendLine(); builder.AppendLine("Details:"); foreach (var pair in notification.Details) builder.AppendLine($"- {pair.Key}: {pair.Value ?? "—"}"); return builder.ToString();
    }

    private static string Truncate(string value, int maxLength) => value.Length <= maxLength ? value : value[..Math.Max(0, maxLength - 1)] + "…";
    private sealed record AlertNotification(Guid Id, string EventType, string Title, string Summary, string Severity, DateTimeOffset OccurredAt, string DedupKey, IReadOnlyDictionary<string, object?> Details);
}

public sealed record EmailDestinationConfiguration(string Host, int Port, string FromAddress, string? UserName, string? Password, bool EnableSsl);
public sealed record AlertSendResult(bool Succeeded, bool Retryable, int? StatusCode, string? Error)
{
    public static AlertSendResult Success(int statusCode) => new(true, false, statusCode, null);
    public static AlertSendResult RetryableFailure(int? statusCode, string error) => new(false, true, statusCode, error);
    public static AlertSendResult PermanentFailure(int? statusCode, string error) => new(false, false, statusCode, error);
}

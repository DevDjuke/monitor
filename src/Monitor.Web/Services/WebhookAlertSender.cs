using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Monitor.Domain;

namespace Monitor.Web.Services;

public sealed class WebhookAlertSender(
    IHttpClientFactory httpClientFactory,
    IDataProtectionProvider dataProtectionProvider,
    IOptions<AlertDeliveryOptions> options)
{
    private readonly IDataProtector _protector = dataProtectionProvider.CreateProtector("Monitor.AlertDelivery.WebhookSecret.v1");
    private readonly AlertDeliveryOptions _options = options.Value;

    public string ProtectSecret(string secret)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new ArgumentException("Webhook secret is required.", nameof(secret));
        }

        return _protector.Protect(secret.Trim());
    }

    public async Task<WebhookSendResult> SendAlertAsync(
        AlertDelivery delivery,
        CancellationToken cancellationToken)
    {
        var alertEvent = delivery.AlertEvent;
        var rule = alertEvent.AlertRule;
        var group = alertEvent.FailureGroup;

        var payload = new
        {
            schemaVersion = 1,
            type = "failure.alert.triggered",
            deliveryId = delivery.Id,
            alertEventId = alertEvent.Id,
            triggeredAt = alertEvent.TriggeredAt,
            occurrenceWindow = new
            {
                start = alertEvent.WindowStart,
                end = alertEvent.WindowEnd,
                occurrences = alertEvent.OccurrencesInWindow,
                threshold = alertEvent.Threshold,
                latestRunSequence = alertEvent.LatestRunSequence
            },
            rule = new
            {
                id = rule.Id,
                name = rule.Name,
                threshold = rule.Threshold,
                windowMinutes = rule.WindowMinutes,
                cooldownMinutes = rule.CooldownMinutes
            },
            failureGroup = new
            {
                id = group.Id,
                fingerprint = group.Fingerprint,
                category = group.Category.ToString(),
                failureType = group.FailureType,
                operation = group.Operation,
                dependency = group.Dependency,
                httpStatusCode = group.HttpStatusCode,
                messageTemplate = group.MessageTemplate,
                firstSeenAt = group.FirstSeenAt,
                lastSeenAt = group.LastSeenAt,
                totalOccurrences = group.Occurrences
            }
        };

        var body = JsonSerializer.Serialize(payload, WebhookJsonContext.Default.Object);
        return await SendAsync(
            delivery.Destination,
            delivery.Id,
            "failure.alert.triggered",
            body,
            cancellationToken);
    }

    public async Task<WebhookSendResult> SendTestAsync(
        AlertDeliveryDestination destination,
        CancellationToken cancellationToken)
    {
        var deliveryId = Guid.NewGuid();
        var body = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            type = "delivery.test",
            deliveryId,
            sentAt = DateTimeOffset.UtcNow,
            message = "Monitor webhook delivery test"
        });

        return await SendAsync(destination, deliveryId, "delivery.test", body, cancellationToken);
    }

    private async Task<WebhookSendResult> SendAsync(
        AlertDeliveryDestination destination,
        Guid deliveryId,
        string eventType,
        string body,
        CancellationToken cancellationToken)
    {
        string secret;
        try
        {
            secret = _protector.Unprotect(destination.ProtectedSecret);
        }
        catch (CryptographicException exception)
        {
            return WebhookSendResult.PermanentFailure(null, $"Webhook secret could not be decrypted: {exception.Message}");
        }

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var canonical = $"{timestamp}.{body}";
        var signatureBytes = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes(canonical));
        var signature = Convert.ToHexString(signatureBytes).ToLowerInvariant();

        using var request = new HttpRequestMessage(HttpMethod.Post, destination.EndpointUrl)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("X-Monitor-Event", eventType);
        request.Headers.TryAddWithoutValidation("X-Monitor-Delivery-Id", deliveryId.ToString());
        request.Headers.TryAddWithoutValidation("X-Monitor-Timestamp", timestamp);
        request.Headers.TryAddWithoutValidation("X-Monitor-Signature", $"sha256={signature}");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.RequestTimeoutSeconds, 1, 120)));

        try
        {
            var client = httpClientFactory.CreateClient("alert-webhooks");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.IsSuccessStatusCode)
            {
                return WebhookSendResult.Success((int)response.StatusCode);
            }

            var responseBody = await ReadErrorBodyAsync(response, timeout.Token);
            var error = string.IsNullOrWhiteSpace(responseBody)
                ? $"Webhook returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase})."
                : $"Webhook returned HTTP {(int)response.StatusCode}: {responseBody}";

            return IsRetryable(response.StatusCode)
                ? WebhookSendResult.RetryableFailure((int)response.StatusCode, error)
                : WebhookSendResult.PermanentFailure((int)response.StatusCode, error);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return WebhookSendResult.RetryableFailure(null, "Webhook request timed out.");
        }
        catch (HttpRequestException exception)
        {
            return WebhookSendResult.RetryableFailure(null, exception.Message);
        }
    }

    private static bool IsRetryable(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.RequestTimeout ||
        (int)statusCode == 429 ||
        (int)statusCode >= 500;

    private static async Task<string?> ReadErrorBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        const int maxLength = 1000;
        return body.Length <= maxLength ? body.Trim() : body[..maxLength].Trim();
    }
}

public sealed record WebhookSendResult(bool Succeeded, bool Retryable, int? StatusCode, string? Error)
{
    public static WebhookSendResult Success(int statusCode) => new(true, false, statusCode, null);
    public static WebhookSendResult RetryableFailure(int? statusCode, string error) => new(false, true, statusCode, error);
    public static WebhookSendResult PermanentFailure(int? statusCode, string error) => new(false, false, statusCode, error);
}

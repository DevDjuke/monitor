using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Monitor.Domain;
using Monitor.Infrastructure;

namespace Monitor.Web.Services;

public sealed class AlertDeliveryDispatcher(
    MonitorDbContext db,
    IHttpClientFactory httpClientFactory,
    WebhookSecretProtector secretProtector,
    IOptions<AlertDeliveryOptions> options,
    ILogger<AlertDeliveryDispatcher> logger)
{
    private const string LockResource = "Monitor.AlertDelivery";
    private const string HttpClientName = "Monitor.AlertDelivery";
    private readonly AlertDeliveryOptions _options = options.Value;

    public async Task<AlertDeliverySweepResult> SweepAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return AlertDeliverySweepResult.Disabled;
        }

        var now = DateTimeOffset.UtcNow;
        var leaseId = Guid.NewGuid();
        var claimedIds = await ClaimDueDeliveriesAsync(now, leaseId, cancellationToken);

        if (claimedIds.Count == 0)
        {
            return new AlertDeliverySweepResult(true, 0, 0, 0, 0);
        }

        var delivered = 0;
        var retryScheduled = 0;
        var deadLettered = 0;

        foreach (var deliveryId in claimedIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            db.ChangeTracker.Clear();

            var delivery = await db.AlertDeliveries
                .Include(x => x.Destination)
                .SingleOrDefaultAsync(
                    x => x.Id == deliveryId && x.LeaseId == leaseId,
                    cancellationToken);

            if (delivery is null)
            {
                continue;
            }

            var result = await DispatchAsync(delivery, cancellationToken);
            switch (result)
            {
                case DispatchResult.Delivered:
                    delivered++;
                    break;
                case DispatchResult.RetryScheduled:
                    retryScheduled++;
                    break;
                case DispatchResult.DeadLettered:
                    deadLettered++;
                    break;
            }
        }

        if (delivered + retryScheduled + deadLettered > 0)
        {
            logger.LogInformation(
                "Alert delivery sweep processed {ProcessedCount} record(s): {DeliveredCount} delivered, {RetryCount} retry scheduled, {DeadLetterCount} dead-lettered.",
                delivered + retryScheduled + deadLettered,
                delivered,
                retryScheduled,
                deadLettered);
        }

        return new AlertDeliverySweepResult(
            true,
            claimedIds.Count,
            delivered,
            retryScheduled,
            deadLettered);
    }

    private async Task<IReadOnlyList<Guid>> ClaimDueDeliveriesAsync(
        DateTimeOffset now,
        Guid leaseId,
        CancellationToken cancellationToken)
    {
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            var connection = db.Database.GetDbConnection();
            if (!await TryAcquireLockAsync(connection, cancellationToken))
            {
                return [];
            }

            try
            {
                var batchSize = Math.Clamp(_options.BatchSize, 1, 500);
                var deliveries = await db.AlertDeliveries
                    .Where(x =>
                        (x.Status == AlertDeliveryStatus.Pending || x.Status == AlertDeliveryStatus.RetryScheduled) &&
                        x.NextAttemptAt <= now &&
                        (x.LeaseExpiresAt == null || x.LeaseExpiresAt <= now))
                    .OrderBy(x => x.NextAttemptAt)
                    .ThenBy(x => x.CreatedAt)
                    .Take(batchSize)
                    .ToListAsync(cancellationToken);

                if (deliveries.Count == 0)
                {
                    return [];
                }

                var requestTimeoutSeconds = Math.Clamp(_options.RequestTimeoutSeconds, 1, 300);
                var configuredLeaseSeconds = Math.Clamp(_options.LeaseSeconds, 30, 86_400);
                var sequentialWorstCaseSeconds = requestTimeoutSeconds * (deliveries.Count + 1);
                var leaseSeconds = Math.Max(configuredLeaseSeconds, sequentialWorstCaseSeconds);
                var leaseExpiresAt = now.AddSeconds(leaseSeconds);

                foreach (var delivery in deliveries)
                {
                    delivery.Claim(leaseId, leaseExpiresAt, now);
                }

                await db.SaveChangesAsync(cancellationToken);
                return deliveries.Select(x => x.Id).ToList();
            }
            finally
            {
                await ReleaseLockAsync(connection, cancellationToken);
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private async Task<DispatchResult> DispatchAsync(
        AlertDelivery delivery,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var maxAttempts = Math.Clamp(_options.MaxAttempts, 1, 100);

        if (!delivery.Destination.Enabled)
        {
            delivery.MarkFailed(
                now,
                null,
                "Destination is disabled.",
                retryable: false,
                maxAttempts,
                now);
            await db.SaveChangesAsync(cancellationToken);
            return DispatchResult.DeadLettered;
        }

        if (delivery.Destination.Kind != AlertDestinationKind.Webhook)
        {
            delivery.MarkFailed(
                now,
                null,
                $"Unsupported destination kind: {delivery.Destination.Kind}.",
                retryable: false,
                maxAttempts,
                now);
            await db.SaveChangesAsync(cancellationToken);
            return DispatchResult.DeadLettered;
        }

        string signingSecret;
        try
        {
            signingSecret = secretProtector.Unprotect(delivery.Destination.ProtectedSigningSecret);
        }
        catch (Exception exception) when (exception is CryptographicException or InvalidOperationException)
        {
            delivery.MarkFailed(
                now,
                null,
                $"Unable to decrypt webhook signing secret: {exception.Message}",
                retryable: false,
                maxAttempts,
                now);
            await db.SaveChangesAsync(cancellationToken);
            return DispatchResult.DeadLettered;
        }

        using var request = CreateRequest(delivery, signingSecret, now);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.RequestTimeoutSeconds, 1, 300)));

        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeoutCts.Token);

            var statusCode = (int)response.StatusCode;
            if (statusCode is >= 200 and <= 299)
            {
                delivery.MarkDelivered(DateTimeOffset.UtcNow, statusCode);
                await db.SaveChangesAsync(cancellationToken);
                return DispatchResult.Delivered;
            }

            var responseExcerpt = await ReadResponseExcerptAsync(response, timeoutCts.Token);
            var retryable = IsRetryable(statusCode);
            var failureAt = DateTimeOffset.UtcNow;
            delivery.MarkFailed(
                failureAt,
                statusCode,
                string.IsNullOrWhiteSpace(responseExcerpt)
                    ? $"Webhook returned HTTP {statusCode}."
                    : $"Webhook returned HTTP {statusCode}: {responseExcerpt}",
                retryable,
                maxAttempts,
                ComputeRetryAt(failureAt, delivery.AttemptCount + 1));

            await db.SaveChangesAsync(cancellationToken);
            return delivery.Status == AlertDeliveryStatus.RetryScheduled
                ? DispatchResult.RetryScheduled
                : DispatchResult.DeadLettered;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var failureAt = DateTimeOffset.UtcNow;
            delivery.MarkFailed(
                failureAt,
                null,
                "Webhook request timed out.",
                retryable: true,
                maxAttempts,
                ComputeRetryAt(failureAt, delivery.AttemptCount + 1));
            await db.SaveChangesAsync(cancellationToken);
            return delivery.Status == AlertDeliveryStatus.RetryScheduled
                ? DispatchResult.RetryScheduled
                : DispatchResult.DeadLettered;
        }
        catch (HttpRequestException exception)
        {
            var failureAt = DateTimeOffset.UtcNow;
            delivery.MarkFailed(
                failureAt,
                exception.StatusCode is null ? null : (int)exception.StatusCode.Value,
                exception.Message,
                retryable: true,
                maxAttempts,
                ComputeRetryAt(failureAt, delivery.AttemptCount + 1));
            await db.SaveChangesAsync(cancellationToken);
            return delivery.Status == AlertDeliveryStatus.RetryScheduled
                ? DispatchResult.RetryScheduled
                : DispatchResult.DeadLettered;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var failureAt = DateTimeOffset.UtcNow;
            delivery.MarkFailed(
                failureAt,
                null,
                exception.Message,
                retryable: true,
                maxAttempts,
                ComputeRetryAt(failureAt, delivery.AttemptCount + 1));
            await db.SaveChangesAsync(cancellationToken);
            logger.LogWarning(exception, "Unexpected alert delivery failure for {DeliveryId}.", delivery.Id);
            return delivery.Status == AlertDeliveryStatus.RetryScheduled
                ? DispatchResult.RetryScheduled
                : DispatchResult.DeadLettered;
        }
    }

    private static HttpRequestMessage CreateRequest(
        AlertDelivery delivery,
        string signingSecret,
        DateTimeOffset timestamp)
    {
        var unixTimestamp = timestamp.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var signatureInput = $"{unixTimestamp}.{delivery.PayloadJson}";

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(signingSecret));
        var signature = Convert.ToHexString(
                hmac.ComputeHash(Encoding.UTF8.GetBytes(signatureInput)))
            .ToLowerInvariant();

        var request = new HttpRequestMessage(HttpMethod.Post, delivery.Destination.Endpoint)
        {
            Content = new StringContent(delivery.PayloadJson, Encoding.UTF8, "application/json")
        };

        request.Headers.TryAddWithoutValidation("User-Agent", "Monitor-Alert-Delivery/1.0");
        request.Headers.TryAddWithoutValidation("X-Monitor-Event", "failure.alert.triggered");
        request.Headers.TryAddWithoutValidation("X-Monitor-Delivery-Id", delivery.Id.ToString("D"));
        request.Headers.TryAddWithoutValidation("X-Monitor-Alert-Event-Id", delivery.AlertEventId.ToString("D"));
        request.Headers.TryAddWithoutValidation("X-Monitor-Timestamp", unixTimestamp);
        request.Headers.TryAddWithoutValidation("X-Monitor-Signature", $"v1={signature}");

        return request;
    }

    private DateTimeOffset ComputeRetryAt(DateTimeOffset now, int attemptNumber)
    {
        var baseRetrySeconds = Math.Clamp(_options.BaseRetrySeconds, 1, 86_400);
        var maxRetrySeconds = Math.Max(
            baseRetrySeconds,
            Math.Clamp(_options.MaxRetrySeconds, 1, 604_800));
        var exponent = Math.Clamp(attemptNumber - 1, 0, 20);
        var retrySeconds = Math.Min(
            maxRetrySeconds,
            baseRetrySeconds * Math.Pow(2, exponent));

        return now.AddSeconds(retrySeconds);
    }

    private static bool IsRetryable(int statusCode) =>
        statusCode is 408 or 425 or 429 || statusCode >= 500;

    private static async Task<string> ReadResponseExcerptAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[2048];
        var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken);
        return read == 0 ? string.Empty : Encoding.UTF8.GetString(buffer, 0, read).Trim();
    }

    private static async Task<bool> TryAcquireLockAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock
                @Resource = @resource,
                @LockMode = 'Exclusive',
                @LockOwner = 'Session',
                @LockTimeout = 0;
            SELECT @result;
            """;

        var parameter = command.CreateParameter();
        parameter.ParameterName = "@resource";
        parameter.Value = LockResource;
        command.Parameters.Add(parameter);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result) >= 0;
    }

    private static async Task ReleaseLockAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "EXEC sys.sp_releaseapplock @Resource = @resource, @LockOwner = 'Session';";

        var parameter = command.CreateParameter();
        parameter.ParameterName = "@resource";
        parameter.Value = LockResource;
        command.Parameters.Add(parameter);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private enum DispatchResult
    {
        Delivered,
        RetryScheduled,
        DeadLettered
    }
}

public sealed record AlertDeliverySweepResult(
    bool Executed,
    int Claimed,
    int Delivered,
    int RetryScheduled,
    int DeadLettered)
{
    public static AlertDeliverySweepResult Disabled { get; } = new(false, 0, 0, 0, 0);
}

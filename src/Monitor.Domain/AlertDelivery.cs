namespace Monitor.Domain;

public enum AlertDeliveryStatus
{
    Pending = 1,
    RetryScheduled = 2,
    Delivered = 3,
    DeadLetter = 4
}

public sealed class AlertDelivery
{
    private AlertDelivery() { }

    public Guid Id { get; private set; }
    public Guid AlertEventId { get; private set; }
    public Guid DestinationId { get; private set; }
    public AlertDeliveryStatus Status { get; private set; }
    public int AttemptCount { get; private set; }
    public string PayloadJson { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset NextAttemptAt { get; private set; }
    public DateTimeOffset? LastAttemptAt { get; private set; }
    public DateTimeOffset? DeliveredAt { get; private set; }
    public int? LastStatusCode { get; private set; }
    public string? LastError { get; private set; }
    public Guid? LeaseId { get; private set; }
    public DateTimeOffset? LeaseExpiresAt { get; private set; }

    public FailureAlertEvent AlertEvent { get; private set; } = null!;
    public AlertDestination Destination { get; private set; } = null!;

    public static AlertDelivery Create(
        Guid alertEventId,
        Guid destinationId,
        string payloadJson,
        DateTimeOffset now)
    {
        if (alertEventId == Guid.Empty)
        {
            throw new ArgumentException("Alert event id is required.", nameof(alertEventId));
        }

        if (destinationId == Guid.Empty)
        {
            throw new ArgumentException("Destination id is required.", nameof(destinationId));
        }

        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            throw new ArgumentException("Delivery payload is required.", nameof(payloadJson));
        }

        return new AlertDelivery
        {
            Id = Guid.NewGuid(),
            AlertEventId = alertEventId,
            DestinationId = destinationId,
            Status = AlertDeliveryStatus.Pending,
            AttemptCount = 0,
            PayloadJson = payloadJson,
            CreatedAt = now,
            UpdatedAt = now,
            NextAttemptAt = now
        };
    }

    public void Claim(Guid leaseId, DateTimeOffset leaseExpiresAt, DateTimeOffset now)
    {
        if (leaseId == Guid.Empty)
        {
            throw new ArgumentException("Lease id is required.", nameof(leaseId));
        }

        LeaseId = leaseId;
        LeaseExpiresAt = leaseExpiresAt;
        UpdatedAt = now;
    }

    public void MarkDelivered(DateTimeOffset now, int statusCode)
    {
        AttemptCount++;
        Status = AlertDeliveryStatus.Delivered;
        LastAttemptAt = now;
        DeliveredAt = now;
        LastStatusCode = statusCode;
        LastError = null;
        LeaseId = null;
        LeaseExpiresAt = null;
        UpdatedAt = now;
    }

    public void MarkFailed(
        DateTimeOffset now,
        int? statusCode,
        string error,
        bool retryable,
        int maxAttempts,
        DateTimeOffset retryAt)
    {
        AttemptCount++;
        LastAttemptAt = now;
        LastStatusCode = statusCode;
        LastError = NormalizeError(error);
        DeliveredAt = null;
        LeaseId = null;
        LeaseExpiresAt = null;
        UpdatedAt = now;

        if (retryable && AttemptCount < maxAttempts)
        {
            Status = AlertDeliveryStatus.RetryScheduled;
            NextAttemptAt = retryAt;
            return;
        }

        Status = AlertDeliveryStatus.DeadLetter;
        NextAttemptAt = now;
    }

    public void Requeue(DateTimeOffset now)
    {
        Status = AlertDeliveryStatus.Pending;
        AttemptCount = 0;
        NextAttemptAt = now;
        LastAttemptAt = null;
        DeliveredAt = null;
        LastStatusCode = null;
        LastError = null;
        LeaseId = null;
        LeaseExpiresAt = null;
        UpdatedAt = now;
    }

    private static string NormalizeError(string error)
    {
        var normalized = string.IsNullOrWhiteSpace(error) ? "Delivery failed." : error.Trim();
        return normalized.Length <= 2000 ? normalized : normalized[..2000];
    }
}

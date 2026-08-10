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
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset NextAttemptAt { get; private set; }
    public DateTimeOffset? LastAttemptAt { get; private set; }
    public DateTimeOffset? DeliveredAt { get; private set; }
    public int? ResponseStatusCode { get; private set; }
    public string? LastError { get; private set; }

    public FailureAlertEvent AlertEvent { get; private set; } = null!;
    public AlertDeliveryDestination Destination { get; private set; } = null!;

    public static AlertDelivery Create(
        FailureAlertEvent alertEvent,
        AlertDeliveryDestination destination,
        DateTimeOffset now)
    {
        return new AlertDelivery
        {
            Id = Guid.NewGuid(),
            AlertEventId = alertEvent.Id,
            DestinationId = destination.Id,
            Status = AlertDeliveryStatus.Pending,
            AttemptCount = 0,
            CreatedAt = now,
            NextAttemptAt = now
        };
    }

    public void MarkDelivered(int? responseStatusCode, DateTimeOffset now)
    {
        AttemptCount++;
        LastAttemptAt = now;
        DeliveredAt = now;
        ResponseStatusCode = responseStatusCode;
        LastError = null;
        Status = AlertDeliveryStatus.Delivered;
    }

    public void MarkFailed(
        string error,
        int? responseStatusCode,
        DateTimeOffset now,
        DateTimeOffset? nextAttemptAt)
    {
        AttemptCount++;
        LastAttemptAt = now;
        ResponseStatusCode = responseStatusCode;
        LastError = string.IsNullOrWhiteSpace(error) ? "Delivery failed." : error.Trim();

        if (nextAttemptAt is null)
        {
            Status = AlertDeliveryStatus.DeadLetter;
            return;
        }

        Status = AlertDeliveryStatus.RetryScheduled;
        NextAttemptAt = nextAttemptAt.Value;
    }

    public void Requeue(DateTimeOffset now)
    {
        if (Status == AlertDeliveryStatus.Delivered)
        {
            return;
        }

        Status = AlertDeliveryStatus.Pending;
        NextAttemptAt = now;
        LastError = null;
    }
}

namespace Monitor.Domain;

public sealed class FailureAlertRoute
{
    private FailureAlertRoute() { }

    public Guid Id { get; private set; }
    public Guid AlertRuleId { get; private set; }
    public Guid DestinationId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public FailureAlertRule AlertRule { get; private set; } = null!;
    public AlertDestination Destination { get; private set; } = null!;

    public static FailureAlertRoute Create(Guid alertRuleId, Guid destinationId, DateTimeOffset now)
    {
        if (alertRuleId == Guid.Empty)
        {
            throw new ArgumentException("Alert rule id is required.", nameof(alertRuleId));
        }

        if (destinationId == Guid.Empty)
        {
            throw new ArgumentException("Destination id is required.", nameof(destinationId));
        }

        return new FailureAlertRoute
        {
            Id = Guid.NewGuid(),
            AlertRuleId = alertRuleId,
            DestinationId = destinationId,
            CreatedAt = now
        };
    }
}

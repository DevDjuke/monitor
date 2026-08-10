namespace Monitor.Domain;

public sealed class FailureAlertRuleDestination
{
    private FailureAlertRuleDestination() { }

    public Guid FailureAlertRuleId { get; private set; }
    public Guid DestinationId { get; private set; }

    public FailureAlertRule FailureAlertRule { get; private set; } = null!;
    public AlertDeliveryDestination Destination { get; private set; } = null!;

    public static FailureAlertRuleDestination Create(Guid failureAlertRuleId, Guid destinationId)
    {
        if (failureAlertRuleId == Guid.Empty)
        {
            throw new ArgumentException("Alert rule id is required.", nameof(failureAlertRuleId));
        }

        if (destinationId == Guid.Empty)
        {
            throw new ArgumentException("Destination id is required.", nameof(destinationId));
        }

        return new FailureAlertRuleDestination
        {
            FailureAlertRuleId = failureAlertRuleId,
            DestinationId = destinationId
        };
    }
}

namespace Monitor.Domain;

public sealed class FailureAlertEvent
{
    private FailureAlertEvent() { }

    public Guid Id { get; private set; }
    public Guid AlertRuleId { get; private set; }
    public Guid FailureGroupId { get; private set; }
    public DateTimeOffset TriggeredAt { get; private set; }
    public DateTimeOffset WindowStart { get; private set; }
    public DateTimeOffset WindowEnd { get; private set; }
    public long OccurrencesInWindow { get; private set; }
    public int Threshold { get; private set; }
    public long LatestRunSequence { get; private set; }
    public DateTimeOffset? AcknowledgedAt { get; private set; }
    public string? AcknowledgedBy { get; private set; }

    public FailureAlertRule AlertRule { get; private set; } = null!;
    public FailureGroup FailureGroup { get; private set; } = null!;
    public ICollection<AlertDelivery> Deliveries { get; private set; } = new List<AlertDelivery>();

    public static FailureAlertEvent Create(
        FailureAlertRule rule,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        long occurrencesInWindow,
        long latestRunSequence)
    {
        return new FailureAlertEvent
        {
            Id = Guid.NewGuid(),
            AlertRuleId = rule.Id,
            FailureGroupId = rule.FailureGroupId,
            TriggeredAt = windowEnd,
            WindowStart = windowStart,
            WindowEnd = windowEnd,
            OccurrencesInWindow = occurrencesInWindow,
            Threshold = rule.Threshold,
            LatestRunSequence = latestRunSequence
        };
    }

    public void Acknowledge(string? userName, DateTimeOffset now)
    {
        if (AcknowledgedAt is not null)
        {
            return;
        }

        AcknowledgedAt = now;
        AcknowledgedBy = string.IsNullOrWhiteSpace(userName) ? null : userName.Trim();
    }
}

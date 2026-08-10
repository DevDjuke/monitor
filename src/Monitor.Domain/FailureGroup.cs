namespace Monitor.Domain;

public sealed class FailureGroup
{
    private FailureGroup() { }

    public Guid Id { get; private set; }
    public string Fingerprint { get; private set; } = string.Empty;
    public FailureCategory Category { get; private set; }
    public string? FailureType { get; private set; }
    public string Operation { get; private set; } = string.Empty;
    public string? Dependency { get; private set; }
    public int? HttpStatusCode { get; private set; }
    public string? MessageTemplate { get; private set; }
    public long Occurrences { get; private set; }
    public DateTimeOffset FirstSeenAt { get; private set; }
    public DateTimeOffset LastSeenAt { get; private set; }

    public ICollection<AgentRun> Runs { get; private set; } = new List<AgentRun>();
    public ICollection<FailureAlertRule> AlertRules { get; private set; } = new List<FailureAlertRule>();
    public ICollection<FailureAlertEvent> AlertEvents { get; private set; } = new List<FailureAlertEvent>();

    public static FailureGroup Create(
        string fingerprint,
        FailureCategory category,
        string? failureType,
        string operation,
        string? dependency,
        int? httpStatusCode,
        string? messageTemplate,
        DateTimeOffset seenAt)
    {
        return new FailureGroup
        {
            Id = Guid.NewGuid(),
            Fingerprint = fingerprint,
            Category = category,
            FailureType = failureType,
            Operation = operation,
            Dependency = dependency,
            HttpStatusCode = httpStatusCode,
            MessageTemplate = messageTemplate,
            Occurrences = 1,
            FirstSeenAt = seenAt,
            LastSeenAt = seenAt
        };
    }

    public void RecordOccurrence(DateTimeOffset seenAt)
    {
        Occurrences++;
        if (seenAt < FirstSeenAt)
        {
            FirstSeenAt = seenAt;
        }

        if (seenAt > LastSeenAt)
        {
            LastSeenAt = seenAt;
        }
    }
}

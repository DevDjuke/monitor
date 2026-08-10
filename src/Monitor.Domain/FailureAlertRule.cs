namespace Monitor.Domain;

public sealed class FailureAlertRule
{
    private FailureAlertRule() { }

    public Guid Id { get; private set; }
    public Guid FailureGroupId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public int Threshold { get; private set; }
    public int WindowMinutes { get; private set; }
    public int CooldownMinutes { get; private set; }
    public bool Enabled { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? LastEvaluatedAt { get; private set; }
    public DateTimeOffset? LastTriggeredAt { get; private set; }
    public long? LastTriggeredRunSequence { get; private set; }

    public FailureGroup FailureGroup { get; private set; } = null!;
    public ICollection<FailureAlertEvent> Events { get; private set; } = new List<FailureAlertEvent>();

    public static FailureAlertRule Create(
        Guid failureGroupId,
        string name,
        int threshold,
        int windowMinutes,
        int cooldownMinutes,
        DateTimeOffset now)
    {
        Validate(threshold, windowMinutes, cooldownMinutes);

        return new FailureAlertRule
        {
            Id = Guid.NewGuid(),
            FailureGroupId = failureGroupId,
            Name = NormalizeName(name),
            Threshold = threshold,
            WindowMinutes = windowMinutes,
            CooldownMinutes = cooldownMinutes,
            Enabled = true,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    public void Update(
        string name,
        int threshold,
        int windowMinutes,
        int cooldownMinutes,
        DateTimeOffset now)
    {
        Validate(threshold, windowMinutes, cooldownMinutes);

        Name = NormalizeName(name);
        Threshold = threshold;
        WindowMinutes = windowMinutes;
        CooldownMinutes = cooldownMinutes;
        UpdatedAt = now;
    }

    public void SetEnabled(bool enabled, DateTimeOffset now)
    {
        Enabled = enabled;
        UpdatedAt = now;
    }

    public void MarkEvaluated(DateTimeOffset now)
    {
        LastEvaluatedAt = now;
    }

    public void MarkTriggered(DateTimeOffset now, long latestRunSequence)
    {
        LastTriggeredAt = now;
        LastTriggeredRunSequence = latestRunSequence;
        LastEvaluatedAt = now;
    }

    private static void Validate(int threshold, int windowMinutes, int cooldownMinutes)
    {
        if (threshold is < 1 or > 100_000)
        {
            throw new ArgumentOutOfRangeException(nameof(threshold), "Threshold must be between 1 and 100,000.");
        }

        if (windowMinutes is < 1 or > 10_080)
        {
            throw new ArgumentOutOfRangeException(nameof(windowMinutes), "Window must be between 1 minute and 7 days.");
        }

        if (cooldownMinutes is < 0 or > 10_080)
        {
            throw new ArgumentOutOfRangeException(nameof(cooldownMinutes), "Cooldown must be between 0 minutes and 7 days.");
        }
    }

    private static string NormalizeName(string name)
    {
        var normalized = name?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("Alert rule name is required.", nameof(name));
        }

        return normalized;
    }
}

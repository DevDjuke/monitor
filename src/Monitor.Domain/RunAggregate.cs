namespace Monitor.Domain;

public sealed class RunAggregate
{
    private RunAggregate() { }

    public Guid Id { get; private set; }
    public DateTimeOffset BucketStart { get; private set; }
    public Guid ComponentId { get; private set; }
    public string ComponentName { get; private set; } = string.Empty;
    public string Environment { get; private set; } = string.Empty;
    public string Model { get; private set; } = string.Empty;
    public long TotalRuns { get; private set; }
    public long SuccessRuns { get; private set; }
    public long FailedRuns { get; private set; }
    public long CancelledRuns { get; private set; }
    public long InputTokens { get; private set; }
    public long OutputTokens { get; private set; }
    public double CostUsd { get; private set; }
    public long TotalDurationMs { get; private set; }
    public long MinDurationMs { get; private set; }
    public long MaxDurationMs { get; private set; }
    public DateTimeOffset FirstStartedAt { get; private set; }
    public DateTimeOffset LastStartedAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static RunAggregate Create(
        DateTimeOffset bucketStart,
        Guid componentId,
        string componentName,
        string environment,
        string model,
        RunAggregateDelta delta,
        DateTimeOffset now)
    {
        var aggregate = new RunAggregate
        {
            Id = Guid.NewGuid(),
            BucketStart = bucketStart,
            ComponentId = componentId,
            ComponentName = componentName,
            Environment = environment,
            Model = model,
            CreatedAt = now,
            UpdatedAt = now
        };

        aggregate.Apply(delta, componentName, environment, now);
        return aggregate;
    }

    public void Apply(
        RunAggregateDelta delta,
        string componentName,
        string environment,
        DateTimeOffset now)
    {
        ComponentName = componentName;
        Environment = environment;
        TotalRuns += delta.TotalRuns;
        SuccessRuns += delta.SuccessRuns;
        FailedRuns += delta.FailedRuns;
        CancelledRuns += delta.CancelledRuns;
        InputTokens += delta.InputTokens;
        OutputTokens += delta.OutputTokens;
        CostUsd += delta.CostUsd;
        TotalDurationMs += delta.TotalDurationMs;

        if (delta.TotalRuns > 0)
        {
            MinDurationMs = TotalRuns == delta.TotalRuns
                ? delta.MinDurationMs
                : Math.Min(MinDurationMs, delta.MinDurationMs);
            MaxDurationMs = Math.Max(MaxDurationMs, delta.MaxDurationMs);
            FirstStartedAt = FirstStartedAt == default
                ? delta.FirstStartedAt
                : (delta.FirstStartedAt < FirstStartedAt ? delta.FirstStartedAt : FirstStartedAt);
            LastStartedAt = LastStartedAt == default
                ? delta.LastStartedAt
                : (delta.LastStartedAt > LastStartedAt ? delta.LastStartedAt : LastStartedAt);
        }

        UpdatedAt = now;
    }
}

public sealed record RunAggregateDelta(
    long TotalRuns,
    long SuccessRuns,
    long FailedRuns,
    long CancelledRuns,
    long InputTokens,
    long OutputTokens,
    double CostUsd,
    long TotalDurationMs,
    long MinDurationMs,
    long MaxDurationMs,
    DateTimeOffset FirstStartedAt,
    DateTimeOffset LastStartedAt);

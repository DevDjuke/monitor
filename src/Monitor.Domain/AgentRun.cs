namespace Monitor.Domain;

public sealed class AgentRun
{
    private AgentRun() { }

    public Guid Id { get; private set; }
    public Guid ComponentId { get; private set; }
    public string? ExternalId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string? Trigger { get; private set; }
    public string? Model { get; private set; }
    public RunStatus Status { get; private set; }
    public DateTimeOffset StartedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public long InputTokens { get; private set; }
    public long OutputTokens { get; private set; }
    public double CostUsd { get; private set; }
    public string? InputJson { get; private set; }
    public string? OutputJson { get; private set; }
    public string? Error { get; private set; }

    public MonitoredComponent Component { get; private set; } = null!;
    public ICollection<TraceSpan> Spans { get; private set; } = new List<TraceSpan>();

    public static AgentRun Start(
        Guid componentId,
        string name,
        string? externalId,
        string? trigger,
        string? model,
        string? inputJson,
        DateTimeOffset now)
    {
        return new AgentRun
        {
            Id = Guid.NewGuid(),
            ComponentId = componentId,
            ExternalId = externalId,
            Name = name,
            Trigger = trigger,
            Model = model,
            InputJson = inputJson,
            Status = RunStatus.Running,
            StartedAt = now
        };
    }

    public void Complete(
        RunStatus status,
        long inputTokens,
        long outputTokens,
        double costUsd,
        string? outputJson,
        string? error,
        DateTimeOffset now)
    {
        if (status == RunStatus.Running)
        {
            throw new ArgumentException("A completed run must have a terminal status.", nameof(status));
        }

        Status = status;
        InputTokens = inputTokens;
        OutputTokens = outputTokens;
        CostUsd = costUsd;
        OutputJson = outputJson;
        Error = error;
        CompletedAt = now;
    }
}

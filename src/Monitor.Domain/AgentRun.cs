namespace Monitor.Domain;

public sealed class AgentRun
{
    private AgentRun() { }

    public Guid Id { get; private set; }
    public long Sequence { get; private set; }
    public Guid ComponentId { get; private set; }
    public string? ExternalId { get; private set; }
    public string? TraceId { get; private set; }
    public Guid? FailureGroupId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string? Trigger { get; private set; }
    public string? Model { get; private set; }
    public RunStatus Status { get; private set; }
    public DateTimeOffset StartedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public DateTimeOffset? AggregatedAt { get; private set; }
    public long InputTokens { get; private set; }
    public long OutputTokens { get; private set; }
    public double CostUsd { get; private set; }
    public string? InputJson { get; private set; }
    public string? OutputJson { get; private set; }
    public string? Error { get; private set; }

    public MonitoredComponent Component { get; private set; } = null!;
    public FailureGroup? FailureGroup { get; private set; }
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

    public static AgentRun StartOtlp(
        Guid componentId,
        string traceId,
        string provisionalName,
        DateTimeOffset startedAt)
    {
        return new AgentRun
        {
            Id = Guid.NewGuid(),
            ComponentId = componentId,
            ExternalId = traceId,
            TraceId = traceId,
            Name = provisionalName,
            Trigger = "OTLP",
            Status = RunStatus.Running,
            StartedAt = startedAt
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

    public void ApplyOtlpRoot(
        string name,
        RunStatus status,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt,
        string? model,
        long inputTokens,
        long outputTokens,
        double costUsd,
        string? error)
    {
        if (status == RunStatus.Running)
        {
            throw new ArgumentException("A completed OTLP root span must have a terminal run status.", nameof(status));
        }

        Name = string.IsNullOrWhiteSpace(name) ? Name : name.Trim();
        Status = status;
        StartedAt = startedAt;
        CompletedAt = completedAt;
        Model = string.IsNullOrWhiteSpace(model) ? Model : model.Trim();
        InputTokens = inputTokens;
        OutputTokens = outputTokens;
        CostUsd = costUsd;
        Error = error;
    }

    public void UpdateOtlpProvisionalStart(DateTimeOffset startedAt, string? model)
    {
        if (Status != RunStatus.Running)
        {
            return;
        }

        if (startedAt < StartedAt)
        {
            StartedAt = startedAt;
        }

        if (string.IsNullOrWhiteSpace(Model) && !string.IsNullOrWhiteSpace(model))
        {
            Model = model.Trim();
        }
    }

    public void AssignFailureGroup(Guid failureGroupId)
    {
        if (Status is not (RunStatus.Failed or RunStatus.Cancelled))
        {
            throw new InvalidOperationException("Only failed or cancelled runs can belong to a failure group.");
        }

        FailureGroupId ??= failureGroupId;
    }

    public void MarkAggregated(DateTimeOffset now)
    {
        if (Status == RunStatus.Running || CompletedAt is null)
        {
            throw new InvalidOperationException("Only terminal runs can be aggregated.");
        }

        AggregatedAt ??= now;
    }
}

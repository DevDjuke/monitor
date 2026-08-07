namespace Monitor.Domain;

public sealed class TraceSpan
{
    private TraceSpan() { }

    public Guid Id { get; private set; }
    public Guid RunId { get; private set; }
    public Guid? ParentSpanId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public SpanKind Kind { get; private set; }
    public SpanStatus Status { get; private set; }
    public DateTimeOffset StartedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public string? AttributesJson { get; private set; }
    public string? Error { get; private set; }

    public AgentRun Run { get; private set; } = null!;

    public static TraceSpan Create(
        Guid runId,
        Guid? parentSpanId,
        string name,
        SpanKind kind,
        SpanStatus status,
        DateTimeOffset startedAt,
        DateTimeOffset? completedAt,
        string? attributesJson,
        string? error)
    {
        return new TraceSpan
        {
            Id = Guid.NewGuid(),
            RunId = runId,
            ParentSpanId = parentSpanId,
            Name = name,
            Kind = kind,
            Status = status,
            StartedAt = startedAt,
            CompletedAt = completedAt,
            AttributesJson = attributesJson,
            Error = error
        };
    }
}

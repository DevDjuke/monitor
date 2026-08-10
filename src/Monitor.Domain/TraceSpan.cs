namespace Monitor.Domain;

public sealed class TraceSpan
{
    private TraceSpan() { }

    public Guid Id { get; private set; }
    public Guid RunId { get; private set; }
    public Guid? ParentSpanId { get; private set; }
    public string? ExternalSpanId { get; private set; }
    public string? ExternalParentSpanId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public SpanKind Kind { get; private set; }
    public SpanStatus Status { get; private set; }
    public DateTimeOffset StartedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public string? AttributesJson { get; private set; }
    public string? Error { get; private set; }
    public string? ErrorType { get; private set; }
    public int? HttpStatusCode { get; private set; }
    public string? Model { get; private set; }
    public long InputTokens { get; private set; }
    public long OutputTokens { get; private set; }
    public double CostUsd { get; private set; }

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

    public static TraceSpan CreateOtlp(
        Guid runId,
        string externalSpanId,
        string? externalParentSpanId,
        string name,
        SpanKind kind,
        SpanStatus status,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt,
        string? attributesJson,
        string? error,
        string? errorType,
        int? httpStatusCode,
        string? model,
        long inputTokens,
        long outputTokens,
        double costUsd)
    {
        return new TraceSpan
        {
            Id = Guid.NewGuid(),
            RunId = runId,
            ExternalSpanId = externalSpanId,
            ExternalParentSpanId = externalParentSpanId,
            Name = name,
            Kind = kind,
            Status = status,
            StartedAt = startedAt,
            CompletedAt = completedAt,
            AttributesJson = attributesJson,
            Error = error,
            ErrorType = errorType,
            HttpStatusCode = httpStatusCode,
            Model = model,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            CostUsd = costUsd
        };
    }

    public void ResolveParent(Guid parentSpanId)
    {
        ParentSpanId ??= parentSpanId;
    }
}

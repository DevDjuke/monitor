namespace Monitor.Domain;

public sealed class LogEvent
{
    private LogEvent() { }

    public Guid Id { get; private set; }
    public Guid ComponentId { get; private set; }
    public Guid? RunId { get; private set; }
    public Guid? SpanId { get; private set; }
    public string? ExternalTraceId { get; private set; }
    public string? ExternalSpanId { get; private set; }
    public string? ExternalRecordId { get; private set; }
    public string? DedupeKey { get; private set; }
    public DateTimeOffset Timestamp { get; private set; }
    public DateTimeOffset ObservedAt { get; private set; }
    public LogEventLevel Level { get; private set; }
    public string? SeverityText { get; private set; }
    public string? EventName { get; private set; }
    public string Message { get; private set; } = string.Empty;
    public string? MessageTemplate { get; private set; }
    public string? PropertiesJson { get; private set; }
    public string? ExceptionType { get; private set; }
    public string? ExceptionMessage { get; private set; }
    public string? ExceptionStackTrace { get; private set; }
    public string? Source { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public MonitoredComponent Component { get; private set; } = null!;
    public AgentRun? Run { get; private set; }
    public TraceSpan? Span { get; private set; }

    public static LogEvent Create(
        Guid componentId,
        Guid? runId,
        Guid? spanId,
        LogEventLevel level,
        DateTimeOffset timestamp,
        DateTimeOffset observedAt,
        string message,
        string? messageTemplate,
        string? propertiesJson,
        string? exceptionType,
        string? exceptionMessage,
        string? exceptionStackTrace,
        string? source,
        string? eventName = null)
    {
        return new LogEvent
        {
            Id = Guid.NewGuid(),
            ComponentId = componentId,
            RunId = runId,
            SpanId = spanId,
            Timestamp = timestamp,
            ObservedAt = observedAt,
            Level = level,
            Message = NormalizeRequired(message, 4000, "(empty log record)"),
            MessageTemplate = Normalize(messageTemplate, 4000),
            PropertiesJson = propertiesJson,
            ExceptionType = Normalize(exceptionType, 240),
            ExceptionMessage = Normalize(exceptionMessage, 4000),
            ExceptionStackTrace = exceptionStackTrace,
            Source = Normalize(source, 240),
            EventName = Normalize(eventName, 256),
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    public static LogEvent CreateOtlp(
        Guid componentId,
        Guid? runId,
        Guid? spanId,
        string? externalTraceId,
        string? externalSpanId,
        string? externalRecordId,
        string dedupeKey,
        LogEventLevel level,
        string? severityText,
        DateTimeOffset timestamp,
        DateTimeOffset observedAt,
        string message,
        string? messageTemplate,
        string? propertiesJson,
        string? exceptionType,
        string? exceptionMessage,
        string? exceptionStackTrace,
        string? source,
        string? eventName,
        DateTimeOffset now)
    {
        return new LogEvent
        {
            Id = Guid.NewGuid(),
            ComponentId = componentId,
            RunId = runId,
            SpanId = spanId,
            ExternalTraceId = Normalize(externalTraceId, 32),
            ExternalSpanId = Normalize(externalSpanId, 16),
            ExternalRecordId = Normalize(externalRecordId, 200),
            DedupeKey = NormalizeRequired(dedupeKey, 64, dedupeKey),
            Timestamp = timestamp,
            ObservedAt = observedAt,
            Level = level,
            SeverityText = Normalize(severityText, 80),
            EventName = Normalize(eventName, 256),
            Message = NormalizeRequired(message, 4000, "(empty log record)"),
            MessageTemplate = Normalize(messageTemplate, 4000),
            PropertiesJson = propertiesJson,
            ExceptionType = Normalize(exceptionType, 240),
            ExceptionMessage = Normalize(exceptionMessage, 4000),
            ExceptionStackTrace = exceptionStackTrace,
            Source = Normalize(source, 240),
            CreatedAt = now
        };
    }

    public void Correlate(Guid runId, Guid? spanId)
    {
        RunId ??= runId;
        if (spanId.HasValue)
        {
            SpanId ??= spanId;
        }
    }

    private static string? Normalize(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        return normalized[..Math.Min(normalized.Length, maxLength)];
    }

    private static string NormalizeRequired(string? value, int maxLength, string fallback)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        return normalized[..Math.Min(normalized.Length, maxLength)];
    }
}

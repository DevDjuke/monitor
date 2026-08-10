namespace Monitor.Domain;

public enum ComponentType
{
    Agent,
    McpServer,
    Bot,
    Workflow,
    Service,
    ScheduledJob,
    Scraper,
    Other
}

public enum ComponentStatus
{
    Unknown,
    Healthy,
    Degraded,
    Offline
}

public enum RunStatus
{
    Running,
    Success,
    Failed,
    Cancelled
}

public enum SpanKind
{
    Agent,
    Model,
    Tool,
    Http,
    Internal
}

public enum SpanStatus
{
    Running,
    Success,
    Failed
}

public enum FailureCategory
{
    Unknown,
    Timeout,
    RateLimit,
    Authentication,
    Authorization,
    Network,
    Http,
    Database,
    Validation,
    Serialization,
    ModelProvider,
    Tool,
    Dependency,
    Cancellation,
    Internal
}

namespace Monitor.Domain;

public sealed class MonitoredComponent
{
    private MonitoredComponent() { }

    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string Slug { get; private set; } = string.Empty;
    public ComponentType Type { get; private set; }
    public string Environment { get; private set; } = string.Empty;
    public string? Version { get; private set; }
    public bool Enabled { get; private set; }
    public ComponentStatus Status { get; private set; }
    public DateTimeOffset? LastHeartbeatAt { get; private set; }
    public DateTimeOffset? LastRunAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public ICollection<AgentRun> Runs { get; private set; } = new List<AgentRun>();
    public ICollection<ComponentIngestionCredential> IngestionCredentials { get; private set; } = new List<ComponentIngestionCredential>();

    public static MonitoredComponent Create(
        string name,
        string slug,
        ComponentType type,
        string environment,
        string? version,
        DateTimeOffset now)
    {
        return new MonitoredComponent
        {
            Id = Guid.NewGuid(),
            Name = name,
            Slug = slug,
            Type = type,
            Environment = environment,
            Version = version,
            Enabled = true,
            Status = ComponentStatus.Unknown,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    public void UpdateRegistration(string name, ComponentType type, string? version, DateTimeOffset now)
    {
        Name = name;
        Type = type;
        Version = version;
        UpdatedAt = now;
    }

    public void Heartbeat(DateTimeOffset now)
    {
        LastHeartbeatAt = now;
        Status = ComponentStatus.Healthy;
        UpdatedAt = now;
    }

    public void MarkRunStarted(DateTimeOffset now)
    {
        LastRunAt = now;
        UpdatedAt = now;
    }

    public ComponentStatus GetEffectiveStatus(DateTimeOffset now, TimeSpan heartbeatTimeout)
    {
        if (!Enabled)
        {
            return ComponentStatus.Offline;
        }

        if (LastHeartbeatAt is null)
        {
            return Status;
        }

        return LastHeartbeatAt < now - heartbeatTimeout
            ? ComponentStatus.Offline
            : Status;
    }
}

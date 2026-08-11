namespace Monitor.Domain;

public enum ComponentControlState
{
    Active = 0,
    Paused = 1,
    Disabled = 2
}

public enum ComponentCommandType
{
    Pause = 1,
    Resume = 2,
    Disable = 3,
    Enable = 4,
    Restart = 5,
    KillRun = 6,
    RefreshConfiguration = 7
}

public enum ComponentCommandStatus
{
    Pending = 1,
    Leased = 2,
    Succeeded = 3,
    Failed = 4,
    Rejected = 5,
    Cancelled = 6,
    Expired = 7
}

public enum ComponentCommandOutcome
{
    Succeeded = 1,
    Failed = 2,
    Rejected = 3
}

public sealed class ComponentCommand
{
    private ComponentCommand() { }

    public Guid Id { get; private set; }
    public Guid ComponentId { get; private set; }
    public ComponentCommandType Type { get; private set; }
    public ComponentCommandStatus Status { get; private set; }
    public Guid? TargetRunId { get; private set; }
    public string? PayloadJson { get; private set; }
    public string? RequestedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset AvailableAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public Guid? LeaseToken { get; private set; }
    public DateTimeOffset? LeasedAt { get; private set; }
    public DateTimeOffset? LeaseExpiresAt { get; private set; }
    public int DeliveryAttempts { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public string? ResultJson { get; private set; }
    public string? Error { get; private set; }
    public DateTimeOffset? CancelledAt { get; private set; }
    public string? CancelledBy { get; private set; }

    public MonitoredComponent Component { get; private set; } = null!;

    public bool IsTerminal => Status is
        ComponentCommandStatus.Succeeded or
        ComponentCommandStatus.Failed or
        ComponentCommandStatus.Rejected or
        ComponentCommandStatus.Cancelled or
        ComponentCommandStatus.Expired;

    public static ComponentCommand Create(
        Guid componentId,
        ComponentCommandType type,
        Guid? targetRunId,
        string? payloadJson,
        string? requestedBy,
        DateTimeOffset now,
        DateTimeOffset expiresAt)
    {
        if (expiresAt <= now)
        {
            throw new ArgumentOutOfRangeException(nameof(expiresAt), "Command expiry must be in the future.");
        }

        if (type == ComponentCommandType.KillRun && targetRunId is null)
        {
            throw new ArgumentException("KillRun requires a target run.", nameof(targetRunId));
        }

        if (type != ComponentCommandType.KillRun && targetRunId is not null)
        {
            throw new ArgumentException("Only KillRun may target a run.", nameof(targetRunId));
        }

        return new ComponentCommand
        {
            Id = Guid.NewGuid(),
            ComponentId = componentId,
            Type = type,
            Status = ComponentCommandStatus.Pending,
            TargetRunId = targetRunId,
            PayloadJson = Normalize(payloadJson),
            RequestedBy = Normalize(requestedBy),
            CreatedAt = now,
            AvailableAt = now,
            ExpiresAt = expiresAt
        };
    }

    public bool CanLease(DateTimeOffset now, int maxDeliveryAttempts)
    {
        if (IsTerminal || AvailableAt > now || ExpiresAt <= now || DeliveryAttempts >= maxDeliveryAttempts)
        {
            return false;
        }

        return Status == ComponentCommandStatus.Pending ||
               (Status == ComponentCommandStatus.Leased && LeaseExpiresAt <= now);
    }

    public Guid Lease(DateTimeOffset now, TimeSpan leaseDuration, int maxDeliveryAttempts)
    {
        if (!CanLease(now, maxDeliveryAttempts))
        {
            throw new InvalidOperationException("The command is not currently leaseable.");
        }

        var token = Guid.NewGuid();
        Status = ComponentCommandStatus.Leased;
        LeaseToken = token;
        LeasedAt = now;
        LeaseExpiresAt = now.Add(leaseDuration);
        DeliveryAttempts++;
        return token;
    }

    public void Complete(
        Guid leaseToken,
        ComponentCommandOutcome outcome,
        string? resultJson,
        string? error,
        DateTimeOffset now)
    {
        if (IsTerminal)
        {
            return;
        }

        if (Status != ComponentCommandStatus.Leased || LeaseToken != leaseToken)
        {
            throw new InvalidOperationException("The command lease is no longer current.");
        }

        Status = outcome switch
        {
            ComponentCommandOutcome.Succeeded => ComponentCommandStatus.Succeeded,
            ComponentCommandOutcome.Failed => ComponentCommandStatus.Failed,
            ComponentCommandOutcome.Rejected => ComponentCommandStatus.Rejected,
            _ => throw new ArgumentOutOfRangeException(nameof(outcome))
        };

        CompletedAt = now;
        ResultJson = Normalize(resultJson);
        Error = Normalize(error);
        LeaseExpiresAt = null;
    }

    public void Cancel(string? cancelledBy, DateTimeOffset now)
    {
        if (IsTerminal)
        {
            return;
        }

        Status = ComponentCommandStatus.Cancelled;
        CancelledAt = now;
        CancelledBy = Normalize(cancelledBy);
        LeaseExpiresAt = null;
    }

    public void Expire(string reason, DateTimeOffset now)
    {
        if (IsTerminal)
        {
            return;
        }

        Status = ComponentCommandStatus.Expired;
        CompletedAt = now;
        Error = Normalize(reason) ?? "Command expired.";
        LeaseExpiresAt = null;
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

namespace Monitor.Domain;

public sealed class UsageBudgetAlertEvent
{
    private UsageBudgetAlertEvent() { }

    public Guid Id { get; private set; }
    public Guid UsageBudgetId { get; private set; }
    public UsageBudgetAlertLevel Level { get; private set; }
    public DateTimeOffset TriggeredAt { get; private set; }
    public DateTimeOffset PeriodStart { get; private set; }
    public DateTimeOffset PeriodEnd { get; private set; }
    public double ObservedCostUsd { get; private set; }
    public long ObservedTokens { get; private set; }
    public double UtilizationPercent { get; private set; }
    public double? CostLimitUsd { get; private set; }
    public long? TokenLimit { get; private set; }
    public int WarningPercent { get; private set; }
    public int CriticalPercent { get; private set; }
    public DateTimeOffset? AcknowledgedAt { get; private set; }
    public string? AcknowledgedBy { get; private set; }

    public UsageBudget UsageBudget { get; private set; } = null!;
    public ICollection<UsageBudgetAlertDelivery> Deliveries { get; private set; } = new List<UsageBudgetAlertDelivery>();

    public static UsageBudgetAlertEvent Create(
        UsageBudget budget,
        UsageBudgetAlertLevel level,
        DateTimeOffset periodStart,
        DateTimeOffset periodEnd,
        double observedCostUsd,
        long observedTokens,
        double utilizationPercent,
        DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        UsageBudgetId = budget.Id,
        Level = level,
        TriggeredAt = now,
        PeriodStart = periodStart,
        PeriodEnd = periodEnd,
        ObservedCostUsd = observedCostUsd,
        ObservedTokens = observedTokens,
        UtilizationPercent = utilizationPercent,
        CostLimitUsd = budget.CostLimitUsd,
        TokenLimit = budget.TokenLimit,
        WarningPercent = budget.WarningPercent,
        CriticalPercent = budget.CriticalPercent
    };

    public void Acknowledge(string? userName, DateTimeOffset now)
    {
        if (AcknowledgedAt is not null) return;
        AcknowledgedAt = now;
        AcknowledgedBy = string.IsNullOrWhiteSpace(userName) ? null : userName.Trim();
    }
}

public sealed class UsageBudgetAlertDelivery
{
    private UsageBudgetAlertDelivery() { }

    public Guid Id { get; private set; }
    public Guid BudgetAlertEventId { get; private set; }
    public Guid DestinationId { get; private set; }
    public AlertDeliveryStatus Status { get; private set; }
    public int AttemptCount { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset NextAttemptAt { get; private set; }
    public DateTimeOffset? LastAttemptAt { get; private set; }
    public DateTimeOffset? DeliveredAt { get; private set; }
    public int? ResponseStatusCode { get; private set; }
    public string? LastError { get; private set; }

    public UsageBudgetAlertEvent BudgetAlertEvent { get; private set; } = null!;
    public AlertDeliveryDestination Destination { get; private set; } = null!;

    public static UsageBudgetAlertDelivery Create(
        UsageBudgetAlertEvent alertEvent,
        AlertDeliveryDestination destination,
        DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        BudgetAlertEventId = alertEvent.Id,
        DestinationId = destination.Id,
        Status = AlertDeliveryStatus.Pending,
        CreatedAt = now,
        NextAttemptAt = now
    };

    public void MarkDelivered(int? responseStatusCode, DateTimeOffset now)
    {
        AttemptCount++;
        LastAttemptAt = now;
        DeliveredAt = now;
        ResponseStatusCode = responseStatusCode;
        LastError = null;
        Status = AlertDeliveryStatus.Delivered;
    }

    public void MarkFailed(string error, int? responseStatusCode, DateTimeOffset now, DateTimeOffset? nextAttemptAt)
    {
        AttemptCount++;
        LastAttemptAt = now;
        ResponseStatusCode = responseStatusCode;
        LastError = string.IsNullOrWhiteSpace(error) ? "Delivery failed." : error.Trim();
        if (nextAttemptAt is null)
        {
            Status = AlertDeliveryStatus.DeadLetter;
            return;
        }
        Status = AlertDeliveryStatus.RetryScheduled;
        NextAttemptAt = nextAttemptAt.Value;
    }

    public void Requeue(DateTimeOffset now)
    {
        if (Status == AlertDeliveryStatus.Delivered) return;
        Status = AlertDeliveryStatus.Pending;
        NextAttemptAt = now;
        LastError = null;
    }
}

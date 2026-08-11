namespace Monitor.Domain;

public enum UsageBudgetPeriod
{
    Daily = 1,
    Monthly = 2
}

public enum UsageBudgetAlertLevel
{
    Warning = 1,
    Critical = 2
}

public sealed class UsageBudget
{
    private UsageBudget() { }

    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public Guid? ComponentId { get; private set; }
    public string? Environment { get; private set; }
    public string? Model { get; private set; }
    public UsageBudgetPeriod Period { get; private set; }
    public double? CostLimitUsd { get; private set; }
    public long? TokenLimit { get; private set; }
    public int WarningPercent { get; private set; }
    public int CriticalPercent { get; private set; }
    public bool Enabled { get; private set; }
    public bool IsDeleted { get; private set; }
    public bool DeliverToAllEnabledDestinations { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? DeletedAt { get; private set; }
    public DateTimeOffset? LastEvaluatedAt { get; private set; }
    public DateTimeOffset? CurrentPeriodStart { get; private set; }
    public UsageBudgetAlertLevel? LastTriggeredLevel { get; private set; }
    public double LastObservedCostUsd { get; private set; }
    public long LastObservedTokens { get; private set; }

    public MonitoredComponent? Component { get; private set; }
    public ICollection<UsageBudgetDestination> DestinationAssignments { get; private set; } = new List<UsageBudgetDestination>();
    public ICollection<UsageBudgetAlertEvent> AlertEvents { get; private set; } = new List<UsageBudgetAlertEvent>();

    public static UsageBudget Create(
        string name,
        Guid? componentId,
        string? environment,
        string? model,
        UsageBudgetPeriod period,
        double? costLimitUsd,
        long? tokenLimit,
        int warningPercent,
        int criticalPercent,
        DateTimeOffset now)
    {
        var budget = new UsageBudget
        {
            Id = Guid.NewGuid(),
            CreatedAt = now,
            Enabled = true,
            DeliverToAllEnabledDestinations = true
        };
        budget.Update(name, componentId, environment, model, period, costLimitUsd, tokenLimit, warningPercent, criticalPercent, now);
        return budget;
    }

    public void Update(
        string name,
        Guid? componentId,
        string? environment,
        string? model,
        UsageBudgetPeriod period,
        double? costLimitUsd,
        long? tokenLimit,
        int warningPercent,
        int criticalPercent,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Budget name is required.", nameof(name));
        if (costLimitUsd is null && tokenLimit is null) throw new ArgumentException("Configure a cost limit, token limit, or both.");
        if (costLimitUsd is <= 0) throw new ArgumentOutOfRangeException(nameof(costLimitUsd), "Cost limit must be greater than zero.");
        if (tokenLimit is <= 0) throw new ArgumentOutOfRangeException(nameof(tokenLimit), "Token limit must be greater than zero.");
        if (warningPercent is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(warningPercent));
        if (criticalPercent is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(criticalPercent));
        if (warningPercent >= criticalPercent) throw new ArgumentException("Warning threshold must be lower than the critical threshold.");

        Name = name.Trim();
        ComponentId = componentId;
        Environment = Normalize(environment);
        Model = Normalize(model);
        Period = period;
        CostLimitUsd = costLimitUsd;
        TokenLimit = tokenLimit;
        WarningPercent = warningPercent;
        CriticalPercent = criticalPercent;
        UpdatedAt = now;
    }

    public void SetEnabled(bool enabled, DateTimeOffset now)
    {
        if (IsDeleted && enabled) throw new InvalidOperationException("Deleted budgets cannot be enabled.");
        Enabled = enabled;
        UpdatedAt = now;
    }

    public void SetDeliveryScope(bool allEnabledDestinations, DateTimeOffset now)
    {
        DeliverToAllEnabledDestinations = allEnabledDestinations;
        UpdatedAt = now;
    }

    public void Delete(DateTimeOffset now)
    {
        if (IsDeleted) return;
        IsDeleted = true;
        Enabled = false;
        DeletedAt = now;
        UpdatedAt = now;
    }

    public void MarkEvaluated(
        DateTimeOffset periodStart,
        double observedCostUsd,
        long observedTokens,
        DateTimeOffset now)
    {
        if (CurrentPeriodStart != periodStart)
        {
            CurrentPeriodStart = periodStart;
            LastTriggeredLevel = null;
        }

        LastObservedCostUsd = observedCostUsd;
        LastObservedTokens = observedTokens;
        LastEvaluatedAt = now;
    }

    public void MarkTriggered(UsageBudgetAlertLevel level, DateTimeOffset now)
    {
        if (LastTriggeredLevel is null || level > LastTriggeredLevel)
        {
            LastTriggeredLevel = level;
        }
        UpdatedAt = now;
    }

    public double GetUtilizationPercent(double costUsd, long tokens)
    {
        var costPercent = CostLimitUsd is > 0 ? costUsd / CostLimitUsd.Value * 100d : 0d;
        var tokenPercent = TokenLimit is > 0 ? (double)tokens / TokenLimit.Value * 100d : 0d;
        return Math.Max(costPercent, tokenPercent);
    }

    public UsageBudgetAlertLevel? GetAlertLevel(double utilizationPercent) =>
        utilizationPercent >= CriticalPercent
            ? UsageBudgetAlertLevel.Critical
            : utilizationPercent >= WarningPercent
                ? UsageBudgetAlertLevel.Warning
                : null;

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class UsageBudgetDestination
{
    private UsageBudgetDestination() { }

    public Guid UsageBudgetId { get; private set; }
    public Guid DestinationId { get; private set; }
    public UsageBudget UsageBudget { get; private set; } = null!;
    public AlertDeliveryDestination Destination { get; private set; } = null!;

    public static UsageBudgetDestination Create(Guid usageBudgetId, Guid destinationId) => new()
    {
        UsageBudgetId = usageBudgetId,
        DestinationId = destinationId
    };
}

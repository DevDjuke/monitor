namespace Monitor.Infrastructure.Failures;

public sealed class FailureAlertingOptions
{
    public const string SectionName = "FailureAlerting";

    public bool Enabled { get; set; } = true;
    public int InitialDelaySeconds { get; set; } = 10;
    public int SweepIntervalSeconds { get; set; } = 30;

    public TimeSpan InitialDelay => TimeSpan.FromSeconds(Math.Clamp(InitialDelaySeconds, 0, 300));
    public TimeSpan SweepInterval => TimeSpan.FromSeconds(Math.Clamp(SweepIntervalSeconds, 1, 3600));
}

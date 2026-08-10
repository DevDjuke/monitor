namespace Monitor.Web.Services;

public sealed class AlertDeliveryOptions
{
    public const string SectionName = "AlertDelivery";

    public bool Enabled { get; set; } = true;
    public int InitialDelaySeconds { get; set; } = 5;
    public int SweepIntervalSeconds { get; set; } = 10;
    public int BatchSize { get; set; } = 25;
    public int RequestTimeoutSeconds { get; set; } = 10;
    public int MaxAttempts { get; set; } = 8;
    public int BaseRetrySeconds { get; set; } = 30;
    public int MaxRetrySeconds { get; set; } = 3600;
    public int LeaseSeconds { get; set; } = 300;
}

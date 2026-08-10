namespace Monitor.Web.Services;

public sealed class AlertDeliveryOptions
{
    public const string SectionName = "AlertDelivery";

    public bool Enabled { get; set; } = true;
    public int SweepIntervalSeconds { get; set; } = 5;
    public int BatchSize { get; set; } = 50;
    public int MaxAttempts { get; set; } = 6;
    public int BaseRetrySeconds { get; set; } = 10;
    public int MaxRetryMinutes { get; set; } = 30;
    public int RequestTimeoutSeconds { get; set; } = 10;
}

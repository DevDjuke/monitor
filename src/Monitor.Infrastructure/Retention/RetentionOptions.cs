namespace Monitor.Infrastructure.Retention;

public sealed class RetentionOptions
{
    public const string SectionName = "Retention";

    public bool Enabled { get; set; } = true;
    public int AggregationDelayMinutes { get; set; } = 5;
    public int SuccessfulRunDetailDays { get; set; } = 30;
    public int SweepIntervalMinutes { get; set; } = 15;
    public int BatchSize { get; set; } = 1000;
    public int MaxBatchesPerSweep { get; set; } = 20;

    public TimeSpan AggregationDelay => TimeSpan.FromMinutes(Math.Max(0, AggregationDelayMinutes));
    public TimeSpan SuccessfulRunDetailRetention => TimeSpan.FromDays(Math.Max(1, SuccessfulRunDetailDays));
    public TimeSpan SweepInterval => TimeSpan.FromMinutes(Math.Max(1, SweepIntervalMinutes));
    public int SafeBatchSize => Math.Clamp(BatchSize, 50, 5000);
    public int SafeMaxBatchesPerSweep => Math.Clamp(MaxBatchesPerSweep, 1, 100);
}

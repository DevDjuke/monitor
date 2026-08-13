using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Monitor.Domain;

namespace Monitor.Infrastructure.Retention;

public sealed class MetricRetentionService(
    MonitorDbContext db,
    IOptions<RetentionOptions> options,
    ILogger<MetricRetentionService> logger)
{
    private readonly RetentionOptions _options = options.Value;

    public async Task<int> SweepAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return 0;
        }

        var cutoff = DateTimeOffset.UtcNow - _options.MetricDetailRetention;
        var totalPurged = 0;

        for (var batchNumber = 0; batchNumber < _options.SafeMaxBatchesPerSweep; batchNumber++)
        {
            var ids = await db.Set<MetricPoint>()
                .AsNoTracking()
                .Where(x => x.Timestamp <= cutoff)
                .OrderBy(x => x.Timestamp)
                .Select(x => x.Id)
                .Take(_options.SafeBatchSize)
                .ToListAsync(cancellationToken);

            if (ids.Count == 0)
            {
                break;
            }

            var deleted = await db.Set<MetricPoint>()
                .Where(x => ids.Contains(x.Id))
                .ExecuteDeleteAsync(cancellationToken);
            totalPurged += deleted;

            if (ids.Count < _options.SafeBatchSize)
            {
                break;
            }
        }

        if (totalPurged > 0)
        {
            logger.LogInformation(
                "Metric retention purged {PurgedMetricPoints} points older than {MetricDetailDays} days.",
                totalPurged,
                _options.MetricDetailDays);
        }

        return totalPurged;
    }
}

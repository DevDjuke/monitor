using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Monitor.Domain;
using Monitor.Infrastructure;

namespace Monitor.Web.Services;

public sealed class AlertDeliveryWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<AlertDeliveryOptions> options,
    ILogger<AlertDeliveryWorker> logger) : BackgroundService
{
    private const string LockResource = "Monitor.AlertDelivery";
    private readonly AlertDeliveryOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            logger.LogInformation("Alert delivery is disabled.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Alert delivery sweep failed.");
            }

            await Task.Delay(
                TimeSpan.FromSeconds(Math.Clamp(_options.SweepIntervalSeconds, 1, 3600)),
                stoppingToken);
        }
    }

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MonitorDbContext>();
        var sender = scope.ServiceProvider.GetRequiredService<AlertDeliverySender>();

        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            var connection = db.Database.GetDbConnection();
            if (!await TryAcquireLockAsync(connection, cancellationToken)) return;

            try
            {
                var now = DateTimeOffset.UtcNow;
                var batchSize = Math.Clamp(_options.BatchSize, 1, 1000);

                var failureDeliveries = await db.AlertDeliveries
                    .Where(x =>
                        x.Destination.Enabled &&
                        (x.Status == AlertDeliveryStatus.Pending || x.Status == AlertDeliveryStatus.RetryScheduled) &&
                        x.NextAttemptAt <= now)
                    .Include(x => x.Destination)
                    .Include(x => x.AlertEvent).ThenInclude(x => x.AlertRule)
                    .Include(x => x.AlertEvent).ThenInclude(x => x.FailureGroup)
                    .OrderBy(x => x.NextAttemptAt)
                    .ThenBy(x => x.CreatedAt)
                    .Take(batchSize)
                    .ToListAsync(cancellationToken);

                foreach (var delivery in failureDeliveries)
                {
                    var attemptedAt = DateTimeOffset.UtcNow;
                    var result = await sender.SendAlertAsync(delivery, cancellationToken);
                    ApplyResult(delivery, result, attemptedAt);
                    delivery.Destination.RecordSuccessOrFailure(result, attemptedAt);
                    await db.SaveChangesAsync(cancellationToken);
                }

                // Each outbox gets its own bounded slice so a continuously full failure queue
                // cannot starve budget notifications (or vice versa) while the shared lock still
                // guarantees one dispatcher across Monitor nodes.
                var budgetDeliveries = await db.UsageBudgetAlertDeliveries
                    .Where(x =>
                        x.Destination.Enabled &&
                        (x.Status == AlertDeliveryStatus.Pending || x.Status == AlertDeliveryStatus.RetryScheduled) &&
                        x.NextAttemptAt <= now)
                    .Include(x => x.Destination)
                    .Include(x => x.BudgetAlertEvent).ThenInclude(x => x.UsageBudget)
                    .OrderBy(x => x.NextAttemptAt)
                    .ThenBy(x => x.CreatedAt)
                    .Take(batchSize)
                    .ToListAsync(cancellationToken);

                foreach (var delivery in budgetDeliveries)
                {
                    var attemptedAt = DateTimeOffset.UtcNow;
                    var result = await sender.SendBudgetAlertAsync(delivery, cancellationToken);
                    ApplyResult(delivery, result, attemptedAt);
                    delivery.Destination.RecordSuccessOrFailure(result, attemptedAt);
                    await db.SaveChangesAsync(cancellationToken);
                }

                var processed = failureDeliveries.Count + budgetDeliveries.Count;
                if (processed > 0)
                {
                    logger.LogInformation("Processed {DeliveryCount} alert delivery outbox item(s).", processed);
                }
            }
            finally
            {
                await ReleaseLockAsync(connection, cancellationToken);
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private void ApplyResult(AlertDelivery delivery, AlertSendResult result, DateTimeOffset attemptedAt)
    {
        if (result.Succeeded)
        {
            delivery.MarkDelivered(result.StatusCode, attemptedAt);
            return;
        }

        delivery.MarkFailed(
            result.Error ?? "Alert delivery failed.",
            result.StatusCode,
            attemptedAt,
            GetNextAttemptAt(delivery.AttemptCount, result.Retryable, attemptedAt));
    }

    private void ApplyResult(UsageBudgetAlertDelivery delivery, AlertSendResult result, DateTimeOffset attemptedAt)
    {
        if (result.Succeeded)
        {
            delivery.MarkDelivered(result.StatusCode, attemptedAt);
            return;
        }

        delivery.MarkFailed(
            result.Error ?? "Alert delivery failed.",
            result.StatusCode,
            attemptedAt,
            GetNextAttemptAt(delivery.AttemptCount, result.Retryable, attemptedAt));
    }

    private DateTimeOffset? GetNextAttemptAt(int attemptCount, bool retryable, DateTimeOffset attemptedAt)
    {
        var nextAttemptNumber = attemptCount + 1;
        var maxAttempts = Math.Clamp(_options.MaxAttempts, 1, 100);
        if (!retryable || nextAttemptNumber >= maxAttempts) return null;

        var exponent = Math.Min(attemptCount, 20);
        var baseSeconds = Math.Clamp(_options.BaseRetrySeconds, 1, 3600);
        var maxSeconds = Math.Clamp(_options.MaxRetryMinutes, 1, 24 * 60) * 60d;
        var delaySeconds = Math.Min(baseSeconds * Math.Pow(2, exponent), maxSeconds);
        return attemptedAt.AddSeconds(delaySeconds);
    }

    private static async Task<bool> TryAcquireLockAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock
                @Resource = @resource,
                @LockMode = 'Exclusive',
                @LockOwner = 'Session',
                @LockTimeout = 0;
            SELECT @result;
            """;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@resource";
        parameter.Value = LockResource;
        command.Parameters.Add(parameter);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) >= 0;
    }

    private static async Task ReleaseLockAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "EXEC sys.sp_releaseapplock @Resource = @resource, @LockOwner = 'Session';";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@resource";
        parameter.Value = LockResource;
        command.Parameters.Add(parameter);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

internal static class AlertDeliveryDestinationHealthExtensions
{
    public static void RecordSuccessOrFailure(this AlertDeliveryDestination destination, AlertSendResult result, DateTimeOffset attemptedAt)
    {
        if (result.Succeeded) destination.RecordSuccess(attemptedAt);
        else destination.RecordFailure(result.Error ?? "Alert delivery failed.", attemptedAt);
    }
}

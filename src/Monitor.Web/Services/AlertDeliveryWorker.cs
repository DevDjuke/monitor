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
        var sender = scope.ServiceProvider.GetRequiredService<WebhookAlertSender>();

        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            var connection = db.Database.GetDbConnection();
            if (!await TryAcquireLockAsync(connection, cancellationToken))
            {
                return;
            }

            try
            {
                var now = DateTimeOffset.UtcNow;
                var batchSize = Math.Clamp(_options.BatchSize, 1, 1000);
                var deliveries = await db.AlertDeliveries
                    .Where(x =>
                        x.Destination.Enabled &&
                        (x.Status == AlertDeliveryStatus.Pending || x.Status == AlertDeliveryStatus.RetryScheduled) &&
                        x.NextAttemptAt <= now)
                    .Include(x => x.Destination)
                    .Include(x => x.AlertEvent)
                        .ThenInclude(x => x.AlertRule)
                    .Include(x => x.AlertEvent)
                        .ThenInclude(x => x.FailureGroup)
                    .OrderBy(x => x.NextAttemptAt)
                    .ThenBy(x => x.CreatedAt)
                    .Take(batchSize)
                    .ToListAsync(cancellationToken);

                foreach (var delivery in deliveries)
                {
                    var attemptedAt = DateTimeOffset.UtcNow;
                    var result = await sender.SendAlertAsync(delivery, cancellationToken);

                    if (result.Succeeded)
                    {
                        delivery.MarkDelivered(result.StatusCode, attemptedAt);
                        delivery.Destination.RecordSuccess(attemptedAt);
                    }
                    else
                    {
                        var nextAttemptAt = GetNextAttemptAt(delivery, result.Retryable, attemptedAt);
                        delivery.MarkFailed(
                            result.Error ?? "Webhook delivery failed.",
                            result.StatusCode,
                            attemptedAt,
                            nextAttemptAt);
                        delivery.Destination.RecordFailure(
                            result.Error ?? "Webhook delivery failed.",
                            attemptedAt);
                    }

                    await db.SaveChangesAsync(cancellationToken);
                }

                if (deliveries.Count > 0)
                {
                    logger.LogInformation("Processed {DeliveryCount} alert delivery outbox item(s).", deliveries.Count);
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

    private DateTimeOffset? GetNextAttemptAt(
        AlertDelivery delivery,
        bool retryable,
        DateTimeOffset attemptedAt)
    {
        var nextAttemptNumber = delivery.AttemptCount + 1;
        var maxAttempts = Math.Clamp(_options.MaxAttempts, 1, 100);
        if (!retryable || nextAttemptNumber >= maxAttempts)
        {
            return null;
        }

        var exponent = Math.Min(delivery.AttemptCount, 20);
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

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result) >= 0;
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

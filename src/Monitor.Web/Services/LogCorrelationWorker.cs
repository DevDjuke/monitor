using Monitor.Infrastructure.Logs;

namespace Monitor.Web.Services;

public sealed class LogCorrelationWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<LogCorrelationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var service = scope.ServiceProvider.GetRequiredService<LogCorrelationService>();
                var correlated = await service.CorrelatePendingAsync(cancellationToken: stoppingToken);
                if (correlated > 0)
                {
                    logger.LogDebug("Correlated {LogEventCount} previously unlinked log events.", correlated);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Log-event correlation sweep failed.");
            }

            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        }
    }
}

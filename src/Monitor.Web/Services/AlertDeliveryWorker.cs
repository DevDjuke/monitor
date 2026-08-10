using Microsoft.Extensions.Options;

namespace Monitor.Web.Services;

public sealed class AlertDeliveryWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<AlertDeliveryOptions> options,
    ILogger<AlertDeliveryWorker> logger) : BackgroundService
{
    private readonly AlertDeliveryOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            logger.LogInformation("Alert delivery is disabled.");
            return;
        }

        var initialDelay = TimeSpan.FromSeconds(Math.Clamp(_options.InitialDelaySeconds, 0, 3600));
        if (initialDelay > TimeSpan.Zero)
        {
            await Task.Delay(initialDelay, stoppingToken);
        }

        var interval = TimeSpan.FromSeconds(Math.Clamp(_options.SweepIntervalSeconds, 1, 3600));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var dispatcher = scope.ServiceProvider.GetRequiredService<AlertDeliveryDispatcher>();
                await dispatcher.SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Alert delivery sweep failed.");
            }

            await Task.Delay(interval, stoppingToken);
        }
    }
}

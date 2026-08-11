using Microsoft.Extensions.Options;
using Monitor.Infrastructure.Control;

namespace Monitor.Web.Services;

public sealed class ComponentCommandExpiryWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<ComponentCommandOptions> options,
    ILogger<ComponentCommandExpiryWorker> logger) : BackgroundService
{
    private readonly ComponentCommandOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            logger.LogInformation("Component commands are disabled.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var service = scope.ServiceProvider.GetRequiredService<ComponentCommandService>();
                await service.ExpireOutstandingAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Component command expiry sweep failed.");
            }

            await Task.Delay(
                TimeSpan.FromSeconds(Math.Clamp(_options.SweepIntervalSeconds, 5, 3600)),
                stoppingToken);
        }
    }
}

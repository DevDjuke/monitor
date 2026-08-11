using Microsoft.Extensions.Options;
using Monitor.Infrastructure.Usage;

namespace Monitor.Web.Services;

public sealed class UsageBudgetWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<UsageBudgetOptions> options,
    ILogger<UsageBudgetWorker> logger) : BackgroundService
{
    private readonly UsageBudgetOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            logger.LogInformation("Usage budget evaluation is disabled.");
            return;
        }

        var initialDelay = Math.Clamp(_options.InitialDelaySeconds, 0, 3600);
        if (initialDelay > 0)
        {
            await Task.Delay(TimeSpan.FromSeconds(initialDelay), stoppingToken);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var service = scope.ServiceProvider.GetRequiredService<UsageBudgetEvaluationService>();
                await service.SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Usage budget sweep failed.");
            }

            await Task.Delay(
                TimeSpan.FromSeconds(Math.Clamp(_options.SweepIntervalSeconds, 5, 3600)),
                stoppingToken);
        }
    }
}

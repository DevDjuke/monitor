using Microsoft.Extensions.Options;
using Monitor.Infrastructure.Failures;

namespace Monitor.Web.Services;

public sealed class FailureAlertingWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<FailureAlertingOptions> options,
    ILogger<FailureAlertingWorker> logger) : BackgroundService
{
    private readonly FailureAlertingOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            logger.LogInformation("Failure alert evaluation is disabled.");
            return;
        }

        try
        {
            if (_options.InitialDelay > TimeSpan.Zero)
            {
                await Task.Delay(_options.InitialDelay, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var service = scope.ServiceProvider.GetRequiredService<FailureAlertEvaluationService>();
                await service.SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Failure alert evaluation sweep failed.");
            }

            try
            {
                await Task.Delay(_options.SweepInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}

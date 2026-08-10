using Microsoft.Extensions.Options;
using Monitor.Client;
using Monitor.Domain;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<MonitorConnectionOptions>(builder.Configuration.GetSection("Monitor"));
builder.Services.Configure<SampleWorkerOptions>(builder.Configuration.GetSection("SampleWorker"));

builder.Services.AddSingleton(sp =>
{
    var options = sp.GetRequiredService<IOptions<MonitorConnectionOptions>>().Value;
    if (string.IsNullOrWhiteSpace(options.BaseUrl))
    {
        throw new InvalidOperationException("Monitor:BaseUrl is required.");
    }

    return new HttpClient
    {
        BaseAddress = new Uri(options.BaseUrl, UriKind.Absolute),
        Timeout = TimeSpan.FromSeconds(10)
    };
});

builder.Services.AddSingleton(sp =>
{
    var options = sp.GetRequiredService<IOptions<MonitorConnectionOptions>>().Value;
    return new MonitorClient(sp.GetRequiredService<HttpClient>(), options.IngestionApiKey);
});

builder.Services.AddHostedService<SampleWorker>();

await builder.Build().RunAsync();

internal sealed class SampleWorker(
    MonitorClient monitor,
    IOptions<SampleWorkerOptions> options,
    ILogger<SampleWorker> logger) : BackgroundService
{
    private readonly SampleWorkerOptions _options = options.Value;
    private Guid _componentId;
    private int _runSequence;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _componentId = await RegisterWithRetryAsync(stoppingToken);
        logger.LogInformation("Registered sample component {ComponentId}.", _componentId);

        var heartbeatTask = HeartbeatLoopAsync(stoppingToken);

        try
        {
            await RunLoopAsync(stoppingToken);
        }
        finally
        {
            try
            {
                await heartbeatTask;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
        }
    }

    private async Task<Guid> RegisterWithRetryAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                var component = await monitor.RegisterComponentAsync(
                    new ComponentRegistration(
                        _options.ComponentName,
                        _options.Slug,
                        ComponentType.Agent,
                        _options.Environment,
                        _options.Version),
                    cancellationToken);

                return component.Id;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Monitor is unavailable; component registration will be retried.");
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            }
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, _options.HeartbeatIntervalSeconds));

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await monitor.HeartbeatAsync(_componentId, cancellationToken);
                logger.LogDebug("Heartbeat sent for {ComponentId}.", _componentId);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Heartbeat failed for {ComponentId}.", _componentId);
            }

            await Task.Delay(interval, cancellationToken);
        }
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, _options.RunIntervalSeconds));

        while (!cancellationToken.IsCancellationRequested)
        {
            await ExecuteSyntheticAuditAsync(cancellationToken);
            await Task.Delay(interval, cancellationToken);
        }
    }

    private async Task ExecuteSyntheticAuditAsync(CancellationToken cancellationToken)
    {
        var sequence = Interlocked.Increment(ref _runSequence);
        var inputTokens = Random.Shared.Next(1_200, 2_400);
        var outputTokens = 0;
        var costUsd = 0d;

        var run = await monitor.StartRunAsync(
            new StartRunOptions(
                _componentId,
                "Synthetic website audit",
                ExternalId: $"sample-{sequence:D6}",
                Trigger: "Scheduled",
                Model: "sample-model",
                Input: new
                {
                    target = _options.TargetUrl,
                    sequence,
                    synthetic = true
                }),
            cancellationToken);

        logger.LogInformation("Started sample run {RunId} (sequence {Sequence}).", run.Id, sequence);
        await run.LogAsync(
            LogEventLevel.Information,
            $"Synthetic audit started for {_options.TargetUrl}.",
            new { target = _options.TargetUrl, sequence, synthetic = true },
            source: "Monitor.SampleWorker",
            messageTemplate: "Synthetic audit started for {Target}.",
            cancellationToken: cancellationToken);

        try
        {
            await run.MeasureSpanAsync(
                "Fetch homepage",
                SpanKind.Http,
                ct => SyntheticDelayAsync(140, 360, ct),
                new { method = "GET", url = _options.TargetUrl, statusCode = 200, synthetic = true },
                cancellationToken: cancellationToken);

            await run.LogAsync(
                LogEventLevel.Debug,
                "Homepage fetched successfully.",
                new { target = _options.TargetUrl, statusCode = 200, synthetic = true },
                source: "Monitor.SampleWorker",
                cancellationToken: cancellationToken);

            await run.MeasureSpanAsync(
                "Extract metadata",
                SpanKind.Tool,
                ct => SyntheticDelayAsync(90, 240, ct),
                new { tool = "html-metadata", fields = 7, synthetic = true },
                cancellationToken: cancellationToken);

            await run.MeasureSpanAsync(
                "Analyze page",
                SpanKind.Model,
                async ct =>
                {
                    await SyntheticDelayAsync(220, 650, ct);

                    if (_options.FailureEvery > 0 && sequence % _options.FailureEvery == 0)
                    {
                        throw new InvalidOperationException(
                            "Synthetic model timeout. This failure is intentional so Monitor has failed runs to display.");
                    }
                },
                new { model = "sample-model", inputTokens, synthetic = true },
                cancellationToken: cancellationToken);

            outputTokens = Random.Shared.Next(320, 820);
            costUsd = Math.Round(0.004 + Random.Shared.NextDouble() * 0.018, 6);

            await run.MeasureSpanAsync(
                "Compose recommendation",
                SpanKind.Agent,
                ct => SyntheticDelayAsync(80, 220, ct),
                new { outputTokens, synthetic = true },
                cancellationToken: cancellationToken);

            await run.LogAsync(
                LogEventLevel.Information,
                "Synthetic audit completed successfully.",
                new { inputTokens, outputTokens, costUsd, synthetic = true },
                source: "Monitor.SampleWorker",
                cancellationToken: cancellationToken);

            await run.CompleteAsync(
                new RunCompletion(
                    inputTokens,
                    outputTokens,
                    costUsd,
                    Output: new
                    {
                        target = _options.TargetUrl,
                        verdict = "Sample audit completed",
                        score = Random.Shared.Next(62, 96),
                        synthetic = true
                    }),
                cancellationToken);

            logger.LogInformation(
                "Completed sample run {RunId}: {InputTokens} input / {OutputTokens} output tokens, ${CostUsd:F4}.",
                run.Id,
                inputTokens,
                outputTokens,
                costUsd);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await TryCancelRunAsync(run);
            throw;
        }
        catch (Exception exception)
        {
            costUsd = Math.Round(0.002 + Random.Shared.NextDouble() * 0.008, 6);

            try
            {
                await run.RecordEventAsync(
                    new LogEventRecord(
                        LogEventLevel.Error,
                        exception.Message,
                        DateTimeOffset.UtcNow,
                        Properties: new { sequence, synthetic = true },
                        ExceptionType: exception.GetType().FullName,
                        ExceptionMessage: exception.Message,
                        ExceptionStackTrace: exception.StackTrace,
                        Source: "Monitor.SampleWorker",
                        EventName: "sample.audit.failed"),
                    CancellationToken.None);
            }
            catch (Exception telemetryException)
            {
                logger.LogDebug(telemetryException, "Could not record failure event for sample run {RunId}.", run.Id);
            }

            try
            {
                await run.FailAsync(exception, inputTokens, outputTokens, costUsd, CancellationToken.None);
            }
            catch (Exception telemetryException)
            {
                logger.LogWarning(telemetryException, "Could not mark sample run {RunId} as failed.", run.Id);
            }

            logger.LogWarning(exception, "Sample run {RunId} failed as intended/observed.", run.Id);
        }
    }

    private async Task TryCancelRunAsync(MonitorRun run)
    {
        if (run.IsCompleted)
        {
            return;
        }

        try
        {
            await run.CancelAsync("Sample worker is shutting down.", CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Could not mark run {RunId} as cancelled during shutdown.", run.Id);
        }
    }

    private static Task SyntheticDelayAsync(int minimumMilliseconds, int maximumMilliseconds, CancellationToken cancellationToken)
    {
        return Task.Delay(Random.Shared.Next(minimumMilliseconds, maximumMilliseconds), cancellationToken);
    }
}

internal sealed class MonitorConnectionOptions
{
    public string BaseUrl { get; set; } = "http://localhost:5000";
    public string IngestionApiKey { get; set; } = string.Empty;
}

internal sealed class SampleWorkerOptions
{
    public string ComponentName { get; set; } = "Sample Website Auditor";
    public string Slug { get; set; } = "sample-website-auditor";
    public string Environment { get; set; } = "development";
    public string Version { get; set; } = "0.1.0";
    public string TargetUrl { get; set; } = "https://example.com";
    public int HeartbeatIntervalSeconds { get; set; } = 15;
    public int RunIntervalSeconds { get; set; } = 30;
    public int FailureEvery { get; set; } = 5;
}

using System.Text.Json;
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

builder.Services.AddSingleton(sp =>
{
    var options = sp.GetRequiredService<IOptions<MonitorConnectionOptions>>().Value;
    return new MonitorControlClient(sp.GetRequiredService<HttpClient>(), options.IngestionApiKey);
});

builder.Services.AddHostedService<SampleWorker>();

await builder.Build().RunAsync();

internal sealed class SampleWorker(
    MonitorClient monitor,
    MonitorControlClient control,
    IOptions<SampleWorkerOptions> options,
    ILogger<SampleWorker> logger) : BackgroundService
{
    private readonly SampleWorkerOptions _options = options.Value;
    private readonly object _activeRunGate = new();
    private Guid _componentId;
    private int _runSequence;
    private volatile bool _paused;
    private volatile bool _disabled;
    private string _targetUrl = string.Empty;
    private int _runIntervalSeconds;
    private Guid? _activeRunId;
    private CancellationTokenSource? _activeRunCancellation;
    private string? _activeRunCancellationReason;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _targetUrl = _options.TargetUrl;
        _runIntervalSeconds = Math.Max(1, _options.RunIntervalSeconds);
        _componentId = await RegisterWithRetryAsync(stoppingToken);
        logger.LogInformation("Registered sample component {ComponentId}.", _componentId);

        var heartbeatTask = HeartbeatLoopAsync(stoppingToken);
        var commandTask = CommandLoopAsync(stoppingToken);

        try
        {
            await RunLoopAsync(stoppingToken);
        }
        finally
        {
            await AwaitBackgroundLoopAsync(heartbeatTask, stoppingToken);
            await AwaitBackgroundLoopAsync(commandTask, stoppingToken);
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

    private async Task CommandLoopAsync(CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, _options.CommandPollIntervalSeconds));

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var command = await control.ClaimNextAsync(_componentId, cancellationToken);
                if (command is null)
                {
                    await Task.Delay(interval, cancellationToken);
                    continue;
                }

                logger.LogInformation(
                    "Claimed Monitor control command {CommandId} ({CommandType}), attempt {Attempt}.",
                    command.Id,
                    command.Type,
                    command.DeliveryAttempt);

                await ExecuteCommandAsync(command, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Component command polling failed; the current lease will be retried if needed.");
                await Task.Delay(interval, cancellationToken);
            }
        }
    }

    private async Task ExecuteCommandAsync(
        ComponentControlCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            switch (command.Type)
            {
                case ComponentCommandType.Pause:
                    _paused = true;
                    await control.SucceedAsync(command, new { paused = true }, cancellationToken);
                    return;

                case ComponentCommandType.Resume:
                    _paused = false;
                    await control.SucceedAsync(command, new { paused = false }, cancellationToken);
                    return;

                case ComponentCommandType.Disable:
                    _disabled = true;
                    await control.SucceedAsync(command, new { disabled = true }, cancellationToken);
                    return;

                case ComponentCommandType.Enable:
                    _disabled = false;
                    _paused = false;
                    await control.SucceedAsync(command, new { disabled = false, paused = false }, cancellationToken);
                    return;

                case ComponentCommandType.KillRun:
                    await ExecuteKillRunAsync(command, cancellationToken);
                    return;

                case ComponentCommandType.RefreshConfiguration:
                    await ExecuteConfigurationRefreshAsync(command, cancellationToken);
                    return;

                case ComponentCommandType.Restart:
                    await control.RejectAsync(
                        command,
                        "Monitor.SampleWorker has no process supervisor. Wire Restart to systemd, Windows Service recovery, Kubernetes, or another host-specific restart mechanism.",
                        cancellationToken: cancellationToken);
                    return;

                default:
                    await control.RejectAsync(command, $"Unsupported command type {command.Type}.", cancellationToken: cancellationToken);
                    return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Control command {CommandId} failed locally.", command.Id);
            try
            {
                await control.FailAsync(
                    command,
                    exception.Message,
                    new { exceptionType = exception.GetType().FullName },
                    CancellationToken.None);
            }
            catch (Exception acknowledgementException)
            {
                logger.LogWarning(
                    acknowledgementException,
                    "Could not report failure for control command {CommandId}; it may be redelivered after the lease expires.",
                    command.Id);
            }
        }
    }

    private async Task ExecuteKillRunAsync(
        ComponentControlCommand command,
        CancellationToken cancellationToken)
    {
        CancellationTokenSource? cancellation = null;
        lock (_activeRunGate)
        {
            if (command.TargetRunId is not null &&
                _activeRunId == command.TargetRunId &&
                _activeRunCancellation is not null)
            {
                _activeRunCancellationReason = $"Killed by Monitor control command {command.Id:D}.";
                cancellation = _activeRunCancellation;
            }
        }

        if (cancellation is null)
        {
            await control.RejectAsync(
                command,
                "The target run is not active in this worker instance.",
                new { command.TargetRunId },
                cancellationToken);
            return;
        }

        cancellation.Cancel();
        await control.SucceedAsync(
            command,
            new { command.TargetRunId, cancellationRequested = true },
            cancellationToken);
    }

    private async Task ExecuteConfigurationRefreshAsync(
        ComponentControlCommand command,
        CancellationToken cancellationToken)
    {
        string? targetUrl = null;
        int? runIntervalSeconds = null;

        if (!string.IsNullOrWhiteSpace(command.PayloadJson))
        {
            using var document = JsonDocument.Parse(command.PayloadJson);
            var root = document.RootElement;
            if (root.TryGetProperty("targetUrl", out var targetUrlElement) && targetUrlElement.ValueKind == JsonValueKind.String)
            {
                targetUrl = targetUrlElement.GetString();
            }

            if (root.TryGetProperty("runIntervalSeconds", out var intervalElement) && intervalElement.TryGetInt32(out var parsedInterval))
            {
                runIntervalSeconds = Math.Clamp(parsedInterval, 1, 3600);
            }
        }

        if (!string.IsNullOrWhiteSpace(targetUrl))
        {
            _targetUrl = targetUrl;
        }

        if (runIntervalSeconds is not null)
        {
            Volatile.Write(ref _runIntervalSeconds, runIntervalSeconds.Value);
        }

        await control.SucceedAsync(
            command,
            new
            {
                applied = true,
                targetUrl = _targetUrl,
                runIntervalSeconds = Volatile.Read(ref _runIntervalSeconds)
            },
            cancellationToken);
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (_paused || _disabled)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                continue;
            }

            using var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            lock (_activeRunGate)
            {
                _activeRunCancellation = runCancellation;
                _activeRunCancellationReason = null;
            }

            try
            {
                await ExecuteSyntheticAuditAsync(runCancellation.Token);
            }
            catch (OperationCanceledException) when (runCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                logger.LogInformation("Active synthetic run was cancelled by a control command.");
            }
            finally
            {
                lock (_activeRunGate)
                {
                    _activeRunId = null;
                    _activeRunCancellation = null;
                    _activeRunCancellationReason = null;
                }
            }

            await Task.Delay(
                TimeSpan.FromSeconds(Math.Max(1, Volatile.Read(ref _runIntervalSeconds))),
                cancellationToken);
        }
    }

    private async Task ExecuteSyntheticAuditAsync(CancellationToken cancellationToken)
    {
        var sequence = Interlocked.Increment(ref _runSequence);
        var inputTokens = Random.Shared.Next(1_200, 2_400);
        var outputTokens = 0;
        var costUsd = 0d;
        var targetUrl = _targetUrl;

        var run = await monitor.StartRunAsync(
            new StartRunOptions(
                _componentId,
                "Synthetic website audit",
                ExternalId: $"sample-{sequence:D6}",
                Trigger: "Scheduled",
                Model: "sample-model",
                Input: new
                {
                    target = targetUrl,
                    sequence,
                    synthetic = true
                }),
            cancellationToken);

        lock (_activeRunGate)
        {
            _activeRunId = run.Id;
        }

        logger.LogInformation("Started sample run {RunId} (sequence {Sequence}).", run.Id, sequence);
        await run.LogAsync(
            LogEventLevel.Information,
            $"Synthetic audit started for {targetUrl}.",
            new { target = targetUrl, sequence, synthetic = true },
            source: "Monitor.SampleWorker",
            messageTemplate: "Synthetic audit started for {Target}.",
            cancellationToken: cancellationToken);

        try
        {
            await run.MeasureSpanAsync(
                "Fetch homepage",
                SpanKind.Http,
                ct => SyntheticDelayAsync(140, 360, ct),
                new { method = "GET", url = targetUrl, statusCode = 200, synthetic = true },
                cancellationToken: cancellationToken);

            await run.LogAsync(
                LogEventLevel.Debug,
                "Homepage fetched successfully.",
                new { target = targetUrl, statusCode = 200, synthetic = true },
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
                        target = targetUrl,
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
            string reason;
            lock (_activeRunGate)
            {
                reason = _activeRunCancellationReason ?? "Sample worker is shutting down.";
            }

            await TryCancelRunAsync(run, reason);
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

    private async Task TryCancelRunAsync(MonitorRun run, string reason)
    {
        if (run.IsCompleted)
        {
            return;
        }

        try
        {
            await run.CancelAsync(reason, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Could not mark run {RunId} as cancelled.", run.Id);
        }
    }

    private static async Task AwaitBackgroundLoopAsync(Task task, CancellationToken stoppingToken)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
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
    public int CommandPollIntervalSeconds { get; set; } = 2;
    public int FailureEvery { get; set; } = 5;
}

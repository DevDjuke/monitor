using System.Diagnostics;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var baseUrl = Environment.GetEnvironmentVariable("Monitor__BaseUrl")?.TrimEnd('/')
    ?? "http://127.0.0.1:5080";
var apiKey = Environment.GetEnvironmentVariable("Monitor__IngestionApiKey")
    ?? throw new InvalidOperationException("Monitor__IngestionApiKey is required.");

using var source = new ActivitySource("Monitor.OtlpSampleWorker");
using var provider = Sdk.CreateTracerProviderBuilder()
    .SetResourceBuilder(CreateResource())
    .AddSource(source.Name)
    .AddOtlpExporter(options =>
    {
        options.Endpoint = new Uri($"{baseUrl}/v1/traces");
        options.Protocol = OtlpExportProtocol.HttpProtobuf;
        options.Headers = $"X-Monitor-Key={apiKey}";
        options.ExportProcessorType = ExportProcessorType.Simple;
    })
    .Build();

using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.SetMinimumLevel(LogLevel.Trace);
    builder.AddOpenTelemetry(options =>
    {
        options.SetResourceBuilder(CreateResource());
        options.IncludeFormattedMessage = true;
        options.ParseStateValues = true;
        options.IncludeScopes = true;
        options.AddOtlpExporter(exporter =>
        {
            exporter.Endpoint = new Uri($"{baseUrl}/v1/logs");
            exporter.Protocol = OtlpExportProtocol.HttpProtobuf;
            exporter.Headers = $"X-Monitor-Key={apiKey}";
        });
    });
});
var logger = loggerFactory.CreateLogger("Monitor.OtlpSampleWorker");

EmitSuccess(source, logger);
EmitRateLimitFailure(source, logger, "req-482991");
EmitRateLimitFailure(source, logger, "req-937214");
provider.ForceFlush();

static ResourceBuilder CreateResource() =>
    ResourceBuilder.CreateDefault()
        .AddService(
            serviceName: "OTLP Sample Auditor",
            serviceNamespace: "monitor.samples",
            serviceVersion: "0.1.0")
        .AddAttributes([
            new KeyValuePair<string, object>("deployment.environment.name", "development")
        ]);

static void EmitSuccess(ActivitySource source, ILogger logger)
{
    using var root = source.StartActivity("OTLP synthetic success", ActivityKind.Internal)
        ?? throw new InvalidOperationException("OTLP root activity was not created.");
    root.SetTag("gen_ai.agent.name", "OTLP Sample Auditor");
    logger.LogInformation("OTLP synthetic audit {AuditId} started", "success-001");

    using var model = source.StartActivity("Generate recommendation", ActivityKind.Client);
    model?.SetTag("gen_ai.operation.name", "chat");
    model?.SetTag("gen_ai.request.model", "sample-otel-model");
    model?.SetTag("gen_ai.response.model", "sample-otel-model");
    model?.SetTag("gen_ai.usage.input_tokens", 120L);
    model?.SetTag("gen_ai.usage.output_tokens", 42L);
    model?.SetTag("monitor.cost_usd", 0.0012d);
    logger.LogDebug("Generated recommendation with {InputTokens} input tokens", 120L);
    model?.SetStatus(ActivityStatusCode.Ok);

    logger.LogInformation("OTLP synthetic audit {AuditId} completed", "success-001");
    root.SetStatus(ActivityStatusCode.Ok);
}

static void EmitRateLimitFailure(ActivitySource source, ILogger logger, string requestId)
{
    using var root = source.StartActivity("OTLP synthetic failure", ActivityKind.Internal)
        ?? throw new InvalidOperationException("OTLP root activity was not created.");
    root.SetTag("gen_ai.agent.name", "OTLP Sample Auditor");
    logger.LogInformation("OTLP synthetic audit {RequestId} started", requestId);

    using var model = source.StartActivity("Generate recommendation", ActivityKind.Client);
    model?.SetTag("gen_ai.operation.name", "chat");
    model?.SetTag("gen_ai.request.model", "sample-otel-model");
    model?.SetTag("gen_ai.provider.name", "sample-provider");
    model?.SetTag("gen_ai.usage.input_tokens", 200L);
    model?.SetTag("gen_ai.usage.output_tokens", 0L);
    model?.SetTag("error.type", "RateLimitError");
    model?.SetTag("http.response.status_code", 429);

    var exception = new InvalidOperationException($"Rate limit exceeded for request {requestId}");
    logger.LogError(exception, "Provider rate limit while handling request {RequestId}", requestId);
    model?.SetStatus(ActivityStatusCode.Error, exception.Message);

    logger.LogWarning("OTLP synthetic audit {RequestId} failed with HTTP {StatusCode}", requestId, 429);
    root.SetStatus(ActivityStatusCode.Ok);
}

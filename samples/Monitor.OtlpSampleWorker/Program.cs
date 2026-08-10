using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var baseUrl = Environment.GetEnvironmentVariable("Monitor__BaseUrl")?.TrimEnd('/')
    ?? "http://127.0.0.1:5080";
var apiKey = Environment.GetEnvironmentVariable("Monitor__IngestionApiKey")
    ?? throw new InvalidOperationException("Monitor__IngestionApiKey is required.");

using var source = new ActivitySource("Monitor.OtlpSampleWorker");
using var provider = Sdk.CreateTracerProviderBuilder()
    .SetResourceBuilder(
        ResourceBuilder.CreateDefault()
            .AddService(
                serviceName: "OTLP Sample Auditor",
                serviceNamespace: "monitor.samples",
                serviceVersion: "0.1.0")
            .AddAttributes([
                new KeyValuePair<string, object>("deployment.environment.name", "development")
            ]))
    .AddSource(source.Name)
    .AddOtlpExporter(options =>
    {
        options.Endpoint = new Uri($"{baseUrl}/v1/traces");
        options.Protocol = OtlpExportProtocol.HttpProtobuf;
        options.Headers = $"X-Monitor-Key={apiKey}";
        options.ExportProcessorType = ExportProcessorType.Simple;
    })
    .Build();

EmitSuccess(source);
EmitRateLimitFailure(source, "req-482991");
EmitRateLimitFailure(source, "req-937214");
provider.ForceFlush();

static void EmitSuccess(ActivitySource source)
{
    using var root = source.StartActivity("OTLP synthetic success", ActivityKind.Internal)
        ?? throw new InvalidOperationException("OTLP root activity was not created.");
    root.SetTag("gen_ai.agent.name", "OTLP Sample Auditor");

    using var model = source.StartActivity("Generate recommendation", ActivityKind.Client);
    model?.SetTag("gen_ai.operation.name", "chat");
    model?.SetTag("gen_ai.request.model", "sample-otel-model");
    model?.SetTag("gen_ai.response.model", "sample-otel-model");
    model?.SetTag("gen_ai.usage.input_tokens", 120L);
    model?.SetTag("gen_ai.usage.output_tokens", 42L);
    model?.SetTag("monitor.cost_usd", 0.0012d);
    model?.SetStatus(ActivityStatusCode.Ok);

    root.SetStatus(ActivityStatusCode.Ok);
}

static void EmitRateLimitFailure(ActivitySource source, string requestId)
{
    using var root = source.StartActivity("OTLP synthetic failure", ActivityKind.Internal)
        ?? throw new InvalidOperationException("OTLP root activity was not created.");
    root.SetTag("gen_ai.agent.name", "OTLP Sample Auditor");

    using var model = source.StartActivity("Generate recommendation", ActivityKind.Client);
    model?.SetTag("gen_ai.operation.name", "chat");
    model?.SetTag("gen_ai.request.model", "sample-otel-model");
    model?.SetTag("gen_ai.provider.name", "sample-provider");
    model?.SetTag("gen_ai.usage.input_tokens", 200L);
    model?.SetTag("gen_ai.usage.output_tokens", 0L);
    model?.SetTag("error.type", "RateLimitError");
    model?.SetTag("http.response.status_code", 429);
    model?.SetStatus(ActivityStatusCode.Error, $"Rate limit exceeded for request {requestId}");

    root.SetStatus(ActivityStatusCode.Ok);
}

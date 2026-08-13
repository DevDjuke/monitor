using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Logs.V1;
using OpenTelemetry.Proto.Metrics.V1;
using OpenTelemetry.Proto.Resource.V1;
using OpenTelemetry.Proto.Trace.V1;
using OtlpSpan = OpenTelemetry.Proto.Trace.V1.Span;
using OtlpStatus = OpenTelemetry.Proto.Trace.V1.Status;

if (args.Length == 0)
{
    throw new InvalidOperationException("Usage: write <directory> [service] [environment] | grpc <directory> <base-url> <key> | grpc-expect <directory> <base-url> <key> <status>");
}

switch (args[0].ToLowerInvariant())
{
    case "write":
        WritePayloads(args);
        break;
    case "grpc":
        await SendGrpcAsync(args, expectedStatus: null);
        break;
    case "grpc-expect":
        await SendGrpcAsync(args, Enum.Parse<StatusCode>(args[4], ignoreCase: true));
        break;
    default:
        throw new InvalidOperationException("Unknown fixture command.");
}

static void WritePayloads(string[] args)
{
    if (args.Length < 2)
    {
        throw new InvalidOperationException("write requires an output directory.");
    }

    var directory = args[1];
    var serviceName = args.Length > 2 ? args[2] : "P14 Agent";
    var environment = args.Length > 3 ? args[3] : "production";
    Directory.CreateDirectory(directory);

    var now = checked((ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000UL);
    var traceId = ByteString.CopyFrom(Convert.FromHexString("11223344556677889900aabbccddeeff"));
    var spanId = ByteString.CopyFrom(Convert.FromHexString("1020304050607080"));

    var traces = BuildTraces(serviceName, environment, now, traceId, spanId);
    var logs = BuildLogs(serviceName, environment, now, traceId, spanId);
    var metrics = BuildMetrics(serviceName, environment, now);

    Write(directory, "traces", traces);
    Write(directory, "logs", logs);
    Write(directory, "metrics", metrics);
    Console.WriteLine($"Wrote P14 JSON/protobuf fixtures to {directory}.");
}

static async Task SendGrpcAsync(string[] args, StatusCode? expectedStatus)
{
    if (args.Length < 4)
    {
        throw new InvalidOperationException("grpc requires directory, base URL and key.");
    }

    var directory = args[1];
    var baseUrl = args[2];
    var key = args[3];
    using var channel = GrpcChannel.ForAddress(baseUrl, new GrpcChannelOptions
    {
        HttpHandler = new SocketsHttpHandler { EnableMultipleHttp2Connections = true }
    });
    var headers = new Metadata { { "x-monitor-key", key } };

    try
    {
        var traceClient = new TraceService.TraceServiceClient(channel);
        var logClient = new LogsService.LogsServiceClient(channel);
        var metricClient = new MetricsService.MetricsServiceClient(channel);

        var traceResponse = await traceClient.ExportAsync(
            ExportTraceServiceRequest.Parser.ParseFrom(File.ReadAllBytes(Path.Combine(directory, "traces.bin"))), headers);
        var logResponse = await logClient.ExportAsync(
            ExportLogsServiceRequest.Parser.ParseFrom(File.ReadAllBytes(Path.Combine(directory, "logs.bin"))), headers);
        var metricResponse = await metricClient.ExportAsync(
            ExportMetricsServiceRequest.Parser.ParseFrom(File.ReadAllBytes(Path.Combine(directory, "metrics.bin"))), headers);

        if (expectedStatus is not null)
        {
            throw new InvalidOperationException($"Expected gRPC {expectedStatus}, but all exports succeeded.");
        }

        EnsureNoPartialSuccess(traceResponse.PartialSuccess?.RejectedSpans ?? 0, "traces");
        EnsureNoPartialSuccess(logResponse.PartialSuccess?.RejectedLogRecords ?? 0, "logs");
        EnsureNoPartialSuccess(metricResponse.PartialSuccess?.RejectedDataPoints ?? 0, "metrics");
        Console.WriteLine("P14 gRPC traces/logs/metrics exports succeeded.");
    }
    catch (RpcException ex) when (expectedStatus is not null)
    {
        if (ex.StatusCode != expectedStatus)
        {
            throw new InvalidOperationException($"Expected gRPC {expectedStatus}, got {ex.StatusCode}.", ex);
        }

        Console.WriteLine($"Verified gRPC {expectedStatus}.");
    }
}

static ExportTraceServiceRequest BuildTraces(
    string serviceName,
    string environment,
    ulong now,
    ByteString traceId,
    ByteString spanId)
{
    var request = new ExportTraceServiceRequest();
    var resourceSpans = new ResourceSpans { Resource = BuildResource(serviceName, environment) };
    var scopeSpans = new ScopeSpans
    {
        Scope = new InstrumentationScope { Name = "Monitor.P14.CI", Version = "1.0.0" }
    };
    scopeSpans.Spans.Add(new OtlpSpan
    {
        TraceId = traceId,
        SpanId = spanId,
        Name = "p14.transport",
        Kind = OtlpSpan.Types.SpanKind.Server,
        StartTimeUnixNano = now - 5_000_000UL,
        EndTimeUnixNano = now,
        Status = new OtlpStatus { Code = OtlpStatus.Types.StatusCode.Ok }
    });
    resourceSpans.ScopeSpans.Add(scopeSpans);
    request.ResourceSpans.Add(resourceSpans);
    return request;
}

static ExportLogsServiceRequest BuildLogs(
    string serviceName,
    string environment,
    ulong now,
    ByteString traceId,
    ByteString spanId)
{
    var request = new ExportLogsServiceRequest();
    var resourceLogs = new ResourceLogs { Resource = BuildResource(serviceName, environment) };
    var scopeLogs = new ScopeLogs
    {
        Scope = new InstrumentationScope { Name = "Monitor.P14.CI", Version = "1.0.0" }
    };
    var record = new LogRecord
    {
        TimeUnixNano = now,
        ObservedTimeUnixNano = now,
        SeverityNumber = SeverityNumber.Info,
        SeverityText = "INFO",
        Body = new AnyValue { StringValue = "P14 transport log" },
        TraceId = traceId,
        SpanId = spanId,
        EventName = "p14.transport"
    };
    record.Attributes.Add(StringAttribute("log.record.uid", "p14-transport-log-1"));
    record.Attributes.Add(StringAttribute("transport.test", "shared"));
    scopeLogs.LogRecords.Add(record);
    resourceLogs.ScopeLogs.Add(scopeLogs);
    request.ResourceLogs.Add(resourceLogs);
    return request;
}

static ExportMetricsServiceRequest BuildMetrics(string serviceName, string environment, ulong now)
{
    var request = new ExportMetricsServiceRequest();
    var resourceMetrics = new ResourceMetrics { Resource = BuildResource(serviceName, environment) };
    var scopeMetrics = new ScopeMetrics
    {
        Scope = new InstrumentationScope { Name = "Monitor.P14.CI", Version = "1.0.0" }
    };
    var metric = new Metric
    {
        Name = "p14.transport.gauge",
        Description = "P14 cross-transport compatibility gauge",
        Unit = "{item}",
        Gauge = new Gauge()
    };
    var point = new NumberDataPoint
    {
        StartTimeUnixNano = now - 60_000_000_000UL,
        TimeUnixNano = now,
        AsInt = 14
    };
    point.Attributes.Add(StringAttribute("transport.test", "shared"));
    metric.Gauge.DataPoints.Add(point);
    scopeMetrics.Metrics.Add(metric);
    resourceMetrics.ScopeMetrics.Add(scopeMetrics);
    request.ResourceMetrics.Add(resourceMetrics);
    return request;
}

static Resource BuildResource(string serviceName, string environment)
{
    var resource = new Resource();
    resource.Attributes.Add(StringAttribute("service.name", serviceName));
    resource.Attributes.Add(StringAttribute("service.namespace", "monitor.ci"));
    resource.Attributes.Add(StringAttribute("deployment.environment.name", environment));
    resource.Attributes.Add(StringAttribute("service.version", "14.0.0"));
    resource.Attributes.Add(StringAttribute("ci.resource", "p14"));
    return resource;
}

static void Write<T>(string directory, string name, T message) where T : IMessage
{
    File.WriteAllBytes(Path.Combine(directory, $"{name}.bin"), message.ToByteArray());
    File.WriteAllText(Path.Combine(directory, $"{name}.json"), JsonFormatter.Default.Format(message));
}

static void EnsureNoPartialSuccess(long rejected, string signal)
{
    if (rejected != 0)
    {
        throw new InvalidOperationException($"Expected no rejected {signal}, got {rejected}.");
    }
}

static KeyValue StringAttribute(string key, string value) =>
    new() { Key = key, Value = new AnyValue { StringValue = value } };

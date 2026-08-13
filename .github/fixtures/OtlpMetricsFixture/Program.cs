using Google.Protobuf;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Metrics.V1;
using OpenTelemetry.Proto.Resource.V1;

if (args.Length < 2)
{
    throw new InvalidOperationException("Usage: write <file> [service] [environment] | verify <response-file> <expected-rejected>");
}

if (string.Equals(args[0], "verify", StringComparison.OrdinalIgnoreCase))
{
    var response = ExportMetricsServiceResponse.Parser.ParseFrom(File.ReadAllBytes(args[1]));
    var expected = long.Parse(args[2]);
    var actual = response.PartialSuccess?.RejectedDataPoints ?? 0;
    if (actual != expected)
    {
        throw new InvalidOperationException($"Expected {expected} rejected points, got {actual}.");
    }

    Console.WriteLine($"Verified partial success with {actual} rejected points.");
    return;
}

if (!string.Equals(args[0], "write", StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidOperationException("Unknown fixture command.");
}

var output = args[1];
var serviceName = args.Length > 2 ? args[2] : "Metrics CI Agent";
var environment = args.Length > 3 ? args[3] : "production";
var now = checked((ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000UL);
var start = now - 60_000_000_000UL;

var request = new ExportMetricsServiceRequest();
var resourceMetrics = new ResourceMetrics
{
    Resource = new Resource(),
    SchemaUrl = "https://opentelemetry.io/schemas/1.37.0"
};
resourceMetrics.Resource.Attributes.Add(StringAttribute("service.name", serviceName));
resourceMetrics.Resource.Attributes.Add(StringAttribute("service.namespace", "monitor.ci"));
resourceMetrics.Resource.Attributes.Add(StringAttribute("deployment.environment.name", environment));
resourceMetrics.Resource.Attributes.Add(StringAttribute("service.version", "13.0.0"));
resourceMetrics.Resource.Attributes.Add(StringAttribute("ci.resource", "p13"));

var scopeMetrics = new ScopeMetrics
{
    Scope = new InstrumentationScope { Name = "Monitor.P13.CI", Version = "1.0.0" },
    SchemaUrl = "https://opentelemetry.io/schemas/1.37.0"
};

var gauge = new Metric
{
    Name = "queue.depth",
    Description = "Items waiting in the critical queue",
    Unit = "{item}",
    Gauge = new Gauge()
};
gauge.Metadata.Add(StringAttribute("stability", "stable"));
var gaugePoint = new NumberDataPoint
{
    StartTimeUnixNano = start,
    TimeUnixNano = now,
    AsInt = 7
};
gaugePoint.Attributes.Add(StringAttribute("queue", "critical"));
gaugePoint.Attributes.Add(StringAttribute("worker", "alpha"));
var exemplar = new Exemplar
{
    TimeUnixNano = now,
    AsDouble = 7,
    TraceId = ByteString.CopyFrom(Convert.FromHexString("00112233445566778899aabbccddeeff")),
    SpanId = ByteString.CopyFrom(Convert.FromHexString("0102030405060708"))
};
exemplar.FilteredAttributes.Add(StringAttribute("sample", "fixture"));
gaugePoint.Exemplars.Add(exemplar);
gauge.Gauge.DataPoints.Add(gaugePoint);
scopeMetrics.Metrics.Add(gauge);

var sumMetric = new Metric
{
    Name = "requests.total",
    Description = "Total processed requests",
    Unit = "{request}",
    Sum = new Sum
    {
        AggregationTemporality = AggregationTemporality.Cumulative,
        IsMonotonic = true
    }
};
var sumPoint = new NumberDataPoint { StartTimeUnixNano = start, TimeUnixNano = now, AsInt = 42 };
sumPoint.Attributes.Add(StringAttribute("route", "/api/work"));
sumMetric.Sum.DataPoints.Add(sumPoint);
scopeMetrics.Metrics.Add(sumMetric);

var histogramMetric = new Metric
{
    Name = "request.duration",
    Description = "Request duration distribution",
    Unit = "ms",
    Histogram = new Histogram { AggregationTemporality = AggregationTemporality.Delta }
};
var histogramPoint = new HistogramDataPoint
{
    StartTimeUnixNano = start,
    TimeUnixNano = now,
    Count = 4,
    Sum = 100,
    Min = 5,
    Max = 70
};
histogramPoint.Attributes.Add(StringAttribute("route", "/api/work"));
histogramPoint.BucketCounts.Add([1UL, 2UL, 1UL]);
histogramPoint.ExplicitBounds.Add([10d, 50d]);
histogramMetric.Histogram.DataPoints.Add(histogramPoint);
scopeMetrics.Metrics.Add(histogramMetric);

var exponentialMetric = new Metric
{
    Name = "payload.size",
    Description = "Payload size distribution",
    Unit = "By",
    ExponentialHistogram = new ExponentialHistogram { AggregationTemporality = AggregationTemporality.Cumulative }
};
var exponentialPoint = new ExponentialHistogramDataPoint
{
    StartTimeUnixNano = start,
    TimeUnixNano = now,
    Count = 4,
    Sum = 12,
    Scale = 2,
    ZeroCount = 1,
    ZeroThreshold = 0,
    Min = -2,
    Max = 8,
    Positive = new ExponentialHistogramDataPoint.Types.Buckets { Offset = 1 },
    Negative = new ExponentialHistogramDataPoint.Types.Buckets { Offset = -1 }
};
exponentialPoint.Positive.BucketCounts.Add(2UL);
exponentialPoint.Negative.BucketCounts.Add(1UL);
exponentialPoint.Attributes.Add(StringAttribute("codec", "json"));
exponentialMetric.ExponentialHistogram.DataPoints.Add(exponentialPoint);
scopeMetrics.Metrics.Add(exponentialMetric);

var summaryMetric = new Metric
{
    Name = "legacy.latency",
    Description = "Legacy summary retained without reinterpretation",
    Unit = "ms",
    Summary = new Summary()
};
var summaryPoint = new SummaryDataPoint
{
    StartTimeUnixNano = start,
    TimeUnixNano = now,
    Count = 3,
    Sum = 60
};
summaryPoint.QuantileValues.Add(new SummaryDataPoint.Types.ValueAtQuantile { Quantile = .5, Value = 20 });
summaryPoint.QuantileValues.Add(new SummaryDataPoint.Types.ValueAtQuantile { Quantile = .9, Value = 30 });
summaryPoint.Attributes.Add(StringAttribute("legacy", "true"));
summaryMetric.Summary.DataPoints.Add(summaryPoint);
scopeMetrics.Metrics.Add(summaryMetric);

var invalidGauge = new Metric { Name = "invalid.timestamp", Gauge = new Gauge() };
invalidGauge.Gauge.DataPoints.Add(new NumberDataPoint { TimeUnixNano = 0, AsInt = 1 });
scopeMetrics.Metrics.Add(invalidGauge);

var invalidHistogram = new Metric
{
    Name = "invalid.histogram",
    Histogram = new Histogram { AggregationTemporality = AggregationTemporality.Delta }
};
var invalidHistogramPoint = new HistogramDataPoint { StartTimeUnixNano = start, TimeUnixNano = now, Count = 5, Sum = 1 };
invalidHistogramPoint.BucketCounts.Add([1UL, 0UL]);
invalidHistogramPoint.ExplicitBounds.Add(10d);
invalidHistogram.Histogram.DataPoints.Add(invalidHistogramPoint);
scopeMetrics.Metrics.Add(invalidHistogram);

resourceMetrics.ScopeMetrics.Add(scopeMetrics);
request.ResourceMetrics.Add(resourceMetrics);
File.WriteAllBytes(output, request.ToByteArray());
Console.WriteLine($"Wrote {output} with 5 valid and 2 intentionally invalid metric points.");

static KeyValue StringAttribute(string key, string value) =>
    new() { Key = key, Value = new AnyValue { StringValue = value } };

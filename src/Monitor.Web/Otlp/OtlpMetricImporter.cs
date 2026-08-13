using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Monitor.Domain;
using Monitor.Infrastructure;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Metrics.V1;
using OtlpMetric = OpenTelemetry.Proto.Metrics.V1.Metric;

namespace Monitor.Web.Otlp;

public sealed partial class OtlpMetricImporter(
    MonitorDbContext db,
    ILogger<OtlpMetricImporter> logger)
{
    private const string ImportLockResource = "Monitor.OtlpMetricImport";
    private const uint NoRecordedValueMask = 1u;

    public async Task<OtlpMetricImportResult> ImportAsync(
        ExportMetricsServiceRequest request,
        CancellationToken cancellationToken)
    {
        var rejected = 0L;
        var duplicateCount = 0;
        var candidates = new Dictionary<string, MetricPoint>(StringComparer.Ordinal);

        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            if (!await TryAcquireImportLockAsync(db.Database.GetDbConnection(), cancellationToken))
            {
                throw new TimeoutException("Monitor could not acquire the OTLP metric ingestion lock within 15 seconds.");
            }

            try
            {
                foreach (var resourceMetrics in request.ResourceMetrics)
                {
                    var resourceAttributes = ToDictionary(resourceMetrics.Resource?.Attributes ?? []);
                    var serviceName = GetString(resourceAttributes, "service.name") ?? "unknown_service";
                    var serviceNamespace = GetString(resourceAttributes, "service.namespace");
                    var environment = GetString(resourceAttributes, "deployment.environment.name")
                        ?? GetString(resourceAttributes, "deployment.environment")
                        ?? "unknown";
                    var version = GetString(resourceAttributes, "service.version");
                    var slug = BuildSlug(serviceNamespace, serviceName);
                    var now = DateTimeOffset.UtcNow;
                    var component = await GetOrCreateComponentAsync(
                        serviceName,
                        slug,
                        environment,
                        version,
                        now,
                        cancellationToken);
                    component.Heartbeat(now);

                    var resourceAttributesJson = SerializeDictionary(resourceAttributes);

                    foreach (var scopeMetrics in resourceMetrics.ScopeMetrics)
                    {
                        var scopeName = NormalizeOptional(scopeMetrics.Scope?.Name);
                        var scopeVersion = NormalizeOptional(scopeMetrics.Scope?.Version);

                        foreach (var metric in scopeMetrics.Metrics)
                        {
                            if (string.IsNullOrWhiteSpace(metric.Name))
                            {
                                rejected += CountDataPoints(metric);
                                continue;
                            }

                            var metricMetadataJson = SerializeDictionary(ToDictionary(metric.Metadata));
                            foreach (var mapped in MapMetric(
                                         component.Id,
                                         metric,
                                         resourceAttributesJson,
                                         metricMetadataJson,
                                         scopeName,
                                         scopeVersion,
                                         NormalizeOptional(resourceMetrics.SchemaUrl),
                                         NormalizeOptional(scopeMetrics.SchemaUrl),
                                         now))
                            {
                                if (mapped.Point is null)
                                {
                                    rejected++;
                                    continue;
                                }

                                if (!candidates.TryAdd(mapped.Point.DedupeKey, mapped.Point))
                                {
                                    duplicateCount++;
                                }
                            }
                        }
                    }
                }

                var existingKeys = await LoadExistingDedupeKeysAsync(candidates.Keys, cancellationToken);
                foreach (var key in existingKeys)
                {
                    if (candidates.Remove(key))
                    {
                        duplicateCount++;
                    }
                }

                if (candidates.Count > 0)
                {
                    db.Set<MetricPoint>().AddRange(candidates.Values);
                }

                await db.SaveChangesAsync(cancellationToken);
            }
            finally
            {
                await ReleaseImportLockAsync(db.Database.GetDbConnection(), cancellationToken);
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }

        logger.LogDebug(
            "Imported {AcceptedMetricPointCount} OTLP metric points, ignored {DuplicateMetricPointCount} duplicates, rejected {RejectedMetricPointCount} points.",
            candidates.Count,
            duplicateCount,
            rejected);

        return new OtlpMetricImportResult(candidates.Count, duplicateCount, rejected);
    }

    private IEnumerable<MappedMetricPoint> MapMetric(
        Guid componentId,
        OtlpMetric metric,
        string? resourceAttributesJson,
        string? metricMetadataJson,
        string? scopeName,
        string? scopeVersion,
        string? resourceSchemaUrl,
        string? scopeSchemaUrl,
        DateTimeOffset now)
    {
        switch (metric.DataCase)
        {
            case OtlpMetric.DataOneofCase.Gauge:
                foreach (var point in metric.Gauge.DataPoints)
                {
                    yield return TryMapNumber(
                        componentId, metric, MetricKind.Gauge, MetricAggregationTemporality.Unspecified,
                        false, point, resourceAttributesJson, metricMetadataJson, scopeName, scopeVersion,
                        resourceSchemaUrl, scopeSchemaUrl, now);
                }
                break;

            case OtlpMetric.DataOneofCase.Sum:
            {
                var temporality = MapTemporality((int)metric.Sum.AggregationTemporality);
                if (temporality == MetricAggregationTemporality.Unspecified)
                {
                    foreach (var _ in metric.Sum.DataPoints)
                    {
                        yield return MappedMetricPoint.Rejected;
                    }
                    break;
                }

                foreach (var point in metric.Sum.DataPoints)
                {
                    yield return TryMapNumber(
                        componentId, metric, MetricKind.Sum, temporality, metric.Sum.IsMonotonic, point,
                        resourceAttributesJson, metricMetadataJson, scopeName, scopeVersion,
                        resourceSchemaUrl, scopeSchemaUrl, now);
                }
                break;
            }

            case OtlpMetric.DataOneofCase.Histogram:
            {
                var temporality = MapTemporality((int)metric.Histogram.AggregationTemporality);
                if (temporality == MetricAggregationTemporality.Unspecified)
                {
                    foreach (var _ in metric.Histogram.DataPoints)
                    {
                        yield return MappedMetricPoint.Rejected;
                    }
                    break;
                }

                foreach (var point in metric.Histogram.DataPoints)
                {
                    yield return TryMapHistogram(
                        componentId, metric, temporality, point, resourceAttributesJson, metricMetadataJson,
                        scopeName, scopeVersion, resourceSchemaUrl, scopeSchemaUrl, now);
                }
                break;
            }

            case OtlpMetric.DataOneofCase.ExponentialHistogram:
            {
                var temporality = MapTemporality((int)metric.ExponentialHistogram.AggregationTemporality);
                if (temporality == MetricAggregationTemporality.Unspecified)
                {
                    foreach (var _ in metric.ExponentialHistogram.DataPoints)
                    {
                        yield return MappedMetricPoint.Rejected;
                    }
                    break;
                }

                foreach (var point in metric.ExponentialHistogram.DataPoints)
                {
                    yield return TryMapExponentialHistogram(
                        componentId, metric, temporality, point, resourceAttributesJson, metricMetadataJson,
                        scopeName, scopeVersion, resourceSchemaUrl, scopeSchemaUrl, now);
                }
                break;
            }

            case OtlpMetric.DataOneofCase.Summary:
                foreach (var point in metric.Summary.DataPoints)
                {
                    yield return TryMapSummary(
                        componentId, metric, point, resourceAttributesJson, metricMetadataJson,
                        scopeName, scopeVersion, resourceSchemaUrl, scopeSchemaUrl, now);
                }
                break;
        }
    }

    private static MappedMetricPoint TryMapNumber(
        Guid componentId,
        OtlpMetric metric,
        MetricKind kind,
        MetricAggregationTemporality temporality,
        bool isMonotonic,
        NumberDataPoint point,
        string? resourceAttributesJson,
        string? metricMetadataJson,
        string? scopeName,
        string? scopeVersion,
        string? resourceSchemaUrl,
        string? scopeSchemaUrl,
        DateTimeOffset now)
    {
        try
        {
            if (point.TimeUnixNano == 0)
            {
                return MappedMetricPoint.Rejected;
            }

            var hasRecordedValue = (point.Flags & NoRecordedValueMask) == 0;
            double? value = point.ValueCase switch
            {
                NumberDataPoint.ValueOneofCase.AsDouble => point.AsDouble,
                NumberDataPoint.ValueOneofCase.AsInt => point.AsInt,
                _ => null
            };

            if (hasRecordedValue && value is null)
            {
                return MappedMetricPoint.Rejected;
            }

            var attributesJson = SerializeDictionary(ToDictionary(point.Attributes));
            var exemplarsJson = SerializeExemplars(point.Exemplars);
            var dedupeKey = BuildDedupeKey(
                componentId, metric.Name, kind, temporality, point.StartTimeUnixNano, point.TimeUnixNano,
                point.Flags, attributesJson, value, null, null, null, null, null, null, null, null);
            dedupeKey = ExtendDedupeKey(
                dedupeKey, metric, isMonotonic, resourceAttributesJson, metricMetadataJson, exemplarsJson,
                scopeName, scopeVersion, resourceSchemaUrl, scopeSchemaUrl);

            return new MappedMetricPoint(MetricPoint.Create(
                componentId,
                metric.Name,
                metric.Description,
                metric.Unit,
                kind,
                temporality,
                isMonotonic,
                hasRecordedValue,
                FromUnixNanoOrNull(point.StartTimeUnixNano),
                FromUnixNano(point.TimeUnixNano),
                value,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                attributesJson,
                resourceAttributesJson,
                metricMetadataJson,
                exemplarsJson,
                scopeName,
                scopeVersion,
                resourceSchemaUrl,
                scopeSchemaUrl,
                point.Flags,
                "OTLP",
                dedupeKey,
                now));
        }
        catch (Exception ex) when (ex is ArgumentException or OverflowException)
        {
            return MappedMetricPoint.Rejected;
        }
    }

    private static MappedMetricPoint TryMapHistogram(
        Guid componentId,
        OtlpMetric metric,
        MetricAggregationTemporality temporality,
        HistogramDataPoint point,
        string? resourceAttributesJson,
        string? metricMetadataJson,
        string? scopeName,
        string? scopeVersion,
        string? resourceSchemaUrl,
        string? scopeSchemaUrl,
        DateTimeOffset now)
    {
        try
        {
            if (point.TimeUnixNano == 0 || !IsValidHistogram(point))
            {
                return MappedMetricPoint.Rejected;
            }

            var hasRecordedValue = (point.Flags & NoRecordedValueMask) == 0;
            var attributesJson = SerializeDictionary(ToDictionary(point.Attributes));
            var bucketCountsJson = point.BucketCounts.Count == 0 ? null : JsonSerializer.Serialize(point.BucketCounts.ToArray());
            var explicitBoundsJson = point.ExplicitBounds.Count == 0 ? null : JsonSerializer.Serialize(point.ExplicitBounds.ToArray());
            var exemplarsJson = SerializeExemplars(point.Exemplars);
            decimal? count = hasRecordedValue ? (decimal)point.Count : null;
            double? sum = hasRecordedValue && point.HasSum ? point.Sum : null;
            double? min = hasRecordedValue && point.HasMin ? point.Min : null;
            double? max = hasRecordedValue && point.HasMax ? point.Max : null;
            var dedupeKey = BuildDedupeKey(
                componentId, metric.Name, MetricKind.Histogram, temporality, point.StartTimeUnixNano,
                point.TimeUnixNano, point.Flags, attributesJson, null, count, sum, min, max,
                bucketCountsJson, explicitBoundsJson, null, null);
            dedupeKey = ExtendDedupeKey(
                dedupeKey, metric, false, resourceAttributesJson, metricMetadataJson, exemplarsJson,
                scopeName, scopeVersion, resourceSchemaUrl, scopeSchemaUrl);

            return new MappedMetricPoint(MetricPoint.Create(
                componentId, metric.Name, metric.Description, metric.Unit, MetricKind.Histogram,
                temporality, false, hasRecordedValue, FromUnixNanoOrNull(point.StartTimeUnixNano),
                FromUnixNano(point.TimeUnixNano), null, count, sum, min, max, null, null, null,
                bucketCountsJson, explicitBoundsJson, null, null, null, attributesJson,
                resourceAttributesJson, metricMetadataJson, exemplarsJson, scopeName, scopeVersion,
                resourceSchemaUrl, scopeSchemaUrl, point.Flags, "OTLP", dedupeKey, now));
        }
        catch (Exception ex) when (ex is ArgumentException or OverflowException)
        {
            return MappedMetricPoint.Rejected;
        }
    }

    private static MappedMetricPoint TryMapExponentialHistogram(
        Guid componentId,
        OtlpMetric metric,
        MetricAggregationTemporality temporality,
        ExponentialHistogramDataPoint point,
        string? resourceAttributesJson,
        string? metricMetadataJson,
        string? scopeName,
        string? scopeVersion,
        string? resourceSchemaUrl,
        string? scopeSchemaUrl,
        DateTimeOffset now)
    {
        try
        {
            if (point.TimeUnixNano == 0 || !IsValidExponentialHistogram(point))
            {
                return MappedMetricPoint.Rejected;
            }

            var hasRecordedValue = (point.Flags & NoRecordedValueMask) == 0;
            var attributesJson = SerializeDictionary(ToDictionary(point.Attributes));
            var positiveBucketsJson = SerializeBuckets(point.Positive);
            var negativeBucketsJson = SerializeBuckets(point.Negative);
            var exemplarsJson = SerializeExemplars(point.Exemplars);
            decimal? count = hasRecordedValue ? (decimal)point.Count : null;
            decimal? zeroCount = hasRecordedValue ? (decimal)point.ZeroCount : null;
            double? sum = hasRecordedValue && point.HasSum ? point.Sum : null;
            double? min = hasRecordedValue && point.HasMin ? point.Min : null;
            double? max = hasRecordedValue && point.HasMax ? point.Max : null;
            var dedupeKey = BuildDedupeKey(
                componentId, metric.Name, MetricKind.ExponentialHistogram, temporality,
                point.StartTimeUnixNano, point.TimeUnixNano, point.Flags, attributesJson, null,
                count, sum, min, max, null, null, positiveBucketsJson, negativeBucketsJson,
                point.Scale, zeroCount, point.ZeroThreshold);
            dedupeKey = ExtendDedupeKey(
                dedupeKey, metric, false, resourceAttributesJson, metricMetadataJson, exemplarsJson,
                scopeName, scopeVersion, resourceSchemaUrl, scopeSchemaUrl);

            return new MappedMetricPoint(MetricPoint.Create(
                componentId, metric.Name, metric.Description, metric.Unit, MetricKind.ExponentialHistogram,
                temporality, false, hasRecordedValue, FromUnixNanoOrNull(point.StartTimeUnixNano),
                FromUnixNano(point.TimeUnixNano), null, count, sum, min, max, point.Scale, zeroCount,
                point.ZeroThreshold, null, null, positiveBucketsJson, negativeBucketsJson, null,
                attributesJson, resourceAttributesJson, metricMetadataJson, exemplarsJson, scopeName,
                scopeVersion, resourceSchemaUrl, scopeSchemaUrl, point.Flags, "OTLP", dedupeKey, now));
        }
        catch (Exception ex) when (ex is ArgumentException or OverflowException)
        {
            return MappedMetricPoint.Rejected;
        }
    }

    private static MappedMetricPoint TryMapSummary(
        Guid componentId,
        OtlpMetric metric,
        SummaryDataPoint point,
        string? resourceAttributesJson,
        string? metricMetadataJson,
        string? scopeName,
        string? scopeVersion,
        string? resourceSchemaUrl,
        string? scopeSchemaUrl,
        DateTimeOffset now)
    {
        try
        {
            if (point.TimeUnixNano == 0 || !IsValidSummary(point))
            {
                return MappedMetricPoint.Rejected;
            }

            var hasRecordedValue = (point.Flags & NoRecordedValueMask) == 0;
            var attributesJson = SerializeDictionary(ToDictionary(point.Attributes));
            var quantilesJson = point.QuantileValues.Count == 0
                ? null
                : JsonSerializer.Serialize(point.QuantileValues.Select(x => new StoredQuantile(x.Quantile, x.Value)).ToArray());
            decimal? count = hasRecordedValue ? (decimal)point.Count : null;
            double? sum = hasRecordedValue ? point.Sum : null;
            var dedupeKey = BuildDedupeKey(
                componentId, metric.Name, MetricKind.Summary, MetricAggregationTemporality.Cumulative,
                point.StartTimeUnixNano, point.TimeUnixNano, point.Flags, attributesJson, null, count,
                sum, null, null, null, null, null, null, null, null, null, quantilesJson);
            dedupeKey = ExtendDedupeKey(
                dedupeKey, metric, false, resourceAttributesJson, metricMetadataJson, null,
                scopeName, scopeVersion, resourceSchemaUrl, scopeSchemaUrl);

            return new MappedMetricPoint(MetricPoint.Create(
                componentId, metric.Name, metric.Description, metric.Unit, MetricKind.Summary,
                MetricAggregationTemporality.Cumulative, false, hasRecordedValue,
                FromUnixNanoOrNull(point.StartTimeUnixNano), FromUnixNano(point.TimeUnixNano), null,
                count, sum, null, null, null, null, null, null, null, null, null, quantilesJson,
                attributesJson, resourceAttributesJson, metricMetadataJson, null, scopeName, scopeVersion,
                resourceSchemaUrl, scopeSchemaUrl, point.Flags, "OTLP", dedupeKey, now));
        }
        catch (Exception ex) when (ex is ArgumentException or OverflowException)
        {
            return MappedMetricPoint.Rejected;
        }
    }

    private static bool IsValidHistogram(HistogramDataPoint point)
    {
        if (point.BucketCounts.Count == 0)
        {
            return point.ExplicitBounds.Count == 0;
        }

        if (point.BucketCounts.Count != point.ExplicitBounds.Count + 1)
        {
            return false;
        }

        decimal bucketTotal = 0;
        foreach (var count in point.BucketCounts)
        {
            bucketTotal += count;
        }

        return bucketTotal == point.Count && IsStrictlyIncreasing(point.ExplicitBounds);
    }

    private static bool IsValidExponentialHistogram(ExponentialHistogramDataPoint point)
    {
        decimal total = point.ZeroCount;
        if (point.Positive is not null)
        {
            foreach (var count in point.Positive.BucketCounts)
            {
                total += count;
            }
        }
        if (point.Negative is not null)
        {
            foreach (var count in point.Negative.BucketCounts)
            {
                total += count;
            }
        }
        return total == point.Count;
    }

    private static bool IsValidSummary(SummaryDataPoint point)
    {
        double previous = -1;
        foreach (var item in point.QuantileValues)
        {
            if (item.Quantile < 0 || item.Quantile > 1 || item.Quantile <= previous || item.Value < 0)
            {
                return false;
            }
            previous = item.Quantile;
        }
        return true;
    }

    private static bool IsStrictlyIncreasing(IEnumerable<double> values)
    {
        double? previous = null;
        foreach (var value in values)
        {
            if (double.IsNaN(value) || previous.HasValue && value <= previous.Value)
            {
                return false;
            }
            previous = value;
        }
        return true;
    }

    private static string ExtendDedupeKey(
        string pointIdentity,
        OtlpMetric metric,
        bool isMonotonic,
        string? resourceAttributesJson,
        string? metricMetadataJson,
        string? exemplarsJson,
        string? scopeName,
        string? scopeVersion,
        string? resourceSchemaUrl,
        string? scopeSchemaUrl)
    {
        var contextIdentity = JsonSerializer.Serialize(new
        {
            pointIdentity,
            metric.Description,
            metric.Unit,
            isMonotonic,
            resourceAttributesJson,
            metricMetadataJson,
            exemplarsJson,
            scopeName,
            scopeVersion,
            resourceSchemaUrl,
            scopeSchemaUrl
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(contextIdentity))).ToLowerInvariant();
    }

    private async Task<HashSet<string>> LoadExistingDedupeKeysAsync(
        IEnumerable<string> keys,
        CancellationToken cancellationToken)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var chunk in keys.Chunk(500))
        {
            var materialized = chunk.ToArray();
            var existing = await db.Set<MetricPoint>()
                .AsNoTracking()
                .Where(x => materialized.Contains(x.DedupeKey))
                .Select(x => x.DedupeKey)
                .ToListAsync(cancellationToken);
            result.UnionWith(existing);
        }
        return result;
    }

    private async Task<MonitoredComponent> GetOrCreateComponentAsync(
        string name,
        string slug,
        string environment,
        string? version,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var normalizedEnvironment = environment.Trim().ToLowerInvariant();
        var component = await db.Components.SingleOrDefaultAsync(
            x => x.Slug == slug && x.Environment == normalizedEnvironment,
            cancellationToken);

        if (component is null)
        {
            component = MonitoredComponent.Create(
                name.Trim(), slug, ComponentType.Service, normalizedEnvironment, version?.Trim(), now);
            db.Components.Add(component);
        }
        else
        {
            component.UpdateRegistration(name.Trim(), component.Type, version?.Trim(), now);
        }
        return component;
    }

    private static int CountDataPoints(OtlpMetric metric) => metric.DataCase switch
    {
        OtlpMetric.DataOneofCase.Gauge => metric.Gauge.DataPoints.Count,
        OtlpMetric.DataOneofCase.Sum => metric.Sum.DataPoints.Count,
        OtlpMetric.DataOneofCase.Histogram => metric.Histogram.DataPoints.Count,
        OtlpMetric.DataOneofCase.ExponentialHistogram => metric.ExponentialHistogram.DataPoints.Count,
        OtlpMetric.DataOneofCase.Summary => metric.Summary.DataPoints.Count,
        _ => 0
    };

    private static MetricAggregationTemporality MapTemporality(int value) => value switch
    {
        1 => MetricAggregationTemporality.Delta,
        2 => MetricAggregationTemporality.Cumulative,
        _ => MetricAggregationTemporality.Unspecified
    };

    private static string BuildDedupeKey(
        Guid componentId,
        string name,
        MetricKind kind,
        MetricAggregationTemporality temporality,
        ulong startTimeUnixNano,
        ulong timeUnixNano,
        uint flags,
        string? attributesJson,
        double? numericValue,
        decimal? count,
        double? sum,
        double? min,
        double? max,
        string? bucketCountsJson,
        string? explicitBoundsJson,
        string? positiveBucketsJson,
        string? negativeBucketsJson,
        int? scale = null,
        decimal? zeroCount = null,
        double? zeroThreshold = null,
        string? quantilesJson = null)
    {
        var identity = JsonSerializer.Serialize(new
        {
            componentId,
            name,
            kind,
            temporality,
            startTimeUnixNano,
            timeUnixNano,
            flags,
            attributesJson,
            numericValue,
            count,
            sum,
            min,
            max,
            bucketCountsJson,
            explicitBoundsJson,
            positiveBucketsJson,
            negativeBucketsJson,
            scale,
            zeroCount,
            zeroThreshold,
            quantilesJson
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
    }

    private static string? SerializeBuckets(ExponentialHistogramDataPoint.Types.Buckets? buckets)
    {
        if (buckets is null || buckets.BucketCounts.Count == 0)
        {
            return null;
        }
        return JsonSerializer.Serialize(new StoredBuckets(buckets.Offset, buckets.BucketCounts.ToArray()));
    }

    private static string? SerializeExemplars(IEnumerable<Exemplar> exemplars)
    {
        var values = exemplars.Select(exemplar => new StoredExemplar(
            exemplar.TimeUnixNano == 0 ? null : FromUnixNano(exemplar.TimeUnixNano),
            exemplar.ValueCase switch
            {
                Exemplar.ValueOneofCase.AsDouble => exemplar.AsDouble,
                Exemplar.ValueOneofCase.AsInt => exemplar.AsInt,
                _ => null
            },
            exemplar.TraceId.Length == 16 ? ToHex(exemplar.TraceId) : null,
            exemplar.SpanId.Length == 8 ? ToHex(exemplar.SpanId) : null,
            SerializeDictionary(ToDictionary(exemplar.FilteredAttributes))))
            .ToArray();
        return values.Length == 0 ? null : JsonSerializer.Serialize(values);
    }

    private static string? SerializeDictionary(IReadOnlyDictionary<string, object?> values) =>
        values.Count == 0 ? null : JsonSerializer.Serialize(values);

    private static SortedDictionary<string, object?> ToDictionary(IEnumerable<KeyValue> values)
    {
        var result = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var item in values)
        {
            if (!string.IsNullOrWhiteSpace(item.Key))
            {
                result[item.Key] = ToObject(item.Value);
            }
        }
        return result;
    }

    private static object? ToObject(AnyValue? value)
    {
        if (value is null) return null;
        return value.ValueCase switch
        {
            AnyValue.ValueOneofCase.StringValue => value.StringValue,
            AnyValue.ValueOneofCase.BoolValue => value.BoolValue,
            AnyValue.ValueOneofCase.IntValue => value.IntValue,
            AnyValue.ValueOneofCase.DoubleValue => value.DoubleValue,
            AnyValue.ValueOneofCase.BytesValue => Convert.ToBase64String(value.BytesValue.ToByteArray()),
            AnyValue.ValueOneofCase.ArrayValue => value.ArrayValue.Values.Select(ToObject).ToArray(),
            AnyValue.ValueOneofCase.KvlistValue => ToDictionary(value.KvlistValue.Values),
            _ => null
        };
    }

    private static string? GetString(IReadOnlyDictionary<string, object?> values, string key) =>
        values.TryGetValue(key, out var value) ? Convert.ToString(value) : null;

    private static DateTimeOffset FromUnixNano(ulong unixNano)
    {
        var seconds = unixNano / 1_000_000_000UL;
        var remainder = unixNano % 1_000_000_000UL;
        return DateTimeOffset.FromUnixTimeSeconds(checked((long)seconds))
            .AddTicks(checked((long)(remainder / 100UL)));
    }

    private static DateTimeOffset? FromUnixNanoOrNull(ulong unixNano) =>
        unixNano == 0 ? null : FromUnixNano(unixNano);

    private static string ToHex(Google.Protobuf.ByteString value) =>
        Convert.ToHexString(value.ToByteArray()).ToLowerInvariant();

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string BuildSlug(string? serviceNamespace, string serviceName)
    {
        var source = string.IsNullOrWhiteSpace(serviceNamespace) ? serviceName : $"{serviceNamespace}-{serviceName}";
        var slug = NonSlugRegex().Replace(source.ToLowerInvariant(), "-").Trim('-');
        slug = RepeatedDashRegex().Replace(slug, "-");
        if (slug.Length == 0) slug = "unknown-service";
        return slug[..Math.Min(slug.Length, 120)];
    }

    private static async Task<bool> TryAcquireImportLockAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock
                @Resource = @resource,
                @LockMode = 'Exclusive',
                @LockOwner = 'Session',
                @LockTimeout = 15000;
            SELECT @result;
            """;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@resource";
        parameter.Value = ImportLockResource;
        command.Parameters.Add(parameter);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) >= 0;
    }

    private static async Task ReleaseImportLockAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "EXEC sys.sp_releaseapplock @Resource = @resource, @LockOwner = 'Session';";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@resource";
        parameter.Value = ImportLockResource;
        command.Parameters.Add(parameter);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed record MappedMetricPoint(MetricPoint? Point)
    {
        public static MappedMetricPoint Rejected { get; } = new((MetricPoint?)null);
    }

    private sealed record StoredBuckets(int Offset, IReadOnlyList<ulong> Counts);
    private sealed record StoredQuantile(double Quantile, double Value);
    private sealed record StoredExemplar(DateTimeOffset? Timestamp, double? Value, string? TraceId, string? SpanId, string? AttributesJson);

    [GeneratedRegex("[^a-z0-9-]+")]
    private static partial Regex NonSlugRegex();

    [GeneratedRegex("-{2,}")]
    private static partial Regex RepeatedDashRegex();
}

public sealed record OtlpMetricImportResult(int AcceptedPoints, int DuplicatePoints, long RejectedPoints);

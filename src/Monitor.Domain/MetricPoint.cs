namespace Monitor.Domain;

public enum MetricKind
{
    Gauge = 1,
    Sum = 2,
    Histogram = 3,
    ExponentialHistogram = 4,
    Summary = 5
}

public enum MetricAggregationTemporality
{
    Unspecified = 0,
    Delta = 1,
    Cumulative = 2
}

public sealed class MetricPoint
{
    private MetricPoint() { }

    public Guid Id { get; private set; }
    public Guid ComponentId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public string? Unit { get; private set; }
    public MetricKind Kind { get; private set; }
    public MetricAggregationTemporality Temporality { get; private set; }
    public bool IsMonotonic { get; private set; }
    public bool HasRecordedValue { get; private set; }
    public DateTimeOffset? StartTimestamp { get; private set; }
    public DateTimeOffset Timestamp { get; private set; }
    public double? NumericValue { get; private set; }
    public decimal? Count { get; private set; }
    public double? Sum { get; private set; }
    public double? Min { get; private set; }
    public double? Max { get; private set; }
    public int? Scale { get; private set; }
    public decimal? ZeroCount { get; private set; }
    public double? ZeroThreshold { get; private set; }
    public string? BucketCountsJson { get; private set; }
    public string? ExplicitBoundsJson { get; private set; }
    public string? PositiveBucketsJson { get; private set; }
    public string? NegativeBucketsJson { get; private set; }
    public string? QuantilesJson { get; private set; }
    public string? AttributesJson { get; private set; }
    public string? ResourceAttributesJson { get; private set; }
    public string? MetricMetadataJson { get; private set; }
    public string? ExemplarsJson { get; private set; }
    public string? ScopeName { get; private set; }
    public string? ScopeVersion { get; private set; }
    public string? ResourceSchemaUrl { get; private set; }
    public string? ScopeSchemaUrl { get; private set; }
    public long Flags { get; private set; }
    public string Source { get; private set; } = string.Empty;
    public string DedupeKey { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; }

    public MonitoredComponent Component { get; private set; } = null!;

    public static MetricPoint Create(
        Guid componentId,
        string name,
        string? description,
        string? unit,
        MetricKind kind,
        MetricAggregationTemporality temporality,
        bool isMonotonic,
        bool hasRecordedValue,
        DateTimeOffset? startTimestamp,
        DateTimeOffset timestamp,
        double? numericValue,
        decimal? count,
        double? sum,
        double? min,
        double? max,
        int? scale,
        decimal? zeroCount,
        double? zeroThreshold,
        string? bucketCountsJson,
        string? explicitBoundsJson,
        string? positiveBucketsJson,
        string? negativeBucketsJson,
        string? quantilesJson,
        string? attributesJson,
        string? resourceAttributesJson,
        string? metricMetadataJson,
        string? exemplarsJson,
        string? scopeName,
        string? scopeVersion,
        string? resourceSchemaUrl,
        string? scopeSchemaUrl,
        long flags,
        string source,
        string dedupeKey,
        DateTimeOffset now)
    {
        if (componentId == Guid.Empty)
        {
            throw new ArgumentException("A component is required.", nameof(componentId));
        }

        if (timestamp == default)
        {
            throw new ArgumentException("A metric timestamp is required.", nameof(timestamp));
        }

        if (kind is MetricKind.Gauge or MetricKind.Sum && hasRecordedValue && numericValue is null)
        {
            throw new ArgumentException("Scalar metric points require a numeric value.", nameof(numericValue));
        }

        if (kind is MetricKind.Histogram or MetricKind.ExponentialHistogram or MetricKind.Summary && hasRecordedValue && count is null)
        {
            throw new ArgumentException("Distribution metric points require a count.", nameof(count));
        }

        return new MetricPoint
        {
            Id = Guid.NewGuid(),
            ComponentId = componentId,
            Name = NormalizeRequired(name, 240, nameof(name)),
            Description = Normalize(description, 1000),
            Unit = Normalize(unit, 80),
            Kind = kind,
            Temporality = temporality,
            IsMonotonic = isMonotonic,
            HasRecordedValue = hasRecordedValue,
            StartTimestamp = startTimestamp,
            Timestamp = timestamp,
            NumericValue = numericValue,
            Count = count,
            Sum = sum,
            Min = min,
            Max = max,
            Scale = scale,
            ZeroCount = zeroCount,
            ZeroThreshold = zeroThreshold,
            BucketCountsJson = NormalizeJson(bucketCountsJson),
            ExplicitBoundsJson = NormalizeJson(explicitBoundsJson),
            PositiveBucketsJson = NormalizeJson(positiveBucketsJson),
            NegativeBucketsJson = NormalizeJson(negativeBucketsJson),
            QuantilesJson = NormalizeJson(quantilesJson),
            AttributesJson = NormalizeJson(attributesJson),
            ResourceAttributesJson = NormalizeJson(resourceAttributesJson),
            MetricMetadataJson = NormalizeJson(metricMetadataJson),
            ExemplarsJson = NormalizeJson(exemplarsJson),
            ScopeName = Normalize(scopeName, 240),
            ScopeVersion = Normalize(scopeVersion, 80),
            ResourceSchemaUrl = Normalize(resourceSchemaUrl, 500),
            ScopeSchemaUrl = Normalize(scopeSchemaUrl, 500),
            Flags = flags,
            Source = NormalizeRequired(source, 40, nameof(source)),
            DedupeKey = NormalizeRequired(dedupeKey, 64, nameof(dedupeKey)),
            CreatedAt = now
        };
    }

    private static string? Normalize(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        return normalized[..Math.Min(normalized.Length, maxLength)];
    }

    private static string NormalizeRequired(string? value, int maxLength, string parameterName)
    {
        var normalized = Normalize(value, maxLength);
        return normalized ?? throw new ArgumentException("A value is required.", parameterName);
    }

    private static string? NormalizeJson(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}

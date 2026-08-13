# OTLP metrics

P13 adds metrics as a first-class Monitor telemetry signal while keeping storage and querying independent of the OTLP transport. The current transport is OTLP/HTTP protobuf at `POST /v1/metrics`; P14 can add OTLP/gRPC and OTLP/HTTP JSON on top of the same importer/domain path.

## Metric-point model

Monitor persists metric data points rather than inventing a second run-like model. Each point retains the metric identity and the source aggregation semantics needed to interpret it:

- component, metric name, description and unit;
- kind: Gauge, Sum, Histogram, ExponentialHistogram or Summary;
- aggregation temporality and monotonicity where the OTLP kind defines them;
- start timestamp and point timestamp;
- scalar value or distribution count/sum/min/max;
- explicit histogram bounds and bucket counts;
- exponential-histogram scale, zero count/threshold and positive/negative buckets;
- summary quantiles without converting the deprecated Summary type into a different aggregation;
- point attributes, resource attributes, metric metadata and instrumentation-scope identity;
- resource/scope schema URLs;
- exemplars including filtered attributes and valid trace/span ids;
- OTLP data-point flags, including the no-recorded-value state.

Distribution counts use `decimal(20,0)` in SQL Server so the OTLP unsigned 64-bit count range is not narrowed to signed `bigint`.

## Ingestion

`POST /v1/metrics` accepts `application/x-protobuf` and `application/protobuf`, with optional gzip content encoding. Authentication is identical to the existing trace/log endpoints:

- the shared ingestion key remains the controlled bootstrap/migration path;
- a component-scoped ingestion credential is accepted only when every resource in the request resolves to that component's service namespace/name slug and environment;
- invalid/revoked credentials return 401 and a valid key scoped to another component returns 403.

Resource identity uses the same attributes as trace/log ingestion: `service.name`, optional `service.namespace`, `deployment.environment.name`/`deployment.environment`, and optional `service.version`.

## Validation and partial success

Monitor rejects malformed individual data points instead of failing the whole valid remainder of an OTLP batch. Rejected points are reported in `ExportMetricsPartialSuccess.rejected_data_points`.

Current validation rejects, among other cases:

- a zero `time_unix_nano`;
- a recorded scalar point without an integer/double value;
- Sum/Histogram/ExponentialHistogram data with unspecified aggregation temporality;
- explicit histograms whose bucket count does not match `bounds + 1`, whose bounds are not strictly increasing, or whose bucket total does not equal `count`;
- exponential histograms whose positive + negative + zero bucket counts do not equal `count`;
- summaries with invalid or non-increasing quantiles.

The OTLP no-recorded-value flag is not treated as malformed. Monitor persists the point with `HasRecordedValue = false` so source gaps remain distinguishable from numeric zero.

## Retry idempotency

Each accepted point receives a SHA-256 deduplication key derived from its semantic identity: component, metric identity/kind/temporality, timestamps, flags, dimensions and the represented scalar/distribution payload. Duplicates in one request and exporter retries already present in SQL Server are ignored. The database also enforces a unique index on the deduplication key.

## Operator surface

`/metrics` is available to every role with `Monitor.View`. It supports URL-addressable filters for:

- time window;
- component;
- environment;
- metric name;
- metric kind;
- instrumentation scope;
- free-text search across metric identity and stored dimensions/resource metadata;
- returned row count.

The page shows scalar values directly and distribution summaries as count/sum/min/max. Full bucket arrays, bounds, quantiles, resource attributes, metric metadata, schemas and exemplars remain available in expandable forensic details.

Metrics are also a first-class Saved Views surface. The canonical metrics query state can be saved/pinned per authenticated user under the same ownership and pin-limit rules as the existing operational surfaces.

P13 intentionally does not manufacture downsampled charts or merge cumulative/delta streams. Such transformations require explicit aggregation semantics and should be driven by measured data volume and operator needs rather than being implied by a UI chart.

## Retention

Raw metric points have a bounded detail-retention window controlled by `Retention:MetricDetailDays`, defaulting to 30 days. The existing retention worker purges old metric points in bounded batches after the normal run/log retention sweep.

P13 does not introduce metric rollups. Daily/monthly or metric-specific rollups remain a P15+ optimization when real cardinality/volume demonstrates the need.

## Deliberate boundaries

P13 does not add:

- OTLP/gRPC or OTLP/HTTP JSON transport (P14);
- Prometheus scraping/export;
- metric-derived alert rules or automated actions;
- server-side metric resampling/downsampling;
- a parallel per-provider metric domain model.

Metric attributes and resource metadata are operator-visible telemetry and may be retained for up to the configured detail window. Instrumentation should therefore avoid emitting secrets, credentials, raw sensitive prompts, or other data that does not belong in operational telemetry.

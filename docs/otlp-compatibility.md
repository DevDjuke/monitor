# OTLP compatibility

P14 expands Monitor's OTLP transport compatibility without adding a second telemetry domain or persistence path.

## Supported transports

Monitor accepts traces, logs and metrics through all of the following transports:

| Transport | Traces | Logs | Metrics |
| --- | --- | --- | --- |
| OTLP/HTTP protobuf | `POST /v1/traces` | `POST /v1/logs` | `POST /v1/metrics` |
| OTLP/HTTP JSON | `POST /v1/traces` | `POST /v1/logs` | `POST /v1/metrics` |
| OTLP/gRPC | `TraceService/Export` | `LogsService/Export` | `MetricsService/Export` |

HTTP protobuf accepts `application/x-protobuf` and `application/protobuf`. HTTP JSON accepts `application/json`, including a charset parameter. Both HTTP encodings support `Content-Encoding: gzip`; other request content encodings are rejected.

OTLP/HTTP JSON is parsed and formatted with protobuf JSON semantics. Field names, enum values, 64-bit integers and byte fields therefore follow the canonical protobuf JSON mapping instead of a Monitor-specific JSON schema.

## One ingestion path

Transport handlers stop at authentication, decoding and protocol-specific response mapping. After decoding, all transports call `OtlpIngestionProcessor`, which applies component-scope validation and delegates to the existing:

- `OtlpTraceImporter`
- `OtlpLogImporter`
- `OtlpMetricImporter`

This preserves the existing correlation, failure grouping, metric fidelity, retry deduplication, application-lock and persistence behavior. A payload repeated across two transports is still the same telemetry to the importer and is deduplicated by the existing signal-specific identity rules.

## Authentication and scope

All OTLP transports use the existing Monitor ingestion credential contract:

- shared/bootstrap ingestion key: privileged ingestion during migration/bootstrap
- component credential: accepted only when every OTLP resource resolves to the credential's component
- invalid/revoked/missing credential: rejected
- valid component credential targeting another component: rejected

HTTP maps those failures to `401 Unauthorized` and `403 Forbidden`. gRPC maps them to `UNAUTHENTICATED` and `PERMISSION_DENIED` respectively.

Human Owner/Operator/Viewer/Auditor roles do not grant OTLP ingestion access.

## Partial success

Signal validation remains importer-owned, so partial-success behavior is identical regardless of transport. Valid sibling telemetry can be committed while invalid individual spans/logs/metric points are reported through the standard OTLP response message.

HTTP responses use the same representation family as the request: protobuf requests receive protobuf responses and JSON requests receive protobuf-JSON responses. gRPC uses the generated OTLP response messages directly.

## Production listener model

The single-node deployment keeps one public TLS origin at Caddy. Internally Monitor uses two Kestrel listeners:

- `8080`: HTTP/1 for the UI, Monitor-native APIs and OTLP/HTTP
- `4317`: cleartext HTTP/2 dedicated to OTLP/gRPC

Caddy terminates TLS publicly and routes only the three OTLP collector gRPC service paths to `h2c://monitor:4317`; all other requests continue to `monitor:8080`. The gRPC listener is not published on the host by the Compose deployment.

The split is intentional. ASP.NET Core gRPC requires HTTP/2, while an unsecured Kestrel endpoint cannot reliably negotiate HTTP/1.1 and HTTP/2 on the same port. Keeping an HTTP/2-only internal listener avoids weakening the existing UI/API listener and gives Caddy an explicit h2c upstream.

Container defaults expose both internal ports. Deployments that replace Caddy must preserve an HTTP/2-capable route to the gRPC listener or terminate TLS directly at a Kestrel HTTP/2 endpoint.

## Compatibility gate

`.github/workflows/otlp-compatibility-ci.yml` is the permanent P14 integration gate. Against real SQL Server it verifies:

- HTTP JSON traces, logs and metrics
- JSON response media type and parseability
- gzip-compressed JSON
- malformed JSON and unsupported media-type rejection
- invalid and cross-component HTTP authentication
- gRPC traces, logs and metrics over a dedicated h2c listener
- `UNAUTHENTICATED` and `PERMISSION_DENIED` gRPC behavior
- stable SQL row counts when the exact same payload is sent first through HTTP JSON and then through gRPC
- the production Caddy h2c route and Compose HTTP/2 listener contract

P14 adds no EF Core migration. It is a transport/deployment compatibility slice over the existing telemetry schema.

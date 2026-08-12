# Alert delivery adapters

P9 extends Monitor's existing durable alert-delivery outbox beyond generic signed webhooks. It does not introduce a second queue or a provider-specific persistence model: failure alerts and budget notifications continue to create the same durable delivery records, and the dispatcher selects a transport from the destination kind.

## Supported destinations

| Kind | Transport | Secret/configuration handling |
| --- | --- | --- |
| Webhook | Monitor JSON over HTTP/HTTPS with `X-Monitor-*` headers and HMAC-SHA256 signing | Existing webhook signing secret remains protected with the original Data Protection purpose for backward compatibility. |
| Slack | Incoming webhook with Slack blocks | Full webhook URL is Data-Protection encrypted; only a redacted scheme/host is stored for display/audit. |
| Microsoft Teams | Incoming webhook / workflow endpoint with an Adaptive Card message | Full webhook URL is Data-Protection encrypted; only a redacted scheme/host is stored for display/audit. |
| Discord | Incoming webhook with an embed and disabled mention expansion | Full webhook URL is Data-Protection encrypted; only a redacted scheme/host is stored for display/audit. |
| PagerDuty | Events API v2 `trigger` event | Integration/routing key is Data-Protection encrypted. The standard Events API v2 endpoint is stored as non-secret destination metadata. |
| Email | SMTP | SMTP host/from/user/password/TLS configuration is serialized and Data-Protection encrypted; the visible endpoint contains only the recipient as `mailto:` metadata. |

Provider webhook URLs must use HTTPS. HTTP is accepted only for loopback endpoints so local development and the integration fixture can exercise the same transport code without weakening remote endpoint requirements.

## Durable delivery semantics

All adapters reuse the existing `AlertDeliveryDestination`, failure-alert outbox, budget-alert outbox, retry/backoff policy, SQL Server application lock, health tracking, manual requeue, dead-letter state, per-rule destination assignment, per-budget destination assignment, and immutable operator audit trail.

The dispatcher preserves the two bounded outbox slices: a continuously full failure queue cannot starve budget notifications, or vice versa. A destination can be disabled without deleting queued evidence; delivery resumes when it is enabled again.

HTTP adapters treat request timeout, HTTP 425, 429, and 5xx responses as retryable. Other non-success responses are permanent for that outbox attempt series. SMTP timeouts and transient SMTP status codes are retryable; permanent SMTP/protocol/configuration failures dead-letter after the normal delivery policy is applied.

## Provider payloads

Failure notifications contain the alert/rule identity, failure group/fingerprint, category, operation, failure type/dependency, threshold, occurrence count, and rolling-window timestamps. Budget notifications contain the budget identity/scope, period, configured limits, observed cost/tokens, utilization, and warning/critical level.

The provider adapters translate that canonical information into native payloads:

- Slack uses a header, summary section, and event/id context block.
- Teams uses an Adaptive Card attachment.
- Discord uses a message/embed and sets `allowed_mentions.parse` to an empty array.
- PagerDuty sends Events API v2 trigger events with a stable event-derived `dedup_key`.
- Email sends a plain-text operational summary plus the canonical details as key/value lines.
- Generic webhooks retain the existing schema and HMAC contract unchanged.

## Secret boundaries

`ProtectedSecret` is deliberately transport-neutral storage. For provider adapters it contains either an encrypted URL, routing key, or serialized SMTP configuration. Protected material is never included in `AuditEvent` before/after JSON and is never rendered back to the operator after destination creation.

For Slack, Teams, and Discord, the visible `EndpointUrl` is redacted to `scheme://host[:port]/***`. This is intentional because those webhook URLs typically contain bearer-like path tokens. The generic Monitor webhook remains visible because its endpoint is not itself the authentication secret; authentication is the separately protected HMAC key.

HTTP alert clients have automatic redirects disabled. This prevents a configured secret-bearing request from being silently replayed to a redirect target.

The broader production security debt remains unchanged: Data Protection keys and operational secrets must ultimately use shared, durable key management / a proper secret store before multi-node or production hardening.

## Operator workflow

Destinations are created and managed from `/alerts`. The form switches fields by destination kind and supports test delivery, enable/disable, health status, delivered/dead-letter counts, and the existing delivery history.

A PagerDuty test is a real Events API trigger and therefore creates an informational incident/event at the configured integration. The UI calls this out on the test action.

Existing alert rules and usage budgets need no migration. Their `all enabled destinations` mode automatically includes new adapter kinds, while explicit destination assignments continue to reference destination ids exactly as before.

## Persistence and migration

No EF Core migration is required for P9. `AlertDeliveryDestination.Kind` is already persisted as an integer and the existing `EndpointUrl` / `ProtectedSecret` columns are large enough for the new metadata and protected configuration. Existing Webhook rows keep enum value `1`; new kinds append values `2` through `6`.

## Integration gate

`.github/workflows/alert-delivery-adapters-ci.yml` is the permanent P9 gate. Against SQL Server it:

1. migrates and starts the real Monitor web application;
2. creates Webhook, Slack, Teams, Discord, PagerDuty, and Email destinations through `/alerts`;
3. verifies destination kinds, protected configuration, redacted UI/audit data, and provider test delivery;
4. ingests the standard OTLP failure sample and creates a real all-enabled-destinations alert rule;
5. waits for one alert event to fan out into exactly six durable delivery rows and requires all six to reach `Delivered`;
6. verifies the legacy webhook HMAC, Slack blocks, Teams Adaptive Card envelope, Discord mention suppression, PagerDuty routing/dedup payload, and SMTP message headers/body.

The HTTP and SMTP receivers used by the gate are local protocol fixtures; no external provider account or secret is required for CI.
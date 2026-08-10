# Component ingestion credentials

Monitor supports component-scoped ingestion credentials in addition to the temporary shared bootstrap ingestion key.

## Purpose

A component credential limits the blast radius of a leaked exporter or worker key. A credential issued to component A is not a second global Monitor key: it is authorized only for A's telemetry identity.

The shared `Monitor__IngestionApiKey` remains a privileged bootstrap/migration path for now. It can register new components and ingest for any component. Production hardening should eventually move that bootstrap capability and all operational secrets into the planned vault/secret-store work.

## Key format and storage

New component keys have this shape:

```text
mon_c_<public-key-id>.<random-secret>
```

The public key id is random and exists only to perform an indexed database lookup. The secret is generated from 32 cryptographically random bytes.

Monitor displays the full plaintext token only when it is issued or rotated. The database stores:

- credential id;
- owning component id;
- operator-visible credential name;
- public key id;
- SHA-256 hash of the complete token;
- creation timestamp and actor;
- last-used timestamp;
- revocation timestamp and actor.

There is no plaintext token or secret column. Authentication hashes the supplied complete token and compares it to the stored 32-byte hash with a constant-time comparison.

`LastUsedAt` is deliberately write-throttled: successful use updates it at most once per minute rather than causing a database write for every telemetry request.

## Authorization scope

Browser-authenticated Monitor operators and the configured bootstrap ingestion key are privileged identities.

A component credential is restricted to its owning component. For the Monitor-native HTTP API it may:

- read only its component from the component endpoint;
- repeat registration/update only for the same component slug and environment;
- heartbeat only its component;
- create runs only for its component;
- query/read only its component's runs;
- complete only its component's runs;
- create spans only under its component's runs.

A valid component credential attempting to target another component receives HTTP `403`. An invalid or revoked credential receives HTTP `401`.

## OTLP scope

`POST /v1/traces` accepts both the bootstrap key and component credentials through the existing `X-Monitor-Key` header.

For a component credential, Monitor validates every OTLP `ResourceSpans` resource before importing anything. The resource must resolve to the credential's component identity using the same mapping as normal OTLP ingestion:

```text
service.namespace + service.name -> component slug
deployment.environment.name      -> environment
```

`deployment.environment` remains the compatibility fallback for environment. If any resource in the request resolves to a different component, the entire request is rejected with HTTP `403`; no spans from that request are imported.

The privileged bootstrap key retains the existing OTLP auto-registration/upsert behavior.

## Issuing, rotating, and revoking

Open `/components/{id}` from the component registry.

An operator can:

- **Issue** a named key. The plaintext is shown once after creation.
- **Rotate** an active key. Monitor revokes the old credential and creates its replacement in one save boundary, then shows the replacement plaintext once.
- **Revoke** an active key. The token becomes invalid immediately while its metadata remains available for history.

Rotation preserves the credential's operator-facing name. Revoked rows are retained so creation, last-use, and revocation metadata remain inspectable.

## Bootstrap migration strategy

Existing installations do not need to switch every component at once.

1. Keep `Monitor__IngestionApiKey` configured during migration.
2. Open each component in Monitor and issue a component credential.
3. Change that worker/exporter to the new key.
4. Confirm `LastUsedAt` advances and telemetry continues to arrive.
5. Repeat for the remaining components.
6. Once all deployments use scoped credentials and a proper secret/vault strategy exists, retire or tightly constrain the shared bootstrap path.

The development key currently kept in solution/local configuration is intentionally temporary technical debt and is still tracked in the roadmap's security section.

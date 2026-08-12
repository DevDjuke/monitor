# Production deployment

P10 defines Monitor's first production deployment contract: a deliberately small, reliable single-node installation rather than a generic Kubernetes or multi-node platform.

The bundled deployment is:

```text
Internet
   |
   v
Caddy :80/:443
   |
   v
Monitor :8080 (internal only)
   |
   v
SQL Server (internal only)
```

Caddy owns public HTTP/HTTPS and TLS. Monitor and SQL Server are not published to the host network. Monitor trusts forwarded headers only from Caddy's explicit internal IP.

## Prerequisites

- A Linux host with a supported Docker Engine and Docker Compose v2.
- A DNS name pointing at the host.
- TCP ports 80 and 443 reachable from the Internet if Caddy is expected to obtain and renew public certificates.
- Enough durable storage for SQL Server, the Data Protection key ring, and backups.

The included SQL Server service uses SQL Server Express. Express is suitable for a small single-node installation but has product limits, including a 10 GB maximum relational database size. Use an appropriately licensed SQL Server edition or an external SQL Server when the installation outgrows those limits.

## First deployment

From `deploy/single-node`:

1. Copy `.env.example` to `.env`.
2. Set `MONITOR_HOST` and `MONITOR_ADMIN_EMAIL`.
3. Generate strong shell-safe secrets. Hex output avoids quoting surprises because `.env` is consumed both by Compose and the secret preparation script, for example:

   ```bash
   openssl rand -hex 32
   ```

   Use separate values for `MONITOR_BOOTSTRAP_PASSWORD` and `MSSQL_SA_PASSWORD`.
4. Optionally set `MONITOR_INGESTION_API_KEY` as a temporary shared bootstrap/migration credential. Prefer per-component ingestion credentials after initial setup.
5. Materialize Monitor's file-backed secrets:

   ```bash
   sh prepare-secrets.sh
   ```

6. Validate and build the deployment:

   ```bash
   docker compose config --quiet
   docker compose build
   ```

7. Start it:

   ```bash
   docker compose up -d
   ```

8. Check status:

   ```bash
   docker compose ps
   curl -fsS https://monitor.example.com/health/ready
   ```

Caddy obtains/renews the public certificate and redirects public HTTP traffic to HTTPS.

## Secrets

Monitor overlays its normal ASP.NET Core configuration from files under `/run/secrets`. A file name such as:

```text
ConnectionStrings__Monitor
```

maps to:

```text
ConnectionStrings:Monitor
```

The bundled `prepare-secrets.sh` creates:

- `ConnectionStrings__Monitor`
- `Monitor__BootstrapAdmin__Password`
- optionally `Monitor__IngestionApiKey`

Real files under `deploy/single-node/secrets/` and the local `.env` are ignored by Git.

The SQL Server container still receives its SA password from the local `.env`, because the stock single-node SQL Server container contract uses an environment variable for that bootstrap credential. Keep `.env` readable only by the deployment operator. A managed/external SQL service or a vault-integrated deployment can replace this later.

Once the first administrator exists, `Monitor__BootstrapAdmin__Password` can be removed. `AuthBootstrapper` only needs the bootstrap credentials while the user database is empty. The email setting may remain in Compose without requiring the password on subsequent starts.

## Data Protection is durable state

Monitor uses ASP.NET Core Data Protection for authentication cookies and protected alert-destination configuration. The production container therefore persists its key ring in:

```text
/var/lib/monitor/data-protection-keys
```

The bundled Compose stack maps this to the `monitor_data_protection` Docker volume.

Do **not** recreate or discard that volume as if it were cache data. Losing the key ring can invalidate authentication cookies and make previously protected configuration impossible to decrypt.

The key ring is persisted but is not yet wrapped by an external vault/HSM/certificate. For this single-node deployment, host access controls, Docker volume permissions, and backup handling form the security boundary. Shared/encrypted key management is intentionally deferred until a multi-node or managed-secret deployment is required.

## Health endpoints

Monitor exposes two anonymous machine endpoints:

- `/health/live` — process liveness only. A successful response means the web process is running.
- `/health/ready` — operational readiness. It requires SQL Server connectivity and zero pending EF Core migrations.

The Docker image health check uses `/health/ready`. Do not use liveness as a deployment-readiness check.

If the database disappears after startup or the running binary observes a stale schema, readiness becomes unhealthy.

## Database migrations

Migration behavior is explicit through `Production:MigrateOnStartup`.

### Single-node automatic migration

The bundled Compose file defaults `MONITOR_MIGRATE_ON_STARTUP=true`. This is reasonable for a single web node because the application migrates before it starts serving requests.

### Explicit migration mode

For operators who want deployment and schema changes to be separate, set:

```text
MONITOR_MIGRATE_ON_STARTUP=false
```

Before starting the new application version, run:

```bash
docker compose run --rm monitor --migrate-only
```

`--migrate-only` applies migrations and exits without starting HTTP listeners or background workers.

When startup migration is disabled, normal Monitor startup checks for pending migrations and refuses to start if the schema is stale. This prevents an old schema from being treated as ready.

## Reverse proxy and forwarded headers

The bundled Compose network gives Caddy the fixed address `172.31.250.10`. Monitor is configured to trust forwarded headers from exactly that proxy address and processes only `X-Forwarded-For` and `X-Forwarded-Proto`, one hop deep.

Monitor does not clear ASP.NET Core's trusted proxy/network collections and does not accept arbitrary Internet-supplied forwarded headers.

If `172.31.250.0/24` collides with an existing Docker/VPN network, change all of these together:

- the Compose subnet;
- Caddy's static IP;
- Monitor's static IP;
- SQL Server's static IP;
- `Production__ForwardedHeaders__KnownProxies__0`.

For a different reverse proxy, set `Production:ForwardedHeaders:Enabled=true` and list the exact IP address(es) of the proxy hops you operate. Do not configure a trust-all proxy range merely to make `X-Forwarded-Proto` work.

`Production:PublicUrl` must remain an absolute HTTPS URL even though the bundled Monitor container has `Production:UseHttpsRedirection=false`: Caddy is the component responsible for edge HTTPS and redirect behavior.

## Production startup validation

In `Production`, Monitor fails before serving requests when deployment-critical configuration is unsafe or incomplete. In particular:

- `ConnectionStrings:Monitor` is required;
- LocalDB is rejected;
- `AllowedHosts` must explicitly name the public host and may not contain `*`;
- `Production:PublicUrl` must be absolute HTTPS;
- `Production:DataProtectionKeyPath` must be absolute and writable;
- `Production:DataProtectionApplicationName` must be non-empty;
- forwarded-header mode requires at least one explicit valid proxy IP.

Development retains the convenient LocalDB/default behavior.

## Backup

Treat these as one logical recovery unit:

1. the `Monitor` SQL Server database;
2. the `monitor_data_protection` key-ring volume.

The database contains the operational/audit state. The key ring contains cryptographic state required to decrypt protected configuration in that database.

A simple single-node backup procedure is:

1. Put the deployment in a maintenance window and stop Monitor so configuration is not changing:

   ```bash
   docker compose stop monitor proxy
   ```

2. Create a SQL Server native backup into a host-mounted or otherwise durable backup location. The exact `BACKUP DATABASE` path depends on the host backup layout. Verify the resulting `.bak` with `RESTORE VERIFYONLY` before considering the backup complete.
3. Copy the Data Protection key ring to the same dated recovery set. One simple method is:

   ```bash
   docker compose cp monitor:/var/lib/monitor/data-protection-keys ./backup/data-protection-keys
   ```

   If Monitor is stopped rather than running, mount the `monitor_data_protection` volume into a temporary utility container and archive its contents instead.
4. Record the Monitor image/commit deployed with that recovery set.
5. Restart the deployment and confirm `/health/ready`.

Automate this once the real hosting environment and backup destination are known. The important invariant is that a database backup without the matching durable key ring is not a complete Monitor recovery set.

Caddy's certificate state can normally be reacquired from the CA and is not application cryptographic state, although backing up the Caddy volumes can make disaster recovery faster.

## Restore

For a disaster restore:

1. Stop Monitor.
2. Restore the SQL Server database from the selected recovery set.
3. Restore the matching Data Protection key ring into `monitor_data_protection`.
4. Deploy a Monitor version compatible with that database schema.
5. Start Monitor.
6. Confirm `/health/ready`, sign-in, and at least one protected alert destination if available.

Do not restore a database from one point in time with an unrelated/new empty Data Protection key ring.

## Upgrade

Recommended sequence:

1. Take and verify a complete recovery set.
2. Pull/update the source or deployment artifact.
3. Build the new image.
4. If using explicit migrations, run `docker compose run --rm monitor --migrate-only`.
5. Recreate Monitor:

   ```bash
   docker compose up -d --build
   ```

6. Wait for `/health/ready`.
7. Exercise sign-in and the main operator pages.

The persistent SQL Server and Data Protection volumes survive ordinary container recreation.

## Rollback

Application-only rollback is safe only when the older binary is compatible with the current schema. EF migrations are generally forward-oriented; blindly starting an older image against a newer schema is not a rollback plan.

If schema compatibility is uncertain, restore the complete pre-upgrade recovery set: database **and** Data Protection keys, then run the corresponding application image.

## Security notes

The production image:

- runs as the .NET image's non-root `app` user;
- disables .NET diagnostics in the container by default;
- uses a read-only root filesystem in the bundled Compose service;
- keeps only `/tmp` writable through tmpfs plus the dedicated Data Protection volume;
- does not publish Monitor's internal port or SQL Server's port;
- uses `no-new-privileges` for Monitor and Caddy;
- requires secure authentication cookies in Production;
- disables automatic HTTP redirects in outbound alert transports, preserving the P9 secret-replay boundary.

Host patching, Docker daemon security, firewall rules, off-host backups, SQL credentials, DNS, and TLS ownership remain operator responsibilities.

## Scope intentionally deferred

P10 is the single-node production baseline. It does not claim:

- Kubernetes packaging;
- multi-node SignalR/backplane support;
- shared Data Protection key storage across web nodes;
- external vault/HSM integration;
- supervisor-specific `Restart` implementations;
- highly available SQL Server.

Those should be added from measured deployment requirements rather than speculatively broadening the first production contract.

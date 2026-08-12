# Security notes

## Development ingestion key

The Visual Studio development setup intentionally keeps the shared local Monitor ingestion key in the solution/launch configuration for convenience while the control plane is being built and dogfooded locally.

This remains accepted local-development debt. It is not the production secret-management model.

## P10 single-node production contract

Production deployment now keeps Monitor application secrets out of committed configuration by loading file-backed values from `/run/secrets`. The bundled single-node package materializes the database connection string, bootstrap password, and optional shared ingestion key into an ignored host directory that is mounted read-only into the Monitor container.

ASP.NET Core Data Protection keys are persisted outside the application container in a dedicated durable Docker volume. That key ring is required for authentication-cookie continuity and for decrypting protected alert-destination configuration; it must be backed up and restored together with the SQL Server database.

Production startup also rejects LocalDB, wildcard `AllowedHosts`, missing/invalid HTTPS public URL configuration, non-durable Data Protection configuration, and forwarded-header mode without explicit proxy IP trust.

The bundled SQL Server container still receives its SA bootstrap password through the untracked deployment `.env`, because that is the stock SQL Server container interface. Host permissions on `.env`, the secret files, Docker volumes, and backup media are therefore part of the single-node security boundary.

## Remaining secret-management work

P10 deliberately does not claim external vault/HSM integration or shared multi-node key management. When Monitor moves beyond the single-node deployment contract, migrate production credentials and Data Protection key protection to an appropriate managed secret/key store.

The future migration must preserve these invariants:

- no production ingestion keys, delivery provider secrets, bootstrap passwords, database passwords, or Data Protection key material are committed to source control;
- Monitor web nodes receive secrets through deployment/runtime configuration;
- multi-node deployments share the required Data Protection key ring through a durable protected store;
- secret rotation does not require rebuilding the application;
- database restore procedures restore the corresponding cryptographic state needed to decrypt persisted protected configuration.

Detailed single-node deployment, backup, restore, and recovery instructions are in `docs/production-deployment.md`.

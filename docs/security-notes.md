# Security notes

## Development ingestion key

The current Visual Studio development setup intentionally keeps the shared local Monitor ingestion key in the solution/launch configuration for convenience while the control plane is still being built and dogfooded locally.

This is accepted temporary technical debt, not the target production secret-management model.

Before Monitor is deployed beyond local development, move ingestion credentials and other sensitive delivery/bootstrap secrets out of tracked configuration into an external secret store/vault. The local developer path should likewise move to a non-tracked mechanism such as .NET User Secrets or a developer vault when that work is scheduled.

The migration must preserve these invariants:

- no production ingestion keys, webhook signing secrets, bootstrap passwords, or Data Protection key material are committed to source control;
- Monitor web nodes receive secrets through deployment/runtime configuration;
- multi-node deployments share the required Data Protection key ring through a durable protected store;
- secret rotation does not require rebuilding the application.

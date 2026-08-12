# Single-node secret files

Monitor reads files from `/run/secrets` after the normal ASP.NET Core configuration providers. A file name containing `__` maps to the equivalent configuration key with `:` separators, and the file contents become the value.

The single-node Compose deployment bind-mounts this directory read-only at `/run/secrets`.

`prepare-secrets.sh` creates:

- `ConnectionStrings__Monitor`
- `Monitor__BootstrapAdmin__Password`
- optionally `Monitor__IngestionApiKey`

Only this README and `*.example` files belong in source control. Real secret files are ignored by `.gitignore` and must be mode `0600` on the host.

After the first administrator has been created, the bootstrap password file can be removed. The shared ingestion key is optional and should be phased out in favor of per-component credentials.

The Data Protection key ring is **not** stored here. It lives in the persistent `monitor_data_protection` Docker volume and must be backed up together with SQL Server because it is required to decrypt protected alert-destination configuration.

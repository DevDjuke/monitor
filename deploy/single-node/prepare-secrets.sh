#!/usr/bin/env sh
set -eu

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
cd "$SCRIPT_DIR"

if [ ! -f .env ]; then
    echo "Missing deploy/single-node/.env. Copy .env.example to .env and fill in real values first." >&2
    exit 1
fi

set -a
# shellcheck disable=SC1091
. ./.env
set +a

: "${MSSQL_SA_PASSWORD:?MSSQL_SA_PASSWORD is required in .env}"
: "${MONITOR_BOOTSTRAP_PASSWORD:?MONITOR_BOOTSTRAP_PASSWORD is required in .env}"

mkdir -p secrets
umask 077

printf '%s' \
    "Server=sqlserver,1433;Database=Monitor;User Id=sa;Password=\"${MSSQL_SA_PASSWORD}\";Encrypt=True;TrustServerCertificate=True;MultipleActiveResultSets=True" \
    > secrets/ConnectionStrings__Monitor

printf '%s' "$MONITOR_BOOTSTRAP_PASSWORD" \
    > secrets/Monitor__BootstrapAdmin__Password

if [ -n "${MONITOR_INGESTION_API_KEY:-}" ]; then
    printf '%s' "$MONITOR_INGESTION_API_KEY" \
        > secrets/Monitor__IngestionApiKey
else
    rm -f secrets/Monitor__IngestionApiKey
fi

chmod 600 secrets/ConnectionStrings__Monitor secrets/Monitor__BootstrapAdmin__Password
if [ -f secrets/Monitor__IngestionApiKey ]; then
    chmod 600 secrets/Monitor__IngestionApiKey
fi

echo "Monitor secret files prepared in deploy/single-node/secrets/."

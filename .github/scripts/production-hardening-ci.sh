#!/usr/bin/env bash
set -euo pipefail

IMAGE="monitor:p10-ci"
NETWORK="monitor-p10-ci"
SQL_CONTAINER="monitor-p10-sql"
APP_CONTAINER="monitor-p10-app"
DP_VOLUME="monitor-p10-dp"
SQL_PASSWORD="MonitorP10Sql2026Password"
ADMIN_EMAIL="p10-admin@monitor.local"
ADMIN_PASSWORD="MonitorP10Admin2026Password"
BASE_URL="http://127.0.0.1:5097"
SECRETS_DIR="$(mktemp -d)"
LOGIN_HTML="$(mktemp)"
LOGIN_HEADERS="$(mktemp)"
COMPOSE_ENV="deploy/single-node/.env"

cleanup() {
  docker rm -f "$APP_CONTAINER" "$SQL_CONTAINER" >/dev/null 2>&1 || true
  docker network rm "$NETWORK" >/dev/null 2>&1 || true
  docker volume rm "$DP_VOLUME" >/dev/null 2>&1 || true
  rm -rf "$SECRETS_DIR" "$LOGIN_HTML" "$LOGIN_HEADERS"
  rm -f "$COMPOSE_ENV"
  rm -f deploy/single-node/secrets/ConnectionStrings__Monitor \
        deploy/single-node/secrets/Monitor__BootstrapAdmin__Password \
        deploy/single-node/secrets/Monitor__IngestionApiKey
}
trap cleanup EXIT

wait_for_sql() {
  for _ in $(seq 1 60); do
    if docker exec "$SQL_CONTAINER" /opt/mssql-tools18/bin/sqlcmd \
      -S localhost -U sa -P "$SQL_PASSWORD" -C -Q "SELECT 1" >/dev/null 2>&1; then
      return 0
    fi
    sleep 2
  done

  echo "SQL Server did not become ready." >&2
  docker logs "$SQL_CONTAINER" >&2 || true
  return 1
}

wait_for_monitor() {
  for _ in $(seq 1 60); do
    if curl -fsS "$BASE_URL/health/ready" >/dev/null 2>&1; then
      return 0
    fi
    sleep 2
  done

  echo "Monitor did not become ready." >&2
  docker logs "$APP_CONTAINER" >&2 || true
  return 1
}

start_monitor() {
  local gateway="$1"
  docker run -d \
    --name "$APP_CONTAINER" \
    --network "$NETWORK" \
    --read-only \
    --tmpfs /tmp:rw,noexec,nosuid,size=64m \
    --security-opt no-new-privileges:true \
    -p 127.0.0.1:5097:8080 \
    -v "$DP_VOLUME:/var/lib/monitor/data-protection-keys" \
    -v "$SECRETS_DIR:/run/secrets:ro" \
    -e ASPNETCORE_ENVIRONMENT=Production \
    -e ASPNETCORE_HTTP_PORTS=8080 \
    -e AllowedHosts=monitor.local \
    -e Production__PublicUrl=https://monitor.local \
    -e Production__MigrateOnStartup=false \
    -e Production__UseHttpsRedirection=false \
    -e Production__ForwardedHeaders__Enabled=true \
    -e Production__ForwardedHeaders__KnownProxies__0="$gateway" \
    -e Monitor__BootstrapAdmin__Email="$ADMIN_EMAIL" \
    -e Retention__Enabled=false \
    -e FailureAlerting__Enabled=false \
    -e UsageBudgets__Enabled=false \
    -e AlertDelivery__Enabled=false \
    -e ComponentCommands__Enabled=false \
    "$IMAGE" >/dev/null
}

echo "Building production image..."
docker build -t "$IMAGE" .

echo "Validating single-node Compose package..."
cat > "$COMPOSE_ENV" <<EOF
MONITOR_HOST=monitor.example.com
MONITOR_ADMIN_EMAIL=admin@example.com
MONITOR_BOOTSTRAP_PASSWORD=MonitorP10ComposeAdmin2026
MSSQL_SA_PASSWORD=MonitorP10ComposeSql2026Password
MONITOR_INGESTION_API_KEY=compose-test-key
MONITOR_MIGRATE_ON_STARTUP=true
MONITOR_HTTP_PORT=8088
MONITOR_HTTPS_PORT=8443
EOF
(
  cd deploy/single-node
  sh prepare-secrets.sh >/dev/null
  docker compose config --quiet
)
rm -f "$COMPOSE_ENV"
rm -f deploy/single-node/secrets/ConnectionStrings__Monitor \
      deploy/single-node/secrets/Monitor__BootstrapAdmin__Password \
      deploy/single-node/secrets/Monitor__IngestionApiKey

echo "Starting SQL Server..."
docker network create "$NETWORK" >/dev/null
docker volume create "$DP_VOLUME" >/dev/null
docker run -d \
  --name "$SQL_CONTAINER" \
  --network "$NETWORK" \
  --network-alias sqlserver \
  -e ACCEPT_EULA=Y \
  -e MSSQL_PID=Developer \
  -e MSSQL_SA_PASSWORD="$SQL_PASSWORD" \
  mcr.microsoft.com/mssql/server:2022-latest >/dev/null
wait_for_sql

printf '%s' \
  "Server=sqlserver,1433;Database=MonitorP10Ci;User Id=sa;Password=${SQL_PASSWORD};Encrypt=True;TrustServerCertificate=True;MultipleActiveResultSets=True" \
  > "$SECRETS_DIR/ConnectionStrings__Monitor"
printf '%s' "$ADMIN_PASSWORD" > "$SECRETS_DIR/Monitor__BootstrapAdmin__Password"
chmod 644 "$SECRETS_DIR"/*

echo "Verifying unsafe Production configuration fails fast..."
set +e
INVALID_OUTPUT=$(docker run --rm \
  --network "$NETWORK" \
  -v "$SECRETS_DIR:/run/secrets:ro" \
  -v "$DP_VOLUME:/var/lib/monitor/data-protection-keys" \
  -e ASPNETCORE_ENVIRONMENT=Production \
  -e AllowedHosts='*' \
  -e Production__PublicUrl=https://monitor.local \
  -e Production__UseHttpsRedirection=false \
  -e Monitor__BootstrapAdmin__Email="$ADMIN_EMAIL" \
  "$IMAGE" 2>&1)
INVALID_STATUS=$?
set -e
if [ "$INVALID_STATUS" -eq 0 ]; then
  echo "Unsafe wildcard AllowedHosts unexpectedly started in Production." >&2
  exit 1
fi
grep -q "wildcard '\*' is not allowed" <<<"$INVALID_OUTPUT"

echo "Applying migrations through explicit one-shot mode..."
docker run --rm \
  --network "$NETWORK" \
  -v "$SECRETS_DIR:/run/secrets:ro" \
  -e ASPNETCORE_ENVIRONMENT=Production \
  "$IMAGE" --migrate-only

GATEWAY=$(docker network inspect "$NETWORK" -f '{{(index .IPAM.Config 0).Gateway}}')

echo "Starting hardened Production container with startup migrations disabled..."
start_monitor "$GATEWAY"
wait_for_monitor

curl -fsS "$BASE_URL/health/live" >/dev/null
curl -fsS "$BASE_URL/health/ready" >/dev/null

USER_ID=$(docker exec "$APP_CONTAINER" id -u)
if [ "$USER_ID" = "0" ]; then
  echo "Monitor production container is running as root." >&2
  exit 1
fi

echo "Verifying trusted forwarded HTTPS and HSTS..."
HSTS_HEADERS=$(curl -fsSI \
  -H 'Host: monitor.local' \
  -H 'X-Forwarded-For: 203.0.113.10' \
  -H 'X-Forwarded-Proto: https' \
  "$BASE_URL/account/login")
grep -qi '^Strict-Transport-Security:' <<<"$HSTS_HEADERS"

echo "Signing in with bootstrap password loaded from /run/secrets..."
curl -fsS \
  -H 'Host: monitor.local' \
  -H 'X-Forwarded-For: 203.0.113.10' \
  -H 'X-Forwarded-Proto: https' \
  "$BASE_URL/account/login" -o "$LOGIN_HTML"
TOKEN=$(python3 - "$LOGIN_HTML" <<'PY'
import html, re, sys
text = open(sys.argv[1], encoding='utf-8').read()
m = re.search(r'name="__RequestVerificationToken"[^>]*value="([^"]+)"', text)
assert m, 'antiforgery token missing'
print(html.unescape(m.group(1)))
PY
)

HTTP_CODE=$(curl -sS -o /dev/null -D "$LOGIN_HEADERS" -w '%{http_code}' \
  -H 'Host: monitor.local' \
  -H 'X-Forwarded-For: 203.0.113.10' \
  -H 'X-Forwarded-Proto: https' \
  --data-urlencode "__RequestVerificationToken=$TOKEN" \
  --data-urlencode "Input.Email=$ADMIN_EMAIL" \
  --data-urlencode "Input.Password=$ADMIN_PASSWORD" \
  --data-urlencode 'Input.RememberMe=false' \
  --data-urlencode 'ReturnUrl=/' \
  "$BASE_URL/account/login")
if [ "$HTTP_CODE" != "302" ]; then
  echo "Login returned HTTP $HTTP_CODE instead of 302." >&2
  cat "$LOGIN_HEADERS" >&2
  exit 1
fi

AUTH_COOKIE=$(python3 - "$LOGIN_HEADERS" <<'PY'
import re, sys
headers = open(sys.argv[1], encoding='utf-8').read()
m = re.search(r'(?im)^set-cookie:\s*Monitor\.Auth=([^;]+)', headers)
assert m, 'Monitor.Auth cookie missing'
print(m.group(1))
PY
)

grep -qi '^Set-Cookie: Monitor.Auth=.*;.*secure' "$LOGIN_HEADERS"

AUTH_STATUS=$(curl -sS -o /dev/null -w '%{http_code}' \
  -H 'Host: monitor.local' \
  -H 'X-Forwarded-For: 203.0.113.10' \
  -H 'X-Forwarded-Proto: https' \
  -H "Cookie: Monitor.Auth=$AUTH_COOKIE" \
  "$BASE_URL/")
[ "$AUTH_STATUS" = "200" ]

echo "Verifying Data Protection key material was persisted..."
KEY_COUNT=$(docker run --rm -v "$DP_VOLUME:/keys:ro" alpine:3.22 sh -c 'find /keys -type f | wc -l' | tr -d ' ')
if [ "$KEY_COUNT" -lt 1 ]; then
  echo "No persistent Data Protection keys were written." >&2
  exit 1
fi

KEY_LIST_BEFORE=$(docker run --rm -v "$DP_VOLUME:/keys:ro" alpine:3.22 sh -c 'find /keys -type f -printf "%f\n" | sort')

echo "Recreating the app container while retaining SQL and Data Protection state..."
docker rm -f "$APP_CONTAINER" >/dev/null
rm -f "$SECRETS_DIR/Monitor__BootstrapAdmin__Password"
start_monitor "$GATEWAY"
wait_for_monitor

KEY_LIST_AFTER=$(docker run --rm -v "$DP_VOLUME:/keys:ro" alpine:3.22 sh -c 'find /keys -type f -printf "%f\n" | sort')
if [ "$KEY_LIST_BEFORE" != "$KEY_LIST_AFTER" ]; then
  echo "Data Protection key set unexpectedly changed during immediate restart." >&2
  diff <(printf '%s\n' "$KEY_LIST_BEFORE") <(printf '%s\n' "$KEY_LIST_AFTER") || true
  exit 1
fi

AUTH_AFTER_RESTART=$(curl -sS -o /dev/null -w '%{http_code}' \
  -H 'Host: monitor.local' \
  -H 'X-Forwarded-For: 203.0.113.10' \
  -H 'X-Forwarded-Proto: https' \
  -H "Cookie: Monitor.Auth=$AUTH_COOKIE" \
  "$BASE_URL/")
if [ "$AUTH_AFTER_RESTART" != "200" ]; then
  echo "Authentication cookie did not survive container recreation with persistent Data Protection keys." >&2
  exit 1
fi

echo "P10 production hardening integration checks passed."

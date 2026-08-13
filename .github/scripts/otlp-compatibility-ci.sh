#!/usr/bin/env bash
set -euo pipefail

HTTP_URL="http://127.0.0.1:5099"
GRPC_URL="http://127.0.0.1:5100"
DB_NAME="MonitorOtlpCompatibilityCi"
SQL_PASSWORD="MonitorOtlpCompatibilityCi!2026Password"
BOOTSTRAP_KEY="ci-p14-bootstrap-key"
ADMIN_EMAIL="p14-ci@monitor.local"
ADMIN_PASSWORD="MonitorP14Admin2026"
COOKIE_JAR="/tmp/monitor-p14-cookies.txt"
APP_LOG="/tmp/monitor-p14-ci.log"
FIXTURE_DLL=".github/fixtures/OtlpCompatibilityFixture/bin/Release/net10.0/OtlpCompatibilityFixture.dll"
FIXTURE_DIR="/tmp/p14-fixtures"

sql_container=$(docker ps --filter 'ancestor=mcr.microsoft.com/mssql/server:2022-latest' --format '{{.ID}}' | head -n 1)
test -n "$sql_container"

scalar() {
  docker exec "$sql_container" /opt/mssql-tools18/bin/sqlcmd \
    -S localhost -U sa -P "$SQL_PASSWORD" -C -b -d "$DB_NAME" -h -1 -W \
    -Q "SET NOCOUNT ON; $1" | tr -d '\r' | xargs
}

page_token() {
  local url="$1" output="$2"
  curl -fsS -b "$COOKIE_JAR" "$url" -o "$output"
  python3 - "$output" <<'PY'
import re,sys
html=open(sys.argv[1],encoding='utf-8').read()
m=re.search(r'name="__RequestVerificationToken"[^>]*value="([^"]+)"',html)
assert m, f'antiforgery token not found in {sys.argv[1]}'
print(m.group(1))
PY
}

register_component() {
  local name="$1" slug="$2"
  curl -fsS \
    -H "X-Monitor-Key: $BOOTSTRAP_KEY" \
    -H 'Content-Type: application/json' \
    -d "{\"name\":\"$name\",\"slug\":\"$slug\",\"type\":\"Agent\",\"environment\":\"production\",\"version\":\"14.0.0\"}" \
    "$HTTP_URL/api/components/register"
}

issue_credential() {
  local component_id="$1" name="$2" output="$3"
  local token status
  token=$(page_token "$HTTP_URL/components/$component_id" "/tmp/${output}-before.html")
  status=$(curl -sS -o /dev/null -w '%{http_code}' \
    -b "$COOKIE_JAR" -c "$COOKIE_JAR" \
    --data-urlencode "__RequestVerificationToken=$token" \
    --data-urlencode "CredentialInput.Name=$name" \
    "$HTTP_URL/components/$component_id?handler=CreateCredential")
  test "$status" = "302"
  curl -fsS -b "$COOKIE_JAR" "$HTTP_URL/components/$component_id" -o "/tmp/${output}-issued.html"
  python3 - "/tmp/${output}-issued.html" <<'PY'
import re,sys
html=open(sys.argv[1],encoding='utf-8').read()
m=re.search(r'mon_c_[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+',html)
assert m, 'one-time component key not found'
print(m.group(0))
PY
}

post_json() {
  local signal="$1" key="$2" body="$3" response="$4" headers="$5"
  curl -sS -D "$headers" -o "$response" -w '%{http_code}' \
    -H "X-Monitor-Key: $key" \
    -H 'Content-Type: application/json' \
    --data-binary "@$body" \
    "$HTTP_URL/v1/$signal"
}

echo 'Applying migrations...'
dotnet tool install --global dotnet-ef --version 10.0.10 >/dev/null
dotnet ef database update \
  --project src/Monitor.Infrastructure \
  --startup-project src/Monitor.Web \
  --context MonitorDbContext \
  --connection "$ConnectionStrings__Monitor"

echo 'Starting Monitor with separate HTTP/1 and h2c HTTP/2 listeners...'
(
  cd src/Monitor.Web
  exec dotnet bin/Release/net10.0/Monitor.Web.dll
) >"$APP_LOG" 2>&1 &
app_pid=$!

cleanup() {
  status=$?
  if [ "$status" -ne 0 ]; then
    echo '=== Monitor P14 log ==='
    cat "$APP_LOG" 2>/dev/null || true
  fi
  kill "$app_pid" 2>/dev/null || true
  wait "$app_pid" 2>/dev/null || true
  return "$status"
}
trap cleanup EXIT

for attempt in {1..60}; do
  if curl -fsS "$HTTP_URL/health/ready" >/dev/null; then break; fi
  if ! kill -0 "$app_pid" 2>/dev/null; then
    cat "$APP_LOG"
    exit 1
  fi
  sleep 1
done
curl -fsS "$HTTP_URL/health/ready" >/dev/null

matching_json=$(register_component 'P14 Agent' 'monitor-ci-p14-agent')
other_json=$(register_component 'Other P14 Agent' 'monitor-ci-other-p14-agent')
matching_id=$(python3 -c 'import json,sys; print(json.loads(sys.stdin.read())["id"])' <<< "$matching_json")
other_id=$(python3 -c 'import json,sys; print(json.loads(sys.stdin.read())["id"])' <<< "$other_json")

curl -fsS -c "$COOKIE_JAR" "$HTTP_URL/account/login" -o /tmp/p14-login.html
login_token=$(python3 - <<'PY'
import re
html=open('/tmp/p14-login.html',encoding='utf-8').read()
m=re.search(r'name="__RequestVerificationToken"[^>]*value="([^"]+)"',html)
assert m
print(m.group(1))
PY
)
login_status=$(curl -sS -o /dev/null -w '%{http_code}' \
  -b "$COOKIE_JAR" -c "$COOKIE_JAR" \
  --data-urlencode "__RequestVerificationToken=$login_token" \
  --data-urlencode "Input.Email=$ADMIN_EMAIL" \
  --data-urlencode "Input.Password=$ADMIN_PASSWORD" \
  --data-urlencode 'Input.RememberMe=false' \
  "$HTTP_URL/account/login")
test "$login_status" = "302"

matching_key=$(issue_credential "$matching_id" 'P14 matching key' 'p14-matching')
other_key=$(issue_credential "$other_id" 'P14 other key' 'p14-other')
test "$matching_key" != "$other_key"

dotnet "$FIXTURE_DLL" write "$FIXTURE_DIR" 'P14 Agent' production

invalid_auth_status=$(post_json metrics definitely-invalid "$FIXTURE_DIR/metrics.json" /tmp/p14-invalid.json /tmp/p14-invalid.headers)
test "$invalid_auth_status" = "401"

wrong_scope_status=$(post_json metrics "$other_key" "$FIXTURE_DIR/metrics.json" /tmp/p14-wrong.json /tmp/p14-wrong.headers)
test "$wrong_scope_status" = "403"

malformed_status=$(curl -sS -o /dev/null -w '%{http_code}' \
  -H "X-Monitor-Key: $matching_key" -H 'Content-Type: application/json' \
  -d '{not-json' "$HTTP_URL/v1/metrics")
test "$malformed_status" = "400"

unsupported_status=$(curl -sS -o /dev/null -w '%{http_code}' \
  -H "X-Monitor-Key: $matching_key" -H 'Content-Type: application/xml' \
  -d '<metrics />' "$HTTP_URL/v1/metrics")
test "$unsupported_status" = "415"

for signal in traces logs metrics; do
  status=$(post_json "$signal" "$matching_key" "$FIXTURE_DIR/$signal.json" "/tmp/p14-$signal.json" "/tmp/p14-$signal.headers")
  test "$status" = "200"
  grep -qi '^content-type: application/json' "/tmp/p14-$signal.headers"
  python3 - "/tmp/p14-$signal.json" <<'PY'
import json,sys
json.load(open(sys.argv[1],encoding='utf-8'))
PY
done

test "$(scalar "SELECT COUNT(*) FROM Runs WHERE ComponentId='$matching_id';")" = "1"
test "$(scalar "SELECT COUNT(*) FROM Spans s INNER JOIN Runs r ON r.Id=s.RunId WHERE r.ComponentId='$matching_id';")" = "1"
test "$(scalar "SELECT COUNT(*) FROM LogEvents WHERE ComponentId='$matching_id' AND Message=N'P14 transport log';")" = "1"
test "$(scalar "SELECT COUNT(*) FROM MetricPoints WHERE ComponentId='$matching_id' AND Name=N'p14.transport.gauge';")" = "1"

gzip -c "$FIXTURE_DIR/metrics.json" >/tmp/p14-metrics.json.gz
gzip_status=$(curl -sS -o /tmp/p14-gzip.json -w '%{http_code}' \
  -H "X-Monitor-Key: $matching_key" \
  -H 'Content-Type: application/json; charset=utf-8' \
  -H 'Content-Encoding: gzip' \
  --data-binary @/tmp/p14-metrics.json.gz \
  "$HTTP_URL/v1/metrics")
test "$gzip_status" = "200"
test "$(scalar "SELECT COUNT(*) FROM MetricPoints WHERE ComponentId='$matching_id' AND Name=N'p14.transport.gauge';")" = "1"

dotnet "$FIXTURE_DLL" grpc-expect "$FIXTURE_DIR" "$GRPC_URL" definitely-invalid Unauthenticated
dotnet "$FIXTURE_DLL" grpc-expect "$FIXTURE_DIR" "$GRPC_URL" "$other_key" PermissionDenied
dotnet "$FIXTURE_DLL" grpc "$FIXTURE_DIR" "$GRPC_URL" "$matching_key"

# The same payload crossed JSON first and gRPC second. Counts must remain stable,
# proving both transports share the existing importer/deduplication path.
test "$(scalar "SELECT COUNT(*) FROM Runs WHERE ComponentId='$matching_id';")" = "1"
test "$(scalar "SELECT COUNT(*) FROM Spans s INNER JOIN Runs r ON r.Id=s.RunId WHERE r.ComponentId='$matching_id';")" = "1"
test "$(scalar "SELECT COUNT(*) FROM LogEvents WHERE ComponentId='$matching_id' AND Message=N'P14 transport log';")" = "1"
test "$(scalar "SELECT COUNT(*) FROM MetricPoints WHERE ComponentId='$matching_id' AND Name=N'p14.transport.gauge';")" = "1"

docker run --rm \
  -e MONITOR_HOST=monitor.local \
  -v "$PWD/deploy/single-node/Caddyfile:/etc/caddy/Caddyfile:ro" \
  caddy:2-alpine caddy validate --config /etc/caddy/Caddyfile >/dev/null

grep -q 'h2c://monitor:4317' deploy/single-node/Caddyfile
grep -q 'Kestrel__Endpoints__Grpc__Protocols: Http2' deploy/single-node/compose.yml

echo 'P14 OTLP compatibility integration passed.'

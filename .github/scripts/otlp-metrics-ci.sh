#!/usr/bin/env bash
set -euo pipefail

BASE_URL="http://127.0.0.1:5099"
DB_NAME="MonitorMetricsCi"
SQL_PASSWORD="MonitorMetricsCi!2026Password"
BOOTSTRAP_KEY="ci-metrics-bootstrap-key"
ADMIN_EMAIL="metrics-ci@monitor.local"
ADMIN_PASSWORD="MonitorMetricsAdmin2026"
COOKIE_JAR="/tmp/monitor-metrics-cookies.txt"
APP_LOG="/tmp/monitor-metrics-ci.log"
FIXTURE_DLL=".github/fixtures/OtlpMetricsFixture/bin/Release/net10.0/OtlpMetricsFixture.dll"

sql_container=$(docker ps --filter 'ancestor=mcr.microsoft.com/mssql/server:2022-latest' --format '{{.ID}}' | head -n 1)
test -n "$sql_container"

sql() {
  docker exec "$sql_container" /opt/mssql-tools18/bin/sqlcmd \
    -S localhost -U sa -P "$SQL_PASSWORD" -C -b -d "$DB_NAME" -Q "$1"
}

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
    -d "{\"name\":\"$name\",\"slug\":\"$slug\",\"type\":\"Agent\",\"environment\":\"production\",\"version\":\"13.0.0\"}" \
    "$BASE_URL/api/components/register"
}

issue_credential() {
  local component_id="$1" name="$2" output="$3"
  local token status
  token=$(page_token "$BASE_URL/components/$component_id" "/tmp/${output}-before.html")
  status=$(curl -sS -o /dev/null -w '%{http_code}' \
    -b "$COOKIE_JAR" -c "$COOKIE_JAR" \
    --data-urlencode "__RequestVerificationToken=$token" \
    --data-urlencode "CredentialInput.Name=$name" \
    "$BASE_URL/components/$component_id?handler=CreateCredential")
  test "$status" = "302"
  curl -fsS -b "$COOKIE_JAR" "$BASE_URL/components/$component_id" -o "/tmp/${output}-issued.html"
  python3 - "/tmp/${output}-issued.html" <<'PY'
import re,sys
html=open(sys.argv[1],encoding='utf-8').read()
m=re.search(r'mon_c_[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+',html)
assert m, 'one-time component key not found'
print(m.group(0))
PY
}

echo "Applying migrations..."
dotnet tool install --global dotnet-ef --version 10.0.10 >/dev/null
dotnet ef database update \
  --project src/Monitor.Infrastructure \
  --startup-project src/Monitor.Web \
  --context MonitorDbContext \
  --connection "$ConnectionStrings__Monitor"

echo "Starting Monitor..."
(
  cd src/Monitor.Web
  exec dotnet bin/Release/net10.0/Monitor.Web.dll
) >"$APP_LOG" 2>&1 &
app_pid=$!

cleanup() {
  status=$?
  if [ "$status" -ne 0 ]; then
    echo '=== Monitor P13 log ==='
    cat "$APP_LOG" 2>/dev/null || true
    echo '=== MetricPoints ==='
    sql "SELECT Id,ComponentId,Name,Kind,Temporality,IsMonotonic,HasRecordedValue,[Timestamp],NumericValue,[Count],[Sum],Min,Max,Scale,ZeroCount,DedupeKey FROM MetricPoints ORDER BY [Timestamp];" 2>/dev/null || true
  fi
  kill "$app_pid" 2>/dev/null || true
  wait "$app_pid" 2>/dev/null || true
  return "$status"
}
trap cleanup EXIT

for attempt in {1..60}; do
  if curl -fsS "$BASE_URL/health/ready" >/dev/null; then break; fi
  if ! kill -0 "$app_pid" 2>/dev/null; then
    cat "$APP_LOG"
    exit 1
  fi
  sleep 1
done
curl -fsS "$BASE_URL/health/ready" >/dev/null

matching_json=$(register_component 'Metrics CI Agent' 'monitor-ci-metrics-ci-agent')
other_json=$(register_component 'Other Metrics Agent' 'monitor-ci-other-metrics-agent')
matching_id=$(python3 -c 'import json,sys; print(json.loads(sys.stdin.read())["id"])' <<< "$matching_json")
other_id=$(python3 -c 'import json,sys; print(json.loads(sys.stdin.read())["id"])' <<< "$other_json")
test -n "$matching_id"
test -n "$other_id"

curl -fsS -c "$COOKIE_JAR" "$BASE_URL/account/login" -o /tmp/metrics-login.html
login_token=$(python3 - <<'PY'
import re
html=open('/tmp/metrics-login.html',encoding='utf-8').read()
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
  "$BASE_URL/account/login")
test "$login_status" = "302"

matching_key=$(issue_credential "$matching_id" 'P13 metrics key' 'metrics-matching')
other_key=$(issue_credential "$other_id" 'P13 other key' 'metrics-other')
test "$matching_key" != "$other_key"

dotnet "$FIXTURE_DLL" write /tmp/metrics-request.bin 'Metrics CI Agent' production

invalid_auth_status=$(curl -sS -o /dev/null -w '%{http_code}' \
  -H 'X-Monitor-Key: definitely-invalid' \
  -H 'Content-Type: application/x-protobuf' \
  --data-binary @/tmp/metrics-request.bin \
  "$BASE_URL/v1/metrics")
test "$invalid_auth_status" = "401"

wrong_scope_status=$(curl -sS -o /dev/null -w '%{http_code}' \
  -H "X-Monitor-Key: $other_key" \
  -H 'Content-Type: application/x-protobuf' \
  --data-binary @/tmp/metrics-request.bin \
  "$BASE_URL/v1/metrics")
test "$wrong_scope_status" = "403"

json_status=$(curl -sS -o /dev/null -w '%{http_code}' \
  -H "X-Monitor-Key: $matching_key" \
  -H 'Content-Type: application/json' \
  -d '{}' "$BASE_URL/v1/metrics")
test "$json_status" = "415"

first_status=$(curl -sS -o /tmp/metrics-response.bin -w '%{http_code}' \
  -H "X-Monitor-Key: $matching_key" \
  -H 'Content-Type: application/x-protobuf' \
  --data-binary @/tmp/metrics-request.bin \
  "$BASE_URL/v1/metrics")
test "$first_status" = "200"
dotnet "$FIXTURE_DLL" verify /tmp/metrics-response.bin 2

test "$(scalar "SELECT COUNT(*) FROM MetricPoints WHERE ComponentId='$matching_id';")" = "5"

second_status=$(curl -sS -o /tmp/metrics-response-duplicate.bin -w '%{http_code}' \
  -H "X-Monitor-Key: $matching_key" \
  -H 'Content-Type: application/protobuf' \
  --data-binary @/tmp/metrics-request.bin \
  "$BASE_URL/v1/metrics")
test "$second_status" = "200"
dotnet "$FIXTURE_DLL" verify /tmp/metrics-response-duplicate.bin 2
test "$(scalar "SELECT COUNT(*) FROM MetricPoints WHERE ComponentId='$matching_id';")" = "5"
test "$(scalar "SELECT COUNT(DISTINCT DedupeKey) FROM MetricPoints WHERE ComponentId='$matching_id';")" = "5"

kind_state=$(scalar "
SELECT CONCAT(
 SUM(CASE WHEN Kind=1 THEN 1 ELSE 0 END),'|',
 SUM(CASE WHEN Kind=2 THEN 1 ELSE 0 END),'|',
 SUM(CASE WHEN Kind=3 THEN 1 ELSE 0 END),'|',
 SUM(CASE WHEN Kind=4 THEN 1 ELSE 0 END),'|',
 SUM(CASE WHEN Kind=5 THEN 1 ELSE 0 END))
FROM MetricPoints WHERE ComponentId='$matching_id';")
test "$kind_state" = "1|1|1|1|1"

gauge_state=$(scalar "SELECT CONCAT(CONVERT(varchar(32),NumericValue),'|',CASE WHEN AttributesJson LIKE '%critical%' THEN 1 ELSE 0 END,'|',CASE WHEN ResourceAttributesJson LIKE '%p13%' THEN 1 ELSE 0 END,'|',CASE WHEN MetricMetadataJson LIKE '%stable%' THEN 1 ELSE 0 END,'|',CASE WHEN ExemplarsJson LIKE '%00112233445566778899aabbccddeeff%' THEN 1 ELSE 0 END) FROM MetricPoints WHERE ComponentId='$matching_id' AND Name=N'queue.depth';")
test "$gauge_state" = "7|1|1|1|1"

sum_state=$(scalar "SELECT CONCAT(CONVERT(varchar(32),NumericValue),'|',Temporality,'|',CASE WHEN IsMonotonic=1 THEN 1 ELSE 0 END) FROM MetricPoints WHERE ComponentId='$matching_id' AND Name=N'requests.total';")
test "$sum_state" = "42|2|1"

hist_state=$(scalar "SELECT CONCAT(CONVERT(varchar(32),[Count]),'|',CONVERT(varchar(32),[Sum]),'|',CONVERT(varchar(32),Min),'|',CONVERT(varchar(32),Max),'|',CASE WHEN BucketCountsJson=N'[1,2,1]' THEN 1 ELSE 0 END,'|',CASE WHEN ExplicitBoundsJson=N'[10,50]' THEN 1 ELSE 0 END) FROM MetricPoints WHERE ComponentId='$matching_id' AND Name=N'request.duration';")
test "$hist_state" = "4|100|5|70|1|1"

exp_state=$(scalar "SELECT CONCAT(CONVERT(varchar(32),[Count]),'|',Scale,'|',CONVERT(varchar(32),ZeroCount),'|',CASE WHEN PositiveBucketsJson LIKE '%\"Counts\":[2]%' THEN 1 ELSE 0 END,'|',CASE WHEN NegativeBucketsJson LIKE '%\"Counts\":[1]%' THEN 1 ELSE 0 END) FROM MetricPoints WHERE ComponentId='$matching_id' AND Name=N'payload.size';")
test "$exp_state" = "4|2|1|1|1"

summary_state=$(scalar "SELECT CONCAT(CONVERT(varchar(32),[Count]),'|',CONVERT(varchar(32),[Sum]),'|',CASE WHEN QuantilesJson LIKE '%\"Quantile\":0.5%' AND QuantilesJson LIKE '%\"Quantile\":0.9%' THEN 1 ELSE 0 END) FROM MetricPoints WHERE ComponentId='$matching_id' AND Name=N'legacy.latency';")
test "$summary_state" = "3|60|1"

curl -fsS -b "$COOKIE_JAR" \
  "$BASE_URL/metrics?Window=24h&Search=critical&Name=queue.depth&Kind=Gauge&Take=50" \
  -o /tmp/metrics-page.html
grep -q '>Metrics<' /tmp/metrics-page.html
grep -q 'queue.depth' /tmp/metrics-page.html
grep -q 'critical' /tmp/metrics-page.html
grep -q 'Gauge' /tmp/metrics-page.html

metrics_token=$(page_token "$BASE_URL/metrics?Window=24h&Search=critical&Name=queue.depth&Kind=Gauge" /tmp/metrics-save.html)
save_status=$(curl -sS -o /dev/null -w '%{http_code}' \
  -b "$COOKIE_JAR" -c "$COOKIE_JAR" \
  --data-urlencode "__RequestVerificationToken=$metrics_token" \
  --data-urlencode 'surface=Metrics' \
  --data-urlencode 'name=Critical queue depth' \
  --data-urlencode 'queryString=?Window=24h&Search=critical&Name=queue.depth&Kind=Gauge&evil=1' \
  --data-urlencode 'isPinned=true' \
  --data-urlencode 'returnUrl=/metrics?Window=24h&Search=critical&Name=queue.depth&Kind=Gauge' \
  "$BASE_URL/saved-views?handler=Create")
test "$save_status" = "302"
saved_state=$(scalar "SELECT CONCAT(Surface,'|',QueryString,'|',CASE WHEN IsPinned=1 THEN 1 ELSE 0 END) FROM SavedViews WHERE NameKey=N'CRITICAL QUEUE DEPTH';")
test "$saved_state" = "8|?Window=24h&Search=critical&Name=queue.depth&Kind=Gauge|1"

# Retention is part of the production worker. Age one point beyond the configured one-day P13 CI window,
# then allow for the worker's 10-second initial delay or one-minute subsequent sweep.
sql "UPDATE MetricPoints SET [Timestamp]=DATEADD(day,-2,SYSUTCDATETIME()) WHERE ComponentId='$matching_id' AND Name=N'legacy.latency';"
retained=1
for attempt in {1..80}; do
  retained=$(scalar "SELECT COUNT(*) FROM MetricPoints WHERE ComponentId='$matching_id' AND Name=N'legacy.latency';")
  if [ "$retained" = "0" ]; then break; fi
  sleep 1
done
test "$retained" = "0"
test "$(scalar "SELECT COUNT(*) FROM MetricPoints WHERE ComponentId='$matching_id';")" = "4"

echo 'P13 OTLP metrics integration passed.'

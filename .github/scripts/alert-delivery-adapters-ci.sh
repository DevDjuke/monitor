#!/usr/bin/env bash
set -euo pipefail

BASE_URL="${BASE_URL:-http://127.0.0.1:5095}"
DB_NAME="${DB_NAME:-MonitorAlertAdaptersCi}"
SQL_PASSWORD="${SQL_PASSWORD:-MonitorAdaptersCi!2026Password}"
COOKIE_JAR=/tmp/monitor-p9-cookies.txt
APP_LOG=/tmp/monitor-p9-app.log
FIXTURE_LOG=/tmp/monitor-p9-fixture.log

sql_container=""
app_pid=""
fixture_pid=""

sql_scalar() {
  docker exec "$sql_container" /opt/mssql-tools18/bin/sqlcmd \
    -S localhost -U sa -P "$SQL_PASSWORD" -C -b -d "$DB_NAME" -h -1 -W \
    -Q "SET NOCOUNT ON; $1" | tr -d '\r' | xargs
}

page_token() {
  local url="$1" output="$2"
  curl -fsS -b "$COOKIE_JAR" "$url" -o "$output"
  python3 - "$output" <<'PY'
import re, sys
html = open(sys.argv[1], encoding='utf-8').read()
m = re.search(r'name="__RequestVerificationToken"[^>]*value="([^"]+)"', html)
assert m, f'antiforgery token not found in {sys.argv[1]}'
print(m.group(1))
PY
}

post_form() {
  local url="$1"; shift
  local args=(-sS -o /dev/null -w '%{http_code}' -b "$COOKIE_JAR" -c "$COOKIE_JAR")
  for value in "$@"; do args+=(--data-urlencode "$value"); done
  curl "${args[@]}" "$url"
}

create_destination() {
  local name="$1" kind="$2"; shift 2
  local token status
  token=$(page_token "$BASE_URL/alerts" /tmp/monitor-p9-alerts.html)
  status=$(post_form "$BASE_URL/alerts?handler=CreateDestination" \
    "__RequestVerificationToken=$token" \
    "DestinationInput.Name=$name" \
    "DestinationInput.Kind=$kind" \
    "$@")
  test "$status" = "302"
}

cleanup() {
  local status=$?
  if [[ $status -ne 0 ]]; then
    echo '=== Monitor P9 app log ==='; cat "$APP_LOG" 2>/dev/null || true
    echo '=== Monitor P9 fixture log ==='; cat "$FIXTURE_LOG" 2>/dev/null || true
    echo '=== HTTP fixture evidence ==='; cat /tmp/monitor-p9-http.ndjson 2>/dev/null || true
    echo '=== SMTP fixture evidence ==='; cat /tmp/monitor-p9-smtp.eml 2>/dev/null || true
  fi
  [[ -n "$app_pid" ]] && kill "$app_pid" 2>/dev/null || true
  [[ -n "$fixture_pid" ]] && kill "$fixture_pid" 2>/dev/null || true
  [[ -n "$app_pid" ]] && wait "$app_pid" 2>/dev/null || true
  [[ -n "$fixture_pid" ]] && wait "$fixture_pid" 2>/dev/null || true
  return "$status"
}
trap cleanup EXIT

dotnet tool install --global dotnet-ef --version 10.0.10
dotnet ef database update \
  --project src/Monitor.Infrastructure \
  --startup-project src/Monitor.Web \
  --context MonitorDbContext \
  --connection "$ConnectionStrings__Monitor"

sql_container=$(docker ps --filter 'ancestor=mcr.microsoft.com/mssql/server:2022-latest' --format '{{.ID}}' | head -n 1)
test -n "$sql_container"

python3 .github/scripts/alert-adapter-fixture.py > "$FIXTURE_LOG" 2>&1 &
fixture_pid=$!
for attempt in {1..20}; do
  if (echo > /dev/tcp/127.0.0.1/5096) 2>/dev/null && (echo > /dev/tcp/127.0.0.1/2525) 2>/dev/null; then break; fi
  sleep 1
done
kill -0 "$fixture_pid"

(
  cd src/Monitor.Web
  exec dotnet bin/Release/net10.0/Monitor.Web.dll
) > "$APP_LOG" 2>&1 &
app_pid=$!

app_ready=false
for attempt in {1..45}; do
  if curl -fsS "$BASE_URL/api/health" > /dev/null; then app_ready=true; break; fi
  if ! kill -0 "$app_pid" 2>/dev/null; then echo 'Monitor exited while starting.'; exit 1; fi
  sleep 1
done
test "$app_ready" = true

# Authenticate through the real operator surface.
curl -fsS -c "$COOKIE_JAR" "$BASE_URL/account/login" -o /tmp/monitor-p9-login.html
login_token=$(python3 - <<'PY'
import re
html=open('/tmp/monitor-p9-login.html', encoding='utf-8').read()
m=re.search(r'name="__RequestVerificationToken"[^>]*value="([^"]+)"', html); assert m; print(m.group(1))
PY
)
login_status=$(post_form "$BASE_URL/account/login" \
  "__RequestVerificationToken=$login_token" \
  'Input.Email=adapters-ci@monitor.local' \
  'Input.Password=MonitorAdaptersAdmin2026' \
  'Input.RememberMe=false')
test "$login_status" = "302"

# Create every supported destination through /alerts.
create_destination 'P9 signed webhook' Webhook \
  'DestinationInput.EndpointUrl=http://127.0.0.1:5096/webhook' \
  'DestinationInput.Secret=p9-signing-secret-2026'
create_destination 'P9 Slack' Slack \
  'DestinationInput.EndpointUrl=http://127.0.0.1:5096/slack'
create_destination 'P9 Teams' MicrosoftTeams \
  'DestinationInput.EndpointUrl=http://127.0.0.1:5096/teams'
create_destination 'P9 Discord' Discord \
  'DestinationInput.EndpointUrl=http://127.0.0.1:5096/discord'
create_destination 'P9 PagerDuty' PagerDuty \
  'DestinationInput.Secret=p9-pagerduty-routing-key-2026'
create_destination 'P9 Email' Email \
  'DestinationInput.EmailRecipient=ops@monitor.local' \
  'DestinationInput.SmtpHost=127.0.0.1' \
  'DestinationInput.SmtpPort=2525' \
  'DestinationInput.SmtpFromAddress=monitor@monitor.local' \
  'DestinationInput.SmtpEnableSsl=false'

# Route PagerDuty to the local fixture only inside this integration database.
sql_scalar "UPDATE AlertDeliveryDestinations SET EndpointUrl = 'http://127.0.0.1:5096/pagerduty' WHERE Name = N'P9 PagerDuty'; SELECT @@ROWCOUNT;" | grep -q '^1$'

configured=$(sql_scalar "
SELECT CONCAT(
 (SELECT COUNT(*) FROM AlertDeliveryDestinations WHERE Name LIKE N'P9 %'), '|',
 (SELECT COUNT(*) FROM AlertDeliveryDestinations WHERE Name = N'P9 signed webhook' AND Kind = 1), '|',
 (SELECT COUNT(*) FROM AlertDeliveryDestinations WHERE Name = N'P9 Slack' AND Kind = 2 AND EndpointUrl LIKE N'http://127.0.0.1:5096/%'), '|',
 (SELECT COUNT(*) FROM AlertDeliveryDestinations WHERE Name = N'P9 Teams' AND Kind = 3), '|',
 (SELECT COUNT(*) FROM AlertDeliveryDestinations WHERE Name = N'P9 Discord' AND Kind = 4), '|',
 (SELECT COUNT(*) FROM AlertDeliveryDestinations WHERE Name = N'P9 PagerDuty' AND Kind = 5), '|',
 (SELECT COUNT(*) FROM AlertDeliveryDestinations WHERE Name = N'P9 Email' AND Kind = 6 AND EndpointUrl = N'mailto:ops@monitor.local'), '|',
 (SELECT COUNT(*) FROM AlertDeliveryDestinations WHERE Name LIKE N'P9 %' AND LEN(ProtectedSecret) > 20)
);")
test "$configured" = '6|1|1|1|1|1|1|6'

# Secret-bearing provider endpoints/config must not appear in immutable audit JSON.
audit_leaks=$(sql_scalar "SELECT COUNT(*) FROM AuditEvents WHERE TargetType = N'alert-destination' AND (AfterJson LIKE N'%/slack%' OR AfterJson LIKE N'%/teams%' OR AfterJson LIKE N'%/discord%' OR AfterJson LIKE N'%p9-pagerduty-routing-key-2026%');")
test "$audit_leaks" = '0'

# Exercise provider-native test sending first, including SMTP.
for name in 'P9 signed webhook' 'P9 Slack' 'P9 Teams' 'P9 Discord' 'P9 PagerDuty' 'P9 Email'; do
  id=$(sql_scalar "SELECT CONVERT(varchar(36), Id) FROM AlertDeliveryDestinations WHERE Name = N'$name';")
  token=$(page_token "$BASE_URL/alerts" /tmp/monitor-p9-test.html)
  status=$(post_form "$BASE_URL/alerts?handler=TestDestination&destinationId=$id" "__RequestVerificationToken=$token")
  test "$status" = '302'
done

healthy=$(sql_scalar "SELECT COUNT(*) FROM AlertDeliveryDestinations WHERE Name LIKE N'P9 %' AND LastSuccessAt IS NOT NULL AND (LastFailureAt IS NULL OR LastSuccessAt >= LastFailureAt);")
test "$healthy" = '6'

# Produce the canonical failure evidence and create an all-destinations rule.
Monitor__BaseUrl="$BASE_URL" Monitor__IngestionApiKey=ci-adapters-ingestion-key \
  dotnet samples/Monitor.OtlpSampleWorker/bin/Release/net10.0/Monitor.OtlpSampleWorker.dll

failure_group_id=''
for attempt in {1..30}; do
  failure_group_id=$(sql_scalar "SELECT TOP(1) CONVERT(varchar(36), Id) FROM FailureGroups WHERE FailureType = N'RateLimitError' AND Operation = N'Generate recommendation' AND Occurrences = 2;")
  [[ -n "$failure_group_id" ]] && break
  sleep 1
done
test -n "$failure_group_id"

editor_token=$(page_token "$BASE_URL/alerts/rules/edit?failureGroupId=$failure_group_id" /tmp/monitor-p9-rule.html)
create_rule_status=$(post_form "$BASE_URL/alerts/rules/edit?handler=Save" \
  "__RequestVerificationToken=$editor_token" \
  "Input.FailureGroupId=$failure_group_id" \
  'Input.Name=P9 all-channel alert' \
  'Input.Threshold=2' \
  'Input.WindowMinutes=60' \
  'Input.CooldownMinutes=0' \
  'Input.Enabled=true' \
  'Input.DeliverToAllEnabledDestinations=true')
test "$create_rule_status" = '302'
rule_id=$(sql_scalar "SELECT CONVERT(varchar(36), Id) FROM FailureAlertRules WHERE Name = N'P9 all-channel alert';")
test -n "$rule_id"

# The normal evaluator must enqueue exactly one durable row per enabled channel and
# the normal dispatcher must deliver all six through their real adapters.
delivered=false
state=''
for attempt in {1..45}; do
  state=$(sql_scalar "
SELECT CONCAT(
 (SELECT COUNT(*) FROM FailureAlertEvents WHERE AlertRuleId = '$rule_id'), '|',
 (SELECT COUNT(*) FROM AlertDeliveries d JOIN FailureAlertEvents e ON e.Id=d.AlertEventId WHERE e.AlertRuleId='$rule_id'), '|',
 (SELECT COUNT(*) FROM AlertDeliveries d JOIN FailureAlertEvents e ON e.Id=d.AlertEventId WHERE e.AlertRuleId='$rule_id' AND d.Status=3), '|',
 (SELECT COUNT(*) FROM AlertDeliveries d JOIN FailureAlertEvents e ON e.Id=d.AlertEventId WHERE e.AlertRuleId='$rule_id' AND d.Status=4)
);")
  if [[ "$state" = '1|6|6|0' ]]; then delivered=true; break; fi
  sleep 1
done
if [[ "$delivered" != true ]]; then echo "P9 delivery state: $state"; exit 1; fi

# Verify provider-specific wire contracts plus legacy HMAC signing.
python3 - <<'PY'
import hashlib, hmac, json
rows=[json.loads(x) for x in open('/tmp/monitor-p9-http.ndjson', encoding='utf-8') if x.strip()]
def event(path, event_type='failure.alert.triggered'):
    matches=[r for r in rows if r['path'].startswith(path) and r['headers'].get('X-Monitor-Event') == event_type]
    assert matches, (path, event_type, rows)
    return matches[-1]
webhook=event('/webhook')
ts=webhook['headers']['X-Monitor-Timestamp']
expected=hmac.new(b'p9-signing-secret-2026', f"{ts}.{webhook['body']}".encode(), hashlib.sha256).hexdigest()
assert webhook['headers']['X-Monitor-Signature'] == f'sha256={expected}'
slack=json.loads(event('/slack')['body']); assert slack['blocks'][0]['type']=='header' and 'P9 all-channel alert' in slack['text']
teams=json.loads(event('/teams')['body']); assert teams['attachments'][0]['contentType']=='application/vnd.microsoft.card.adaptive'
discord=json.loads(event('/discord')['body']); assert discord['embeds'] and discord['allowed_mentions']['parse']==[]
pd=json.loads(event('/pagerduty')['body']); assert pd['routing_key']=='p9-pagerduty-routing-key-2026' and pd['event_action']=='trigger' and pd['dedup_key'].startswith('monitor:failure:')
PY

grep -q 'Subject: Monitor failure alert: P9 all-channel alert' /tmp/monitor-p9-smtp.eml
grep -q 'X-Monitor-Event: failure.alert.triggered' /tmp/monitor-p9-smtp.eml
grep -q 'ops@monitor.local' /tmp/monitor-p9-smtp.eml

# The Alerts surface exposes every adapter but never renders protected material.
curl -fsS -b "$COOKIE_JAR" "$BASE_URL/alerts" -o /tmp/monitor-p9-final.html
grep -q '>Slack<' /tmp/monitor-p9-final.html
grep -q '>Microsoft Teams<' /tmp/monitor-p9-final.html
grep -q '>Discord<' /tmp/monitor-p9-final.html
grep -q '>PagerDuty<' /tmp/monitor-p9-final.html
grep -q '>Email<' /tmp/monitor-p9-final.html
! grep -q 'p9-pagerduty-routing-key-2026' /tmp/monitor-p9-final.html
! grep -q '/slack' /tmp/monitor-p9-final.html
! grep -q '/teams' /tmp/monitor-p9-final.html
! grep -q '/discord' /tmp/monitor-p9-final.html

echo 'P9 alert-delivery adapter integration checks passed.'

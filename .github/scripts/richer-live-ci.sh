#!/usr/bin/env bash
set -euo pipefail

BASE_URL="${BASE_URL:-http://127.0.0.1:5094}"
DB_NAME="${DB_NAME:-MonitorRicherLiveCi}"
SQL_PASSWORD="${SQL_PASSWORD:-MonitorRicherLiveCi!2026Password}"
COOKIE_JAR=/tmp/monitor-richer-live-cookies.txt
APP_LOG=/tmp/monitor-richer-live-ci.log

sql_container=""
app_pid=""

sql_scalar() {
  docker exec "$sql_container" /opt/mssql-tools18/bin/sqlcmd \
    -S localhost -U sa -P "$SQL_PASSWORD" -C -b -d "$DB_NAME" -h -1 -W \
    -Q "SET NOCOUNT ON; $1" | tr -d '\r' | xargs
}

page_token() {
  local url="$1"
  local output="$2"
  curl -fsS -b "$COOKIE_JAR" "$url" -o "$output"
  python3 - "$output" <<'PY'
import re
import sys
page = open(sys.argv[1], encoding='utf-8').read()
match = re.search(r'name="__RequestVerificationToken"[^>]*value="([^"]+)"', page)
assert match, f'antiforgery token not found in {sys.argv[1]}'
print(match.group(1))
PY
}

post_form() {
  local url="$1"
  shift
  local args=(-sS -o /dev/null -w '%{http_code}' -b "$COOKIE_JAR" -c "$COOKIE_JAR")
  for value in "$@"; do
    args+=(--data-urlencode "$value")
  done
  curl "${args[@]}" "$url"
}

cleanup() {
  local status=$?
  if [[ $status -ne 0 ]]; then
    echo '=== Monitor richer-live log ==='
    cat "$APP_LOG" 2>/dev/null || true
    echo '=== End diagnostic log ==='
  fi
  if [[ -n "$app_pid" ]]; then
    kill "$app_pid" 2>/dev/null || true
    wait "$app_pid" 2>/dev/null || true
  fi
  return "$status"
}
trap cleanup EXIT

dotnet tool install --global dotnet-ef --version 10.0.10
dotnet ef database update \
  --project src/Monitor.Infrastructure \
  --startup-project src/Monitor.Web \
  --context MonitorDbContext \
  --connection "$ConnectionStrings__Monitor"

sql_container=$(docker ps \
  --filter 'ancestor=mcr.microsoft.com/mssql/server:2022-latest' \
  --format '{{.ID}}' | head -n 1)
test -n "$sql_container"

(
  cd src/Monitor.Web
  exec dotnet bin/Release/net10.0/Monitor.Web.dll
) > "$APP_LOG" 2>&1 &
app_pid=$!

app_ready=false
for attempt in {1..45}; do
  if curl -fsS "$BASE_URL/api/health" > /dev/null; then
    app_ready=true
    break
  fi
  if ! kill -0 "$app_pid" 2>/dev/null; then
    echo 'Monitor exited while starting.'
    wait "$app_pid" || true
    exit 1
  fi
  sleep 1
done
test "$app_ready" = true

# Bootstrap operator login.
curl -fsS -c "$COOKIE_JAR" "$BASE_URL/account/login" -o /tmp/richer-live-login.html
login_token=$(python3 - <<'PY'
import re
page = open('/tmp/richer-live-login.html', encoding='utf-8').read()
match = re.search(r'name="__RequestVerificationToken"[^>]*value="([^"]+)"', page)
assert match
print(match.group(1))
PY
)
login_status=$(post_form "$BASE_URL/account/login" \
  "__RequestVerificationToken=$login_token" \
  'Input.Email=richer-live-ci@monitor.local' \
  'Input.Password=MonitorRicherLiveAdmin2026' \
  'Input.RememberMe=false')
test "$login_status" = '302'

# SignalR remains private: anonymous negotiate is rejected; authenticated negotiate succeeds.
anonymous_hub_status=$(curl -sS -o /tmp/richer-live-hub-anon.json -w '%{http_code}' \
  -X POST "$BASE_URL/hubs/monitor/negotiate?negotiateVersion=1")
test "$anonymous_hub_status" = '401'
curl -fsS -b "$COOKIE_JAR" -X POST \
  "$BASE_URL/hubs/monitor/negotiate?negotiateVersion=1" \
  -o /tmp/richer-live-hub-auth.json
python3 - <<'PY'
import json
payload = json.load(open('/tmp/richer-live-hub-auth.json'))
assert payload.get('connectionId') or payload.get('connectionToken'), payload
PY

# Create one component and a running native run.
component_json=$(curl -fsS \
  -H 'X-Monitor-Key: ci-richer-live-bootstrap-key' \
  -H 'Content-Type: application/json' \
  -d '{"name":"Richer Live Agent","slug":"richer-live-agent","type":"Agent","environment":"production","version":"1.0.0"}' \
  "$BASE_URL/api/components/register")
component_id=$(python3 -c 'import json,sys; print(json.loads(sys.stdin.read())["id"])' <<< "$component_json")
test -n "$component_id"

run_json=$(curl -fsS \
  -H 'X-Monitor-Key: ci-richer-live-bootstrap-key' \
  -H 'Content-Type: application/json' \
  -d "{\"componentId\":\"$component_id\",\"name\":\"Live reconciliation run\",\"externalId\":\"p8-ci\",\"trigger\":\"CI\",\"model\":\"gpt-live-ci\",\"inputJson\":\"{\\\"input\\\":true}\"}" \
  "$BASE_URL/api/runs")
run_id=$(python3 -c 'import json,sys; print(json.loads(sys.stdin.read())["id"])' <<< "$run_json")
test -n "$run_id"

curl -fsS -b "$COOKIE_JAR" "$BASE_URL/run/$run_id" -o /tmp/richer-live-running.html
grep -q 'id="run-live-root"' /tmp/richer-live-running.html
grep -q 'data-run-status="Running"' /tmp/richer-live-running.html
grep -q 'id="run-live-connection"' /tmp/richer-live-running.html
grep -q '/js/run-live.js' /tmp/richer-live-running.html

# Add an active span and a structured event. The authoritative snapshot must expose both.
span_json=$(curl -fsS \
  -H 'X-Monitor-Key: ci-richer-live-bootstrap-key' \
  -H 'Content-Type: application/json' \
  -d '{"parentSpanId":null,"name":"Live tool call","kind":"Tool","status":"Running","startedAt":null,"completedAt":null,"attributesJson":"{\"tool\":\"ci\"}","error":null}' \
  "$BASE_URL/api/runs/$run_id/spans")
span_id=$(python3 -c 'import json,sys; print(json.loads(sys.stdin.read())["id"])' <<< "$span_json")
test -n "$span_id"

curl -fsS \
  -H 'X-Monitor-Key: ci-richer-live-bootstrap-key' \
  -H 'Content-Type: application/json' \
  -d "{\"level\":\"Information\",\"message\":\"streamed native log\",\"spanId\":\"$span_id\",\"eventName\":\"p8.native\",\"propertiesJson\":\"{\\\"phase\\\":1}\"}" \
  "$BASE_URL/api/runs/$run_id/events" \
  -o /tmp/richer-live-log-created.json

curl -fsS -b "$COOKIE_JAR" "$BASE_URL/api/runs/$run_id" -o /tmp/richer-live-snapshot-running.json
python3 - <<'PY'
import json
p = json.load(open('/tmp/richer-live-snapshot-running.json'))
assert p['status'] == 'Running', p['status']
assert any(s['name'] == 'Live tool call' and s['status'] == 'Running' for s in p['spans'])
assert any(l['message'] == 'streamed native log' for l in p['logs'])
assert p['environment'] == 'production'
PY

# Complete the run. A newly loaded terminal run is a frozen forensic snapshot in the client contract.
complete_status=$(curl -sS -o /tmp/richer-live-complete.json -w '%{http_code}' \
  -H 'X-Monitor-Key: ci-richer-live-bootstrap-key' \
  -H 'Content-Type: application/json' \
  -d '{"status":"Success","inputTokens":12,"outputTokens":8,"costUsd":0.0123,"outputJson":"{\"done\":true}","error":null}' \
  "$BASE_URL/api/runs/$run_id/complete")
test "$complete_status" = '204'

curl -fsS -b "$COOKIE_JAR" "$BASE_URL/run/$run_id" -o /tmp/richer-live-terminal.html
grep -q 'data-run-status="Success"' /tmp/richer-live-terminal.html
grep -q 'id="run-live-update-banner"' /tmp/richer-live-terminal.html

# Late telemetry is retained by the authoritative snapshot; browser policy decides whether to apply it.
curl -fsS \
  -H 'X-Monitor-Key: ci-richer-live-bootstrap-key' \
  -H 'Content-Type: application/json' \
  -d '{"level":"Warning","message":"late forensic log","eventName":"p8.late"}' \
  "$BASE_URL/api/runs/$run_id/events" \
  -o /tmp/richer-live-late-log.json
curl -fsS -b "$COOKIE_JAR" "$BASE_URL/api/runs/$run_id" -o /tmp/richer-live-snapshot-terminal.json
python3 - <<'PY'
import json
p = json.load(open('/tmp/richer-live-snapshot-terminal.json'))
assert p['status'] == 'Success'
assert p['outputJson'] == '{"done":true}'
assert any(l['message'] == 'late forensic log' for l in p['logs'])
PY

# Issue a command through the real operator Razor surface.
component_token=$(page_token "$BASE_URL/components/$component_id" /tmp/richer-live-component-before.html)
issue_status=$(post_form "$BASE_URL/components/$component_id?handler=IssueCommand" \
  "__RequestVerificationToken=$component_token" \
  'CommandInput.Type=RefreshConfiguration' \
  'CommandInput.PayloadJson={"refresh":"p8"}' \
  'CommandInput.ExpiryMinutes=5')
test "$issue_status" = '302'
command_id=$(sql_scalar "SELECT TOP 1 CONVERT(varchar(36), Id) FROM ComponentCommands WHERE ComponentId = '$component_id' ORDER BY CreatedAt DESC;")
test -n "$command_id"

curl -fsS -b "$COOKIE_JAR" "$BASE_URL/commands?componentId=$component_id" -o /tmp/richer-live-commands.html
grep -qi "data-command-id=\"$command_id\"" /tmp/richer-live-commands.html
grep -q 'id="command-live-root"' /tmp/richer-live-commands.html
grep -q '/js/commands-live.js' /tmp/richer-live-commands.html

curl -fsS -b "$COOKIE_JAR" "$BASE_URL/components/$component_id" -o /tmp/richer-live-component.html
grep -q 'id="command-live-root"' /tmp/richer-live-component.html
grep -qi "data-command-id=\"$command_id\"" /tmp/richer-live-component.html

# Claim and complete the same durable command, proving the server-side states the live UI represents.
curl -fsS -X POST \
  -H 'X-Monitor-Key: ci-richer-live-bootstrap-key' \
  "$BASE_URL/api/components/$component_id/commands/claim" \
  -o /tmp/richer-live-command-claim.json
read -r claimed_id lease_token <<< "$(python3 - <<'PY'
import json
p = json.load(open('/tmp/richer-live-command-claim.json'))
print(p['id'], p['leaseToken'])
PY
)"
test "${claimed_id,,}" = "${command_id,,}"
test "$(sql_scalar "SELECT Status FROM ComponentCommands WHERE Id = '$command_id';")" = '2'

command_complete_status=$(curl -sS -o /tmp/richer-live-command-complete.json -w '%{http_code}' \
  -H 'X-Monitor-Key: ci-richer-live-bootstrap-key' \
  -H 'Content-Type: application/json' \
  -d "{\"leaseToken\":\"$lease_token\",\"outcome\":\"Succeeded\",\"resultJson\":\"{\\\"refreshed\\\":true}\",\"error\":null}" \
  "$BASE_URL/api/components/$component_id/commands/$command_id/complete")
test "$command_complete_status" = '200'
test "$(sql_scalar "SELECT Status FROM ComponentCommands WHERE Id = '$command_id';")" = '3'

# Browser contracts: granular groups, live reconciliation, duration ticking, frozen history, and no silent filtered reshuffle.
curl -fsS -b "$COOKIE_JAR" "$BASE_URL/js/run-live.js" -o /tmp/richer-live-run.js
grep -q "invoke('WatchRun'" /tmp/richer-live-run.js
grep -q "connection.on('RunDetailChanged'" /tmp/richer-live-run.js
grep -q "mode === 'frozen'" /tmp/richer-live-run.js
grep -q 'showPendingChange' /tmp/richer-live-run.js
grep -q 'reconcileTimeline' /tmp/richer-live-run.js
grep -q 'reconcileTrace' /tmp/richer-live-run.js
grep -q 'data-live-duration' /tmp/richer-live-running.html

curl -fsS -b "$COOKIE_JAR" "$BASE_URL/js/commands-live.js" -o /tmp/richer-live-commands.js
grep -q "invoke('WatchCommands'" /tmp/richer-live-commands.js
grep -q "connection.on('CommandChanged'" /tmp/richer-live-commands.js
grep -q "state.mode === 'historical'" /tmp/richer-live-commands.js
grep -q 'command-live-filter-mismatch' /tmp/richer-live-commands.js
grep -q 'Connection restored' /tmp/richer-live-commands.js

# Persistence integration is model-wide: native + OTLP + workers share one post-save invalidation boundary.
grep -q 'case AgentRun run when IsVisibleRunChange(entry):' src/Monitor.Web/Realtime/MonitorRealtimeSaveChangesInterceptor.cs
grep -q 'nameof(AgentRun.AggregatedAt)' src/Monitor.Web/Realtime/MonitorRealtimeSaveChangesInterceptor.cs
grep -q 'case TraceSpan span:' src/Monitor.Web/Realtime/MonitorRealtimeSaveChangesInterceptor.cs
grep -q 'case LogEvent logEvent' src/Monitor.Web/Realtime/MonitorRealtimeSaveChangesInterceptor.cs
grep -q 'case ComponentCommand command:' src/Monitor.Web/Realtime/MonitorRealtimeSaveChangesInterceptor.cs
grep -q 'SavedChangesAsync' src/Monitor.Web/Realtime/MonitorRealtimeSaveChangesInterceptor.cs
grep -q 'RunDetailChanged' src/Monitor.Web/Realtime/MonitorRealtimeSaveChangesInterceptor.cs
grep -q 'CommandChanged' src/Monitor.Web/Realtime/MonitorRealtimeSaveChangesInterceptor.cs
if grep -q 'context.Set<ComponentCommand>' src/Monitor.Web/Realtime/MonitorRealtimeSaveChangesInterceptor.cs; then
  echo 'Realtime interceptor must not query the same DbContext from SavedChanges.'
  exit 1
fi

echo 'Richer live experience integration assertions passed.'

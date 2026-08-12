#!/usr/bin/env bash
set -euo pipefail

BASE_URL="http://127.0.0.1:5098"
DB="MonitorPolicyActionCi"
SQL_PASSWORD="MonitorPolicyActionCi!2026Password"
BOOTSTRAP_KEY="ci-policy-action-key"
ADMIN_EMAIL="policy-ci@monitor.local"
ADMIN_PASSWORD="MonitorPolicyAdmin2026"
COOKIE_JAR="/tmp/monitor-policy-cookies.txt"
APP_LOG="/tmp/monitor-policy-ci.log"

sql_container=$(docker ps --filter 'ancestor=mcr.microsoft.com/mssql/server:2022-latest' --format '{{.ID}}' | head -n 1)
test -n "$sql_container"

sql() {
  docker exec "$sql_container" /opt/mssql-tools18/bin/sqlcmd \
    -S localhost -U sa -P "$SQL_PASSWORD" -C -b -d "$DB" -Q "$1"
}

scalar() {
  docker exec "$sql_container" /opt/mssql-tools18/bin/sqlcmd \
    -S localhost -U sa -P "$SQL_PASSWORD" -C -b -d "$DB" -h -1 -W \
    -Q "SET NOCOUNT ON; $1" | tr -d '\r' | xargs
}

get_token() {
  local url="$1" output="$2"
  curl -fsS -b "$COOKIE_JAR" "$url" -o "$output"
  python3 - "$output" <<'PY'
import re,sys
html=open(sys.argv[1],encoding='utf-8').read()
m=re.search(r'name="__RequestVerificationToken"[^>]*value="([^"]+)"',html)
assert m, 'antiforgery token not found'
print(m.group(1))
PY
}

register_component() {
  local name="$1" slug="$2"
  curl -fsS \
    -H "X-Monitor-Key: $BOOTSTRAP_KEY" \
    -H 'Content-Type: application/json' \
    -d "{\"name\":\"$name\",\"slug\":\"$slug\",\"type\":\"Agent\",\"environment\":\"production\",\"version\":\"1.0.0\"}" \
    "$BASE_URL/api/components/register"
}

create_budget() {
  local name="$1" component_id="$2" action="$3"
  local token status
  token=$(get_token "$BASE_URL/budgets/edit" "/tmp/policy-budget-${action}.html")
  status=$(curl -sS -o /tmp/policy-budget-save.html -w '%{http_code}' \
    -b "$COOKIE_JAR" -c "$COOKIE_JAR" \
    --data-urlencode "__RequestVerificationToken=$token" \
    --data-urlencode "Input.Name=$name" \
    --data-urlencode "Input.ComponentId=$component_id" \
    --data-urlencode 'Input.Environment=' \
    --data-urlencode 'Input.Model=policy-model' \
    --data-urlencode 'Input.Period=Daily' \
    --data-urlencode 'Input.CostLimitUsd=10' \
    --data-urlencode 'Input.TokenLimit=' \
    --data-urlencode 'Input.WarningPercent=80' \
    --data-urlencode 'Input.CriticalPercent=100' \
    --data-urlencode "Input.CriticalAction=$action" \
    --data-urlencode 'Input.Enabled=true' \
    --data-urlencode 'Input.DeliverToAllEnabledDestinations=true' \
    "$BASE_URL/budgets/edit?handler=Save")
  test "$status" = "302"
}

create_run() {
  local component_id="$1" external_id="$2"
  local json run_id
  json=$(curl -fsS \
    -H "X-Monitor-Key: $BOOTSTRAP_KEY" \
    -H 'Content-Type: application/json' \
    -d "{\"componentId\":\"$component_id\",\"name\":\"policy action run\",\"externalId\":\"$external_id\",\"trigger\":\"CI\",\"model\":\"policy-model\",\"inputJson\":\"{}\"}" \
    "$BASE_URL/api/runs")
  run_id=$(python3 -c 'import json,sys; print(json.loads(sys.stdin.read())["id"])' <<< "$json")
  curl -fsS -o /dev/null \
    -H "X-Monitor-Key: $BOOTSTRAP_KEY" \
    -H 'Content-Type: application/json' \
    -d '{"status":"Success","inputTokens":100,"outputTokens":100,"costUsd":11.0,"outputJson":"{}","error":null}' \
    "$BASE_URL/api/runs/$run_id/complete"
}

claim_and_complete() {
  local component_id="$1" expected_type="$2"
  local claim_file="/tmp/policy-claim-${expected_type}.json"
  local status command_id lease_token actual_type
  status=$(curl -sS -o "$claim_file" -w '%{http_code}' \
    -X POST -H "X-Monitor-Key: $BOOTSTRAP_KEY" \
    "$BASE_URL/api/components/$component_id/commands/claim")
  test "$status" = "200"
  read -r command_id lease_token actual_type < <(python3 - "$claim_file" <<'PY'
import json,sys
x=json.load(open(sys.argv[1],encoding='utf-8'))
print(x['id'], x['leaseToken'], x['type'])
PY
)
  test "$actual_type" = "$expected_type"

  status=$(curl -sS -o /tmp/policy-complete.json -w '%{http_code}' \
    -H "X-Monitor-Key: $BOOTSTRAP_KEY" \
    -H 'Content-Type: application/json' \
    -d "{\"leaseToken\":\"$lease_token\",\"outcome\":\"Succeeded\",\"resultJson\":\"{\\\"policyCi\\\":true}\",\"error\":null}" \
    "$BASE_URL/api/components/$component_id/commands/$command_id/complete")
  test "$status" = "200"
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
    echo '=== Monitor P11 log ==='
    cat "$APP_LOG" 2>/dev/null || true
    echo '=== Enforcement policies ==='
    sql "SELECT * FROM UsageBudgetEnforcementPolicies;" 2>/dev/null || true
    echo '=== Commands ==='
    sql "SELECT Id,ComponentId,Type,Status,RequestedBy,PayloadJson FROM ComponentCommands ORDER BY CreatedAt;" 2>/dev/null || true
  fi
  kill "$app_pid" 2>/dev/null || true
  wait "$app_pid" 2>/dev/null || true
  return "$status"
}
trap cleanup EXIT

for attempt in {1..45}; do
  if curl -fsS "$BASE_URL/api/health" >/dev/null; then break; fi
  if ! kill -0 "$app_pid" 2>/dev/null; then
    cat "$APP_LOG"
    exit 1
  fi
  sleep 1
done
curl -fsS "$BASE_URL/api/health" >/dev/null

pause_component_json=$(register_component 'Policy Pause Agent' 'policy-pause-agent')
disable_component_json=$(register_component 'Policy Disable Agent' 'policy-disable-agent')
pause_component_id=$(python3 -c 'import json,sys; print(json.loads(sys.stdin.read())["id"])' <<< "$pause_component_json")
disable_component_id=$(python3 -c 'import json,sys; print(json.loads(sys.stdin.read())["id"])' <<< "$disable_component_json")

echo "Signing in..."
curl -fsS -c "$COOKIE_JAR" "$BASE_URL/account/login" -o /tmp/policy-login.html
login_token=$(python3 -c 'import re; h=open("/tmp/policy-login.html").read(); print(re.search(r"name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"",h).group(1))')
login_status=$(curl -sS -o /dev/null -w '%{http_code}' -b "$COOKIE_JAR" -c "$COOKIE_JAR" \
  --data-urlencode "__RequestVerificationToken=$login_token" \
  --data-urlencode "Input.Email=$ADMIN_EMAIL" \
  --data-urlencode "Input.Password=$ADMIN_PASSWORD" \
  --data-urlencode 'Input.RememberMe=false' \
  "$BASE_URL/account/login")
test "$login_status" = '302'

echo "Rejecting ambiguous global enforcement..."
invalid_token=$(get_token "$BASE_URL/budgets/edit" /tmp/policy-invalid.html)
invalid_status=$(curl -sS -o /tmp/policy-invalid-result.html -w '%{http_code}' \
  -b "$COOKIE_JAR" -c "$COOKIE_JAR" \
  --data-urlencode "__RequestVerificationToken=$invalid_token" \
  --data-urlencode 'Input.Name=Invalid global enforcement' \
  --data-urlencode 'Input.ComponentId=' \
  --data-urlencode 'Input.Period=Daily' \
  --data-urlencode 'Input.CostLimitUsd=10' \
  --data-urlencode 'Input.WarningPercent=80' \
  --data-urlencode 'Input.CriticalPercent=100' \
  --data-urlencode 'Input.CriticalAction=Pause' \
  --data-urlencode 'Input.Enabled=true' \
  --data-urlencode 'Input.DeliverToAllEnabledDestinations=true' \
  "$BASE_URL/budgets/edit?handler=Save")
test "$invalid_status" = '200'
grep -q 'Automatic enforcement requires a budget scoped to one component' /tmp/policy-invalid-result.html
test "$(scalar "SELECT COUNT(*) FROM UsageBudgets WHERE Name=N'Invalid global enforcement';")" = '0'

echo "Creating Pause and Disable policies through the operator UI..."
create_budget 'CI critical pause budget' "$pause_component_id" Pause
create_budget 'CI critical disable budget' "$disable_component_id" Disable

pause_budget_id=$(scalar "SELECT CONVERT(varchar(36),Id) FROM UsageBudgets WHERE Name=N'CI critical pause budget';")
disable_budget_id=$(scalar "SELECT CONVERT(varchar(36),Id) FROM UsageBudgets WHERE Name=N'CI critical disable budget';")
test -n "$pause_budget_id"
test -n "$disable_budget_id"
test "$(scalar "SELECT COUNT(*) FROM UsageBudgetEnforcementPolicies WHERE UsageBudgetId='$pause_budget_id' AND CriticalAction=1;")" = '1'
test "$(scalar "SELECT COUNT(*) FROM UsageBudgetEnforcementPolicies WHERE UsageBudgetId='$disable_budget_id' AND CriticalAction=2;")" = '1'

echo "Crossing both Critical thresholds..."
create_run "$pause_component_id" p11-pause
create_run "$disable_component_id" p11-disable

ready=false
for attempt in {1..12}; do
  state=$(scalar "
    SELECT CONCAT(
      (SELECT COUNT(*) FROM UsageBudgetAlertEvents WHERE UsageBudgetId IN ('$pause_budget_id','$disable_budget_id') AND Level=2),N'|',
      (SELECT COUNT(*) FROM ComponentCommands WHERE RequestedBy=N'policy:usage-budget' AND ComponentId IN ('$pause_component_id','$disable_component_id')),N'|',
      (SELECT COUNT(*) FROM ComponentCommands WHERE RequestedBy=N'policy:usage-budget' AND ComponentId='$pause_component_id' AND Type=1),N'|',
      (SELECT COUNT(*) FROM ComponentCommands WHERE RequestedBy=N'policy:usage-budget' AND ComponentId='$disable_component_id' AND Type=3),N'|',
      (SELECT COUNT(*) FROM AuditEvents WHERE Action=N'component-command.issued' AND ActorType=1 AND ActorName=N'UsageBudgetEvaluator')
    );")
  if [ "$state" = '2|2|1|1|2' ]; then ready=true; break; fi
  sleep 2
done
test "$ready" = true

pause_alert_id=$(scalar "SELECT TOP 1 CONVERT(varchar(36),Id) FROM UsageBudgetAlertEvents WHERE UsageBudgetId='$pause_budget_id' AND Level=2;")
disable_alert_id=$(scalar "SELECT TOP 1 CONVERT(varchar(36),Id) FROM UsageBudgetAlertEvents WHERE UsageBudgetId='$disable_budget_id' AND Level=2;")
pause_payload=$(scalar "SELECT TOP 1 PayloadJson FROM ComponentCommands WHERE ComponentId='$pause_component_id' AND RequestedBy=N'policy:usage-budget' ORDER BY CreatedAt DESC;")
disable_payload=$(scalar "SELECT TOP 1 PayloadJson FROM ComponentCommands WHERE ComponentId='$disable_component_id' AND RequestedBy=N'policy:usage-budget' ORDER BY CreatedAt DESC;")
python3 - "$pause_payload" "$pause_budget_id" "$pause_alert_id" Pause <<'PY'
import json,sys
payload=json.loads(sys.argv[1])
assert payload['source']=='usage-budget'
assert payload['budgetId'].lower()==sys.argv[2].lower()
assert payload['alertEventId'].lower()==sys.argv[3].lower()
assert payload['level']=='Critical'
assert payload['action']==sys.argv[4]
assert payload['utilizationPercent'] >= 100
PY
python3 - "$disable_payload" "$disable_budget_id" "$disable_alert_id" Disable <<'PY'
import json,sys
payload=json.loads(sys.argv[1])
assert payload['source']=='usage-budget'
assert payload['budgetId'].lower()==sys.argv[2].lower()
assert payload['alertEventId'].lower()==sys.argv[3].lower()
assert payload['level']=='Critical'
assert payload['action']==sys.argv[4]
PY

echo "Verifying repeated sweeps do not duplicate policy actions..."
sleep 6
test "$(scalar "SELECT COUNT(*) FROM ComponentCommands WHERE RequestedBy=N'policy:usage-budget';")" = '2'
test "$(scalar "SELECT COUNT(*) FROM UsageBudgetAlertEvents WHERE UsageBudgetId IN ('$pause_budget_id','$disable_budget_id') AND Level=2;")" = '2'

echo "Executing the generated commands through the ordinary component protocol..."
claim_and_complete "$pause_component_id" Pause
claim_and_complete "$disable_component_id" Disable

test "$(scalar "SELECT COUNT(*) FROM Components WHERE Id='$pause_component_id' AND ControlState=1 AND Enabled=1;")" = '1'
test "$(scalar "SELECT COUNT(*) FROM Components WHERE Id='$disable_component_id' AND ControlState=2 AND Enabled=0;")" = '1'
test "$(scalar "SELECT COUNT(*) FROM ComponentCommands WHERE RequestedBy=N'policy:usage-budget' AND Status=3;")" = '2'
test "$(scalar "SELECT COUNT(*) FROM AuditEvents WHERE Action=N'component-command.succeeded' AND ActorType=2;")" = '2'

echo "Verifying recovery remains manual..."
sleep 6
test "$(scalar "SELECT COUNT(*) FROM ComponentCommands WHERE Type IN (2,4) AND RequestedBy=N'policy:usage-budget';")" = '0'
test "$(scalar "SELECT COUNT(*) FROM ComponentCommands WHERE RequestedBy=N'policy:usage-budget';")" = '2'

echo "P11 automated policy action integration checks passed."

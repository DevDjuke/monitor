#!/usr/bin/env bash
set -euo pipefail

BASE_URL="http://127.0.0.1:5099"
DB="MonitorRolesCi"
SQL_PASSWORD="MonitorRolesCi!2026Password"
BOOTSTRAP_KEY="ci-roles-bootstrap-key"
OWNER_EMAIL="owner-ci@monitor.local"
OWNER_PASSWORD="MonitorOwner2026"
VIEWER_EMAIL="viewer-ci@monitor.local"
VIEWER_PASSWORD="MonitorViewer2026"
AUDITOR_EMAIL="auditor-ci@monitor.local"
AUDITOR_PASSWORD="MonitorAuditor2026"
AUDITOR_NEW_PASSWORD="MonitorAuditorNew2026"
OPERATOR_EMAIL="operator-ci@monitor.local"
OPERATOR_PASSWORD="MonitorOperator2026"
TEMP_EMAIL="temporary-ci@monitor.local"
TEMP_PASSWORD="MonitorTemporary2026"
APP_LOG="/tmp/monitor-roles-ci.log"

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
  local jar="$1" url="$2" output="$3"
  curl -fsS -b "$jar" -c "$jar" "$url" -o "$output"
  python3 - "$output" <<'PY'
import re,sys
html=open(sys.argv[1],encoding='utf-8').read()
m=re.search(r'name="__RequestVerificationToken"[^>]*value="([^"]+)"', html)
assert m, 'antiforgery token not found'
print(m.group(1))
PY
}

login() {
  local email="$1" password="$2" jar="$3" prefix="$4"
  rm -f "$jar"
  curl -fsS -c "$jar" "$BASE_URL/account/login" -o "/tmp/${prefix}-login.html"
  local token status
  token=$(python3 - "/tmp/${prefix}-login.html" <<'PY'
import re,sys
html=open(sys.argv[1],encoding='utf-8').read()
m=re.search(r'name="__RequestVerificationToken"[^>]*value="([^"]+)"', html)
assert m, 'login antiforgery token not found'
print(m.group(1))
PY
)
  status=$(curl -sS -o "/tmp/${prefix}-login-result.html" -w '%{http_code}' \
    -b "$jar" -c "$jar" \
    --data-urlencode "__RequestVerificationToken=$token" \
    --data-urlencode "Input.Email=$email" \
    --data-urlencode "Input.Password=$password" \
    --data-urlencode 'Input.RememberMe=false' \
    "$BASE_URL/account/login")
  test "$status" = '302'
  test "$(curl -sS -o /dev/null -w '%{http_code}' -b "$jar" "$BASE_URL/")" = '200'
}

assert_get_200() {
  local jar="$1" path="$2"
  local status
  status=$(curl -sS -o /dev/null -w '%{http_code}' -b "$jar" "$BASE_URL$path")
  test "$status" = '200'
}

assert_get_denied() {
  local jar="$1" path="$2" prefix="$3"
  local status
  status=$(curl -sS -o /dev/null -D "/tmp/${prefix}-headers.txt" -w '%{http_code}' -b "$jar" "$BASE_URL$path")
  test "$status" = '302'
  grep -qiE '^location: .*account/access-denied' "/tmp/${prefix}-headers.txt"
}

assert_post_denied() {
  local jar="$1" url="$2" token="$3" prefix="$4"
  shift 4
  local args=()
  while [ "$#" -gt 0 ]; do
    args+=(--data-urlencode "$1")
    shift
  done
  local status
  status=$(curl -sS -o "/tmp/${prefix}-body.html" -D "/tmp/${prefix}-headers.txt" -w '%{http_code}' \
    -b "$jar" -c "$jar" \
    --data-urlencode "__RequestVerificationToken=$token" \
    "${args[@]}" \
    "$url")
  test "$status" = '302'
  grep -qiE '^location: .*account/access-denied' "/tmp/${prefix}-headers.txt"
}

create_operator() {
  local email="$1" password="$2" role="$3" suffix="$4"
  local token status
  token=$(get_token "$OWNER_JAR" "$BASE_URL/operators" "/tmp/create-${suffix}.html")
  status=$(curl -sS -o "/tmp/create-${suffix}-result.html" -w '%{http_code}' \
    -b "$OWNER_JAR" -c "$OWNER_JAR" \
    --data-urlencode "__RequestVerificationToken=$token" \
    --data-urlencode "Input.Email=$email" \
    --data-urlencode "Input.Password=$password" \
    --data-urlencode "Input.Role=$role" \
    "$BASE_URL/operators?handler=Create")
  test "$status" = '302'
}

register_component() {
  curl -fsS \
    -H "X-Monitor-Key: $BOOTSTRAP_KEY" \
    -H 'Content-Type: application/json' \
    -d '{"name":"Roles CI Agent","slug":"roles-ci-agent","type":"Agent","environment":"production","version":"1.0.0"}' \
    "$BASE_URL/api/components/register"
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
    echo '=== Monitor P12 log ==='
    cat "$APP_LOG" 2>/dev/null || true
    echo '=== Roles ==='
    sql "SELECT Id,Name,NormalizedName FROM AspNetRoles ORDER BY Name;" 2>/dev/null || true
    echo '=== Users / roles ==='
    sql "SELECT u.Email,r.Name FROM AspNetUsers u LEFT JOIN AspNetUserRoles ur ON ur.UserId=u.Id LEFT JOIN AspNetRoles r ON r.Id=ur.RoleId ORDER BY u.Email,r.Name;" 2>/dev/null || true
    echo '=== Operator audits ==='
    sql "SELECT Action,ActorName,TargetName,BeforeJson,AfterJson,MetadataJson FROM AuditEvents WHERE TargetType=N'operator-account' ORDER BY OccurredAt;" 2>/dev/null || true
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

echo "Verifying role bootstrap and Owner compatibility..."
test "$(scalar "SELECT COUNT(*) FROM AspNetRoles WHERE Name IN (N'Owner',N'Operator',N'Viewer',N'Auditor');")" = '4'
test "$(scalar "SELECT COUNT(*) FROM AspNetUsers u JOIN AspNetUserRoles ur ON ur.UserId=u.Id JOIN AspNetRoles r ON r.Id=ur.RoleId WHERE u.Email=N'$OWNER_EMAIL' AND r.Name=N'Owner';")" = '1'

OWNER_JAR="/tmp/roles-owner.cookies"
VIEWER_JAR="/tmp/roles-viewer.cookies"
AUDITOR_JAR="/tmp/roles-auditor.cookies"
OPERATOR_JAR="/tmp/roles-operator.cookies"
login "$OWNER_EMAIL" "$OWNER_PASSWORD" "$OWNER_JAR" owner
assert_get_200 "$OWNER_JAR" /operators
assert_get_200 "$OWNER_JAR" /audit

echo "Creating Viewer, Auditor and Operator accounts through the Owner UI..."
create_operator "$VIEWER_EMAIL" "$VIEWER_PASSWORD" Viewer viewer
create_operator "$AUDITOR_EMAIL" "$AUDITOR_PASSWORD" Auditor auditor
create_operator "$OPERATOR_EMAIL" "$OPERATOR_PASSWORD" Operator operator
create_operator "$TEMP_EMAIL" "$TEMP_PASSWORD" Viewer temporary

test "$(scalar "SELECT COUNT(*) FROM AspNetUsers u JOIN AspNetUserRoles ur ON ur.UserId=u.Id JOIN AspNetRoles r ON r.Id=ur.RoleId WHERE (u.Email=N'$VIEWER_EMAIL' AND r.Name=N'Viewer') OR (u.Email=N'$AUDITOR_EMAIL' AND r.Name=N'Auditor') OR (u.Email=N'$OPERATOR_EMAIL' AND r.Name=N'Operator') OR (u.Email=N'$TEMP_EMAIL' AND r.Name=N'Viewer');")" = '4'
test "$(scalar "SELECT COUNT(*) FROM AuditEvents WHERE Action=N'operator-account.created' AND TargetType=N'operator-account';")" = '4'

component_json=$(register_component)
component_id=$(python3 -c 'import json,sys; print(json.loads(sys.stdin.read())["id"])' <<< "$component_json")
test -n "$component_id"

echo "Checking Viewer read-only access and mutation denial..."
login "$VIEWER_EMAIL" "$VIEWER_PASSWORD" "$VIEWER_JAR" viewer
for path in / /components /runs /logs /usage /budgets /alerts "/components/$component_id"; do
  assert_get_200 "$VIEWER_JAR" "$path"
done
assert_get_denied "$VIEWER_JAR" /audit viewer-audit
assert_get_denied "$VIEWER_JAR" /operators viewer-operators
assert_get_denied "$VIEWER_JAR" /budgets/edit viewer-budget-edit
viewer_token=$(get_token "$VIEWER_JAR" "$BASE_URL/components/$component_id" /tmp/viewer-component.html)
assert_post_denied "$VIEWER_JAR" "$BASE_URL/components/$component_id?handler=IssueCommand" "$viewer_token" viewer-command \
  'CommandInput.Type=Pause' 'CommandInput.ExpiryMinutes=10'
assert_post_denied "$VIEWER_JAR" "$BASE_URL/components/$component_id?handler=CreateCredential" "$viewer_token" viewer-credential \
  'CredentialInput.Name=viewer must not create'
test "$(scalar "SELECT COUNT(*) FROM ComponentCommands WHERE ComponentId='$component_id';")" = '0'
test "$(scalar "SELECT COUNT(*) FROM ComponentIngestionCredentials WHERE ComponentId='$component_id';")" = '0'

echo "Checking Auditor investigation + audit-only access..."
login "$AUDITOR_EMAIL" "$AUDITOR_PASSWORD" "$AUDITOR_JAR" auditor
assert_get_200 "$AUDITOR_JAR" /audit
assert_get_200 "$AUDITOR_JAR" /runs
assert_get_denied "$AUDITOR_JAR" /operators auditor-operators
assert_get_denied "$AUDITOR_JAR" /budgets/edit auditor-budget-edit
auditor_token=$(get_token "$AUDITOR_JAR" "$BASE_URL/components/$component_id" /tmp/auditor-component.html)
assert_post_denied "$AUDITOR_JAR" "$BASE_URL/components/$component_id?handler=IssueCommand" "$auditor_token" auditor-command \
  'CommandInput.Type=Pause' 'CommandInput.ExpiryMinutes=10'

echo "Checking Operator configuration and control access without Owner/Audit privileges..."
login "$OPERATOR_EMAIL" "$OPERATOR_PASSWORD" "$OPERATOR_JAR" operator
assert_get_200 "$OPERATOR_JAR" /budgets/edit
assert_get_200 "$OPERATOR_JAR" "/components/$component_id"
assert_get_denied "$OPERATOR_JAR" /operators operator-operators
assert_get_denied "$OPERATOR_JAR" /audit operator-audit
operator_token=$(get_token "$OPERATOR_JAR" "$BASE_URL/components/$component_id" /tmp/operator-component.html)
command_status=$(curl -sS -o /tmp/operator-command-result.html -w '%{http_code}' \
  -b "$OPERATOR_JAR" -c "$OPERATOR_JAR" \
  --data-urlencode "__RequestVerificationToken=$operator_token" \
  --data-urlencode 'CommandInput.Type=Pause' \
  --data-urlencode 'CommandInput.ExpiryMinutes=10' \
  "$BASE_URL/components/$component_id?handler=IssueCommand")
test "$command_status" = '302'
test "$(scalar "SELECT COUNT(*) FROM ComponentCommands WHERE ComponentId='$component_id' AND Type=1;")" = '1'
credential_status=$(curl -sS -o /tmp/operator-credential-result.html -w '%{http_code}' \
  -b "$OPERATOR_JAR" -c "$OPERATOR_JAR" \
  --data-urlencode "__RequestVerificationToken=$operator_token" \
  --data-urlencode 'CredentialInput.Name=Operator CI credential' \
  "$BASE_URL/components/$component_id?handler=CreateCredential")
test "$credential_status" = '302'
test "$(scalar "SELECT COUNT(*) FROM ComponentIngestionCredentials WHERE ComponentId='$component_id' AND Name=N'Operator CI credential';")" = '1'

echo "Checking Owner guardrails and account lifecycle audit..."
owner_id=$(scalar "SELECT Id FROM AspNetUsers WHERE Email=N'$OWNER_EMAIL';")
temp_id=$(scalar "SELECT Id FROM AspNetUsers WHERE Email=N'$TEMP_EMAIL';")
auditor_id=$(scalar "SELECT Id FROM AspNetUsers WHERE Email=N'$AUDITOR_EMAIL';")
test -n "$owner_id"
test -n "$temp_id"
test -n "$auditor_id"

owner_token=$(get_token "$OWNER_JAR" "$BASE_URL/operators" /tmp/owner-operators.html)
self_demote_status=$(curl -sS -o /tmp/self-demote.html -w '%{http_code}' \
  -b "$OWNER_JAR" -c "$OWNER_JAR" \
  --data-urlencode "__RequestVerificationToken=$owner_token" \
  --data-urlencode "userId=$owner_id" \
  --data-urlencode 'role=Viewer' \
  "$BASE_URL/operators?handler=ChangeRole")
test "$self_demote_status" = '302'
test "$(scalar "SELECT COUNT(*) FROM AspNetUserRoles ur JOIN AspNetRoles r ON r.Id=ur.RoleId WHERE ur.UserId=N'$owner_id' AND r.Name=N'Owner';")" = '1'

role_change_status=$(curl -sS -o /tmp/temp-role-change.html -w '%{http_code}' \
  -b "$OWNER_JAR" -c "$OWNER_JAR" \
  --data-urlencode "__RequestVerificationToken=$owner_token" \
  --data-urlencode "userId=$temp_id" \
  --data-urlencode 'role=Auditor' \
  "$BASE_URL/operators?handler=ChangeRole")
test "$role_change_status" = '302'
test "$(scalar "SELECT COUNT(*) FROM AspNetUserRoles ur JOIN AspNetRoles r ON r.Id=ur.RoleId WHERE ur.UserId=N'$temp_id' AND r.Name=N'Auditor';")" = '1'
test "$(scalar "SELECT COUNT(*) FROM AuditEvents WHERE Action=N'operator-account.role-changed' AND TargetId=N'$temp_id';")" = '1'

reset_status=$(curl -sS -o /tmp/auditor-reset.html -w '%{http_code}' \
  -b "$OWNER_JAR" -c "$OWNER_JAR" \
  --data-urlencode "__RequestVerificationToken=$owner_token" \
  --data-urlencode "userId=$auditor_id" \
  --data-urlencode "newPassword=$AUDITOR_NEW_PASSWORD" \
  "$BASE_URL/operators?handler=ResetPassword")
test "$reset_status" = '302'
test "$(scalar "SELECT COUNT(*) FROM AuditEvents WHERE Action=N'operator-account.password-reset' AND TargetId=N'$auditor_id';")" = '1'
login "$AUDITOR_EMAIL" "$AUDITOR_NEW_PASSWORD" /tmp/roles-auditor-new.cookies auditor-new

owner_token=$(get_token "$OWNER_JAR" "$BASE_URL/operators" /tmp/owner-operators-delete.html)
delete_status=$(curl -sS -o /tmp/temp-delete.html -w '%{http_code}' \
  -b "$OWNER_JAR" -c "$OWNER_JAR" \
  --data-urlencode "__RequestVerificationToken=$owner_token" \
  --data-urlencode "userId=$temp_id" \
  "$BASE_URL/operators?handler=Delete")
test "$delete_status" = '302'
test "$(scalar "SELECT COUNT(*) FROM AspNetUsers WHERE Id=N'$temp_id';")" = '0'
test "$(scalar "SELECT COUNT(*) FROM AuditEvents WHERE Action=N'operator-account.deleted' AND TargetId=N'$temp_id';")" = '1'

echo "Verifying password material never enters immutable audit JSON..."
test "$(scalar "SELECT COUNT(*) FROM AuditEvents WHERE TargetType=N'operator-account' AND (COALESCE(BeforeJson,N'') LIKE N'%$AUDITOR_NEW_PASSWORD%' OR COALESCE(AfterJson,N'') LIKE N'%$AUDITOR_NEW_PASSWORD%' OR COALESCE(MetadataJson,N'') LIKE N'%$AUDITOR_NEW_PASSWORD%');")" = '0'

echo "P12 roles and permissions integration checks passed."

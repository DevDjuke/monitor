#!/usr/bin/env bash
set -euo pipefail

BASE_URL="${BASE_URL:-http://127.0.0.1:5092}"
DB_NAME="${DB_NAME:-MonitorSavedViewCi}"
SQL_PASSWORD="${SQL_PASSWORD:-MonitorSavedViewCi!2026Password}"
COOKIE_JAR=/tmp/monitor-saved-view-cookies.txt
APP_LOG=/tmp/monitor-saved-view-ci.log

sql_container=""
app_pid=""

sql_scalar() {
  docker exec "$sql_container" /opt/mssql-tools18/bin/sqlcmd \
    -S localhost -U sa -P "$SQL_PASSWORD" -C -b -d "$DB_NAME" -h -1 -W \
    -Q "$1" | tr -d '\r' | xargs
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
    echo '=== Monitor saved-view log ==='
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

# Authenticate as the bootstrap owner.
curl -fsS -c "$COOKIE_JAR" "$BASE_URL/account/login" -o /tmp/saved-view-login.html
login_token=$(python3 - <<'PY'
import re
page = open('/tmp/saved-view-login.html', encoding='utf-8').read()
match = re.search(r'name="__RequestVerificationToken"[^>]*value="([^"]+)"', page)
assert match, 'login antiforgery token not found'
print(match.group(1))
PY
)
login_status=$(post_form "$BASE_URL/account/login" \
  "__RequestVerificationToken=$login_token" \
  'Input.Email=saved-view-ci@monitor.local' \
  'Input.Password=MonitorSavedViewAdmin2026' \
  'Input.RememberMe=false')
test "$login_status" = '302'

# The reusable TagHelper/ViewComponent toolbar belongs only to supported operational surfaces.
for surface in runs logs usage alerts budgets audit commands; do
  curl -fsS -b "$COOKIE_JAR" "$BASE_URL/$surface" -o "/tmp/saved-view-$surface.html"
  grep -q 'aria-label="Saved views"' "/tmp/saved-view-$surface.html"
  grep -q 'Save current view' "/tmp/saved-view-$surface.html"
done
curl -fsS -b "$COOKIE_JAR" "$BASE_URL/alerts/rules" -o /tmp/saved-view-alert-rules.html
if grep -q 'aria-label="Saved views"' /tmp/saved-view-alert-rules.html; then
  echo 'Saved-view toolbar leaked onto an unsupported route.'
  exit 1
fi

# Canonicalization strips transient/unknown parameters before they can reach persistence.
runs_token=$(page_token \
  "$BASE_URL/runs?status=Failed&environment=production&before=999&evil=1" \
  /tmp/saved-view-runs-filtered.html)
python3 - <<'PY'
import html
import re
page = html.unescape(open('/tmp/saved-view-runs-filtered.html', encoding='utf-8').read())
match = re.search(r'name="queryString" value="([^"]*)"[^>]*data-saved-view-current-query', page)
assert match, 'canonical saved-view input not found'
assert match.group(1) == '?status=Failed&environment=production', match.group(1)
assert 'before=999' not in match.group(1)
assert 'evil=1' not in match.group(1)
PY

audit_before=$(sql_scalar 'SET NOCOUNT ON; SELECT COUNT(*) FROM AuditEvents;')

create_status=$(post_form "$BASE_URL/saved-views?handler=Create" \
  "__RequestVerificationToken=$runs_token" \
  'surface=Runs' \
  'name=Production failures' \
  'queryString=?status=Failed&environment=production&before=999&evil=1' \
  'isPinned=true' \
  'returnUrl=/runs?status=Failed&environment=production')
test "$create_status" = '302'

owner_id=$(sql_scalar "SET NOCOUNT ON; SELECT TOP 1 Id FROM AspNetUsers WHERE NormalizedEmail = N'SAVED-VIEW-CI@MONITOR.LOCAL';")
test -n "$owner_id"
saved_id=$(sql_scalar "SET NOCOUNT ON; SELECT TOP 1 CONVERT(nvarchar(36), Id) FROM SavedViews WHERE UserId = N'$owner_id' AND Surface = 1 AND NameKey = N'PRODUCTION FAILURES';")
test -n "$saved_id"
stored_query=$(sql_scalar "SET NOCOUNT ON; SELECT QueryString FROM SavedViews WHERE Id = '$saved_id';")
test "$stored_query" = '?status=Failed&environment=production'

# Exact canonical filter state recognizes and applies the named view.
curl -fsS -b "$COOKIE_JAR" \
  "$BASE_URL/runs?status=Failed&environment=production" \
  -o /tmp/saved-view-applied.html
python3 - <<'PY'
import html
import re
page = html.unescape(open('/tmp/saved-view-applied.html', encoding='utf-8').read())
options = re.findall(r'<option\b[^>]*>[^<]*</option>', page)
selected = [option for option in options if re.search(r'\bselected(?:\s*=|\s|>)', option)]
assert any('Production failures' in option for option in selected), selected
assert re.search(r'<strong>\s*Applied\s*</strong>', page), 'Applied state missing'
PY

# A pin is visible globally as a fast link.
curl -fsS -b "$COOKIE_JAR" "$BASE_URL/logs" -o /tmp/saved-view-pinned.html
grep -q 'PINNED VIEWS' /tmp/saved-view-pinned.html
grep -q 'Production failures' /tmp/saved-view-pinned.html

# Rename and uniqueness semantics.
manage_token=$(page_token "$BASE_URL/saved-views" /tmp/saved-view-manage.html)
rename_status=$(post_form "$BASE_URL/saved-views?handler=Rename" \
  "__RequestVerificationToken=$manage_token" \
  "id=$saved_id" \
  'name=Prod failures' \
  'returnUrl=/saved-views')
test "$rename_status" = '302'
test "$(sql_scalar "SET NOCOUNT ON; SELECT Name FROM SavedViews WHERE Id = '$saved_id';")" = 'Prod failures'

manage_token=$(page_token "$BASE_URL/saved-views" /tmp/saved-view-manage-2.html)
post_form "$BASE_URL/saved-views?handler=Create" \
  "__RequestVerificationToken=$manage_token" \
  'surface=Runs' \
  'name=Prod failures' \
  'queryString=?status=Success' \
  'returnUrl=/saved-views' > /dev/null
same_surface_count=$(sql_scalar "SET NOCOUNT ON; SELECT COUNT(*) FROM SavedViews WHERE UserId = N'$owner_id' AND Surface = 1 AND NameKey = N'PROD FAILURES';")
test "$same_surface_count" = '1'

manage_token=$(page_token "$BASE_URL/saved-views" /tmp/saved-view-manage-3.html)
other_surface_status=$(post_form "$BASE_URL/saved-views?handler=Create" \
  "__RequestVerificationToken=$manage_token" \
  'surface=Logs' \
  'name=Prod failures' \
  'queryString=?Window=24h' \
  'returnUrl=/saved-views')
test "$other_surface_status" = '302'
test "$(sql_scalar "SET NOCOUNT ON; SELECT COUNT(*) FROM SavedViews WHERE UserId = N'$owner_id' AND Surface = 2 AND NameKey = N'PROD FAILURES';")" = '1'

# Sidebar pinning is deliberately bounded to six views.
for n in 2 3 4 5 6; do
  manage_token=$(page_token "$BASE_URL/saved-views" "/tmp/saved-view-pin-$n.html")
  post_form "$BASE_URL/saved-views?handler=Create" \
    "__RequestVerificationToken=$manage_token" \
    'surface=Runs' \
    "name=Pinned $n" \
    'queryString=' \
    'isPinned=true' \
    'returnUrl=/saved-views' > /dev/null
done
test "$(sql_scalar "SET NOCOUNT ON; SELECT COUNT(*) FROM SavedViews WHERE UserId = N'$owner_id' AND IsPinned = 1;")" = '6'

manage_token=$(page_token "$BASE_URL/saved-views" /tmp/saved-view-pin-overflow.html)
post_form "$BASE_URL/saved-views?handler=Create" \
  "__RequestVerificationToken=$manage_token" \
  'surface=Runs' \
  'name=Pinned overflow' \
  'queryString=' \
  'isPinned=true' \
  'returnUrl=/saved-views' > /dev/null
test "$(sql_scalar "SET NOCOUNT ON; SELECT COUNT(*) FROM SavedViews WHERE UserId = N'$owner_id' AND IsPinned = 1;")" = '6'
test "$(sql_scalar "SET NOCOUNT ON; SELECT COUNT(*) FROM SavedViews WHERE UserId = N'$owner_id' AND NameKey = N'PINNED OVERFLOW';")" = '0'

# Seed another Identity user's private preference and verify ownership isolation in both projection and mutation.
# `-I` gives the raw test session QUOTED_IDENTIFIER ON, matching normal application/EF SQL sessions.
docker exec "$sql_container" /opt/mssql-tools18/bin/sqlcmd -I \
  -S localhost -U sa -P "$SQL_PASSWORD" -C -b -d "$DB_NAME" \
  -Q "
    INSERT INTO AspNetUsers
      (Id, UserName, NormalizedUserName, Email, NormalizedEmail, EmailConfirmed, PasswordHash, SecurityStamp, ConcurrencyStamp, PhoneNumber, PhoneNumberConfirmed, TwoFactorEnabled, LockoutEnd, LockoutEnabled, AccessFailedCount)
    VALUES
      (N'ci-other-user', N'other@monitor.local', N'OTHER@MONITOR.LOCAL', N'other@monitor.local', N'OTHER@MONITOR.LOCAL', 1, NULL, NEWID(), NEWID(), NULL, 0, 0, NULL, 0, 0);

    INSERT INTO SavedViews
      (Id, UserId, Surface, Name, NameKey, QueryString, IsPinned, CreatedAt, UpdatedAt)
    VALUES
      ('11111111-1111-1111-1111-111111111111', N'ci-other-user', 1, N'Other private view', N'OTHER PRIVATE VIEW', N'?status=Failed', 0, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET());
  "

curl -fsS -b "$COOKIE_JAR" "$BASE_URL/saved-views" -o /tmp/saved-view-owner-only.html
if grep -q 'Other private view' /tmp/saved-view-owner-only.html; then
  echo 'Another user saved view leaked into the owner workspace.'
  exit 1
fi
manage_token=$(python3 - <<'PY'
import re
page = open('/tmp/saved-view-owner-only.html', encoding='utf-8').read()
match = re.search(r'name="__RequestVerificationToken"[^>]*value="([^"]+)"', page)
assert match
print(match.group(1))
PY
)
cross_delete_status=$(post_form "$BASE_URL/saved-views?handler=Delete" \
  "__RequestVerificationToken=$manage_token" \
  'id=11111111-1111-1111-1111-111111111111' \
  'returnUrl=/saved-views')
test "$cross_delete_status" = '404'
test "$(sql_scalar "SET NOCOUNT ON; SELECT COUNT(*) FROM SavedViews WHERE Id = '11111111-1111-1111-1111-111111111111';")" = '1'

# Personal preferences deliberately stay out of the operational forensic audit stream.
audit_after=$(sql_scalar 'SET NOCOUNT ON; SELECT COUNT(*) FROM AuditEvents;')
test "$audit_after" = "$audit_before"

# Schema guarantees Identity ownership and per-user/per-surface name uniqueness.
schema_state=$(sql_scalar "
  SET NOCOUNT ON;
  SELECT CONCAT(
    (SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID(N'SavedViews') AND name = N'IX_SavedViews_UserId_Surface_NameKey' AND is_unique = 1), N'|',
    (SELECT COUNT(*) FROM sys.foreign_keys WHERE parent_object_id = OBJECT_ID(N'SavedViews') AND referenced_object_id = OBJECT_ID(N'AspNetUsers'))
  );")
test "$schema_state" = '1|1'

# Runs now has stable browser filter state while `before` remains API/keyset state only.
curl -fsS -b "$COOKIE_JAR" "$BASE_URL/js/runs.js" -o /tmp/saved-view-runs.js
grep -q 'history.replaceState' /tmp/saved-view-runs.js
grep -q "params.set('before', state.cursor)" /tmp/saved-view-runs.js
grep -q 'monitor:saved-view-url-changed' /tmp/saved-view-runs.js

echo 'Saved views integration assertions passed.'

#!/usr/bin/env bash
# Mock-mode suites (Development, no APS). Resets this project's database volume before each suite.
# Needs: docker, python, dotnet, node + `npm install` in tests/e2e and `npm run build` in frontend/.
set -u
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"; cd "$ROOT"
TMP="${TEMP:-/tmp}"
unset Aps__Mode Aps__ClientId Aps__ClientSecret Aps__Bucket Aps__AuthBaseUrl Aps__DesignAutomationBaseUrl Aps__OssBaseUrl

DB_PW=$(grep -E '^MEL_DB_PASSWORD=' "$ROOT/.env.dev" 2>/dev/null | cut -d= -f2- | tr -d '\r')
[ -n "$DB_PW" ] || { echo "Missing .env.dev: run tools/init-dev-secrets.ps1 first"; exit 2; }
export ConnectionStrings__Mel="Host=localhost;Port=5433;Database=mel_test;Username=mel;Password=$DB_PW"   # NEVER the real "mel" database
stop() { taskkill //F //IM MasterEquipmentApi.exe >/dev/null 2>&1; }
reset_test_db() {   # drops/recreates ONLY mel_test; the real "mel" database (APS connection, linked models) is never touched
  docker exec mel-aps-postgres psql -U mel -d postgres -q -c 'DROP DATABASE IF EXISTS mel_test WITH (FORCE)' -c 'CREATE DATABASE mel_test' >/dev/null
}
fresh() {
  stop; docker compose --env-file "$ROOT/.env.dev" up -d >/dev/null 2>&1
  for i in $(seq 1 20); do [ "$(docker inspect -f '{{.State.Health.Status}}' mel-aps-postgres 2>/dev/null)" = healthy ] && break; sleep 3; done
  reset_test_db
  (cd backend && dotnet run --no-build --urls http://localhost:5080 > "$TMP/mel-mock.log" 2>&1 &); sleep 10
}

rc=0
want() { [ -z "${1:-}" ] || [ "$1" = "$2" ]; }   # usage: run_mock_tests.sh [rbac|jobs|ui]
want "${1:-}" rbac && { echo "=== API: auth + RBAC + audit"; fresh; python -I tests/e2e/api_auth_rbac.py || rc=1; }
want "${1:-}" jobs && { echo "=== API: sync jobs (mock)";    fresh; python -I tests/e2e/api_sync_jobs.py || rc=1; }
want "${1:-}" ui   && { echo "=== Browser: full workflow";   fresh; (cd tests/e2e && node ui_workflow.cjs) || rc=1; }
stop
exit $rc

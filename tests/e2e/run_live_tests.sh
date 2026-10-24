#!/usr/bin/env bash
# Live-mode pipeline tests against the FAKE APS (tests/e2e/fake_aps.py). Needs: docker, python, dotnet, a built bundle zip
# (powershell -File tools/build-bundle.ps1). Resets this project's database volume.
set -u
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
TMP="${TEMP:-/tmp}"
cd "$ROOT"

export Aps__Mode=Live Aps__ClientId=test-id Aps__ClientSecret=test-secret Aps__Bucket=mel-test-bucket
export Aps__CallbackUrl=http://localhost:5080/api/aps/callback
export Aps__AuthBaseUrl=http://localhost:5099
export Aps__DesignAutomationBaseUrl=http://localhost:5099/da/us-east/v3
export Aps__OssBaseUrl=http://localhost:5099/oss/v2
export Aps__UserInfoUrl=http://localhost:5099/userinfo
export Aps__DataManagementBaseUrl=http://localhost:5099   # every APS host must point at the fake, or tests would call the real service
export Aps__PollSeconds=1
export Aps__BrowseBudgetSeconds=3   # short, so the 'budget ends' scenario is quick to test

DB_PW=$(grep -E '^MEL_DB_PASSWORD=' "$ROOT/.env.dev" 2>/dev/null | cut -d= -f2- | tr -d '\r')
[ -n "$DB_PW" ] || { echo "Missing .env.dev: run tools/init-dev-secrets.ps1 first"; exit 2; }
export ConnectionStrings__Mel="Host=localhost;Port=5433;Database=mel_test;Username=mel;Password=$DB_PW"   # NEVER the real "mel" database
stop() { taskkill //F //IM MasterEquipmentApi.exe >/dev/null 2>&1; }
reset_test_db() {   # drops/recreates ONLY mel_test; the real "mel" database (APS connection, linked models) is never touched
  docker exec mel-aps-postgres psql -U mel -d postgres -q -c 'DROP DATABASE IF EXISTS mel_test WITH (FORCE)' -c 'CREATE DATABASE mel_test' >/dev/null
}
fresh_db() {
  docker compose --env-file "$ROOT/.env.dev" up -d >/dev/null 2>&1
  for i in $(seq 1 20); do [ "$(docker inspect -f '{{.State.Health.Status}}' mel-aps-postgres 2>/dev/null)" = healthy ] && break; sleep 3; done
  reset_test_db
}
start_backend() { (cd backend && dotnet run --no-build --urls http://localhost:5080 > "$TMP/mel-live.log" 2>&1 &); sleep 10; }

stop; fresh_db
python -I tests/e2e/fake_aps.py 5099 & FAKE=$!
sleep 1

echo "=== aps-setup against the fake (run twice: create, then new versions)"
(cd backend && dotnet run --no-build -- aps-setup ../artifacts 2>&1 | tail -8)
(cd backend && dotnet run --no-build -- aps-setup ../artifacts 2>&1 | tail -6)
python -I - <<'PY'
import json, urllib.request
s = json.load(urllib.request.urlopen("http://localhost:5099/_control/state"))
acts = {a["id"]: a for a in s["activities"]}   # latest definition per activity id
ok = lambda n, c: print(("PASS " if c else "FAIL ") + n)
YEARS = (2025, 2026)
ok("a bundle per Revit release uploaded on each run (2 releases x 2 runs), file part last",
   len(s["bundle_uploads"]) == 4 and all(u["file_last"] for u in s["bundle_uploads"])
   and sorted({u["key"].split("-v")[0] for u in s["bundle_uploads"]}) == ["MelSyncApp2025", "MelSyncApp2026"])
ok("every alias moved to v2 on the second run (PATCH, not re-created)",
   all(s["aliases"].get(f"{k}/{i}{y}/prod") == 2 for k, i in (("appbundles", "MelSyncApp"), ("activities", "MelSyncActivity")) for y in YEARS)
   and len(s.get("alias_patches", [])) == 4)
ok("each activity runs revitcoreconsole with ITS OWN release's bundle",
   all("revitcoreconsole.exe /al" in acts[f"MelSyncActivity{y}"]["commandLine"][0] and f"appbundles[MelSyncApp{y}].path" in acts[f"MelSyncActivity{y}"]["commandLine"][0] for y in YEARS))
ok("activities declare input.json (get) and result.json (put)",
   all(acts[f"MelSyncActivity{y}"]["parameters"]["inputJson"]["localName"] == "input.json" and acts[f"MelSyncActivity{y}"]["parameters"]["result"]["verb"] == "put" for y in YEARS))
ok("each activity uses its own bundle alias and the matching engine",
   all(acts[f"MelSyncActivity{y}"]["appbundles"] == [f"fakenick.MelSyncApp{y}+prod"] and acts[f"MelSyncActivity{y}"]["engine"] == f"Autodesk.Revit+{y}" for y in YEARS))
ok("OSS bucket ensured by setup", "mel-test-bucket" in s["buckets"])
PY

echo "=== sync pipeline (Live mode)"
start_backend
python -I tests/e2e/api_sync_live.py; RC1=$?
stop

echo "=== job timeout (Aps__JobTimeoutMinutes=0)"
fresh_db; curl -s -XPOST localhost:5099/_control/reset >/dev/null
Aps__JobTimeoutMinutes=0 start_backend
python -I tests/e2e/api_sync_live.py timeout; RC2=$?
stop

echo "=== Link model helper in the browser (connect round trip + finder)"
fresh_db; curl -s -XPOST localhost:5099/_control/reset >/dev/null
start_backend
(cd tests/e2e && node ui_model_finder.cjs); RC3=$?
stop; kill $FAKE 2>/dev/null
echo; echo "live tests exit codes: main=$RC1 timeout=$RC2 finder-ui=$RC3"
[ $RC1 -eq 0 ] && [ $RC2 -eq 0 ] && [ $RC3 -eq 0 ]

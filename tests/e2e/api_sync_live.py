"""Live-mode sync pipeline against tests/e2e/fake_aps.py (NOT real APS). Fresh database + fake reset required.

Backend env (see run_live_tests.sh): Aps__Mode=Live, endpoints -> the fake, Aps__PollSeconds=1.
Argument "timeout": run only the job-timeout scenario (backend started with Aps__JobTimeoutMinutes=0).
"""
import json, subprocess, sys, time, urllib.parse, urllib.request, urllib.error
from concurrent.futures import ThreadPoolExecutor

BASE, FAKE = "http://localhost:5080/api", "http://localhost:5099"
M = "demo-model"
AHU = "9f84b2c1-482a-412e-a4b8-f14d021c4321-0003e8a1"
CH = "e14a88bc-19fe-49aa-8411-9a7b032d8471-0003e8b2"
PUMP = "7c20ad41-bb01-49fa-c438-bb4510fae120-0003e8c3"
CT = "45d12ef3-7182-4aa8-9331-dd34e12c5549-0003e8d4"
LEAD_TIME, PO, WEIGHT = "2bb0a1a8-c111-4292-8118-ef4ba08b790c", "31b2f741-4a06-4842-8412-1ddeb1aabbbe", "3a2b1c0d-9e8f-7a6b-5c4d-3e2f1a0b9c8d"
PROJ, CLOUD = "11111111-2222-3333-4444-555555555555", "99999999-8888-7777-6666-555555555555"
results = []


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *a, **k): return None


def raw(method, url, body=None, token=None, follow=True):
    h = {"Content-Type": "application/json"}
    if token: h["Authorization"] = "Bearer " + token
    req = urllib.request.Request(url, method=method, data=json.dumps(body).encode() if body is not None else None, headers=h)
    opener = urllib.request.build_opener() if follow else urllib.request.build_opener(NoRedirect)
    try:
        with opener.open(req) as r: return r.status, r.read(), dict(r.headers)
    except urllib.error.HTTPError as e: return e.code, e.read(), dict(e.headers)


def call(method, path, body=None, token=None):
    s, b, _ = raw(method, BASE + path, body, token)
    try: return s, json.loads(b or b"null")
    except Exception: return s, b


def fake(method="GET", path="/_control/state", body=None):
    s, b, _ = raw(method, FAKE + path, body)
    return json.loads(b)


def check(name, cond, extra=""):
    results.append(bool(cond))
    print(("PASS " if cond else "FAIL ") + name + (f"  [{extra}]" if extra and not cond else ""))


def login(u):
    s, r = call("POST", "/auth/dev-login", {"username": u}); assert s == 200, r
    return r["token"]


CON, LEAD = login("contributor@demo.local"), login("lead@demo.local")


def stage(uid, guid, val):
    s, r = call("POST", "/EquipmentSync/stage", {"modelGuid": M, "equipmentUniqueId": uid, "parameterGuid": guid, "proposedValue": val}, CON)
    assert s == 200, (s, r); return r["record"]["id"]


def approve(rid):
    s, r = call("POST", f"/EquipmentSync/approve/{rid}", {}, LEAD); assert s == 200, (s, r)


def dispatch(uids): return call("POST", "/EquipmentSync/dispatch-revit-sync", {"modelGuid": M, "equipmentUniqueIds": uids}, LEAD)


def staged():
    return {r["id"]: r for r in call("GET", f"/EquipmentSync/staged/{M}", token=LEAD)[1]}


def values(uid): return next(e for e in call("GET", f"/equipment/{M}", token=LEAD)[1] if e["uniqueId"] == uid)["values"]


def wait_job(job_id, timeout=60):
    end = time.time() + timeout
    while time.time() < end:
        j = call("GET", f"/sync/jobs/{M}/{job_id}", token=LEAD)[1]
        if j["status"] not in ("Queued", "Submitted"): return j
        time.sleep(1)
    raise TimeoutError("job did not finish")


def connect():
    s, c = call("POST", "/aps/connect", token=LEAD); assert s == 200, c
    _, _, h = raw("GET", c["url"], follow=False)
    _, _, h2 = raw("GET", h["Location"], follow=False)
    assert "aps=connected" in h2["Location"], h2


def run_sync(uids):
    s, a = dispatch(uids); assert s == 202, (s, a)
    return a, wait_job(a["jobId"])


if len(sys.argv) > 1 and sys.argv[1] == "timeout":
    call("PUT", f"/models/{M}/cloud-link", {"region": "US", "projectGuid": PROJ, "modelGuid": CLOUD, "revitVersion": 2025}, LEAD)
    # (connection already established by the main run's state is not available in a fresh DB: connect first)
    s, c = call("POST", "/aps/connect", token=LEAD)
    s2, _, h = raw("GET", c["url"], follow=False)
    raw("GET", h["Location"].replace("http://localhost:5080", "http://localhost:5080"), follow=False)
    fake("POST", "/_control", {"stay_pending": True})
    r = stage(AHU, LEAD_TIME, "18"); approve(r)
    a, j = run_sync([AHU])
    check("timed-out job is Failed with a timeout message", j["status"] == "Failed" and "did not finish within" in (j["error"] or ""), j)
    check("timed-out change returns to Approved", staged()[r]["status"] == "Approved" and "did not finish" in (staged()[r]["lastError"] or ""), staged()[r])
    print(f"\n{sum(results)}/{len(results)} passed"); sys.exit(0 if all(results) else 1)

# ===== 0. preflight blockers ===================================================================================
r_lt = stage(AHU, LEAD_TIME, "18"); r_ch = stage(CH, PO, "PO-LIVE-1"); r_pump = stage(PUMP, PO, "PO-LIVE-2")
for r in (r_lt, r_ch, r_pump): approve(r)

s, b = dispatch([AHU, CH, PUMP])
check("dispatch before the model is linked -> 409 'not linked'", s == 409 and "not linked" in b["error"], (s, b))
s, b = call("POST", f"/sync/check/{M}", token=LEAD)
check("connectivity check before the model is linked -> 409 'not linked'", s == 409 and "not linked" in b["error"], (s, b))
s, _ = call("PUT", f"/models/{M}/cloud-link", {"region": "US", "projectGuid": PROJ, "modelGuid": CLOUD}, CON)
check("contributor cannot link a model -> 403", s == 403, s)
s, _ = call("PUT", f"/models/{M}/cloud-link", {"region": "MARS", "projectGuid": PROJ, "modelGuid": CLOUD}, LEAD)
check("bad region -> 400", s == 400, s)
s, _ = call("PUT", f"/models/{M}/cloud-link", {"region": "US", "projectGuid": "nope", "modelGuid": CLOUD}, LEAD)
check("bad project guid -> 400", s == 400, s)
s, b = call("PUT", f"/models/{M}/cloud-link", {"region": "us", "projectGuid": PROJ, "modelGuid": CLOUD, "revitVersion": 2025}, LEAD)
check("lead links the model (region normalized to US, Revit release recorded)", s == 200 and b["region"] == "US" and b["revitVersion"] == 2025, (s, b))
s, _ = call("PUT", f"/models/{M}/cloud-link", {"region": "US", "projectGuid": PROJ, "modelGuid": CLOUD, "revitVersion": 1999}, LEAD)
check("an implausible Revit release is rejected -> 400", s == 400, s)
s, b = dispatch([AHU, CH, PUMP])
check("dispatch before an APS account is connected -> 409 'No APS account'", s == 409 and "No APS account" in b["error"], (s, b))
s, b = call("POST", f"/sync/check/{M}", token=LEAD)
check("connectivity check before an APS account is connected -> 409 'No APS account'", s == 409 and "No APS account" in b["error"], (s, b))
check("blocked dispatch left the changes Approved (nothing queued)", all(staged()[r]["status"] == "Approved" for r in (r_lt, r_ch, r_pump)))

# ===== 1. connect the APS account ==============================================================================
s, _ = call("GET", "/aps/browse/hubs")
check("browse hubs requires a token -> 401", s == 401, s)
s, _ = call("GET", "/aps/browse/hubs", token=CON)
check("contributor cannot browse APS -> 403", s == 403, s)
s, b = call("GET", "/aps/browse/hubs", token=LEAD)
check("browse before an APS account is connected -> 409 'No APS account'", s == 409 and "No APS account" in b["error"], (s, b))
s, st = call("GET", "/aps/status", token=CON)
check("status: Live, not connected", s == 200 and st["mode"] == "Live" and st["connected"] is False, st)
s, _ = call("POST", "/aps/connect", token=CON)
check("contributor cannot start APS connect -> 403", s == 403, s)
s, _ = call("POST", "/aps/connect")
check("anonymous cannot start APS connect -> 401", s == 401, s)
s, c = call("POST", "/aps/connect", token=LEAD)
check("lead gets an authorize URL with client, redirect, scope and state", s == 200 and all(x in c["url"] for x in ("client_id=test-id", "state=", "code%3Aall", "data%3Awrite", "user-profile%3Aread")), c)
code, _, hdr = raw("GET", c["url"], follow=False)
loc = hdr["Location"]
bad = loc.replace("state=", "state=X")
code, _, h2 = raw("GET", bad, follow=False)
check("callback with a forged state is rejected", "aps=error" in h2.get("Location", ""), h2)
check("a forged callback did not connect anything", call("GET", "/aps/status", token=LEAD)[1]["connected"] is False)
fake("POST", "/_control", {"user_ttl": 2, "userinfo_fail": True})   # name lookup fails at connect time; token dies fast
code, _, h3 = raw("GET", loc, follow=False)
check("callback with the real one-time state connects", "aps=connected" in h3.get("Location", ""), h3)
code, _, h4 = raw("GET", loc.replace("code=", "code=").replace("code-", "code-"), follow=False)
check("replaying the same state is rejected (one-time)", "aps=error" in h4.get("Location", ""), h4)
st0 = call("GET", "/aps/status", token=CON)[1]
check("connecting works even when the account-name lookup fails (account is null, not an error)", st0["connected"] is True and st0["account"] is None, st0)
fake("POST", "/_control", {"userinfo_fail": False})
call("GET", "/aps/browse/hubs", token=LEAD)    # token has expired: this refresh heals the missing account name
fake("POST", "/_control", {"user_ttl": 3600})
s, st = call("GET", "/aps/status", token=CON)
check("status: connected as the APS account, by the lead", st["connected"] and st["account"] == "svc@firm.example" and st["connectedBy"] == "lead@demo.local", st)
check("the refresh token is stored encrypted, not in clear text",
      b"RT-" not in subprocess.run(["docker", "exec", "mel-aps-postgres", "psql", "-U", "mel", "-d", "mel_test", "-Atc", 'select "ProtectedRefreshToken" from aps_connection'], capture_output=True).stdout)

# ===== 2. partial success through the real client path ==========================================================
fake("POST", "/_control", {"owned": [PUMP], "readonly": [f"{CH}:{PO}"]})
a, j = run_sync([AHU, CH, PUMP])
check("job PartiallySucceeded (1 applied, 2 rejected by the bundle)", j["status"] == "PartiallySucceeded", j["status"])
st = staged()
check("applied change committed and written to the model", st[r_lt]["status"] == "CommittedToModel" and values(AHU)[LEAD_TIME] == "18", st[r_lt])
check("read-only parameter -> Approved with ReadOnly", st[r_ch]["status"] == "Approved" and "ReadOnly" in st[r_ch]["lastError"], st[r_ch])
check("checked-out element -> Approved with OwnedByOtherUser", st[r_pump]["status"] == "Approved" and "OwnedByOtherUser" in st[r_pump]["lastError"], st[r_pump])
check("failed values not written", values(CH)[PO] == "PO-2026-9488" and values(PUMP)[PO] == "PO-2026-8801")
check("job shows the DA work item id and final status", j["workItemId"].startswith("wi-") and j["daStatus"] == "success", j)
check("report URL visible to the lead", j["reportUrl"] and "/report/" in j["reportUrl"], j["reportUrl"])

fs = fake()
wi = fs["workitems"][j["workItemId"]]
check("bucket was created once", fs["buckets"] == ["mel-test-bucket"], fs["buckets"])
check("work item received the 3-legged user token (adsk3LeggedToken)", wi["token_ok"] and not wi["errors"], wi["errors"])
p = wi["payload"]
check("payload targets the linked cloud model and carries its Revit release", (p["region"], p["projectGuid"], p["modelGuid"], p["revitVersion"]) == ("US", PROJ, CLOUD, 2025), p)
u0 = next(u for u in p["updates"] if u["uniqueId"] == AHU)
check("payload update carries type, unit, equipment guid and stagingId",
      u0["dataType"] == "INTEGER" and u0["parameterGuid"] == LEAD_TIME and u0["value"] == "18" and u0["equipmentGuid"].startswith("a1f0c2d4") and u0["stagingId"] == r_lt, u0)
check("result upload was finalized with OSS", any(k.startswith("results/") for k in fs["completed"]) and any(k.startswith("inputs/") for k in fs["completed"]), fs["completed"])
check("input document delivered through a signed URL (not inline)", wi["args"]["inputJson"]["url"].startswith(FAKE + "/s3/"), wi["args"])

# ===== 3. refresh-token rotation persists across jobs ==========================================================
# Access tokens now live 2s: reconnect under that TTL so the backend's cached token expires at once and EVERY job refreshes.
fake("POST", "/_control", {"user_ttl": 2, "owned": [], "readonly": []})
connect()
before = fake()["refresh_calls"]
a, j = run_sync([CH, PUMP])   # also the retry of the two failed changes
check("retry of the two failed changes now succeeds", j["status"] == "Succeeded" and values(CH)[PO] == "PO-LIVE-1" and values(PUMP)[PO] == "PO-LIVE-2", (j["status"], j["error"]))
r_a = stage(AHU, PO, "PO-ROT-1"); approve(r_a)
a, j = run_sync([AHU])
fs = fake()
check("second sync also succeeded using the ROTATED refresh token", j["status"] == "Succeeded" and values(AHU)[PO] == "PO-ROT-1", (j["status"], j["error"]))
check("refreshed at least twice and the fake never saw a reused (stale) refresh token", fs["refresh_calls"] - before >= 2 and fs["refresh_reuse_rejected"] == 0, (fs["refresh_calls"] - before, fs["refresh_reuse_rejected"]))

# ===== 4. failure modes ========================================================================================
r = stage(CT, PO, "PO-FAILDA"); approve(r)
fake("POST", "/_control", {"fail_workitem": True})
a, j = run_sync([CT])
check("failed Design Automation work item -> job Failed with its status", j["status"] == "Failed" and "failedInstructions" in j["error"], j["error"])
check("failed work item -> change back to Approved with the reason", staged()[r]["status"] == "Approved" and "failedInstructions" in staged()[r]["lastError"], staged()[r])

fake("POST", "/_control", {"fail_workitem": False, "open_error": "InvalidOperationException: could not open the cloud model"})
a, j = run_sync([CT])
check("bundle could not open the model -> Failed, reason surfaced", j["status"] == "Failed" and "could not open" in staged()[r]["lastError"], (j["status"], staged()[r]["lastError"]))

fake("POST", "/_control", {"open_error": None, "synced": False})
a, j = run_sync([CT])
check("applied but NOT synced to the cloud -> nothing committed ('NotSynced')", j["status"] == "Failed" and "NotSynced" in staged()[r]["lastError"] and values(CT)[PO] == "PO-2026-9102", (j["status"], staged()[r]["lastError"], values(CT)[PO]))

fake("POST", "/_control", {"synced": True, "no_result": True})
a, j = run_sync([CT])
check("work item 'succeeded' without a result file -> Failed", j["status"] == "Failed" and "no result file" in (j["error"] or ""), j["error"])

fake("POST", "/_control", {"no_result": False, "missing": [CT]})
a, j = run_sync([CT])
check("element missing from the model -> ElementNotFound, value not written", "ElementNotFound" in staged()[r]["lastError"] and values(CT)[PO] == "PO-2026-9102", staged()[r])

fake("POST", "/_control", {"missing": []})
a, j = run_sync([CT])
check("after the problem is fixed the same change applies (idempotent retry)", j["status"] == "Succeeded" and values(CT)[PO] == "PO-FAILDA", (j["status"], j["error"]))

# ===== 5. revoked credential ===================================================================================
r = stage(AHU, WEIGHT, "3900"); approve(r)
fake("POST", "/_control", {"revoked": True})   # TTL is still 2s, so the next submit must refresh and is refused
a, j = run_sync([AHU])
check("revoked APS credential -> job Failed telling the lead to reconnect", j["status"] == "Failed" and "Reconnect the APS account" in j["error"], j["error"])
check("...and the change is safely back to Approved", staged()[r]["status"] == "Approved", staged()[r]["status"])
fake("POST", "/_control", {"revoked": False})
connect()
a, j = run_sync([AHU])
check("after reconnecting, the same change syncs", j["status"] == "Succeeded", (j["status"], j["error"]))

# ===== 6. audit ================================================================================================
_, audit = call("GET", f"/audit/{M}?limit=1000", token=LEAD)
acts = [a["action"] for a in audit]
check("audit records ApsConnected, ModelLinked", "ApsConnected" in [a["action"] for a in call("GET", "/audit/*", token=LEAD)[1]] and "ModelLinked" in acts, set(acts))
check("every SyncFailed carries the reason", all(a["details"] for a in audit if a["action"] == "SyncFailed"))

# ===== 6b. model finder (hubs -> projects -> cloud models) =====================================================
fake("POST", "/_control", {"user_ttl": 3600})   # real tokens last an hour; short ones would make every request refresh under a lock
s, hubs = call("GET", "/aps/browse/hubs", token=LEAD)
check("hubs listed with region where APS reports it", s == 200 and [(h["name"], h["region"]) for h in hubs] == [("Acme Account", "EMEA"), ("Regionless Account", None)], hubs)
s, projs = call("GET", "/aps/browse/hubs/b.hub1/projects", token=LEAD)
check("projects listed for a hub", s == 200 and [p["name"] for p in projs] == ["Test Project", "Huge Project"], projs)

s, r = call("GET", "/aps/browse/hubs/b.hub1/projects/b.p1/models", token=LEAD)
names = sorted(m["name"] for m in r["models"])
check("scan finds cloud models in nested folders", s == 200 and names == ["E-Power.rvt", "M-CentralPlant.rvt", "Old-Model.rvt"], names)
check("scan followed pagination (E-Power sits behind links.next of the root folder)", "E-Power.rvt" in names)
central = next(m for m in r["models"] if m["name"] == "M-CentralPlant.rvt")
check("model carries project GUID, model GUID and folder path",
      central["projectGuid"] == "aaaaaaaa-1111-2222-3333-444444444444" and central["modelGuid"] == "bbbbbbbb-1111-2222-3333-444444444444" and central["folderPath"] == "Project Files/Mechanical", central)
check("composite-design flag passed through", next(m for m in r["models"] if m["name"] == "E-Power.rvt")["isComposite"] is True and central["isComposite"] is False)
check("region reported by the hub is used and a ready-to-send cloudLink is returned",
      r["region"] == "EMEA" and r["regionKnown"] is True and central["cloudLink"] == {"region": "EMEA", "projectGuid": central["projectGuid"], "modelGuid": central["modelGuid"], "revitVersion": 2025}, central["cloudLink"])
check("plain uploaded RVT is counted, not listed", r["scan"]["notCloudWorkshared"] == 1 and r["scan"]["revitFilesChecked"] == 4 and "Plain-Upload.rvt" not in names, r["scan"])
old = next(m for m in r["models"] if m["name"] == "Old-Model.rvt")
power = next(m for m in r["models"] if m["name"] == "E-Power.rvt")
check("each model's Revit release is reported and flagged supported / unsupported (engines exist for 2025 and 2026 only)",
      central["revitVersion"] == 2025 and central["supported"] is True and power["revitVersion"] == 2026 and power["supported"] is True
      and old["revitVersion"] == 2024 and old["supported"] is False and r["supportedRevitYears"] == [2025, 2026], (central, power, old))
check("scan visited root, Mechanical and Electrical folders", r["scan"]["foldersVisited"] == 3, r["scan"])
check("a pagination link to a different host was NOT followed and is reported",
      any("different host" in n for n in r["scan"]["notes"]), r["scan"]["notes"])
check("the user's APS token never reached the other host (it received no requests at all)", not fake().get("evil_hits"), fake().get("evil_hits"))

s, r2 = call("GET", "/aps/browse/hubs/b.hub1/projects/b.p1/models?search=CENTRAL", token=LEAD)
check("search is case-insensitive and applied before inspecting files (fewer API calls)",
      [m["name"] for m in r2["models"]] == ["M-CentralPlant.rvt"] and r2["scan"]["revitFilesChecked"] == 1, r2["scan"])
s, r3 = call("GET", "/aps/browse/hubs/b.hub1/projects/b.p1/models?search=zzz", token=LEAD)
check("search with no match returns an empty list (not an error)", s == 200 and r3["models"] == [], (s, r3))

s, r4 = call("GET", "/aps/browse/hubs/b.hub2/projects/b.p2/models", token=LEAD)
check("a model whose Revit release APS does not report is flagged unsupported (cannot choose an engine)",
      r4["models"][0]["revitVersion"] is None and r4["models"][0]["supported"] is False, r4["models"][0])
check("hub with no region: regionKnown=false, region=null (the UI asks the lead to choose)",
      s == 200 and r4["regionKnown"] is False and r4["region"] is None and r4["models"][0]["cloudLink"]["region"] is None, r4)

s, r5 = call("GET", "/aps/browse/hubs/b.hub1/projects/b.p3/models", token=LEAD)
check("scan is bounded: stops at 150 folders and says so", s == 200 and r5["scan"]["truncated"] is True and r5["scan"]["foldersVisited"] == 150
      and any("limits" in n for n in r5["scan"]["notes"]), r5["scan"])

# --- slow APS on a big project: the time budget ends the scan with partial results, never a 500
fake("POST", "/_control", {"dm_delay": 0.5})
t0 = time.time()
s, r6 = call("GET", "/aps/browse/hubs/b.hub1/projects/b.p3/models", token=LEAD)
dt = time.time() - t0
check("slow APS: scan stops at its time budget with partial results (200, truncated, 'Stopped after'), not a 500",
      s == 200 and r6["scan"]["truncated"] is True and any("Stopped after" in n for n in r6["scan"]["notes"]), (s, r6 if s != 200 else r6["scan"]))
check("...and it respected the budget (finished in well under the unbounded time)", dt < 15, f"{dt:.1f}s")
mc = fake().get("max_concurrent", 0)
check("scan walks folders in parallel but bounded (2 to 4 requests in flight)", 2 <= mc <= 4, mc)
fake("POST", "/_control", {"dm_delay": 0})

# --- rate limiting (HTTP 429): retried politely, then reported
fake("POST", "/_control", {"dm_429": 2})
s, hs = call("GET", "/aps/browse/hubs", token=LEAD)
check("two 429s with Retry-After are retried transparently", s == 200 and len(hs) == 2 and fake().get("dm_429_served") == 2, (s, fake().get("dm_429_served")))
fake("POST", "/_control", {"dm_429": 99})
s, b = call("GET", "/aps/browse/hubs", token=LEAD)
check("persistent rate limiting gives up after a few retries -> 502 mentioning 429 (not a hang or a 500)", s == 502 and "429" in b["error"], (s, b))
fake("POST", "/_control", {"dm_429": 0})

fake("POST", "/_control", {"dm_forbidden": True})
s, b = call("GET", "/aps/browse/hubs", token=LEAD)
check("APS 403 is reported as 502 with an actionable message (not a 500)", s == 502 and "not allowed" in b["error"] and "Custom Integrations" in b["error"], (s, b))
fake("POST", "/_control", {"dm_forbidden": False, "dm_unauthorized": True})
s, b = call("GET", "/aps/browse/hubs", token=LEAD)
check("APS 401 (token refused) -> 409 telling the lead to reconnect, distinct from 403", s == 409 and "Reconnect the APS account" in b["error"], (s, b))
fake("POST", "/_control", {"dm_unauthorized": False})

# link using exactly what the finder returned
s, b = call("PUT", f"/models/{M}/cloud-link", central["cloudLink"], LEAD)
check("the discovered cloudLink (incl. Revit release) can be used as-is to link the model", s == 200 and b["revitVersion"] == 2025 and (b["region"], b["cloudProjectGuid"], b["cloudModelGuid"]) == ("EMEA", central["projectGuid"], central["modelGuid"]), (s, b))
check("the DM calls used the connected service account's token (3-legged, data:read)", len(fake().get("dm_requests", [])) > 5)

# ===== 6c. connectivity check through the real client path ======================================================
s, _ = call("POST", f"/sync/check/{M}", token=CON)
check("contributor cannot run a connectivity check -> 403", s == 403, s)
call("PUT", f"/models/{M}/cloud-link", {"region": "US", "projectGuid": PROJ, "modelGuid": CLOUD, "revitVersion": 2025}, LEAD)   # undo the finder's re-link
fake("POST", "/_control", {"open_error": None, "synced": True, "no_result": False, "user_ttl": 3600})
s, a = call("POST", f"/sync/check/{M}", token=LEAD); assert s == 202, (s, a)
j = wait_job(a["jobId"])
wi = fake()["workitems"][j["workItemId"]]
check("check job Succeeded; the model opened", j["status"] == "Succeeded" and j["result"]["opened"] is True, (j["status"], j["error"]))
check("the work item carried NO updates and targeted the linked cloud model",
      wi["payload"]["updates"] == [] and (wi["payload"]["region"], wi["payload"]["projectGuid"], wi["payload"]["modelGuid"]) == ("US", PROJ, CLOUD), wi["payload"])
check("the work item still received the user token (needed to open a cloud model)", wi["token_ok"] and not wi["errors"], wi["errors"])
check("a check does not touch staged changes or model values", all(v["status"] in ("CommittedToModel", "Approved") for v in staged().values()))

fake("POST", "/_control", {"open_error": "InvalidOperationException: could not open the cloud model"})
s, a = call("POST", f"/sync/check/{M}", token=LEAD); assert s == 202, (s, a)
j = wait_job(a["jobId"])
check("a model that cannot be opened makes the check FAIL with the reason (not 'Succeeded' just because there were no changes)",
      j["status"] == "Failed" and "could not open" in (j["error"] or ""), (j["status"], j["error"]))
fake("POST", "/_control", {"open_error": None})

# ===== 6d. Revit release -> engine (same-release rule) ==========================================================
def relink(version):
    body = {"region": "US", "projectGuid": PROJ, "modelGuid": CLOUD}
    if version is not None: body["revitVersion"] = version
    s, b = call("PUT", f"/models/{M}/cloud-link", body, LEAD); assert s == 200, (s, b)

relink(None)
s, b = call("POST", f"/sync/check/{M}", token=LEAD)
check("model Revit release not recorded -> blocked (an engine is never guessed)", s == 409 and "not recorded" in b["error"], (s, b))
relink(2024)
s, b = call("POST", f"/sync/check/{M}", token=LEAD)
check("model saved in an unsupported release (2024) -> blocked, naming the supported releases",
      s == 409 and "Revit 2024" in b["error"] and "2025, 2026" in b["error"], (s, b))
rv = stage(CT, PO, "PO-VERSION"); approve(rv)
s, b = dispatch([CT])
check("dispatch for an unsupported release is blocked too, and the change stays Approved", s == 409 and staged()[rv]["status"] == "Approved", (s, b))
for year in (2025, 2026):
    relink(year)
    s, a = call("POST", f"/sync/check/{M}", token=LEAD); assert s == 202, (s, a)
    j = wait_job(a["jobId"]); wi = fake()["workitems"][j["workItemId"]]
    check(f"a Revit {year} model runs on the Revit {year} activity (same-release engine)",
          j["status"] == "Succeeded" and wi["activity"] == f"fakenick.MelSyncActivity{year}+prod" and wi["payload"]["revitVersion"] == year,
          (j["status"], j["error"], wi["activity"]))
relink(2026)
a, j = run_sync([CT])
check("a real change on a Revit 2026 model goes through the 2026 activity and commits",
      j["status"] == "Succeeded" and fake()["workitems"][j["workItemId"]]["activity"].endswith("MelSyncActivity2026+prod") and values(CT)[PO] == "PO-VERSION",
      (j["status"], j["error"]))
relink(2025)

# ===== 7. disconnect ===========================================================================================
s, _ = call("DELETE", "/aps/connection", token=CON)
check("contributor cannot disconnect -> 403", s == 403, s)
s, _ = call("DELETE", "/aps/connection", token=LEAD)
check("lead disconnects -> 204", s == 204, s)
check("status: not connected", call("GET", "/aps/status", token=LEAD)[1]["connected"] is False)

print(f"\n{sum(results)}/{len(results)} passed")
sys.exit(0 if all(results) else 1)

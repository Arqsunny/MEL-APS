"""Sync job pipeline against a running backend in Mock mode (Development) with a FRESH database.

Mock runner semantics (Development only): a proposed value starting with FAIL fails, SKIP is reported as checked out by
another user, everything else applies. The job takes one extra poll to finish so the in-flight state is observable.
"""
import json, sys, time, urllib.request, urllib.error
from concurrent.futures import ThreadPoolExecutor

BASE = "http://localhost:5080/api"
M = "demo-model"
AHU = "9f84b2c1-482a-412e-a4b8-f14d021c4321-0003e8a1"
CH = "e14a88bc-19fe-49aa-8411-9a7b032d8471-0003e8b2"
PUMP = "7c20ad41-bb01-49fa-c438-bb4510fae120-0003e8c3"
CT = "45d12ef3-7182-4aa8-9331-dd34e12c5549-0003e8d4"
LEAD_TIME = "2bb0a1a8-c111-4292-8118-ef4ba08b790c"
PO = "31b2f741-4a06-4842-8412-1ddeb1aabbbe"
results = []


def call(method, path, body=None, token=None):
    h = {"Content-Type": "application/json"}
    if token: h["Authorization"] = "Bearer " + token
    req = urllib.request.Request(BASE + path, method=method, data=json.dumps(body).encode() if body is not None else None, headers=h)
    try:
        with urllib.request.urlopen(req) as r:
            return r.status, json.loads(r.read() or b"null")
    except urllib.error.HTTPError as e:
        raw = e.read()
        try: return e.code, json.loads(raw or b"null")
        except Exception: return e.code, raw


def check(name, cond, extra=""):
    results.append(bool(cond))
    print(("PASS " if cond else "FAIL ") + name + (f"  [{extra}]" if extra and not cond else ""))


def login(u):
    s, r = call("POST", "/auth/dev-login", {"username": u}); assert s == 200, r
    return r["token"]


CON, LEAD, LEAD2 = login("contributor@demo.local"), login("lead@demo.local"), login("lead2@demo.local")


def stage(uid, guid, val, tok=CON):
    s, r = call("POST", "/EquipmentSync/stage", {"modelGuid": M, "equipmentUniqueId": uid, "parameterGuid": guid, "proposedValue": val}, tok)
    assert s == 200, (s, r)
    return r["record"]["id"]


def approve(rid):
    s, r = call("POST", f"/EquipmentSync/approve/{rid}", {}, LEAD)
    assert s == 200, (s, r)


def staged():
    _, rows = call("GET", f"/EquipmentSync/staged/{M}", token=LEAD)
    return {r["id"]: r for r in rows}


def equipment(uid):
    _, items = call("GET", f"/equipment/{M}", token=LEAD)
    return next(e for e in items if e["uniqueId"] == uid)["values"]


def wait_job(job_id, timeout=40):
    end = time.time() + timeout
    while time.time() < end:
        s, j = call("GET", f"/sync/jobs/{M}/{job_id}", token=LEAD)
        if j["status"] not in ("Queued", "Submitted"): return j
        time.sleep(1)
    raise TimeoutError("job did not finish")


# ---- stage + approve three changes on three pieces of equipment -------------------------------------------------
r_ok = stage(AHU, LEAD_TIME, "18")
r_fail = stage(CH, PO, "FAIL-PO-9")
r_skip = stage(PUMP, PO, "SKIP-PO-7")
for r in (r_ok, r_fail, r_skip): approve(r)

s, _ = call("POST", "/EquipmentSync/dispatch-revit-sync", {"modelGuid": M, "equipmentUniqueIds": [AHU, CH, PUMP]}, CON)
check("contributor cannot dispatch -> 403", s == 403, s)

with ThreadPoolExecutor(5) as ex:
    out = list(ex.map(lambda _: call("POST", "/EquipmentSync/dispatch-revit-sync", {"modelGuid": M, "equipmentUniqueIds": [AHU, CH, PUMP]}, LEAD), range(5)))
codes = [c for c, _ in out]
check("5 concurrent dispatches -> exactly one 202", codes.count(202) == 1 and all(c in (202, 400, 409) for c in codes), codes)
acc = next(b for c, b in out if c == 202)
job_id = acc["jobId"]
check("dispatch returns Queued + runner Mock + 3 items", acc["status"] == "Queued" and acc["runner"] == "Mock" and acc["itemsQueued"] == 3, acc)

# ---- in flight: nothing is committed until the runner reports -------------------------------------------------
st = staged()
check("all three records are Dispatched (in flight)", all(st[r]["status"] == "Dispatched" for r in (r_ok, r_fail, r_skip)), [st[r]["status"] for r in (r_ok, r_fail, r_skip)])
check("model values NOT changed at dispatch time", equipment(AHU)[LEAD_TIME] == "22", equipment(AHU)[LEAD_TIME])

extra = stage(CT, PO, "PO-NEW-1"); approve(extra)
s, b = call("POST", "/EquipmentSync/dispatch-revit-sync", {"modelGuid": M, "equipmentUniqueIds": [CT]}, LEAD)
j_now = call("GET", f"/sync/jobs/{M}/{job_id}", token=LEAD)[1]
if j_now["status"] in ("Queued", "Submitted"):
    check("second dispatch while a sync is active -> 409 (one active job per model)", s == 409, (s, b))
else:
    print("SKIP (job already finished before the second dispatch could be tried)")

# ---- job completes with partial success ----------------------------------------------------------------------
job = wait_job(job_id)
check("job finished PartiallySucceeded", job["status"] == "PartiallySucceeded", job["status"])
check("job result lists one outcome per change", len(job["result"]["results"]) == 3, job["result"])
check("job records who dispatched it", job["requestedBy"] == "lead@demo.local", job["requestedBy"])

st = staged()
check("applied change is CommittedToModel", st[r_ok]["status"] == "CommittedToModel", st[r_ok]["status"])
check("model value updated only for the applied change", equipment(AHU)[LEAD_TIME] == "18", equipment(AHU)[LEAD_TIME])
check("failed change returns to Approved with the reason", st[r_fail]["status"] == "Approved" and "MockFailure" in (st[r_fail]["lastError"] or ""), st[r_fail])
check("checked-out element returns to Approved with OwnedByOtherUser", st[r_skip]["status"] == "Approved" and "OwnedByOtherUser" in (st[r_skip]["lastError"] or ""), st[r_skip])
check("failed values were NOT written to the model", equipment(CH)[PO] == "PO-2026-9488" and equipment(PUMP)[PO] == "PO-2026-8801", (equipment(CH)[PO], equipment(PUMP)[PO]))

# ---- audit ---------------------------------------------------------------------------------------------------
_, audit = call("GET", f"/audit/{M}?batchId={acc['batchId']}", token=LEAD)
acts = sorted(a["action"] for a in audit)
check("audit for the batch: 3 Dispatched, 1 ModelValueChanged, 1 CommittedToModel, 2 SyncFailed",
      acts == sorted(["Dispatched"] * 3 + ["ModelValueChanged", "CommittedToModel"] + ["SyncFailed"] * 2), acts)
check("Dispatched entries carry the lead's proven identity",
      all(a["actor"] == "lead@demo.local" and a["actorId"] == "dev-lead@demo.local" for a in audit if a["action"] == "Dispatched"))
check("result entries are attributed to the sync worker", all(a["actor"] == "system:sync-worker" for a in audit if a["action"] in ("SyncFailed", "CommittedToModel", "ModelValueChanged")))

# ---- retry: failed items can be dispatched again (new job, new batch) -----------------------------------------
s, acc2 = call("POST", "/EquipmentSync/dispatch-revit-sync", {"modelGuid": M, "equipmentUniqueIds": [CH, PUMP, CT]}, LEAD)
check("failed + newly approved items can be dispatched again -> 202 (4 total approved: CH, PUMP, CT)", s == 202 and acc2["itemsQueued"] == 3 and acc2["batchId"] != acc["batchId"], (s, acc2))
job2 = wait_job(acc2["jobId"])
st = staged()
check("retry job: CT-01 PO now committed, the two FAIL/SKIP items fail again", st[extra]["status"] == "CommittedToModel" and st[r_fail]["status"] == "Approved" and st[r_skip]["status"] == "Approved", [st[x]["status"] for x in (extra, r_fail, r_skip)])
check("retry job is PartiallySucceeded", job2["status"] == "PartiallySucceeded", job2["status"])
check("CT-01 PO written to model", equipment(CT)[PO] == "PO-NEW-1", equipment(CT)[PO])

# ---- visibility ----------------------------------------------------------------------------------------------
s, lst = call("GET", f"/sync/jobs/{M}", token=CON)
check("job list readable by any signed-in user, newest first", s == 200 and len(lst) == 2 and lst[0]["id"] == acc2["jobId"], lst)
s, d = call("GET", f"/sync/jobs/{M}/{job_id}", token=CON)
check("job detail hides the DA report URL from non-leads", s == 200 and d["reportUrl"] is None)
s, _ = call("GET", f"/sync/jobs/{M}/{job_id}")
check("job endpoints require a token -> 401", s == 401, s)

# ---- connectivity check (no changes) ------------------------------------------------------------------------
s, _ = call("POST", f"/sync/check/{M}", token=CON)
check("contributor cannot run a connectivity check -> 403", s == 403, s)
s, _ = call("POST", f"/sync/check/{M}")
check("connectivity check requires a token -> 401", s == 401, s)
s, chk = call("POST", f"/sync/check/{M}", token=LEAD)
check("lead can run a connectivity check -> 202 Queued", s == 202 and chk["status"] == "Queued", (s, chk))
s2, dup = call("POST", f"/sync/check/{M}", token=LEAD)
check("a second check while one is active -> 409 (one active job per model)", s2 == 409 or call("GET", f"/sync/jobs/{M}/{chk['jobId']}", token=LEAD)[1]["status"] not in ("Queued", "Submitted"), (s2, dup))
cj = wait_job(chk["jobId"])
check("check job Succeeded with NO changes and the model reported as opened", cj["status"] == "Succeeded" and cj["changes"] == [] and cj["result"]["opened"] is True and cj["result"]["results"] == [], cj)
before_vals = equipment(AHU)
check("the check changed nothing in the model data", equipment(AHU) == before_vals and equipment(CH)[PO] == "PO-2026-9488")
_, audit = call("GET", f"/audit/{M}?batchId={chk['batchId']}", token=LEAD)
check("audit records the check under the lead's identity", [(a["action"], a["actor"]) for a in audit] == [("ConnectivityCheck", "lead@demo.local")], audit)
_, lst = call("GET", f"/sync/jobs/{M}", token=CON)
check("check shows in the job list as a job with 0 items", lst[0]["id"] == chk["jobId"] and lst[0]["items"] == 0, lst[0])

print(f"\n{sum(results)}/{len(results)} passed")
sys.exit(0 if all(results) else 1)

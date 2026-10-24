import json, sys, time, subprocess, urllib.request, urllib.error
from concurrent.futures import ThreadPoolExecutor

BASE = "http://localhost:5080/api"
M = "demo-model"
AHU = "9f84b2c1-482a-412e-a4b8-f14d021c4321-0003e8a1"
LEAD = "2bb0a1a8-c111-4292-8118-ef4ba08b790c"
WEIGHT = "3a2b1c0d-9e8f-7a6b-5c4d-3e2f1a0b9c8d"
results = []

def call(method, path, body=None, token=None, raw_headers=None):
    h = {"Content-Type": "application/json"}
    if token: h["Authorization"] = "Bearer " + token
    if raw_headers: h.update(raw_headers)
    req = urllib.request.Request(BASE + path, method=method,
                                 data=json.dumps(body).encode() if body is not None else None, headers=h)
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

def login(user):
    s, r = call("POST", "/auth/dev-login", {"username": user})
    assert s == 200, (user, s, r)
    return r["token"]

CON, LEADT, LEAD2, VIEW = (login(u) for u in
    ("contributor@demo.local", "lead@demo.local", "lead2@demo.local", "viewer@demo.local"))

def stage(tok, guid, val, spoof=True):
    body = {"modelGuid": M, "equipmentUniqueId": AHU, "parameterGuid": guid, "proposedValue": val}
    if spoof: body.update({"requestedBy": "ceo@evil.example", "oldValue": "SPOOFED"})
    return call("POST", "/EquipmentSync/stage", body, tok)

# --- authentication
s, _ = call("GET", f"/equipment/{M}")
check("no token -> 401 on read", s == 401, s)
s, _ = call("GET", "/parameters")
check("no token -> 401 on catalog", s == 401, s)
s, _ = call("POST", "/EquipmentSync/stage", {"modelGuid": M}, None)
check("no token -> 401 on stage", s == 401, s)
s, _ = call("GET", f"/equipment/{M}", token="not.a.jwt")
check("garbage token -> 401", s == 401, s)
tampered = CON[:-4] + ("AAAA" if not CON.endswith("AAAA") else "BBBB")
s, _ = call("GET", f"/equipment/{M}", token=tampered)
check("tampered signature -> 401", s == 401, s)
s, cfg = call("GET", "/auth/config")
check("auth/config is anonymous and lists dev users", s == 200 and cfg["mode"] == "dev" and len(cfg["devUsers"]) == 4, cfg)
s, _ = call("POST", "/auth/dev-login", {"username": "nobody@x"})
check("unknown dev user -> 401", s == 401, s)
s, me = call("GET", "/auth/me", token=LEADT)
check("/auth/me reflects token", s == 200 and me["username"] == "lead@demo.local" and me["roles"] == ["BimLead"], me)

# --- RBAC: stage
s, _ = stage(VIEW, LEAD, "5")
check("viewer (no role) cannot stage -> 403", s == 403, s)
s, _ = call("GET", f"/equipment/{M}", token=VIEW)
check("viewer can read -> 200", s == 200, s)

s, r = stage(CON, LEAD, "18")
check("contributor can stage -> 200", s == 200, s)
rec = r["record"]
check("requestedBy comes from token, body 'ceo@evil.example' ignored", rec["requestedBy"] == "contributor@demo.local", rec["requestedBy"])
check("oldValue from DB, not body", rec["oldValue"] == "22", rec["oldValue"])

# --- RBAC: review
s, _ = call("POST", f"/EquipmentSync/approve/{rec['id']}", {}, CON)
check("contributor cannot approve -> 403", s == 403, s)
s, _ = call("POST", f"/EquipmentSync/reject/{rec['id']}", {"reason": "x"}, CON)
check("contributor cannot reject -> 403", s == 403, s)
s, _ = call("POST", f"/EquipmentSync/approve/{rec['id']}", {}, VIEW)
check("viewer cannot approve -> 403", s == 403, s)
s, _ = call("POST", f"/EquipmentSync/approve/{rec['id']}", {}, None)
check("anonymous cannot approve -> 401", s == 401, s)

# lead stages own change -> cannot self-approve
s, r2 = stage(LEADT, WEIGHT, "3855.53")
check("BIM lead can stage -> 200", s == 200, s)
own = r2["record"]
s, b = call("POST", f"/EquipmentSync/approve/{own['id']}", {}, LEADT)
check("lead cannot approve own request -> 403", s == 403, (s, b))
s, b = call("POST", f"/EquipmentSync/reject/{own['id']}", {"reason": "self"}, LEADT)
check("lead cannot reject own request -> 403", s == 403, (s, b))
s, b = call("POST", f"/EquipmentSync/approve/{own['id']}", {}, LEAD2)
check("a different lead can approve it -> 200", s == 200 and b["reviewedBy"] == "lead2@demo.local", (s, b))

# --- dispatch RBAC
s, _ = call("POST", "/EquipmentSync/dispatch-revit-sync", {"modelGuid": M, "equipmentUniqueIds": [AHU]}, CON)
check("contributor cannot dispatch -> 403", s == 403, s)
s, _ = call("POST", "/EquipmentSync/dispatch-revit-sync", {"modelGuid": M, "equipmentUniqueIds": [AHU]}, None)
check("anonymous cannot dispatch -> 401", s == 401, s)
s, _ = call("POST", f"/EquipmentSync/approve/{rec['id']}", {"reviewedBy": "ceo@evil.example"}, LEADT)
check("lead approves contributor's change (body reviewedBy ignored)", s == 200, s)
s, out = call("POST", "/EquipmentSync/dispatch-revit-sync", {"modelGuid": M, "equipmentUniqueIds": [AHU]}, LEADT)
check("lead can dispatch -> 202 Queued (2 items)", s == 202 and out["itemsQueued"] == 2, (s, out))
batch = out["batchId"]
for _ in range(40):                                    # the sync runs in the background worker
    j = call("GET", f"/sync/jobs/{M}/{out['jobId']}", token=LEADT)[1]
    if j["status"] not in ("Queued", "Submitted"): break
    time.sleep(1)
check("sync job finished Succeeded", j["status"] == "Succeeded", j["status"])

# --- audit identities
s, audit = call("GET", f"/audit/{M}?equipmentUniqueId={AHU}", token=VIEW)
check("viewer can read audit", s == 200, s)
by_action = {}
for a in audit: by_action.setdefault(a["action"], []).append(a)
check("no audit entry carries a spoofed identity", all("evil" not in a["actor"] for a in audit))
check("Staged actors are the token users",
      sorted(a["actor"] for a in by_action["Staged"]) == ["contributor@demo.local", "lead@demo.local"],
      [a["actor"] for a in by_action["Staged"]])
check("Approved actors are the reviewing leads",
      sorted(a["actor"] for a in by_action["Approved"]) == ["lead2@demo.local", "lead@demo.local"],
      [a["actor"] for a in by_action["Approved"]])
check("Dispatched entries record the dispatching lead + batch",
      len(by_action["Dispatched"]) == 2 and all(a["actor"] == "lead@demo.local" and a["batchId"] == batch for a in by_action["Dispatched"]))
check("Commit entries are written by the sync worker under the same batch",
      all(a["actor"] == "system:sync-worker" and a["batchId"] == batch for a in by_action["CommittedToModel"] + by_action["ModelValueChanged"]))
check("every user-attributed audit row carries the immutable subject id (system rows have none)",
      all(a["actorId"] and a["actorId"].startswith("dev-") for a in audit if a["actor"] != "system:sync-worker"),
      [(a["actor"], a["actorId"]) for a in audit][:4])

# --- the gate still holds + concurrency still holds under auth
s, r3 = stage(CON, LEAD, "17")
s2, _ = call("POST", "/EquipmentSync/dispatch-revit-sync", {"modelGuid": M, "equipmentUniqueIds": [AHU]}, LEADT)
check("gate: dispatch with nothing approved -> 400", s2 == 400, s2)
with ThreadPoolExecutor(6) as ex:
    codes = list(ex.map(lambda _: call("POST", f"/EquipmentSync/approve/{r3['record']['id']}", {}, LEADT)[0], range(6)))
check("6 concurrent approvals -> exactly one 200", codes.count(200) == 1, codes)

print(f"\n{sum(results)}/{len(results)} passed")
sys.exit(0 if all(results) else 1)

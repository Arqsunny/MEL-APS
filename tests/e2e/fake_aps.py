"""A fake APS (auth + Design Automation + OSS) for testing the backend's Live mode without real credentials.

IMPORTANT: this encodes the backend author's understanding of the APS contracts. Passing tests against it prove the
client/worker logic, NOT that real APS behaves identically; see docs/APS.md for the live smoke test.

Run:  python fake_aps.py [port]      (default 5099)
Control API (tests only):  POST /_control {..}   POST /_control/reset   GET /_control/state
"""
import base64, itertools, json, re, sys, threading, time, urllib.parse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 5099
HOST = f"http://localhost:{PORT}"
CLIENT_ID, CLIENT_SECRET = "test-id", "test-secret"
N = itertools.count(1)
LOCK = threading.RLock()
CONC_LOCK = threading.Lock()
CUR = 0   # Data Management requests in flight right now


def fresh():
    return {
        "tokens": {},            # access token -> {"scope":..., "kind": "2L"|"3L", "expires": ts}
        "codes": {},             # auth code -> redirect_uri
        "current_refresh": None, "revoked": False, "user_ttl": 3600,
        "refresh_calls": 0, "refresh_reuse_rejected": 0,
        "last_3l": None,
        "versions": {"appbundles": {}, "activities": {}}, "aliases": {}, "bundle_uploads": [], "activities": [], "bundles": [],
        "buckets": set(), "objects": {}, "uploads": {}, "completed": [],
        "workitems": {}, "submitted": [],
        "ctl": {"readonly": [], "owned": [], "missing": [], "fail_workitem": False, "synced": True,
                "open_error": None, "no_result": False, "stay_pending": False},
    }


S = fresh()


def now(): return time.time()


class H(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"
    def log_message(self, *a): pass

    # ---- plumbing ------------------------------------------------------------------------------------------
    def body(self):
        # HttpClient sends JSON bodies chunked (no Content-Length); real servers accept both, so must the fake.
        if "chunked" in (self.headers.get("Transfer-Encoding") or "").lower():
            out = b""
            while True:
                size = int(self.rfile.readline().split(b";")[0].strip() or b"0", 16)
                if size == 0:
                    self.rfile.readline()   # trailing CRLF (no trailers expected)
                    return out
                out += self.rfile.read(size)
                self.rfile.readline()
        n = int(self.headers.get("Content-Length") or 0)
        return self.rfile.read(n) if n else b""

    def send(self, code, obj=None, raw=None, headers=None, ctype="application/json"):
        data = raw if raw is not None else (json.dumps(obj).encode() if obj is not None else b"")
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(data)))
        for k, v in (headers or {}).items(): self.send_header(k, v)
        self.end_headers()
        if self.command != "HEAD": self.wfile.write(data)

    def token(self, need_scope=None, kind=None):
        auth = self.headers.get("Authorization", "")
        if not auth.startswith("Bearer "): self.send(401, {"reason": "no bearer"}); return None
        t = S["tokens"].get(auth[7:])
        if not t or t["expires"] < now(): self.send(401, {"reason": "invalid/expired token"}); return None
        if kind and t["kind"] != kind: self.send(403, {"reason": f"need {kind} token"}); return None
        if need_scope and not all(sc in t["scope"].split() for sc in need_scope.split()):
            self.send(403, {"reason": f"scope {need_scope} missing"}); return None
        return t

    def route(self, method):
        u = urllib.parse.urlsplit(self.path)
        path = u.path
        q = dict(urllib.parse.parse_qsl(u.query))
        if path.startswith(("/project/v1/", "/data/v1/")) and self.server.server_address[1] == PORT:
            global CUR
            with CONC_LOCK:
                CUR += 1
                S["max_concurrent"] = max(S.get("max_concurrent", 0), CUR)
            try:
                delay = S["ctl"].get("dm_delay", 0)
                if delay: time.sleep(delay)
                with LOCK:
                    return self.dispatch(method, path, q)
            finally:
                with CONC_LOCK: CUR -= 1
        with LOCK:
            return self.dispatch(method, path, q)

    def do_GET(self): self.route("GET")
    def do_POST(self): self.route("POST")
    def do_PUT(self): self.route("PUT")
    def do_PATCH(self): self.route("PATCH")

    # ---- routes --------------------------------------------------------------------------------------------
    def dispatch(self, m, path, q):
        # control
        if path == "/_control/reset" and m == "POST":
            self.body(); S.clear(); S.update(fresh()); return self.send(200, {"ok": True})
        if path == "/_control" and m == "POST":
            c = json.loads(self.body() or b"{}")
            for k in ("user_ttl", "revoked"):
                if k in c: S[k] = c.pop(k)
            S["ctl"].update(c); return self.send(200, S["ctl"])
        if path == "/_control/state":
            snap = {k: (sorted(v) if isinstance(v, set) else v) for k, v in S.items() if k not in ("objects", "aliases")}
            snap["aliases"] = {f"{k[0]}/{k[1]}/{k[2]}": v for k, v in S["aliases"].items()}   # tuple keys are not JSON
            snap["object_keys"] = sorted(f"{b}/{o}" for b, o in S["objects"])
            return self.send(200, snap)

        if path == "/authentication/v2/token" and m == "POST": return self.token_endpoint()
        if path == "/authentication/v2/authorize" and m == "GET":
            code = f"code-{next(N)}"; S["codes"][code] = q["redirect_uri"]
            S["authorize_scope"] = q.get("scope")
            return self.send(302, raw=b"", headers={"Location": f"{q['redirect_uri']}?code={code}&state={q['state']}"})
        if path == "/userprofile/v1/users/@me" and m == "GET":   # retired: the real service answers 410 Gone
            return self.send(410, {"reason": "this endpoint has been retired"})
        if path == "/userinfo" and m == "GET":
            if not self.token(kind="3L", need_scope="user-profile:read"): return
            if S["ctl"].get("userinfo_fail"): return self.send(500, {"reason": "temporarily unavailable"})
            return self.send(200, {"sub": "u-1", "name": "Service Account", "email": "svc@firm.example", "preferred_username": "svc"})

        if self.server.server_address[1] == PORT + 1:
            # Second listener = "some other host". Nothing legitimate should ever be sent here.
            S.setdefault("evil_hits", []).append({"path": path, "auth": self.headers.get("Authorization")})
            return self.send(404, {})
        if path.startswith("/project/v1/") or path.startswith("/data/v1/"): return self.dm(m, path, q)
        if path.startswith("/da/us-east/v3/"): return self.da(m, path[len("/da/us-east/v3"):], q)
        if path.startswith("/oss/v2/"): return self.oss(m, path[len("/oss/v2"):], q)
        if path.startswith("/s3/"): return self.s3(m, path)
        self.send(404, {"reason": f"no route {m} {path}"})

    def token_endpoint(self):
        auth = self.headers.get("Authorization", "")
        exp = "Basic " + base64.b64encode(f"{CLIENT_ID}:{CLIENT_SECRET}".encode()).decode()
        if auth != exp: self.body(); return self.send(401, {"developerMessage": "bad client credentials"})
        f = dict(urllib.parse.parse_qsl(self.body().decode()))
        g = f.get("grant_type")
        if g == "client_credentials":
            t = f"2L-{next(N)}"; S["tokens"][t] = {"scope": f.get("scope", ""), "kind": "2L", "expires": now() + 3600}
            return self.send(200, {"access_token": t, "token_type": "Bearer", "expires_in": 3600})
        if g == "authorization_code":
            if f.get("code") not in S["codes"] or S["codes"].pop(f["code"]) != f.get("redirect_uri"):
                return self.send(400, {"error": "invalid_grant"})
            return self.issue_user(f.get("scope") or S.get("authorize_scope") or "")
        if g == "refresh_token":
            S["refresh_calls"] += 1
            if S["revoked"] or f.get("refresh_token") != S["current_refresh"]:
                if f.get("refresh_token") != S["current_refresh"]: S["refresh_reuse_rejected"] += 1
                return self.send(400, {"error": "invalid_grant", "error_description": "refresh token invalid, revoked or already used"})
            return self.issue_user(f.get("scope", ""))
        self.send(400, {"error": "unsupported_grant_type"})

    def issue_user(self, scope):
        t, r = f"3L-{next(N)}", f"RT-{next(N)}"
        S["tokens"][t] = {"scope": scope, "kind": "3L", "expires": now() + S["user_ttl"]}
        S["current_refresh"] = r; S["last_3l"] = t   # old refresh token is now dead (rotation)
        return self.send(200, {"access_token": t, "refresh_token": r, "token_type": "Bearer", "expires_in": S["user_ttl"]})

    # ---- Data Management (hubs / projects / folders / items) -------------------------------------------------
    EVIL = f"http://localhost:{PORT + 1}"
    C4R = "versions:autodesk.bim360:C4RModel"
    GUIDS = {
        "urn:i:central": ("aaaaaaaa-1111-2222-3333-444444444444", "bbbbbbbb-1111-2222-3333-444444444444", False, 2025),
        "urn:i:power":   ("aaaaaaaa-5555-6666-7777-888888888888", "bbbbbbbb-5555-6666-7777-888888888888", True, 2026),
        "urn:i:old":     ("aaaaaaaa-2222-3333-4444-555555555555", "bbbbbbbb-2222-3333-4444-555555555555", False, 2024),
        "urn:i:solo":    ("aaaaaaaa-9999-0000-1111-222222222222", "bbbbbbbb-9999-0000-1111-222222222222", False, None),   # APS did not report a version
    }

    def dm(self, m, path, q):
        t = self.token(kind="3L", need_scope="data:read")
        if not t: return
        if S["ctl"].get("dm_429", 0) > 0:
            S["ctl"]["dm_429"] -= 1
            S["dm_429_served"] = S.get("dm_429_served", 0) + 1
            return self.send(429, {"reason": "rate limit"}, headers={"Retry-After": "1"})
        if S["ctl"].get("dm_unauthorized"): return self.send(401, {"developerMessage": "token rejected"})
        if S["ctl"].get("dm_forbidden"): return self.send(403, {"reason": "user has no access to this resource"})
        S.setdefault("dm_requests", []).append(path)
        from urllib.parse import unquote
        if path == "/project/v1/hubs":
            return self.send(200, {"data": [
                {"id": "b.hub1", "type": "hubs", "attributes": {"name": "Acme Account", "region": "EMEA"}},
                {"id": "b.hub2", "type": "hubs", "attributes": {"name": "Regionless Account"}}]})
        mm = re.fullmatch(r"/project/v1/hubs/([^/]+)", path)
        if mm:
            hubs = {"b.hub1": ("Acme Account", "EMEA"), "b.hub2": ("Regionless Account", None)}
            h = hubs.get(unquote(mm.group(1)))
            if not h: return self.send(404, {})
            attrs = {"name": h[0]}
            if h[1]: attrs["region"] = h[1]
            return self.send(200, {"data": {"id": unquote(mm.group(1)), "type": "hubs", "attributes": attrs}})
        mm = re.fullmatch(r"/project/v1/hubs/([^/]+)/projects", path)
        if mm:
            hub = unquote(mm.group(1))
            if hub == "b.hub1":
                return self.send(200, {"data": [{"id": "b.p1", "attributes": {"name": "Test Project"}}, {"id": "b.p3", "attributes": {"name": "Huge Project"}}]})
            if hub == "b.hub2": return self.send(200, {"data": [{"id": "b.p2", "attributes": {"name": "Solo Project"}}]})
            return self.send(404, {})
        # The real service 404s on /data/v1/projects/{p}/topFolders (the bug found live); it lives under the hub path.
        mm = re.fullmatch(r"/project/v1/hubs/([^/]+)/projects/([^/]+)/topFolders", path)
        if mm:
            pid = unquote(mm.group(2))
            return self.send(200, {"data": [{"id": f"urn:f:root:{pid}", "type": "folders", "attributes": {"displayName": "Project Files"}}]})
        mm = re.fullmatch(r"/data/v1/projects/([^/]+)/folders/([^/]+)/contents", path)
        if mm: return self.dm_contents(unquote(mm.group(1)), unquote(mm.group(2)), q)
        mm = re.fullmatch(r"/data/v1/projects/([^/]+)/items/([^/]+)/tip", path)
        if mm: return self.dm_tip(unquote(mm.group(2)))
        self.send(404, {"reason": f"no DM route {path}"})

    def dm_contents(self, pid, fid, q):
        def item(i, name): return {"id": i, "type": "items", "attributes": {"displayName": name}}
        def folder(i, name): return {"id": i, "type": "folders", "attributes": {"displayName": name}}
        base = f"{HOST}/data/v1/projects/{urllib.parse.quote(pid, safe='')}/folders/{urllib.parse.quote(fid, safe='')}/contents"
        if pid == "b.p1" and fid == "urn:f:root:b.p1":
            if q.get("page") == "2":   # second page, reached through links.next
                return self.send(200, {"data": [item("urn:i:spec", "spec.pdf"), folder("urn:f:power", "Electrical")]})
            return self.send(200, {"data": [folder("urn:f:mech", "Mechanical"), item("urn:i:plain", "Plain-Upload.rvt")],
                                   "links": {"next": {"href": base + "?page=2"}}})
        if pid == "b.p1" and fid == "urn:f:mech":
            # A hostile/odd next link to ANOTHER host: the client must not follow it (it would carry the user's token).
            return self.send(200, {"data": [item("urn:i:central", "M-CentralPlant.rvt"), item("urn:i:old", "Old-Model.rvt")], "links": {"next": {"href": self.EVIL + "/data/v1/steal"}}})
        if pid == "b.p1" and fid == "urn:f:power":
            return self.send(200, {"data": [item("urn:i:power", "E-Power.rvt")]})
        if pid == "b.p2" and fid == "urn:f:root:b.p2":
            return self.send(200, {"data": [item("urn:i:solo", "Solo.rvt")]})
        if pid == "b.p3" and fid == "urn:f:root:b.p3":   # 160 empty folders: more than the scan limit
            return self.send(200, {"data": [folder(f"urn:f:many{n}", f"F{n}") for n in range(160)]})
        return self.send(200, {"data": []})

    def dm_tip(self, iid):
        if iid == "urn:i:plain":
            return self.send(200, {"data": {"type": "versions", "attributes": {"extension": {"type": "versions:autodesk.bim360:File", "data": {}}}}})
        g = self.GUIDS.get(iid)
        if not g: return self.send(404, {})
        return self.send(200, {"data": {"type": "versions", "attributes": {
            "lastModifiedTime": "2026-10-01T10:00:00.0000000Z",
            "extension": {"type": self.C4R, "data": {"projectGuid": g[0], "modelGuid": g[1], "isCompositeDesign": g[2],
                                                                    **({"revitProjectVersion": g[3]} if g[3] else {})}}}}})

    # ---- Design Automation ---------------------------------------------------------------------------------
    def da(self, m, p, q):
        if not self.token(kind="2L", need_scope="code:all"): self.body(); return
        if p == "/forgeapps/me": return self.send(200, "fakenick")  # JSON string body
        b = json.loads(self.body() or b"{}") if m in ("POST", "PATCH") else {}

        mm = re.fullmatch(r"/(appbundles|activities)", p)
        if mm and m == "POST":
            kind, cid = mm.group(1), b.get("id")
            if cid in S["versions"][kind]: return self.send(409, {"reason": "already exists"})
            return self.create_version(kind, cid, b)
        mm = re.fullmatch(r"/(appbundles|activities)/([^/]+)/versions", p)
        if mm and m == "POST":
            return self.create_version(mm.group(1), mm.group(2), b)
        mm = re.fullmatch(r"/(appbundles|activities)/([^/]+)/aliases", p)
        if mm and m == "POST":
            k = (mm.group(1), mm.group(2), b["id"])
            if k in S["aliases"]: return self.send(409, {"reason": "alias exists"})
            S["aliases"][k] = b["version"]; return self.send(200, {"id": b["id"], "version": b["version"]})
        mm = re.fullmatch(r"/(appbundles|activities)/([^/]+)/aliases/([^/]+)", p)
        if mm and m == "PATCH":
            k = (mm.group(1), mm.group(2), mm.group(3))
            if k not in S["aliases"]: return self.send(404, {})
            S["aliases"][k] = b["version"]; S.setdefault("alias_patches", []).append(["/".join(k), b["version"]])
            return self.send(200, {"id": k[2], "version": b["version"]})

        if p == "/workitems" and m == "POST": return self.workitem_create(b)
        mm = re.fullmatch(r"/workitems/([^/]+)", p)
        if mm and m == "GET": return self.workitem_get(mm.group(1))
        self.send(404, {"reason": f"no DA route {m} {p}"})

    def create_version(self, kind, cid, b):
        store = S["versions"][kind]
        store[cid] = store.get(cid, 0) + 1
        v = store[cid]
        if kind == "activities":
            S["activities"].append({"id": cid, "version": v, **b})
            return self.send(200, {"id": f"fakenick.{cid}", "version": v})
        S["bundles"].append({"id": cid, "version": v, "engine": b.get("engine")})
        return self.send(200, {"id": f"fakenick.{cid}", "version": v, "engine": b.get("engine"),
                               "uploadParameters": {"endpointURL": f"{HOST}/s3/appbundle", "formData": {"key": f"{cid}-v{v}", "policy": "p"}}})

    def workitem_create(self, b):
        wid = f"wi-{next(N)}"
        args = b.get("arguments", {})
        errors = []
        am = re.fullmatch(r"fakenick\.MelSyncActivity(\d{4})\+prod", b.get("activityId") or "")
        year = int(am.group(1)) if am else None
        if not am or ("activities", f"MelSyncActivity{year}") not in [(k[0], k[1]) for k in S["aliases"]]:
            errors.append(f"unknown activity {b.get('activityId')}")
        tok = args.get("adsk3LeggedToken")
        t3 = S["tokens"].get(tok or "")
        if not t3 or t3["kind"] != "3L" or t3["expires"] < now(): errors.append("adsk3LeggedToken missing/invalid/expired")
        if "url" not in args.get("inputJson", {}): errors.append("inputJson.url missing")
        if args.get("result", {}).get("verb") != "put" or "url" not in args.get("result", {}): errors.append("result put url missing")
        rec = {"id": wid, "year": year, "activity": b.get("activityId"), "polls": 0, "errors": errors, "args": {k: ("<token>" if k == "adsk3LeggedToken" else v) for k, v in args.items()},
               "token_ok": not any("adsk3" in e for e in errors), "status": None, "payload": None}
        if not errors:
            inp = self.fetch_object(args["inputJson"]["url"])
            rec["payload"] = json.loads(inp) if inp else None
            if rec["payload"] is None: rec["errors"].append("input not downloadable")
        S["workitems"][wid] = rec
        S["submitted"].append(wid)
        return self.send(200, {"id": wid, "status": "pending"})

    def fetch_object(self, url):
        mm = re.match(re.escape(HOST) + r"/s3/(.+?)/(.+?)(\?.*)?$", url)
        if not mm: return None
        return S["objects"].get((mm.group(1), urllib.parse.unquote(mm.group(2))))

    def run_bundle(self, rec):
        c, payload = S["ctl"], rec["payload"]
        if payload.get("revitVersion") != rec["year"]:
            return {"version": 1, "batchId": payload["batchId"], "opened": False, "synced": False, "revitVersion": str(rec["year"]),
                    "error": f"InvalidOperationException: the model is Revit {payload.get('revitVersion')} but this is a Revit {rec['year']} engine", "results": []}
        result = {"version": 1, "batchId": payload["batchId"], "opened": c["open_error"] is None, "synced": c["synced"],
                  "error": c["open_error"], "revitVersion": "2025", "results": []}
        if c["open_error"]:
            result["synced"] = False; return result
        for u in payload["updates"]:
            key = f"{u['uniqueId']}:{u['parameterGuid']}"
            if u["uniqueId"] in c["missing"]: r = ("Failed", "ElementNotFound", "no such element")
            elif u["uniqueId"] in c["owned"]: r = ("Skipped", "OwnedByOtherUser", "checked out by jane@firm.com")
            elif key in c["readonly"]: r = ("Failed", "ReadOnly", "parameter is read-only")
            else: r = ("Applied", "OK", None)
            result["results"].append({"stagingId": u["stagingId"], "outcome": r[0], "code": r[1], "message": r[2],
                                      "appliedValue": u["value"] if r[0] == "Applied" else None})
        return result

    def workitem_get(self, wid):
        rec = S["workitems"].get(wid)
        if not rec: return self.send(404, {})
        rec["polls"] += 1
        report = f"{HOST}/report/{wid}"
        if rec["errors"]: return self.send(200, {"id": wid, "status": "failedInstructions", "reportUrl": report})
        if S["ctl"]["stay_pending"] or rec["polls"] == 1: return self.send(200, {"id": wid, "status": "pending", "reportUrl": report})
        if rec["polls"] == 2: return self.send(200, {"id": wid, "status": "inprogress", "reportUrl": report})
        if S["ctl"]["fail_workitem"]: return self.send(200, {"id": wid, "status": "failedInstructions", "reportUrl": report})
        if rec["status"] is None:
            rec["status"] = "success"
            if not S["ctl"]["no_result"]:
                # the bundle "PUTs" result.json straight to the signed S3 url it was given
                url = rec["args"]["result"]["url"]
                mm = re.match(re.escape(HOST) + r"/s3/(.+?)/(.+?)(\?.*)?$", url)
                S["objects"][(mm.group(1), urllib.parse.unquote(mm.group(2)))] = json.dumps(self.run_bundle(rec)).encode()
                rec["result_written"] = True
        return self.send(200, {"id": wid, "status": "success", "reportUrl": report})

    # ---- OSS + S3 ------------------------------------------------------------------------------------------
    def oss(self, m, p, q):
        if not self.token(kind="2L", need_scope="data:write"): self.body(); return
        if p == "/buckets" and m == "POST":
            b = json.loads(self.body())
            if b["bucketKey"] in S["buckets"]: return self.send(409, {"reason": "exists"})
            if b.get("policyKey") != "transient": return self.send(400, {"reason": "policy"})
            S["buckets"].add(b["bucketKey"]); return self.send(200, {"bucketKey": b["bucketKey"]})
        mm = re.fullmatch(r"/buckets/([^/]+)/objects/([^/]+)/(signeds3upload|signeds3download)", p)
        if not mm: return self.send(404, {"reason": f"no OSS route {m} {p}"})
        bucket, obj, op = mm.group(1), urllib.parse.unquote(mm.group(2)), mm.group(3)
        if bucket not in S["buckets"]: self.body(); return self.send(404, {"reason": "bucket"})
        if op == "signeds3upload" and m == "GET":
            uk = f"uk-{next(N)}"; S["uploads"][uk] = (bucket, obj)
            return self.send(200, {"uploadKey": uk, "uploadExpiration": "2099-01-01T00:00:00Z",
                                   "urls": [f"{HOST}/s3/{bucket}/{urllib.parse.quote(obj, safe='')}?sig={uk}"]})
        if op == "signeds3upload" and m == "POST":
            b = json.loads(self.body())
            if S["uploads"].get(b.get("uploadKey")) != (bucket, obj): return self.send(400, {"reason": "bad uploadKey"})
            S["completed"].append(obj); return self.send(200, {"bucketKey": bucket, "objectKey": obj})
        if op == "signeds3download" and m == "GET":
            if (bucket, obj) not in S["objects"]: return self.send(404, {"reason": "object not found"})
            return self.send(200, {"status": "complete", "url": f"{HOST}/s3/{bucket}/{urllib.parse.quote(obj, safe='')}?dl=1"})
        self.send(404, {})

    def s3(self, m, path):
        # NOTE: S3 URLs are pre-signed: no Authorization header is expected (and none must be required).
        if path == "/s3/appbundle" and m == "POST":
            data = self.body()
            key = re.search(rb'name="?key"?', data)                       # .NET writes name=key (unquoted); both are valid
            files = [m.start() for m in re.finditer(rb'name="?file"?', data)]
            ok = bool(key and files and files[-1] > key.start() and b"PK" in data)
            km = re.search(rb'name="?key"?\r\n\r\n([^\r\n]+)', data)
            S["bundle_uploads"].append({"bytes": len(data), "file_last": ok, "key": km.group(1).decode() if km else None})
            return self.send(204, raw=b"")
        mm = re.fullmatch(r"/s3/([^/]+)/([^/?]+)", path.split("?")[0])
        if not mm: return self.send(404, {})
        key = (mm.group(1), urllib.parse.unquote(mm.group(2)))
        if m == "PUT": S["objects"][key] = self.body(); return self.send(200, raw=b"")
        if m == "GET":
            return self.send(200, raw=S["objects"][key]) if key in S["objects"] else self.send(404, {})
        self.send(405, {})


if __name__ == "__main__":
    # PORT+1 plays "a different host"; it records every request it receives (see dispatch).
    threading.Thread(target=ThreadingHTTPServer(("127.0.0.1", PORT + 1), H).serve_forever, daemon=True).start()
    ThreadingHTTPServer(("127.0.0.1", PORT), H).serve_forever()

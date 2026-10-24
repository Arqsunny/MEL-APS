# Project Analysis (2026-10-24)

Scope: the repo at `C:\MasterEquipmentList-APS` compared with [the spec](MEL-Architecture-Specification.txt). Findings come from reading the code. Nothing was run against live APS or Revit.

## Inventory
| Area | Files | State |
|------|-------|-------|
| `backend/` | controllers, `Data/` (DbContext, migrations, seeder), `Models/` | .NET 8 Web API on EF Core + PostgreSQL 16 |
| `revit_automation/` | `MelSyncApp.cs` | DA `IExternalDBApplication`; no `.csproj`; not compiled |
| `frontend/` | Vite + React + TS app (`src/`) | Wired to the API; viewer is a placeholder; original mockup archived in `docs/mockup/` |
| `docs/` | spec PDF/TXT, shared parameter file, these MD files | new |

## Gaps vs. spec
| Spec requirement | Reality |
|------------------|---------|
| PostgreSQL 16 + EF Core + temporal audit | ✅ Implemented (2026-10-24). Audit is an append-only table, since PostgreSQL has no system-versioned tables |
| Auth / RBAC | ✅ Implemented (2026-10-24): JWT bearer + role policies + segregation of duties; browser SSO still pending. See [AUTH.md](AUTH.md) |
| Real DA WorkItem | ✅ Implemented (2026-10-24) and tested against a fake APS; **not yet run against real APS or Revit** (see [APS.md](APS.md)) |
| SI normalization | Absent end to end |
| APS Viewer v7 | Fake boxes (`mock-equipment`); label says "SVF2 Stream Active" but nothing streams |
| ACC webhooks / republish | Absent |
| Lock detection / soft locks / QR / heatmap | Absent |
| Multi-model linking by `MEL_Equipment_GUID` | The staging API keys on `EquipmentUniqueId` only |

## Risks and defects found
1. ~~**Parameter set mismatch.**~~ **Resolved 2026-10-24:** the catalog in [parameters.md](parameters.md) (`mel-parameters.json`) is now the single source of truth. It drives the shared parameter file, backend validation and the UI labels.
2. ~~**Unit handling is unsafe.**~~ **Resolved in code (tested logic, not yet run in Revit):** SI → internal-unit conversion with spec/unit compatibility checks. Original issue: `MelSyncApp.AssignValue` does `param.Set(double)`. Revit stores lengths, weights and power in internal units, so values written as pounds or kW land wrong by a large factor. Needs `UnitUtils.ConvertToInternalUnits` driven by the parameter's spec type, fed from SI values.
3. ~~**Payload carries only strings.**~~ **Resolved:** typed payload (`dataType`, `siUnit`) from `MelSync.Core`. Original issue: Type information (`StorageType`, unit) is lost between API and bundle; rely on the parameter's own storage type plus an explicit unit field.
4. ~~**Silent failure paths in the bundle.**~~ **Resolved:** one result per change, reasons surfaced in the UI and audit. Original issue: Missing elements and params are `continue`d with no report. A sync can "succeed" while changing nothing. It needs a result file.
5. ~~**`UniqueId` instability.**~~ **Resolved:** `MEL_Equipment_GUID` fallback lookup (unit-tested). Original issue: A `UniqueId` can change if an element is deleted and recreated. `MEL_Equipment_GUID` is meant as the stable key. The write path should resolve by GUID first, then fall back to `UniqueId`.
6. **Gate was previously bypassable.** Original dispatch sent every staged item regardless of status. Fixed in this session; needs tests so it can't regress.
7. ~~**Frontend not served by the backend.**~~ **Resolved:** backend serves `frontend/dist` (SPA fallback); dev uses a Vite proxy. Open CORS policy removed.
8. **Deprecated SDK.** (Update: the new APS code uses plain `HttpClient`; the `Autodesk.Forge` package is still referenced but unused and can be removed.) `Autodesk.Forge` 1.9.7 is the legacy Forge SDK, and it is also unused so far. Prefer the current APS SDK (`Autodesk.*` APS packages) or plain `HttpClient`.
9. **No repo hygiene.** `git` reports the project as inside a repo rooted at `C:\` (owned by TrustedInstaller). Initialize a dedicated repo before committing.
10. **Revit version/runtime.** Revit 2024 DA bundles target .NET Framework 4.8; 2025+ target .NET 8. The runtime must be chosen deliberately, and the bundle needs its own project file.

## Errors in the spec itself (don't copy blindly)
- The Revit sample contains a stray `loop:` label and refers to `data.RevitDoc` without handling documents opened from ACC cloud URNs. DA input for C4R models needs the cloud path/URN input and 3-legged or 2-legged auth with the correct scopes.
- Section 8 shows `POST /autodeskviewing/.../publish` under "ACC Data Management Webhook"; that is an endpoint to call, not a webhook. Verify against current ACC Publish API docs.
- The "NURBS / Shared Parameter File" comment is wrong, and the sample parameter table is missing `VISIBLE`/`DESCRIPTION` columns (fixed in `MEL_SharedParameters.txt`).
- The TypeScript heatmap uses `THREE` without importing it (Viewer exposes `THREE` globally).
- The spec assumes a 15-minute soft lock but defines no release or heartbeat behavior.

## What is solid
- Staging gate state machine and dispatch filtering (this session).
- Clear separation of the three tiers.
- The bundle's overall flow (apply → commit → sync with relinquish) matches the spec.
- The mockup's information design (viewer + grid + inspector + approval modal) is a reasonable target.

## Open decisions (need the owner)
1. ~~Frontend stack~~ Decided: React + TypeScript (backend stays .NET 8).
2. Revit target year, and so the .NET runtime of the bundle.
3. Hosting and DB (Azure? existing Postgres?).
4. Identity provider (Autodesk 3-legged login vs company Entra ID).
5. ~~Final shared-parameter list~~ Settled as v1 draft in [parameters.md](parameters.md); confirm data types in Revit before families adopt it.

## Update log
- **Staging/UI integration bug caught in testing:** the API serialized `ApprovalStatus` as numbers while the UI expects names, so staged items were invisible. Fixed with `JsonStringEnumConverter`. Tests should pin the JSON contract.
- **Known demo limits:** role switch is client-side only (anyone can call approve/reject directly); data is in memory; the viewer is a placeholder; only weight and airflow have imperial conversions.
- **Database layer verified against real PostgreSQL 16 (20 API/DB checks + 3 restart checks):** gate enforcement, server-derived old values, duplicate-pending 409, concurrent approve/dispatch (exactly one winner), audit completeness and batch grouping, DB-level append-only triggers, persistence across restart. The script lives outside the repo; turning it into automated tests is still open.
- **Resolved:** actors now come from validated tokens (name + immutable subject id), not request bodies. Still open: the `Autodesk.Forge` package is unused.
- **Environment note:** an unrelated Postgres container (from another project) already occupied port 5432 on the development machine and was left untouched; this project uses `mel-aps-postgres` on 5433.
- **Auth verified (33 API + 15 browser checks, fresh DB):** 401 without/with garbage/tampered tokens; viewer cannot stage; contributor cannot approve/reject/dispatch; lead cannot review own request but another lead can; spoofed `requestedBy`/`reviewedBy`/`oldValue` in bodies ignored; audit actors match token users and carry `actorId`; the UI follows the real roles and returns to login on an invalid token.
- **Two defects found by testing this work:** (1) the authorization fallback policy blocked the SPA's static assets (fixed by serving static files before auth); (2) missing auth config in Production only failed on the first request (now fails at startup). Also note `dotnet run` honors `launchSettings.json` and silently forces Development: use `--no-launch-profile` when testing other environments.
- **Remaining auth work:** Entra ID app registration + MSAL in the SPA (needs tenant admin), per-model authorization if required, token refresh.

### Design Automation pipeline (2026-10-24)
- **What changed in the design vs. the spec/original code:** cloud models are opened by `ModelPathUtils.ConvertCloudGUIDsToCloudPath(region, projectGuid, modelGuid)` with a 3-legged `adsk3LeggedToken`, not via an input-file URN, per Autodesk's published guidance. So the system needs a **connected APS service account** and each model needs its **cloud GUIDs** recorded.
- **Dispatch is now asynchronous:** it returns 202 and the worker finishes the job. A change is committed only after Revit applied it *and* the model saved. New states: `Dispatched`; failures return to `Approved` with `lastError`.
- **Verified:** 29 unit tests; 35 + 24 API checks and 20 browser checks (mock runner); 48 live-pipeline checks + 2 timeout + 6 setup checks against a fake APS; startup guards.
- **Bugs found by those tests:** the Inspector kept a stale form after model values changed (fixed by keying on values); the fake APS initially mishandled chunked bodies and unquoted multipart names (test-side). The tests also forced real design choices: refresh-token rotation must be persisted, applied-but-unsaved changes must not count as committed, one in-flight sync per model.
- **Link-model helper added (BIM Lead only):** browses hubs/projects/cloud models via the connected account. Bounded scan, host-restricted pagination (a test proves the token never reaches another host), US/EMEA chosen by the lead when APS does not report a hub region. 70 API checks + 15 browser checks against the fake. **Found by testing:** the harness had not pointed the new Data Management URL at the fake, so the backend called the *real* APS with a fake token (harmless, but a reminder that every APS host must be overridden in tests); and a 401 needed its own "reconnect" message instead of the 403 "no access" one.
- **First contact with the real APS (same day):** credentials, engine, `aps-setup`, connect, hubs, projects and the model scan all work. Two request-path bugs were found *only* by the real service, because my fake APS had been written from the same wrong understanding: the retired profile endpoint (410) and the top-folders path (404). Both fixed, and the fake now returns the real status for the old paths. **Lesson:** a fake proves logic, never contracts; every endpoint needs one real call.
- **Process fix:** my test runners wiped the database that also held the real APS connection, and one restore attempt failed (I deleted the backup too early). Tests now use a separate throwaway database (`mel_test`); the real `mel` database is never touched.
- **Same-release rule (found while chasing the first real failure):** APS reports each model's Revit release (one test model was saved in Revit 2024, the models of a larger project in 2025). A mismatched engine either cannot open the model or would upgrade the shared cloud model for everyone, so the app now records the release at link time, runs one engine per release (2025, 2026), and refuses anything else with a reason. 93 live-pipeline checks, 36 unit tests, 17 + 24 browser checks and the mock suites pass.
- **Honest limits:** never run against real APS/Revit; the fake encodes my assumptions. [APS.md](APS.md) lists exactly what is unproven and a live smoke-test checklist.

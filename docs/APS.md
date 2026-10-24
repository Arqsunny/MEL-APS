# APS Design Automation pipeline

How approved changes reach the Revit cloud model, what is verified, and what you must still check against real APS.

## Flow
```
BIM Lead: "Authorize & Sync"  ->  POST /api/EquipmentSync/dispatch-revit-sync
   |  creates a sync_job (Queued), marks the changes Dispatched, returns 202
   v
SyncWorker (background, every Aps:PollSeconds)
   Queued    -> runner.Submit : upload input.json (OSS) -> signed result URL -> POST /workitems (+ adsk3LeggedToken)
   Submitted -> runner.Poll   : GET /workitems/{id} ... success -> finalize upload -> download result.json
   |
   v
SyncResultApplier (one DB transaction)
   Applied AND the model was saved -> CommittedToModel (+ equipment value, audit)
   anything else                    -> back to Approved with the reason (retry is safe)
```
Inside Design Automation, the `MelSync` bundle opens the cloud model from its GUIDs, applies each update (unit-aware), saves with `SynchronizeWithCentral` (or `SaveCloudModel` if not workshared), and writes `result.json` with **one outcome per change**.

## Revit releases and engines (same-release rule)
A Revit engine may only open a model saved in the **same release**. An older engine cannot open a newer model, and a newer engine would **upgrade the shared cloud model for every user** the moment it syncs. So:
- Each supported release (default **2025 and 2026**, `Aps:RevitYears`) has its own AppBundle (`MelSyncApp2025`, ...), Activity (`MelSyncActivity2026`, ...) and engine (`Autodesk.Revit+2026`).
- The model's release is read from APS (`attributes.extension.data.revitProjectVersion`, confirmed reported by the real APS) and **recorded when you link the model** (Link model). The right activity is chosen from it.
- Refused, with the reason, before anything runs: release not recorded (relink), or no engine for that release (e.g. a Revit 2024 model). The finder shows the release per model and disables Link for unsupported ones.
- The bundle checks again inside the engine (`MelSync.EngineGuard`, unit-tested) and refuses without opening the model.

## Guarantees
| Guarantee | How |
|-----------|-----|
| Nothing reaches Revit unapproved | Dispatch only takes `Approved` changes (unchanged staging gate) |
| Model values change only after Revit confirms | DB values are updated by the result applier, never at dispatch |
| "Applied" is not enough | A change is committed only if Revit applied it **and** the model was saved/synchronized; otherwise it returns to Approved (`NotSynced`) |
| One bad element never blocks the rest | Each update is applied and reported independently |
| No double-apply | One in-flight sync per model (partial unique index) and `xmin` concurrency on job/changes |
| Failures are retryable | Failed items go back to `Approved` with `lastError`; re-dispatch creates a new job. Writes are idempotent |
| Stuck jobs end | `Aps:JobTimeoutMinutes` (default 75) fails the job and releases the items |
| Mock can't masquerade as production | `Aps:Mode=Mock` refuses to start outside Development |

### Per-element outcomes (`result.json` / `lastError`)
`ElementNotFound` (UniqueId gone and no matching `MEL_Equipment_GUID`), `ParameterNotFound`, `ReadOnly`, `TypeMismatch` (catalog says TEXT/INTEGER/measured but the model parameter differs), `UnitMismatch` (parameter spec cannot hold that SI unit), `UnsupportedUnit`, `InvalidValue`, `OwnedByOtherUser` (checked out; skipped, retry later), `NotSynced`, `NoResult`, `Error`.

### Unit-aware writes
Values travel and are stored in SI (kg, kW, L/s, V). The bundle converts with `UnitUtils.ConvertToInternalUnits(value, unit)` after checking the parameter's own spec accepts that unit, so a Length/Number parameter can never receive kilograms. The value is read back and returned in SI.

## What is verified (and how)
| Area | Evidence |
|------|----------|
| Update logic (units, types, locks, bad values, robustness, JSON contract) | 29 xUnit tests (`tests/MelSync.Core.Tests`) over a fake model |
| Revit adapter + bundle **compile** against the real Revit 2025 API and the DA bridge package | `dotnet build`; `tools/build-bundle.ps1` produces the bundle zip |
| Backend job lifecycle, worker, applier, retry, audit, RBAC, concurrency (mock runner) | `tests/e2e/api_sync_jobs.py`, `api_auth_rbac.py`, browser suite |
| Live-mode client/worker logic: token handling, **refresh-token rotation persistence**, OSS signed URLs, work item args, polling, every failure mode, revoked credentials, connect flow (one-time state), `aps-setup` idempotency, the link-model helper (pagination, bounded scan, foreign-host guard, region handling, error mapping) incl. a browser test of connect + finder | `tests/e2e/run_live_tests.sh` against **`fake_aps.py`** |

## Verified against the REAL APS so far
| What | How |
|------|-----|
| App credentials work; tokens for `code:all` and OSS scopes | token requests |
| `Autodesk.Revit+2025` engine exists; `us-east` Design Automation base URL; `forgeapps/me` returns a JSON string | read-only GETs |
| `aps-setup`: AppBundle upload (multipart, file last), alias, Activity JSON, OSS transient bucket | ran it; GET showed the created definitions |
| 3-legged sign-in round trip (callback accepted, refresh token stored encrypted), `userinfo` for the account name | clicked Connect; the account email was stored |
| Hubs (with region), projects, folder walk, `C4RModel` GUIDs | Link-model scan found a real project's cloud model |

**Bugs only real APS could reveal (the fake had encoded my mistakes):** (1) the legacy `/userprofile/v1/users/@me` answers `410 Gone`: use `api.userprofile.autodesk.com/userinfo`; (2) top folders are at `/project/v1/hubs/{hub}/projects/{project}/topFolders`, not under `/data/v1/projects/...` (404). The fake now rejects both old paths.

## What is NOT verified (read this)
The fake APS encodes my understanding of the APS contracts. Passing against it proves our logic, **not** that real APS behaves identically. Never run against real Revit or real APS in this work. Treat these as unproven until the smoke test below passes:

1. **The Revit bundle has never executed.** It compiles; it has not run inside Design Automation or real Revit. Unproven: `GetCheckoutStatus` behaviour in DA, `SynchronizeWithCentral` from DA on your models, that `UnitTypeId.Kilowatts` etc. are valid for the parameter specs, loading `MelSync.Core.dll` from the bundle.
2. **OSS signed-S3 upload as a work item output**, and the finalize call afterwards. I believe this is the current documented pattern; confirm it.
3. **Exact endpoint/JSON shapes** (DA `us-east` base URL, activity JSON, `adsk3LeggedToken` placement, work item status names). Based on Autodesk's published Revit cloud-model guidance; not tested live.
4. **Service-account access.** Revit cloud models need a user context (Autodesk: `adsk3LeggedToken`, scope `code:all`). That APS user must be an ACC project member with edit rights on the model, and your ACC account must have the APS app provisioned (Account Admin > Custom Integrations). Not verified.
5. **ACC publish after sync is NOT built.** The spec's step 8 (publish the model so the viewer refreshes, then react to `extraction.finished`) is still open. Autodesk notes there is no Revit API to publish a workshared cloud model; it needs the Data Management publish command.

## Setup (once per environment)
1. Register an APS app (Autodesk Platform Services portal): note Client ID/Secret; add the callback `https://<host>/api/aps/callback`. Provision the app in ACC (Account Admin > Custom Integrations).
2. Configuration (secrets via environment variables or user-secrets, never committed):
   ```
   Aps__Mode=Live  Aps__ClientId=...  Aps__ClientSecret=...  Aps__CallbackUrl=https://<host>/api/aps/callback
   Aps__Bucket=<globally-unique-lowercase-name>      # OSS bucket for job scratch data (transient, 24h)
   ```
   Optional: `Aps__RevitEngine` (default `Autodesk.Revit+2025`), `Aps__DesignAutomationBaseUrl` (EMEA), `Aps__PollSeconds`, `Aps__JobTimeoutMinutes`.
   Shortcut: put `APS_CLIENT_ID` and `APS_CLIENT_SECRET` in the git-ignored `.env` and use `powershell -File tools/run-live.ps1 -Command setup|run`; it maps them onto these settings without printing the secret (`-DryRun` shows what it would use).
3. Build and publish the bundles (needs Revit 2025 and 2026 installed locally for their API references; a release that is not installed is skipped):
   ```
   powershell -File tools/build-bundle.ps1                  # artifacts/MelSync.bundle.R2025.zip and ...R2026.zip
   dotnet run --project backend -- aps-setup                # one AppBundle + Activity per release, aliases, bucket
   ```
   Re-run after every bundle change; it creates new versions and moves the `prod` alias. (Earlier single-release resources `MelSyncApp` / `MelSyncActivity` from the first test are no longer used and can be deleted.)
4. Start the app, sign in as a BIM Lead, click **Connect** in the header (APS chip) and sign in with the service account.
5. Link each model. Easiest: as a BIM Lead click **Link model** in the header (next to the connected APS account), pick the hub and project, optionally type part of the file name, click **Find models**, and **Link** the right one. The helper lists only Revit *cloud* models (Revit Cloud Worksharing) and shows their region, project GUID and model GUID.
   By hand instead: `PUT /api/models/{modelGuid}/cloud-link` with `{ "region": "US", "projectGuid": "...", "modelGuid": "..." }`. These are the Revit **cloud** project/model GUIDs (the model version's `attributes.extension.data.projectGuid` / `modelGuid`), **not** the ACC project or item ids.

### Other Revit releases
Add the year to `Aps:RevitYears`, add its Design Automation package version to `MelSync.Revit.csproj` (`DaPackageVersion`) and build it with `tools/build-bundle.ps1 -Years <year>`. Revit 2024 and earlier are .NET Framework 4.8 and are not supported by this project as configured.

## Live smoke test (do this before trusting it)
Use a **copy** of a non-production cloud model linked in step 5, with a test parameter on one equipment instance.
0. **Connectivity check first (no changes).** As a BIM Lead open *Review & Sync* and click **Test connection** (or `POST /api/sync/check/{modelGuid}`). The activity opens the linked model as the connected account, writes nothing, saves nothing and closes it. `Succeeded` proves the bundle loads in Design Automation, the model opens with the service account, and the result file comes back. `Failed` shows the reason (and the report URL for leads). It appears in the job list as a job with 0 changes. A model that cannot be opened is reported as a failure, never as success.
1. Stage + approve one TEXT change (e.g. `MEL_PO_Number`) -> Authorize & Sync.
2. Watch `GET /api/sync/jobs/{model}/{id}`: `Queued -> Submitted -> Succeeded`. On failure read `error` and the `reportUrl` (BIM Lead).
3. Open the model in Revit: the value is there and a sync with comment `MEL automated parameter sync <batch>` appears in the history.
4. Repeat with a measured value (`MEL_Operating_Weight` in kg): confirm Revit shows the correct value in its display units (this validates unit conversion).
5. Check out the element in Revit as another user and sync again: expect `OwnedByOtherUser`, item back in Approved, retry succeeds after relinquishing.
6. Stage a value for an element whose UniqueId you changed (delete/recreate) to confirm the `MEL_Equipment_GUID` fallback.
Record anything that differs from this document and fix the fake and code together.

## Link-model helper (BIM Lead only)
`GET /api/aps/browse/hubs`, `.../hubs/{hub}/projects`, `.../hubs/{hub}/projects/{project}/models?search=`. Read-only; acts as the connected APS account.
- **How it finds models:** walks the project's folders breadth-first, and for each `.rvt` file reads its latest version. A file is listed only if its extension type is `versions:autodesk.bim360:C4RModel` and `projectGuid`/`modelGuid` are present (field names per Autodesk's published guidance). Plain uploaded RVTs are counted, not listed.
- **Bounded:** at most 150 folders, 100 models, depth 8, 20 pages per list and a 90-second budget. When it stops early it says so (`scan.truncated` and a note); type part of the file name to narrow it. The name filter is applied before files are inspected.
- **Token safety:** the account's token is sent only to the configured Data Management host (`Aps__DataManagementBaseUrl`). Pagination links to any other host are ignored and reported, never followed (tested).
- **Region:** read from the hub's `attributes.region`. **Confirmed live** for the two hubs of the connected account (`US`). Not guaranteed for every hub, so when it is missing the UI asks the lead to choose US or EMEA (where the project is hosted).
- **Errors:** 401 from APS becomes "reconnect the APS account" (409); 403 becomes an explanation about ACC project membership / Custom Integrations (502).
- **Verified against the real APS (2026-10-24):** hubs + region, projects, folder walk and the `C4RModel` type with `projectGuid`/`modelGuid` (a real project's Revit cloud model was found). Not yet exercised live: pagination across several pages, very large projects, composite designs.

## First real runs (2026-10-24)
- Connectivity check #1 on a linked model: the bundle loaded and ran on the 2025 engine and the result file round-tripped through OSS (so the S3 upload/finalize pattern works), but Revit failed to open the model: `RevitServerCommunicationException: The central server could not be reached`. Cause not yet established; a model saved in a newer release than the engine is a prime suspect, which led to the same-release rule above. Diagnostics inside the bundle (engine release, whether the session has an Autodesk account, the exact open attempt) now come back in `result.log`.
- Revit 2026's API marks `ModelPathUtils.CloudRegionUS/EMEA` obsolete; the bundle now passes the literal strings `"US"` / `"EMEA"`.

## Operations notes
- **Single instance:** the worker and the in-memory connect state assume one backend instance. Multiple instances need a distributed lock (e.g. `FOR UPDATE SKIP LOCKED`) before scaling out.
- **Data Protection keys:** the stored APS refresh token is encrypted with ASP.NET Data Protection. In production persist and protect the key ring (shared volume / Key Vault); if keys change, the lead must reconnect.
- **Refresh tokens rotate** (each refresh invalidates the previous one); the newest is persisted after every refresh.
- **Costs/limits:** Design Automation work items have processing-time limits (about an hour) and are billed. The 60-minute signed result URL bounds the maximum run.
- **Disconnecting** (`DELETE /api/aps/connection`) deletes the stored credential; queued jobs then fail with a "connect an APS account" reason and their changes return to Approved.

# Status & how to resume (2026-10-24)

## Where the live smoke test stands
| Step | State |
|------|-------|
| APS app credentials (`.env`, git-ignored), tokens, `Autodesk.Revit+2025/2026` engines | ✅ verified against the real APS |
| Callback `http://localhost:5080/api/aps/callback` registered; Connect works; account stored encrypted (the Autodesk account you signed in with) | ✅ |
| Published to the APS account: `MelSyncApp2025/2026`, `MelSyncActivity2025/2026` (v1, alias `prod`), a transient bucket (name derived by `tools/run-live.ps1`, or pass `-Bucket`) | ✅ |
| Link-model helper (hubs, projects, folder scan, GUIDs, Revit release) | ✅ works on real projects |
| Connectivity check #1 (before the same-release rule) | bundle ran in Design Automation and the result round-tripped, but Revit could not open the model: `RevitServerCommunicationException: central server could not be reached` |
| **Model linked WITH its Revit release** | ⬜ **pending: the current link has no recorded release, so checks/syncs are refused (by design)** |
| Connectivity check #2 on the 2025/2026 bundles with diagnostics | ⬜ after the relink |
| Real parameter sync (stage -> approve -> sync) on a **copy** of a model | ⬜ |

## Resume in 4 steps
1. Start the app against the real APS: `powershell -File tools/run-live.ps1 -Command run` (needs Docker: `docker compose --env-file .env.dev up -d` for the database on port 5433; run `tools/init-dev-secrets.ps1` once first), open `http://localhost:5080`, sign in as **Lee BIM Lead**. The APS connection is stored in the database and survives restarts.
2. **Link model**: pick hub + project, **Find models**, check the **Revit 2025/2026 badge**, **Link** a **copy** of a non-production model. The dialog's "Currently linked" line must show the Revit year. (Or give the project and file name and have it linked through the API.)
3. **Review & Sync -> Test connection** (opens the model in Design Automation, changes nothing). Read the job's `error` and `result.log`; the Design Automation report is available to leads.
4. If it passes, run the smoke test in `docs/APS.md` (one TEXT change, then a measured value, then a checked-out element).

## Things to remember
- **Never run the e2e runners against the real database.** They use a separate `mel_test` database and kill any running `MasterEquipmentApi`; stop the real app first (shared port 5080). Losing the real database also loses the APS connection (one click to reconnect).
- `.env` may be shared with another project's APS app; only `APS_CLIENT_ID` / `APS_CLIENT_SECRET` are used here. An unrelated Postgres container on port 5432 may belong to another project: do not touch it.
- A fake APS passes tests that real APS fails: four request-path or contract bugs were found only against the real service (profile endpoint 410, top-folders path, obsolete region constants, version rule). Every new endpoint needs one real call.
- Use only a **copy** of a model for any write test. Opening a model in a different Revit release than it was saved in can upgrade the shared cloud model for everyone; the app now refuses that.

## Still open (not blocked on the smoke test)
- ACC publish after sync + `extraction.finished` webhook (viewer refresh), spec section 8.
- Real APS Viewer instead of the placeholder.
- Own git repository (the folder is currently detected under `C:\`), then CI for the unit tests and the fake-APS suites.
- Entra ID single sign-on for the browser (needs tenant admin); per-model authorization.
- Delete the unused first-test resources `MelSyncApp` / `MelSyncActivity` from the APS account.
- Multi-instance worker safety (distributed lock); persist the Data Protection key ring for production.
- Pagination across several pages and very large projects are exercised only against the fake.

# MEL Implementation Plan

Phases follow the spec's roadmap (24 weeks). Status reflects the repo as of 2026-10-24. See [ANALYSIS.md](ANALYSIS.md) for the findings behind the ordering.

Legend: ✅ done · 🟡 partial · ⬜ not started

## Phase 0: Foundations (do first; blocks everything)
| Item | Status |
|------|--------|
| Copy spec + shared parameter file into `docs/` | ✅ |
| Give the project its own git repo (it is currently detected under `C:\`) and commit baseline | ⬜ |
| Decide the real shared-parameter set → [parameters.md](parameters.md) (v1 draft, 11 params) | ✅ |
| Decide frontend stack → **Vite + React + TypeScript** with .NET 8 backend serving the build | ✅ |
| Register an APS app (client id/secret), define secrets handling (user-secrets / env) | ⬜ |

## Phase 1: Core infrastructure & schema (spec weeks 1-6)
| Item | Status |
|------|--------|
| Staging gate state machine (Pending → Approved/Rejected → CommittedToModel) | ✅ |
| Approve / reject endpoints, reason required on reject | ✅ |
| Dispatch only approved items, grouped per model | ✅ (mock WorkItem) |
| PostgreSQL 16 + EF Core 8 (`equipment_master`, `staging_parameter_updates`, migrations, docker-compose) | ✅ |
| Audit history: append-only `audit_log` with batch ID (DB-enforced); replaces temporal tables, which PostgreSQL lacks | ✅ |
| Concurrency safety (xmin tokens, one pending change per equipment+parameter) | ✅ tested |
| Auth + RBAC: JWT bearer, `Contributor`/`BimLead` policies, actors from token, self-review blocked, fail-fast config ([AUTH.md](AUTH.md)) | ✅ dev issuer; Entra ID wiring pending |
| Browser SSO (MSAL + Entra app registration) | ⬜ needs tenant admin |
| Parameter catalog + validation on staging (`GET /api/parameters`) | ✅ |
| SI normalization service (unit conversion in/out, uses catalog `siUnit`) | ⬜ |
| E2E checks in `tests/e2e/` (33 API + 15 browser, passing); convert to CI-run xUnit/Playwright with a throwaway DB | 🟡 |

## Phase 2: APS automation pipeline (weeks 7-12)
| Item | Status |
|------|--------|
| Revit bundle compiles against the real Revit 2025 API + DA bridge; update logic unit-tested (29) | ✅ compiles; ⬜ never run in DA/Revit |
| Separate bundle projects (`MelSync.Core`, `MelSync.Revit`) + packaging script (`tools/build-bundle.ps1`) | ✅ (Revit 2025) |
| Unit-aware writes (SI → internal via `UnitUtils`, spec/unit compatibility check, read-back) | ✅ logic tested; ⬜ not run in Revit |
| Per-element results (`result.json`) → committed / back to Approved with reason | ✅ |
| AppBundle + Activity registration (`aps-setup`, idempotent) | ✅ vs fake APS; ⬜ vs real |
| Real WorkItem submit + polling, APS auth (2-legged + rotating 3-legged service account), OSS, background worker, timeouts | ✅ vs fake APS; ⬜ vs real APS ([APS.md](APS.md) smoke test) |
| ACC publish trigger + webhooks (`extraction.finished`) | ⬜ not built |
| Link-model helper: lead-only browse of hubs/projects/cloud models with region + GUIDs and one-click link | ✅ vs fake APS; ⬜ vs real (field names, hub region) |
| Multi-instance safety for the worker (distributed lock) | ⬜ |

## Phase 3: Staging gate UI & client (weeks 13-18)
| Item | Status |
|------|--------|
| React app scaffolded; mockup layout/styles ported (`frontend/`) | ✅ |
| Wire grid/inspector/review modal to the API (stage, list, approve, reject, dispatch) | ✅ (demo data, role switch is a stand-in) |
| Unit toggle SI / Imperial (display conversion only, storage stays SI) | ✅ weight, airflow |
| Embed the real APS Viewer v7 (selection ↔ `externalId` ↔ grid) | ⬜ |
| Approval modal driven by real pending items | ✅ |
| Real-time updates (SignalR) | ⬜ |

## Phase 4: Field & analytics (weeks 19-24)
Approval-state heatmap (`setThemingColor`), lock detection and soft locks, signed QR codes plus mobile route, cross-model coordination alerts, load/stress test.

## Immediate next sprint (recommended)
1. Phase 0 leftovers: own git repo, APS app registration and secrets handling.
2. ~~EF Core + PostgreSQL~~ done.
3. ~~Authentication + RBAC~~ done (dev issuer); Entra ID + MSAL next, needs your tenant.
4. Automated tests in CI (xUnit + Testcontainers, Playwright Test).
5. Real APS Viewer once credentials exist.

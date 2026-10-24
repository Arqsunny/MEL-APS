# Master Equipment List (MEL) Management System

A cloud-based, cross-discipline equipment tracking platform bridging **Autodesk Forma**, **APS Viewer (SVF2)**, and **Headless Revit Design Automation** for Cloud Workshared RVTs in Autodesk Construction Cloud (ACC).

## Project Structure
- `frontend/`: Vite + React + TypeScript SPA: equipment grid, inspector/staging panel, BIM Lead review modal (3D viewer is a placeholder until APS credentials exist).
- `backend/`: .NET 8 Web API orchestrating the staging gate and Design Automation WorkItems.
- `revit_automation/`: `MelSync.Core` (pure update logic + wire contracts, unit-tested) and `MelSync.Revit` (Design Automation bundle for Revit 2025).
- `docs/`: Specification, vision, plan, analysis and shared parameter file.

## Documentation
| Doc | Purpose |
|-----|---------|
| [docs/VISION.md](docs/VISION.md) | The idea, principles, actors, end-to-end flow |
| [docs/PLAN.md](docs/PLAN.md) | Phased plan with per-item status |
| [docs/ANALYSIS.md](docs/ANALYSIS.md) | Gap analysis, defects, spec errors, open decisions |
| [docs/APS.md](docs/APS.md) | Design Automation pipeline: flow, guarantees, setup, **what is not verified**, live smoke test |
| [docs/AUTH.md](docs/AUTH.md) | Authentication, roles, Entra ID setup |
| [docs/parameters.md](docs/parameters.md) | Shared-parameter catalog (source: `docs/mel-parameters.json`) |
| [docs/MEL_SharedParameters.txt](docs/MEL_SharedParameters.txt) | Revit shared parameter file (generated: `python tools/gen_shared_params.py`) |
| [docs/MEL-Architecture-Specification.pdf](docs/MEL-Architecture-Specification.pdf) | Source specification |

## Staging gate API
All endpoints require a bearer token. `POST /api/EquipmentSync/stage` (Contributor/BIM Lead) → Pending · `POST .../approve/{id}` and `.../reject/{id}` (BIM Lead; reason required; not your own request) · `POST .../dispatch-revit-sync` (BIM Lead) · `GET .../staged/{modelGuid}?status=` · `GET /api/parameters` · `GET /api/equipment/{modelGuid}` · `POST .../dispatch-revit-sync` (approved items only).

## Quick Start
Requirements: .NET 8+ SDK, Node.js 20+, Docker (for the local PostgreSQL 16).

```bash
powershell -File tools/init-dev-secrets.ps1   # once: random DB password + dev sign-in key (git-ignored .env.dev + user secrets)
docker compose --env-file .env.dev up -d       # PostgreSQL 16 on localhost:5433 (container mel-aps-postgres)
cd frontend && npm install && npm run build
cd ../backend && dotnet run          # http://localhost:5080: API + built UI
```
In Development the backend applies EF migrations and seeds demo data (model `demo-model`, 4 pieces of equipment) on startup (`Database:AutoMigrate`, `Database:SeedDemoData`). Data persists in the `mel-aps-pgdata` Docker volume.

For UI hot reload, run `npm run dev` in `frontend/` (http://localhost:5173, proxies `/api` to the backend).

**Configuration:** the connection string is `ConnectionStrings:Mel` (`ConnectionStrings__Mel` env var in production). The password in `appsettings.Development.json` / `docker-compose.yml` is for the local container only.

**Migrations**
```bash
cd backend
dotnet ef migrations add <Name> -o Data/Migrations
dotnet ef database update
```

**Signing in locally:** the login screen offers four demo users (contributor, two BIM Leads, a read-only viewer). The API issues a signed token and enforces roles server-side; see [docs/AUTH.md](docs/AUTH.md). In any non-Development environment the app refuses to start without `Auth:Authority` and `Auth:Audience`.

**Syncing to Revit:** dispatch returns immediately and a background worker runs the job. In Development the **mock runner** applies changes locally (values starting `FAIL`/`SKIP` simulate failures); against a real Revit cloud model set `Aps__Mode=Live` and follow [docs/APS.md](docs/APS.md). The real pipeline has **not** been run against live APS yet.

**Tests:** `dotnet test tests/MelSync.Core.Tests` (29 unit tests) and `tests/e2e/` (API, browser and fake-APS suites; see its README). The original static mockup is kept at `docs/mockup/`.

## Data & audit
- `equipment_master` (parameter values as SI `jsonb`), `staging_parameter_updates`, `audit_log` (append-only: UPDATE/DELETE/TRUNCATE rejected by DB triggers).
- Integrity: unique (model, UniqueId); one *Pending* change per equipment + parameter (partial unique index); optimistic concurrency on `xmin` so concurrent approvals/dispatches cannot double-apply.
- `GET /api/audit/{modelGuid}?equipmentUniqueId=&batchId=` returns the trail; every dispatch gets a `batchId` that groups its entries.

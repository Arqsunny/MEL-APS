# End-to-end checks

Black-box checks against a **running** backend with a **fresh** database (they stage and commit real changes). Prefer the runner scripts below, which reset the database for you.

> The runners use their own throwaway database `mel_test` (in the same Postgres container) and kill any running `MasterEquipmentApi` process, so **stop the real app first** (they share port 5080). Your real `mel` database is never touched.

> Needs the untracked `.env.dev` (run `powershell -File tools/init-dev-secrets.ps1` once): the runners read the database password from it.

## Suites
| Script | What | Needs |
|--------|------|-------|
| `run_mock_tests.sh [rbac\|jobs\|ui]` | Auth/RBAC/audit (35), sync jobs in mock mode (24), browser workflow (20). Resets the DB between suites | docker, python, dotnet, node, Edge, built frontend |
| `run_live_tests.sh` | `aps-setup` + the whole Live pipeline incl. the link-model helper (70) + timeout (2) + browser test of APS connect and the finder (15) against **`fake_aps.py`** | docker, python, dotnet, node, Edge, built bundle zip and frontend |

`fake_aps.py` is a fake APS built from the author's reading of the APS docs. It proves our client/worker logic, not real APS behaviour; see `docs/APS.md` for the live smoke test.

These are scripts, not part of CI yet; converting them to xUnit/Playwright Test with a throwaway database is tracked in `docs/PLAN.md`.

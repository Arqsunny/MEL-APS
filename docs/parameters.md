# MEL Shared-Parameter Catalog (v1 draft)

**Source of truth for the MEP set:** [`MASTER_MEP_SHARED_PARAMETER_FILE.txt`](MASTER_MEP_SHARED_PARAMETER_FILE.txt), imported by `python tools/import_shared_params.py` into [`mel-parameters.json`](mel-parameters.json). The 11 `MEL_*` rows below are app-owned and are kept across imports. Everything else derives from the JSON:
- `MEL_SharedParameters.txt` (Revit file): `python tools/gen_shared_params.py`
- Backend validation and `GET /api/parameters`: `backend/Models/ParameterCatalog.cs`
- UI labels in `frontend/index.html` (manual for now)

GUIDs are **immutable once any family is published**. Change names, units or types only before rollout. To add a parameter, append to the JSON, regenerate, and re-issue the file.

| Parameter | Category | Revit type | SI storage unit | Stageable | Notes |
|-----------|----------|-----------|-----------------|-----------|-------|
| `MEL_Equipment_GUID` | Identity | TEXT | n/a | no | Persistent link across A/M/E/S models; system-assigned |
| `MEL_Tag_Number` | Identity | TEXT | n/a | yes | e.g. `AHU-01` |
| `MEL_Approval_Status` | Workflow | TEXT | n/a | no | Written back by the system |
| `MEL_Operating_Weight` | Engineering | MASS | kg | yes | Spec said `WEIGHT`; modern Revit uses `MASS`. **Verify** |
| `MEL_Electrical_Load` | Engineering | ELECTRICAL_POWER | kW | yes | |
| `MEL_Design_Airflow` | Engineering | AIRFLOW | L/s | yes | New: UI had it (CFM) |
| `MEL_Voltage` | Engineering | ELECTRICAL_POTENTIAL | V | yes | New: split from UI's "480V / 3 Phase" |
| `MEL_Phases` | Engineering | INTEGER | n/a | yes | New: 1 or 3 |
| `MEL_PO_Number` | Procurement | TEXT | n/a | yes | New |
| `MEL_Lead_Time_Weeks` | Procurement | INTEGER | weeks | yes | New |
| `MEL_Procurement_Status` | Procurement | TEXT | n/a | yes | New: free text for now; consider a fixed list |

## Rules
- **System-owned** parameters (`Stageable = no`) are rejected by `POST /stage`; the system writes them.
- Values are stored in the SI unit above; display units are converted at the edge.
- The same GUID must be bound to every discipline's families so the equipment links across models.

## Items to confirm
1. Revit data-type names (`MASS`, `AIRFLOW`, `ELECTRICAL_POTENTIAL`) in a test shared-parameter file import.
2. Should `MEL_Procurement_Status` become a controlled vocabulary?
3. Missing from the UI today but possibly wanted: manufacturer, model number, discipline, area/room, commissioning date.

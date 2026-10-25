"""Import a Revit shared-parameter TXT into docs/mel-parameters.json.

The TXT is the source for GUID, name, data type, group, visibility, description,
and user-modifiable. App-owned MEL_* parameters already in the JSON are kept.
A parameter is stageable only when this app can write it: TEXT, INTEGER, or a
measured type whose SI unit the sync bundle converts (kg, kW, L/s, V).
"""
import json
import pathlib

root = pathlib.Path(__file__).resolve().parent.parent
source = root / "docs" / "MASTER_MEP_SHARED_PARAMETER_FILE.txt"
catalog_path = root / "docs" / "mel-parameters.json"

# Spec types the Revit bundle stores in these SI units. Anything else is catalogued
# but not offered for staging (the writer would reject the unit or the storage type).
SI_UNITS = {
    "MASS": "kg",
    "ELECTRICAL_POWER": "kW",
    "AIRFLOW": "L/s",
    "HVAC_AIR_FLOW": "L/s",
    "ELECTRICAL_POTENTIAL": "V",
}
WRITABLE = {"TEXT", "INTEGER"} | set(SI_UNITS)
# Sheet and title-block fields, not equipment.
SKIP_STAGE_GROUPS = {7}

raw_bytes = source.read_bytes()
if raw_bytes.startswith(b"\xff\xfe") or raw_bytes.startswith(b"\xfe\xff"):
    text = raw_bytes.decode("utf-16")
else:
    text = raw_bytes.decode("utf-8-sig")
groups = {}
params = []
for raw in text.splitlines():
    line = raw.rstrip("\r")
    if not line or line.startswith("#") or line.startswith("*"):
        continue
    cols = line.split("\t")
    kind = cols[0]
    if kind == "GROUP":
        groups[int(cols[1])] = cols[2]
    elif kind == "PARAM":
        if len(cols) < 10:
            raise SystemExit(f"short PARAM row: {line}")
        params.append({
            "guid": cols[1],
            "name": cols[2],
            "dataType": cols[3],
            "dataCategory": cols[4],
            "revitGroupId": int(cols[5]),
            "visible": cols[6] == "1",
            "description": cols[7],
            "userModifiable": cols[8] == "1",
            "hideWhenNoValue": cols[9] == "1",
        })

guids = [p["guid"] for p in params]
if len(guids) != len(set(guids)):
    raise SystemExit("duplicate GUID in the shared parameter file")
names = {}
for p in params:
    names.setdefault(p["name"], []).append(p["guid"])
dupes = {n: g for n, g in names.items() if len(g) > 1}

existing = json.loads(catalog_path.read_text(encoding="utf-8"))
imported_guids = set(guids)
kept = [p for p in existing["parameters"] if p["name"].startswith("MEL_") and p["guid"] not in imported_guids]

imported = []
stageable_n = 0
by_type = {}
for p in params:
    by_type[p["dataType"]] = by_type.get(p["dataType"], 0) + 1
    group_name = groups.get(p["revitGroupId"], f"Group {p['revitGroupId']}")
    si = SI_UNITS.get(p["dataType"])
    stageable = (
        p["userModifiable"]
        and p["visible"]
        and p["revitGroupId"] not in SKIP_STAGE_GROUPS
        and p["dataType"] in WRITABLE
    )
    if stageable:
        stageable_n += 1
    imported.append({
        "name": p["name"],
        "category": group_name,
        "dataType": p["dataType"],
        "siUnit": si,
        "guid": p["guid"],
        "description": p["description"],
        "userModifiable": p["userModifiable"],
        "stageable": stageable,
        "revitGroupId": p["revitGroupId"],
        "dataCategory": p["dataCategory"],
        "visible": p["visible"],
        "hideWhenNoValue": p["hideWhenNoValue"],
    })

group_list = [{"id": 11, "name": "MEL"}] + [{"id": i, "name": n} for i, n in sorted(groups.items())]
out = {
    "version": 2,
    "source": "docs/MASTER_MEP_SHARED_PARAMETER_FILE.txt",
    "group": {"id": 11, "name": "MEL"},
    "groups": group_list,
    "parameters": kept + imported,
}
catalog_path.write_text(json.dumps(out, indent=2) + "\n", encoding="utf-8")
print(f"kept {len(kept)} MEL parameters, imported {len(imported)}, stageable {stageable_n}")
print("types:", ", ".join(f"{k}={v}" for k, v in sorted(by_type.items(), key=lambda kv: -kv[1])))
if dupes:
    print(f"duplicate names: {len(dupes)}")

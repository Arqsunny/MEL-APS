"""Generate docs/MEL_SharedParameters.txt (Revit shared parameter file) from docs/mel-parameters.json.

Revit writes this format as UTF-16. GUIDs and group ids are copied through unchanged.
"""
import json, pathlib
root = pathlib.Path(__file__).resolve().parent.parent
cat = json.loads((root / "docs" / "mel-parameters.json").read_text(encoding="utf-8"))
groups = cat.get("groups") or [cat["group"]]
default_group = str(cat["group"]["id"])
lines = ["# MEL Shared Parameter File (generated from docs/mel-parameters.json; do not edit by hand)",
         "# Parameter GUIDs are immutable once families are published.",
         "*META\tVERSION\tMINVERSION", "META\t2\t1",
         "*GROUP\tID\tNAME"]
for g in groups:
    lines.append(f"GROUP\t{g['id']}\t{g['name']}")
lines.append("*PARAM\tGUID\tNAME\tDATATYPE\tDATACATEGORY\tGROUP\tVISIBLE\tDESCRIPTION\tUSERMODIFIABLE\tHIDEWHENNOVALUE")
for p in cat["parameters"]:
    lines.append("\t".join([
        "PARAM", p["guid"], p["name"], p["dataType"], p.get("dataCategory") or "",
        str(p.get("revitGroupId", default_group)),
        "0" if p.get("visible") is False else "1",
        p.get("description") or "",
        "1" if p["userModifiable"] else "0",
        "1" if p.get("hideWhenNoValue") else "0",
    ]))
(root / "docs" / "MEL_SharedParameters.txt").write_bytes(("\r\n".join(lines) + "\r\n").encode("utf-16"))
print(f"wrote {len(cat['parameters'])} parameters")

"""Generate docs/MEL_SharedParameters.txt (Revit shared parameter file) from docs/mel-parameters.json."""
import json, pathlib
root = pathlib.Path(__file__).resolve().parent.parent
cat = json.loads((root / "docs" / "mel-parameters.json").read_text(encoding="utf-8"))
g = cat["group"]
lines = ["# MEL Shared Parameter File (generated from docs/mel-parameters.json; do not edit by hand)",
         "# Parameter GUIDs are immutable once families are published.",
         "*META\tVERSION\tMINVERSION", "META\t2\t1",
         "*GROUP\tID\tNAME", f"GROUP\t{g['id']}\t{g['name']}",
         "*PARAM\tGUID\tNAME\tDATATYPE\tDATACATEGORY\tGROUP\tVISIBLE\tDESCRIPTION\tUSERMODIFIABLE\tHIDEWHENNOVALUE"]
for p in cat["parameters"]:
    lines.append("\t".join(["PARAM", p["guid"], p["name"], p["dataType"], "", str(g["id"]), "1",
                            p["description"], "1" if p["userModifiable"] else "0", "0"]))
(root / "docs" / "MEL_SharedParameters.txt").write_bytes(("\r\n".join(lines) + "\r\n").encode("utf-8"))
print(f"wrote {len(cat['parameters'])} parameters")

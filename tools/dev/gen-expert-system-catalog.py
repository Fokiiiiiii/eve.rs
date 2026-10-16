#!/usr/bin/env python3
"""Writes the expert system catalogue the server embeds (dotnet/src/Eve.Skills/Data/expert-systems.v1.json).

The rows come from EveJS v0.12.9 (tools/DatabaseCreator/staticTables/expertSystems/data.json), which mirrors the client's expertsystems.jsonl
(the ClientSDE of build 3365090). The pinned SDE (sde/lock.json) has no expert system table, so the catalogue is repo data.

    tools/dev/gen-expert-system-catalog.py /path/to/evejs-v0.12.9
"""

import json
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
OUT = HERE.parent.parent / "dotnet" / "src" / "Eve.Skills" / "Data" / "expert-systems.v1.json"


def main() -> None:
    evejs = Path(sys.argv[1]) if len(sys.argv) > 1 else Path("/home/user/evejs-v0.12.9")
    source = json.loads((evejs / "tools/DatabaseCreator/staticTables/expertSystems/data.json").read_text(encoding="utf-8"))
    rows = [
        {
            "typeID": row["typeID"],
            "name": row["name"],
            "durationDays": row["durationDays"],
            "hidden": bool(row.get("hidden", False)),
            "retired": bool(row.get("retired", False)),
            "published": bool(row.get("published", True)),
            "groupID": row.get("groupID", 0),
            "groupName": row.get("groupName", ""),
            "skillsGranted": [{"typeID": grant["typeID"], "level": grant["level"]} for grant in row.get("skillsGranted", [])],
            "associatedTypeIDs": row.get("associatedTypeIDs", []),
        }
        for row in sorted(source["expertSystems"], key=lambda row: row["typeID"])
    ]
    document = {
        "source": {
            "evejs": "evejs-v0.12.9 tools/DatabaseCreator/staticTables/expertSystems/data.json",
            "generatedAt": source["source"]["generatedAt"],
            "authority": source["source"]["authority"],
        },
        "constants": source["constants"],
        "expertSystems": rows,
    }
    OUT.parent.mkdir(parents=True, exist_ok=True)
    OUT.write_text(json.dumps(document, ensure_ascii=False, indent=1) + "\n", encoding="utf-8")
    print(f"wrote {len(rows)} expert systems to {OUT}")


if __name__ == "__main__":
    main()

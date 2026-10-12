#!/usr/bin/env python3
"""Writes dotnet/src/Eve.Services.Space/Sites/Data/wormhole-site-templates.json: the 48 J-space sites (24 combat anomalies, 12 data and 12 relic signatures).

    tools/dev/extract-wormhole-site-templates.py [/path/to/evejs-v0.12.9]

The sites are the client's wormhole dungeons (build 3475087) as the EveJS content pack `evejs-wormhole-sites-1.2.0.json` holds them: the name, the dungeon id,
the archetype, faction, difficulty and entry object, the waves of Sleeper NPCs (`encounters`, in key order, each a spawn query and an amount) and the containers
with their analyzer and type names. A wormhole's ore and gas sites are not listed here: the director places no ore or gas site (DEVIATIONS).
Each template's id is `wormhole-<dungeon id>`, the dungeon key the plan (WormholeSites) names it by.
"""

import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "..", "..", "dotnet", "src", "Eve.Services.Space", "Sites", "Data", "wormhole-site-templates.json")
EVEJS = sys.argv[1] if len(sys.argv) > 1 else os.path.join(HERE, "..", "..", "..", "evejs-v0.12.9")
PACK = os.path.join(EVEJS, "content-packs", "evejs-wormhole-sites-1.2.0.json")
BAND = "wormhole"


def containers_of(hints):
    groups = {}
    for entry in hints["containers"]:
        key = (entry["role"], entry.get("analyzer") or None)
        names = entry.get("typeNameCandidates") or [entry["typeName"]]
        group = groups.setdefault(key, {"role": key[0], "analyzer": key[1], "count": 0, "types": []})
        group["count"] += entry["count"]
        group["types"] += [name for name in names if name not in group["types"]]
    return list(groups.values())


def template_of(record):
    hints = record["populationHints"]
    site = hints["wormholeSite"]
    waves = [{"query": wave["spawnQuery"], "amount": wave["amount"]} for wave in hints["encounters"]]
    if not all(wave["supported"] for wave in hints["encounters"]):
        raise SystemExit(f"{record['resolvedName']}: an encounter is not supported by the pack")
    containers = containers_of(hints)
    if sum(c["count"] for c in containers) != site["containerCount"]:
        raise SystemExit(f"{record['resolvedName']}: container count differs from the pack")
    kind = "anomaly" if record["siteKind"] == "anomaly" else "signature"
    return {
        "id": f"wormhole-{record['sourceDungeonID']}",
        "dungeonId": record["sourceDungeonID"],
        "name": record["resolvedName"],
        "kind": kind,
        "family": record["siteFamily"],
        "difficulty": record["difficulty"],
        "bands": [BAND],
        "archetypeId": record["archetypeID"],
        "factionId": record["factionID"],
        "dungeonNameId": record["dungeonNameID"],
        "entryObjectTypeId": record["entryObjectTypeID"],
        "waves": waves,
        "containers": containers,
    }


def main():
    with open(PACK, encoding="utf-8") as handle:
        pack = json.load(handle)
    records = pack["payload"]["dungeon"]["templatesByID"].values()
    templates = sorted((template_of(record) for record in records), key=lambda t: t["dungeonId"])
    with open(OUT, "w", encoding="utf-8") as handle:
        json.dump({"templates": templates}, handle, indent=1, ensure_ascii=False)
        handle.write("\n")
    print(f"wrote {len(templates)} templates to {os.path.normpath(OUT)}")


if __name__ == "__main__":
    main()

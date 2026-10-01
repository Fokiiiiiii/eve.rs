#!/usr/bin/env python3
"""Writes dotnet/src/Eve.Npc/Data/npc_authority.json: the NPCs the reference authored (what a /npc can spawn).

    tools/dev/extract-npc-authority.py [/path/to/evejs-v0.12.9]

The reference keeps its NPCs in static tables (`tools/DatabaseCreator/staticTables/npcProfiles`, `npcLoadouts`, `npcSpawnPools`): a profile names a ship type
and whose it is, a loadout what is fitted to it (almost nothing: a retail entity type carries its weapon on the type itself), a pool which profiles are
rolled at what weight. This keeps the fields the host reads, in the reference's order (the first row of a name is the one a query finds). The tables are
game data and are used as data; none of the reference's code is copied (docs/adr/0009, oracle/PROVENANCE.md).
"""

import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "..", "..", "dotnet", "src", "Eve.Npc", "Data", "npc_authority.json")
EVEJS = sys.argv[1] if len(sys.argv) > 1 else "/home/user/evejs-v0.12.9"
TABLES = os.path.join(EVEJS, "tools", "DatabaseCreator", "staticTables")


def read(table, key):
    with open(os.path.join(TABLES, table, "data.json"), encoding="utf-8") as handle:
        return json.load(handle)[key]


profiles = [
    {
        "id": p["profileID"],
        "name": p.get("name", ""),
        "aliases": p.get("aliases", []),
        "entityType": p.get("entityType", ""),
        "shipTypeID": p.get("shipTypeID", 0),
        "corporationID": p.get("corporationID", 0),
        "allianceID": p.get("allianceID", 0),
        "factionID": p.get("factionID", 0),
        "loadoutID": p.get("loadoutID", ""),
        "lootTableID": p.get("lootTableID", ""),
        "shipNameTemplate": p.get("shipNameTemplate", ""),
        "securityStatus": p.get("securityStatus", 0),
        "bounty": p.get("bounty", 0),
        "spawnDistanceMeters": p.get("spawnDistanceMeters", 20000),
    }
    for p in read("npcProfiles", "profiles")
]
loadouts = [
    {
        "id": l["loadoutID"],
        "name": l.get("name", ""),
        "modules": [[m["typeID"], m.get("quantity", 1)] for m in l.get("modules", [])],
        "charges": [[c["typeID"], c.get("quantityPerModule", 1)] for c in l.get("charges", [])],
    }
    for l in read("npcLoadouts", "loadouts")
]
pools = [
    {
        "id": p["spawnPoolID"],
        "name": p.get("name", ""),
        "aliases": p.get("aliases", []),
        "entityType": p.get("entityType", ""),
        "entries": [[e["profileID"], e.get("weight", 1)] for e in p.get("entries", [])],
    }
    for p in read("npcSpawnPools", "spawnPools")
]

os.makedirs(os.path.dirname(OUT), exist_ok=True)
with open(OUT, "w", encoding="utf-8") as handle:
    json.dump({"profiles": profiles, "loadouts": loadouts, "pools": pools}, handle, separators=(",", ":"), ensure_ascii=False)
    handle.write("\n")
print(f"{len(profiles)} profiles, {len(loadouts)} loadouts, {len(pools)} pools -> {OUT}")

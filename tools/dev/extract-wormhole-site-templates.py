#!/usr/bin/env python3
"""Writes dotnet/src/Eve.Services.Space/Sites/Data/wormhole-site-templates.json: the 68 J-space sites of the plan (24 combat anomalies, 12 data and
12 relic signatures, 9 gas reservoirs as signatures, 11 ore deposits as anomalies).

    tools/dev/extract-wormhole-site-templates.py [/path/to/evejs-v0.12.9]

The gas and ore sites come from the EveJS content packs evejs-gas-sites-1.0.0.json and evejs-ore-anomalies-1.1.0.json (client build 3475087): a
reservoir's clouds are the harvestable cloud types of its `gasTypeIDs`; a deposit's rocks are the client's rock objects (type, quantity, and the
position relative to the centre of the rocks). The director places them (SpaceContainers), not NPCs.

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

def gas_template_of(record):
    gas = record["resourceComposition"]["gasTypeIDs"]
    if not gas:
        raise SystemExit(f"{record['resolvedName']}: the reservoir names no cloud")
    return {
        "id": f"wormhole-{record['sourceDungeonID']}",
        "dungeonId": record["sourceDungeonID"],
        "name": record["resolvedName"],
        "kind": "signature",
        "family": "gas",
        "difficulty": record["difficulty"],
        "bands": [BAND],
        "archetypeId": record["archetypeID"],
        "factionId": record.get("factionID"),
        "dungeonNameId": record.get("dungeonNameID"),
        "entryObjectTypeId": record.get("entryObjectTypeID"),
        "waves": [],
        "containers": [],
        "clouds": gas,
    }

def ore_template_of(record):
    rocks = record["populationHints"]["miningRocks"]
    if not rocks:
        raise SystemExit(f"{record['resolvedName']}: the deposit has no rock")
    xs = [rock["sourcePosition"][0] for rock in rocks]
    ys = [rock["sourcePosition"][1] for rock in rocks]
    zs = [rock["sourcePosition"][2] for rock in rocks]
    centre = (sum(xs) / len(xs), sum(ys) / len(ys), sum(zs) / len(zs))
    return {
        "id": f"wormhole-{record['sourceDungeonID']}",
        "dungeonId": record["sourceDungeonID"],
        "name": record["resolvedName"],
        "kind": "anomaly",
        "family": "ore",
        "difficulty": record["difficulty"],
        "bands": [BAND],
        "archetypeId": record["archetypeID"],
        "factionId": record["factionID"],
        "dungeonNameId": record["dungeonNameID"],
        "entryObjectTypeId": record["entryObjectTypeID"],
        "waves": [],
        "containers": [],
        "rocks": [
            {"typeId": rock["typeID"], "quantity": int(round(rock["quantity"])),
             "offset": [rock["sourcePosition"][0] - centre[0], rock["sourcePosition"][1] - centre[1], rock["sourcePosition"][2] - centre[2]]}
            for rock in rocks
        ],
    }

def resource_templates(path_gas, path_ore):
    with open(path_gas, encoding="utf-8") as handle:
        gas = json.load(handle)["payload"]["dungeon"]["templatesByID"].values()
    with open(path_ore, encoding="utf-8") as handle:
        ore = json.load(handle)["payload"]["dungeon"]["templatesByID"].values()
    return [gas_template_of(record) for record in gas if record.get("siteFamily") == "gas" and record.get("sourceDungeonID") in WORMHOLE_GAS_IDS] + [
        ore_template_of(record) for record in ore if record.get("siteFamily") == "ore" and record.get("sourceDungeonID") in WORMHOLE_ORE_IDS
    ]

WORMHOLE_GAS_IDS = {3555, 3556, 3557, 3558, 3559, 3560, 3561, 3562, 3563}
WORMHOLE_ORE_IDS = {3807, 3811, 3813, 3814, 3815, 3817, 3818, 3820, 3822, 3823, 5605}

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
    templates += resource_templates(
        os.path.join(EVEJS, "content-packs", "evejs-gas-sites-1.0.0.json"),
        os.path.join(EVEJS, "content-packs", "evejs-ore-anomalies-1.1.0.json"))
    with open(OUT, "w", encoding="utf-8") as handle:
        json.dump({"templates": templates}, handle, indent=1, ensure_ascii=False)
        handle.write("\n")
    print(f"wrote {len(templates)} templates to {os.path.normpath(OUT)}")

if __name__ == "__main__":
    main()

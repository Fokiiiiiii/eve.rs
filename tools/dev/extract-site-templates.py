#!/usr/bin/env python3
"""Writes dotnet/src/Eve.Services.Space/Sites/Data/site-templates.json: the cosmic sites the host spawns (combat anomalies and signatures, data and relic signatures).

    tools/dev/extract-site-templates.py [/path/to/evejs-v0.12.9] [postgres-url]

A site is a dungeon of the static data. The names, factions and archetypes of the combat sites are the SDE's (`sde.dungeons`, matched by English name); the waves of
NPCs each site holds, its difficulty and its security bands are the EVE University wiki's site tables (the reference keeps them as `dungeonAuthority` templates of
source `eve-university`); the message id of a site's name, its entry object and its difficulty are the client's dungeon data (templates of source `client`). The SDE has
no data or relic site, so those come from the client's dungeon list as the reference extracted it, with their containers and the guards they hold when they are entered.
The tables are game data and are used as data; none of the reference's code is copied (docs/design/adr/0009, oracle/PROVENANCE.md).
Only a site of one room is kept: the host has no gates between pockets.
"""

import json
import os
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "..", "..", "dotnet", "src", "Eve.Services.Space", "Sites", "Data", "site-templates.json")
NPC = os.path.join(HERE, "..", "..", "dotnet", "src", "Eve.Npc", "Data", "npc_authority.json")
EVEJS = sys.argv[1] if len(sys.argv) > 1 else "/home/user/evejs-v0.12.9"
PG = sys.argv[2] if len(sys.argv) > 2 else os.environ.get("EVE_PG_URL", "postgres://postgres@127.0.0.1:5432/eve")
AUTHORITY = os.path.join(EVEJS, "tools", "DatabaseCreator", "staticTables", "dungeonAuthority", "data.json")

# the security bands a family spawns in when its table gives none (the reference's spawn profiles, dungeonAuthority `spawnProfiles`)
DEFAULT_BANDS = {
    "combat": ["highsec", "lowsec", "nullsec"],
    "data": ["highsec", "lowsec", "nullsec"],
    "relic": ["lowsec", "nullsec"],
}
# the difficulty range a family has in a band when the site gives none (the same profiles, `bandTuning`); the middle of it is used
BAND_DIFFICULTY = {
    "combat": {"highsec": [1, 4], "lowsec": [2, 5], "nullsec": [3, 5]},
    "data": {"highsec": [0, 3], "lowsec": [1, 4], "nullsec": [2, 5]},
    "relic": {"lowsec": [1, 4], "nullsec": [2, 5]},
}


def sde_dungeons():
    sql = "select key, doc->'name'->>'en', doc->>'archetypeID', coalesce(doc->>'factionID','') from sde.dungeons order by ord"
    text = subprocess.run(["psql", PG, "-At", "-F|", "-c", sql], capture_output=True, text=True, check=True).stdout
    by_name = {}
    for line in text.strip().split("\n"):
        key, name, archetype, faction = line.split("|")
        by_name.setdefault(name.lower(), []).append((int(key), int(archetype), int(faction) if faction else None))
    return by_name


def pools():
    with open(NPC, encoding="utf-8") as handle:
        return {p["id"] for p in json.load(handle)["pools"]}


def middle(family, bands):
    ranges = [BAND_DIFFICULTY[family][b] for b in bands if b in BAND_DIFFICULTY[family]]
    return max(1, round(sum((lo + hi) / 2 for lo, hi in ranges) / len(ranges))) if ranges else 1


def waves(template, only_on_load_first=False):
    result = []
    for e in template["populationHints"].get("encounters", []):
        if not e.get("supported") or e.get("trigger") == "hack_failure":
            continue
        result.append({"query": e["spawnQuery"], "amount": int(e["amount"])})
    return result


def containers(template):
    result = []
    for c in template["populationHints"].get("containers", []):
        result.append({"role": c.get("role", ""), "analyzer": c.get("analyzer"), "count": int(c.get("count", 1)), "types": c.get("typeNameCandidates") or []})
    return result


def main():
    with open(AUTHORITY, encoding="utf-8") as handle:
        templates = json.load(handle)["templatesByID"].values()
    names = sde_dungeons()
    known_pools = pools()
    client = {t["sourceDungeonID"]: t for t in templates if t.get("source") == "client" and t.get("sourceDungeonID")}
    out = {}

    def client_facts(dungeon_id):
        c = client.get(dungeon_id) or {}
        return c.get("dungeonNameID"), c.get("entryObjectTypeID"), c.get("difficulty")

    # combat anomalies and signatures: the wiki's site tables, matched to the SDE's dungeons by name
    for t in templates:
        if t.get("source") != "eve-university" or t.get("siteFamily") != "combat" or t.get("siteKind") not in ("anomaly", "signature"):
            continue
        matches = names.get(t["resolvedName"].lower())
        if not matches or t.get("gates"):
            continue
        w = waves(t)
        if not w or any(x["query"] not in known_pools for x in w):
            continue
        # the dungeon of the combat archetypes first (24 combat sites, 31 escalations, ...), then the lowest key
        dungeon_id, archetype, faction = sorted(matches, key=lambda m: (m[1] != 24, m[0]))[0]
        name_id, entry, raw = client_facts(dungeon_id)
        bands = t.get("securityBands") or DEFAULT_BANDS["combat"]
        difficulty = int(raw or t.get("difficulty") or middle("combat", bands))
        out[(t["siteKind"], dungeon_id)] = {
            "id": f"{t['siteKind']}-{dungeon_id}", "dungeonId": dungeon_id, "name": t["resolvedName"], "kind": t["siteKind"], "family": "combat",
            "difficulty": max(1, difficulty), "bands": bands, "archetypeId": archetype, "factionId": faction, "dungeonNameId": name_id,
            "entryObjectTypeId": entry, "waves": w, "containers": [],
        }

    # data and relic signatures: the client's dungeon list (the SDE has none of them)
    for t in templates:
        if t.get("source") != "client" or t.get("siteFamily") not in ("data", "relic") or t.get("siteKind") != "signature" or t.get("connections"):
            continue
        c = containers(t)
        w = waves(t)
        if not c or any(x["query"] not in known_pools for x in w):
            continue
        family = t["siteFamily"]
        dungeon_id = t["sourceDungeonID"]
        bands = DEFAULT_BANDS[family]
        out[("signature", dungeon_id)] = {
            "id": f"signature-{dungeon_id}", "dungeonId": dungeon_id, "name": t["resolvedName"], "kind": "signature", "family": family,
            "difficulty": max(1, int(t.get("difficulty") or middle(family, bands))), "bands": bands, "archetypeId": t.get("archetypeID"),
            "factionId": t.get("factionID"), "dungeonNameId": t.get("dungeonNameID"), "entryObjectTypeId": t.get("entryObjectTypeID"), "waves": w, "containers": c,
        }

    ordered = [out[k] for k in sorted(out, key=lambda k: (k[0], k[1]))]
    with open(OUT, "w", encoding="utf-8") as handle:
        json.dump({"templates": ordered}, handle, ensure_ascii=False, indent=1)
        handle.write("\n")
    by = {}
    for t in ordered:
        by[(t["kind"], t["family"])] = by.get((t["kind"], t["family"]), 0) + 1
    print(len(ordered), by)


main()

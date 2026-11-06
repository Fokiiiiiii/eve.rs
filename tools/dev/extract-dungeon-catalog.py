#!/usr/bin/env python3
"""Writes the dungeon catalog the host reads: dotnet/src/Eve.Dungeons/Data/catalog/v1/.

    tools/dev/extract-dungeon-catalog.py <evejs-root> <sde-jsonl-dir> [out-dir]

Input (read only, never executed): the dungeon authority, the client dungeon content overlay, the mining pack, the golden
security policy and the mission authority of an EveJS checkout, plus the official SDE JSONL of build 3405148 for validation.
Output: manifest.json, dungeons-*.json (one record per client dungeon id, split by id range), missions.json (mission ->
killMission dungeon binding and the golden policy) and known-gaps.json (the defects and the ids that have no template).
Every type id, dungeon id and archetype id is checked against the SDE; mismatches are printed and counted in the manifest.
"""

import json
import os
import sys

SCHEMA_DUNGEON = "eve.dungeon-catalog.dungeon.v1"
SCHEMA_MISSIONS = "eve.dungeon-catalog.missions.v1"
SCHEMA_GAPS = "eve.dungeon-catalog.known-gaps.v1"
SCHEMA_MANIFEST = "eve.dungeon-catalog.manifest.v1"
OVERLAY_SCHEMA = "evejs.dungeon-client-content.v1"
SPLIT_SIZE = 4000

HERE = os.path.dirname(os.path.abspath(__file__))
DEFAULT_OUT = os.path.join(HERE, "..", "..", "dotnet", "src", "Eve.Dungeons", "Data", "catalog", "v1")

def load_json(path):
    with open(path, encoding="utf-8") as handle:
        return json.load(handle)

def load_jsonl(path):
    with open(path, encoding="utf-8") as handle:
        return [json.loads(line) for line in handle if line.strip()]

def merge_value(base, overlay):
    # the same rule as the reference's mergeClientContentValue: arrays and scalars are replaced, objects are merged key by key
    if overlay is None:
        return json.loads(json.dumps(base))
    if isinstance(overlay, list):
        return json.loads(json.dumps(overlay))
    if isinstance(overlay, dict):
        base_obj = base if isinstance(base, dict) else {}
        keys = list(base_obj.keys()) + [k for k in overlay.keys() if k not in base_obj]
        return {k: merge_value(base_obj.get(k), overlay.get(k)) for k in keys}
    return overlay

def int_or_none(value):
    return value if isinstance(value, int) and not isinstance(value, bool) else None

def position(value):
    if not isinstance(value, dict):
        return None
    return {"x": value.get("x"), "y": value.get("y"), "z": value.get("z")}

class Validation:
    def __init__(self, type_ids, archetype_ids):
        self.type_ids = type_ids
        self.archetype_ids = archetype_ids
        self.mismatches = {"typeID": [], "archetypeID": []}
        self.singular_not_listed = []
        self.sde_dungeon_archetype = {}
        self.client_only_archetypes = 0

    def type_id(self, value, where):
        if value is None:
            return
        if value not in self.type_ids:
            self.mismatches["typeID"].append((value, where))

    def archetype(self, value, dungeon_id, where):
        if dungeon_id in self.sde_dungeon_archetype:
            if value != self.sde_dungeon_archetype[dungeon_id]:
                self.mismatches["archetypeID"].append((value, where))
        else:
            self.client_only_archetypes += 1

def project_template(dungeon_id, template, check):
    where = f"dungeon {dungeon_id}"
    hints = template.get("populationHints") or {}
    scene = template.get("siteSceneProfile") or {}
    entry_room = template.get("entryRoomKey")
    entry_type = int_or_none(template.get("entryObjectTypeID"))
    check.type_id(entry_type, where + " entryObjectTypeID")
    archetype = int_or_none(template.get("archetypeID"))
    check.archetype(archetype, dungeon_id, where)

    rooms = []
    seen_rooms = set()
    for room in scene.get("roomProfiles") or []:
        key = room.get("roomKey")
        if not key or key in seen_rooms:
            continue
        seen_rooms.add(key)
        entry = key == entry_room or room.get("source") == "derived_entry" or room.get("stage") == "entry"
        rooms.append({"key": key, "roomID": int_or_none(room.get("roomID")), "entry": bool(entry)})

    containers = []
    for box in hints.get("containers") or []:
        containers.append({
            "role": box.get("role"),
            "count": int_or_none(box.get("count")),
            "lootProfile": box.get("lootProfile"),
            "lootTags": [t for t in box.get("lootTags") or [] if isinstance(t, str)],
            "trigger": box.get("trigger"),
            "bonus": box.get("bonus"),
            "failureExplodes": box.get("failureExplodes"),
            "persistsAfterResponse": box.get("persistsAfterResponse"),
        })
    hazards = []
    for hazard in hints.get("hazards") or []:
        if isinstance(hazard, str):
            hazards.append({"kind": hazard})
        else:
            hazards.append({k: v for k, v in hazard.items() if k != "label"})

    gates = []
    for gate in scene.get("gateProfiles") or []:
        type_id = int_or_none(gate.get("typeID"))
        check.type_id(type_id, where + " gate " + str(gate.get("gateKey")))
        gates.append({
            "key": gate.get("gateKey"),
            "typeID": type_id,
            "fromObjectID": int_or_none(gate.get("fromObjectID")),
            "toObjectID": int_or_none(gate.get("toObjectID")),
            "destinationRoomKey": gate.get("destinationRoomKey"),
            "allowedRaces": [r for r in gate.get("allowedRaces") or [] if isinstance(r, int)],
        })

    waves = []
    listed = hints.get("encounters") or []
    singular = hints.get("encounter")
    if singular and singular.get("key") not in {e.get("key") for e in listed}:
        check.singular_not_listed.append(where)
    for enc in listed:
        spawns = []
        for entry in enc.get("spawnEntries") or []:
            type_id = int_or_none(entry.get("typeID"))
            check.type_id(type_id, where + " wave " + str(enc.get("key")))
            spawns.append({"typeID": type_id, "entityKind": entry.get("entityKind")})
        waves.append({
            "key": enc.get("key"),
            "waveIndex": int_or_none(enc.get("waveIndex")),
            "trigger": enc.get("trigger"),
            "roomKey": enc.get("roomKey"),
            "spawnQuery": enc.get("spawnQuery"),
            "amount": int_or_none(enc.get("amount")),
            "deadspace": enc.get("deadspace"),
            "countdownSeconds": int_or_none(enc.get("countdownSeconds")),
            "lootProfile": enc.get("lootProfile"),
            "lootTags": [t for t in enc.get("lootTags") or [] if isinstance(t, str)],
            "spawns": spawns,
        })

    props = []
    for prop in hints.get("environmentProps") or []:
        type_id = int_or_none(prop.get("typeID"))
        if type_id is not None:
            check.type_id(type_id, where + " prop " + str(prop.get("key")))
        names = [n for n in prop.get("typeNameCandidates") or [] if isinstance(n, str)]
        props.append({
            "key": prop.get("key"),
            "typeID": type_id,
            "typeNameCandidates": names,
            "roomKey": prop.get("roomKey"),
            "positionOffset": position(prop.get("positionOffset")),
        })

    mining = []
    for rock in hints.get("miningRocks") or []:
        type_id = int_or_none(rock.get("typeID"))
        ore = int_or_none(rock.get("oreTypeID"))
        check.type_id(type_id, where + " rock")
        check.type_id(ore, where + " rock ore")
        mining.append({
            "key": rock.get("key"),
            "typeID": type_id,
            "oreTypeID": ore,
            "roomKey": rock.get("roomKey"),
            "count": int_or_none(rock.get("count")),
            "quantity": rock.get("quantity"),
            "positionOffset": position(rock.get("positionOffset")),
        })

    loot = []
    for profile in hints.get("lootProfiles") or []:
        loot.append({"key": profile.get("key"), "tags": [t for t in profile.get("tags") or [] if isinstance(t, str)]})

    resources_src = template.get("resourceComposition") or hints.get("resources") or {}
    resources = {}
    for name, field in (("ore", "oreTypeIDs"), ("gas", "gasTypeIDs"), ("ice", "iceTypeIDs")):
        values = [v for v in resources_src.get(field) or [] if isinstance(v, int)]
        for v in values:
            check.type_id(v, where + " resource " + name)
        resources[name] = values

    completion = hints.get("completion") or {}
    return {
        "dungeonID": dungeon_id,
        "archetypeID": archetype,
        "difficulty": int_or_none(template.get("difficulty")),
        "siteFamily": template.get("siteFamily"),
        "siteKind": template.get("siteKind"),
        "entryObjectTypeID": entry_type,
        "entryRoomKey": entry_room,
        "completionMode": completion.get("objectiveMode") if isinstance(completion, dict) else None,
        "resources": resources,
        "rooms": rooms,
        "gates": gates,
        "waves": waves,
        "props": props,
        "containers": containers,
        "hazards": hazards,
        "miningLocks": mining,
        "lootProfiles": loot,
    }

def main(argv):
    if len(argv) < 3:
        sys.exit(__doc__)
    evejs, sde_dir = argv[1], argv[2]
    out_dir = os.path.abspath(argv[3]) if len(argv) > 3 else os.path.abspath(DEFAULT_OUT)

    types = load_jsonl(os.path.join(sde_dir, "types.jsonl"))
    archetypes = load_jsonl(os.path.join(sde_dir, "archetypes.jsonl"))
    sde_missions = {m["_key"]: m for m in load_jsonl(os.path.join(sde_dir, "missions.jsonl"))}
    sde_dungeons = {d["_key"] for d in load_jsonl(os.path.join(sde_dir, "dungeons.jsonl"))}
    epic_members = set()
    for arc in load_jsonl(os.path.join(sde_dir, "epicArcs.jsonl")):
        for mission in arc.get("missions") or []:
            epic_members.add(mission.get("_key"))
    sde_build = None
    for record in load_jsonl(os.path.join(sde_dir, "_sde.jsonl")):
        sde_build = record.get("buildNumber", sde_build)
    check = Validation({t["_key"] for t in types}, {a["_key"] for a in archetypes})
    check.sde_dungeon_archetype = {d["_key"]: d.get("archetypeID") for d in load_jsonl(os.path.join(sde_dir, "dungeons.jsonl"))}

    da = load_json(os.path.join(evejs, "tools/DatabaseCreator/staticTables/dungeonAuthority/data.json"))["templatesByID"]
    overlay = load_json(os.path.join(evejs, "tools/DatabaseCreator/staticTables/dungeonClientContent/data.json"))
    if overlay.get("schema") != OVERLAY_SCHEMA:
        sys.exit("unexpected overlay schema")
    pack = load_json(os.path.join(evejs, "content-packs/evejs-mining-missions-1.0.0.json"))["payload"]["dungeon"]["templatesByID"]
    golden = load_json(os.path.join(evejs, "server/src/config/productionMissionPolicy.json"))["goldenSecurityMissions"]
    missions = load_json(os.path.join(evejs, "tools/DatabaseCreator/staticTables/missionAuthority/data.json"))["missionsByID"]

    effective = {}
    for template_id, template in da.items():
        if not template_id.startswith("client-dungeon:"):
            continue
        dungeon_id = template.get("sourceDungeonID")
        if template_id != f"client-dungeon:{dungeon_id}":
            sys.exit(f"template key mismatch {template_id}")
        effective[dungeon_id] = template
    applied_overlay = 0
    for template_id, template in overlay["templatesByID"].items():
        dungeon_id = template.get("sourceDungeonID")
        if dungeon_id not in effective:
            sys.exit(f"overlay without authority template {template_id}")
        effective[dungeon_id] = merge_value(effective[dungeon_id], template)
        applied_overlay += 1
    pack_added = 0
    for template_id, template in pack.items():
        dungeon_id = template.get("sourceDungeonID")
        if dungeon_id in effective:
            sys.exit(f"pack template {template_id} collides with an authority template")
        effective[dungeon_id] = template
        pack_added += 1

    records = [project_template(d, effective[d], check) for d in sorted(effective)]

    bindings = []
    for mission_id in sorted(sde_missions):
        kill = sde_missions[mission_id].get("killMission")
        if not isinstance(kill, dict) or not isinstance(kill.get("dungeonID"), int):
            continue
        bindings.append({"missionID": mission_id, "dungeonID": kill["dungeonID"]})
    official_binding = {b["missionID"]: b["dungeonID"] for b in bindings}
    defects = []
    no_dungeon = []
    for mission_id in sorted(missions):
        kill = missions[mission_id].get("killMission")
        if kill is None:
            continue
        dungeon = kill.get("dungeonID") if isinstance(kill, dict) else None
        if isinstance(dungeon, int) and not isinstance(dungeon, bool):
            if official_binding.get(int(mission_id)) != dungeon:
                sys.exit(f"mission {mission_id} dungeon {dungeon} disagrees with SDE {official_binding.get(int(mission_id))}")
            continue
        if int(mission_id) in official_binding:
            sys.exit(f"mission {mission_id} has no owner dungeon but SDE has one")
        if isinstance(kill, dict) and "__repr__" in kill:
            defects.append({"missionID": int(mission_id), "reason": "killMission is a stored Python object repr with no dungeonID in the owner data; the official mission has no killMission"})
        elif isinstance(kill, dict):
            no_dungeon.append({"missionID": int(mission_id), "reason": "killMission has an objective quantity but no dungeonID; the official mission has the same record, so the mission has no dungeon"})
        else:
            sys.exit(f"unexpected killMission for mission {mission_id}")

    binding_dungeons = {}
    for b in bindings:
        binding_dungeons.setdefault(b["dungeonID"], []).append(b["missionID"])
    template_ids = {r["dungeonID"] for r in records}
    missing = []
    for dungeon in sorted(binding_dungeons):
        if dungeon not in template_ids:
            missing.append({"dungeonID": dungeon, "missionIDs": sorted(binding_dungeons[dungeon])})

    golden_rows = []
    for g in golden:
        if f"client-dungeon:{g['dungeonID']}" != g.get("templateID"):
            sys.exit(f"golden template id mismatch for mission {g['missionID']}")
        if g["dungeonID"] not in template_ids:
            sys.exit(f"golden dungeon {g['dungeonID']} has no template")
        golden_rows.append({"missionID": g["missionID"], "dungeonID": g["dungeonID"], "agentLevel": g["agentLevel"]})

    epic_no_arc = []
    for mission_id, record in sorted(missions.items()):
        mid = int(mission_id)
        if record.get("missionFlavor") == "epicArc" and mid not in epic_members and mid in sde_missions:
            epic_no_arc.append({"missionID": mid, "reason": "the owner data marks it as an epic arc mission, but no official epic arc (epicArcs.jsonl) lists it"})

    epic_missions = {m for m in epic_members}
    for row in missing:
        in_sde = row["dungeonID"] in sde_dungeons
        epic = any(m in epic_missions for m in row["missionIDs"])
        row["reason"] = ("the official dungeons table lists this id, but the owner data has no client dungeon template for it"
                         if in_sde else "the owner data has no client dungeon template for this id, and the official dungeons table does not list it")
        if epic:
            row["reason"] += "; the mission is an official epic arc step"
    gaps = {
        "schema": SCHEMA_GAPS,
        "missingDungeonTemplates": missing,
        "missionDefects": defects,
        "missionsWithoutDungeon": no_dungeon,
        "epicLikeMissionsWithoutOfficialArc": epic_no_arc,
    }

    os.makedirs(out_dir, exist_ok=True)
    for name in os.listdir(out_dir):
        if name.endswith(".json"):
            os.remove(os.path.join(out_dir, name))
    files = []
    chunks = [records[i:i + SPLIT_SIZE] for i in range(0, len(records), SPLIT_SIZE)]
    for index, chunk in enumerate(chunks):
        name = f"dungeons-{index + 1:02d}.json"
        with open(os.path.join(out_dir, name), "w", encoding="utf-8") as handle:
            json.dump({"schema": SCHEMA_DUNGEON, "dungeons": chunk}, handle, ensure_ascii=False, separators=(",", ":"))
            handle.write("\n")
        files.append(name)
    with open(os.path.join(out_dir, "missions.json"), "w", encoding="utf-8") as handle:
        json.dump({"schema": SCHEMA_MISSIONS, "killMissionDungeons": bindings, "goldenSecurityMissions": golden_rows}, handle, ensure_ascii=False, separators=(",", ":"))
        handle.write("\n")
    with open(os.path.join(out_dir, "known-gaps.json"), "w", encoding="utf-8") as handle:
        json.dump(gaps, handle, ensure_ascii=False, indent=1)
        handle.write("\n")
    manifest = {
        "schema": SCHEMA_MANIFEST,
        "sdeBuild": sde_build,
        "dungeonFiles": files,
        "missionFile": "missions.json",
        "knownGapsFile": "known-gaps.json",
        "counts": {
            "dungeonTemplates": len(records),
            "fromAuthority": len(records) - pack_added,
            "fromMiningPack": pack_added,
            "overlayApplied": applied_overlay,
            "killMissionBindings": len(bindings),
            "missionDungeonIDs": len(binding_dungeons),
            "missionDungeonIDsWithTemplate": len(binding_dungeons) - len(missing),
            "missionDungeonIDsWithoutTemplate": len(missing),
            "goldenSecurityMissions": len(golden_rows),
            "missionDefects": len(defects),
            "missionsWithoutDungeon": len(no_dungeon),
            "epicLikeMissionsWithoutOfficialArc": len(epic_no_arc),
        },
        "validation": {
            "typeIDMismatches": len(check.mismatches["typeID"]),
            "archetypeIDMismatches": len(check.mismatches["archetypeID"]),
            "encounterSingularNotInList": len(check.singular_not_listed),
            "archetypeIDClientOnly": check.client_only_archetypes,
        },
    }
    with open(os.path.join(out_dir, "manifest.json"), "w", encoding="utf-8") as handle:
        json.dump(manifest, handle, indent=1)
        handle.write("\n")
    print(json.dumps(manifest["counts"], indent=1))
    print("typeID mismatches:", len(check.mismatches["typeID"]), "archetype mismatches:", len(check.mismatches["archetypeID"]))
    for value, where in check.mismatches["typeID"][:20]:
        print("  typeID", value, where)
    for value, where in check.mismatches["archetypeID"][:20]:
        print("  archetypeID", value, where)

if __name__ == "__main__":
    main(sys.argv)

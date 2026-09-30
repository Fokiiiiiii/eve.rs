#!/usr/bin/env python3
"""Writes oracle/fixtures/simlink/messages.json: the frames between the host and the simulation of docs/adr/0007, byte for byte.

This is the third writer of the wire format (the Rust simulation and the C# host are the other two); both replay the
file, so a layout change has to be made here first. 64-bit integers are decimal strings in the JSON.
"""
import json
import os
import struct

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "..", "..", "oracle", "fixtures", "simlink", "messages.json")

KINDS = {"Hello": 1, "HelloAck": 2, "Enter": 3, "Leave": 4, "Bind": 5, "UpdateState": 6, "Command": 7,
         "ShipSpec": 8, "Notify": 9, "Docked": 10, "Refused": 11, "Accepted": 12, "EnterReply": 13, "Approaching": 14,
         "Jumping": 15, "Jumped": 16, "Targets": 17, "TargetRefused": 18, "Activate": 19, "Condition": 20,
         "Destroyed": 21, "Spawn": 22, "SpawnReply": 23, "Destroy": 24, "Respawn": 25, "PlaceWreck": 26}


def s(text):  # u16 length + UTF-8
    data = text.encode("utf-8")
    return struct.pack("<H", len(data)) + data


def b(data):  # u32 length + bytes
    return struct.pack("<I", len(data)) + data


def frame(kind, body):
    payload = bytes([KINDS[kind]]) + body
    return struct.pack("<I", len(payload)) + payload


def i64(value):
    return struct.pack("<q", int(value))


def u64(value):
    return struct.pack("<Q", int(value))


def ship(m):
    body = i64(m["shipId"]) + i64(m["characterId"]) + struct.pack("<iii", m["typeId"], m["groupId"], m["categoryId"])
    body += s(m["name"]) + i64(m["ownerId"]) + struct.pack("<iii", m["corporationId"], m["allianceId"], m["warFactionId"])
    body += struct.pack("<12d", m["securityStatus"], m["radius"], m["mass"], m["maxVelocity"], m["agility"],
                        m["warpSpeedAu"], m["shieldRechargeMs"], m["maxTargetRange"], m["scanResolution"],
                        m["signatureRadius"], m["capacitorCapacity"], m["capacitorRechargeS"])
    body += struct.pack("<i", m["maxLockedTargets"])
    body += vitals(m["vitals"]) + condition(m["condition"])
    body += struct.pack("<I", len(m["modules"]))
    for module in m["modules"]:
        body += i64(module["itemId"]) + struct.pack("<ii", module["typeId"], module["flag"])
    return body


def vitals(v):
    r = v["resonances"]
    return struct.pack("<15d", v["shieldCapacity"], v["armorHp"], v["structureHp"], *r["shield"], *r["armor"], *r["structure"])


def condition(c):
    return struct.pack("<4d", c["damage"], c["armorDamage"], c["shieldCharge"], c["charge"])


def numbers(n):
    return struct.pack("<3d", n["mass"], n["maxVelocity"], n["signatureRadius"])


def changes(items):
    return struct.pack("<I", len(items)) + b"".join(
        i64(c["itemId"]) + struct.pack("<id", c["attributeId"], c["value"]) for c in items)


def effect(e):
    body = i64(e["moduleId"]) + struct.pack("<ii", e["moduleType"], e["effectId"]) + s(e["fx"])
    body += struct.pack("<ddi", e["durationMs"], e["capacitorNeed"], e["repeat"]) + i64(e["targetId"])
    on, off = e.get("on"), e.get("off")
    body += bytes([(1 if on else 0) | (2 if off else 0)])
    for n in (on, off):
        if n:
            body += numbers(n)
    return body + changes(e["onChanges"]) + changes(e["offChanges"])


def identity(i):
    body = struct.pack("<qqd", i["hostileResponseThreshold"], i["friendlyResponseThreshold"], i["aggressionRange"])
    body += struct.pack("<I", len(i["weapons"]))
    for w in i["weapons"]:
        body += i64(w["moduleId"]) + struct.pack("<idd", w["effectId"], w["cycleMs"], w["capacitorNeed"])
        body += struct.pack("<i", w["weaponTypeId"]) + s(w["fx"])
        body += struct.pack("<8d", *w["rawDamage"], w["optimalRange"], w["falloff"], w["trackingSpeed"], w["signatureResolution"])
    return body + wreck_kind(i.get("wreck")) + bytes([1 if i.get("hull") else 0])


def wreck_kind(wreck):
    if wreck:
        return bytes([1]) + struct.pack("<i", wreck["typeId"]) + s(wreck["name"]) + struct.pack("<i", wreck["graphicId"])
    return bytes([0])


def build(m):
    k = m["kind"]
    if k == "Hello":
        return frame(k, struct.pack("<H", m["version"]))
    if k == "HelloAck":
        return frame(k, struct.pack("<HI", m["version"], m["pollHz"]))
    if k == "Enter":
        return frame(k, u64(m["sessionId"]) + struct.pack("<iB", m["solarSystemId"], m["how"]) + i64(m["stationId"])
                     + ship(m["ship"]))
    if k == "EnterReply":
        return frame(k, u64(m["sessionId"]) + bytes([0 if m["ok"] else 1]) + s("" if m["ok"] else m["reason"]))
    if k == "Leave":
        return frame(k, u64(m["sessionId"]) + bytes([m["why"]]))
    if k in ("Bind", "UpdateState"):
        return frame(k, u64(m["sessionId"]))
    if k == "Command":
        return frame(k, u64(m["sessionId"]) + bytes([m["command"], len(m["args"])])
                     + b"".join(struct.pack("<d", a) for a in m["args"]))
    if k == "ShipSpec":
        return frame(k, u64(m["sessionId"]) + ship(m["ship"]))
    if k == "Notify":
        return frame(k, u64(m["sessionId"]) + s(m["notification"]) + s(m["idType"]) + b(bytes.fromhex(m["args"])))
    if k == "Docked":
        return frame(k, u64(m["sessionId"]) + i64(m["stationId"]) + condition(m["condition"]))
    if k == "Condition":
        return frame(k, u64(m["sessionId"]) + bytes([m["command"]]) + condition(m["condition"]))
    if k == "Refused":
        return frame(k, u64(m["sessionId"]) + bytes([m["command"]]) + s(m["reason"]))
    if k == "Accepted":
        return frame(k, u64(m["sessionId"]) + bytes([m["command"]]) + i64(m["at"]))
    if k == "Approaching":
        return frame(k, u64(m["sessionId"]) + bytes([m["command"]]) + struct.pack("<d", m["distance"]))
    if k == "Jumping":
        return frame(k, u64(m["sessionId"]) + bytes([m["command"]]) + struct.pack("<i", m["destinationSystem"])
                     + i64(m["destinationGate"]))
    if k == "Activate":
        return frame(k, u64(m["sessionId"]) + effect(m["effect"]))
    if k == "Targets":
        return frame(k, u64(m["sessionId"]) + bytes([m["command"], 1 if m["pending"] else 0])
                     + struct.pack("<I", len(m["targets"])) + b"".join(i64(t) for t in m["targets"]))
    if k == "TargetRefused":
        return frame(k, u64(m["sessionId"]) + bytes([m["command"]]) + s(m["reason"])
                     + struct.pack("<i", m["targetType"]) + struct.pack("<dd", m["desiredRange"], m["actualDistance"]))
    if k == "Jumped":
        return frame(k, u64(m["sessionId"]) + struct.pack("<i", m["fromSystem"]) + i64(m["fromGate"])
                     + struct.pack("<i", m["toSystem"]) + i64(m["toGate"]))
    if k == "Destroyed":
        return frame(k, u64(m["sessionId"]) + i64(m["shipId"]) + i64(m["wreckId"]) + struct.pack("<7d", *m["position"], *m["direction"], m["radius"])
                     + i64(m["killerShipId"]) + wreck_kind(m.get("wreckKind")))
    if k == "Respawn":
        return frame(k, u64(m["sessionId"]) + ship(m["ship"]) + struct.pack("<6d", *m["position"], *m["direction"]))
    if k == "PlaceWreck":
        return frame(k, struct.pack("<i", m["solarSystemId"]) + i64(m["wreckId"]) + wreck_kind(m["wreck"])
                     + struct.pack("<7d", *m["position"], *m["direction"], m["radius"]) + i64(m["launcherId"])
                     + i64(m["characterId"]) + i64(m["corporationId"]) + i64(m["allianceId"]) + i64(m["warFactionId"])
                     + bytes([1 if m["isEmpty"] else 0]))
    if k == "Spawn":
        return frame(k, u64(m["requestId"]) + struct.pack("<i", m["solarSystemId"]) + ship(m["ship"]) + i64(m["anchorShipId"])
                     + struct.pack("<9d", *m["position"], *m["velocity"], *m["direction"]) + s(m["behavior"]) + identity(m["identity"]))
    if k == "SpawnReply":
        return frame(k, u64(m["requestId"]) + (bytes([0]) + u64(m["session"]) if m["ok"] else bytes([1]) + s(m["reason"])))
    if k == "Destroy":
        return frame(k, i64(m["shipId"]) + i64(m["characterId"]) + i64(m["corporationId"]) + i64(m["allianceId"]) + i64(m["warFactionId"]))
    raise ValueError(k)


IBIS = {"shipId": "9988400001939", "characterId": "140000342", "typeId": 601, "groupId": 237, "categoryId": 6,
        "name": "Ibis", "ownerId": "140000342", "corporationId": 1000006, "allianceId": 0, "warFactionId": 0,
        "securityStatus": 0.0, "radius": 47.0, "mass": 1163000.0, "maxVelocity": 339.25, "agility": 4.0185,
        "warpSpeedAu": 3.0, "shieldRechargeMs": 860000.0, "maxTargetRange": 29700.0, "scanResolution": 660.0,
        "signatureRadius": 28.0, "maxLockedTargets": 4, "capacitorCapacity": 149.5, "capacitorRechargeS": 55.25,
        "vitals": {"shieldCapacity": 220.0, "armorHp": 137.5, "structureHp": 201.25,
                   "resonances": {"shield": [0.0, 0.8, 0.6, 0.5], "armor": [0.5, 0.35, 0.25, 0.1],
                                  "structure": [0.33, 0.33, 0.33, 0.33]}},
        "condition": {"damage": 0.35, "armorDamage": 0.6, "shieldCharge": 1.0, "charge": 1.0},
        "modules": [{"itemId": "9988400001940", "typeId": 21857, "flag": 19},
                    {"itemId": "9988400001941", "typeId": 3638, "flag": 27}]}
BARE = dict(IBIS, name="", modules=[], allianceId=99000001, securityStatus=-2.5,
            vitals={"shieldCapacity": 0.0, "armorHp": 0.0, "structureHp": 0.0,
                    "resonances": {"shield": [1.0] * 4, "armor": [1.0] * 4, "structure": [1.0] * 4}},
            condition={"damage": 0.0, "armorDamage": 0.0, "shieldCharge": 1.0, "charge": 1.0})

valid = [
    {"name": "hello", "kind": "Hello", "version": 1},
    {"name": "hello ack", "kind": "HelloAck", "version": 1, "pollHz": 4},
    {"name": "enter by undocking", "kind": "Enter", "sessionId": "1790646377000015", "solarSystemId": 30100032,
     "how": 0, "stationId": "60015249", "ship": IBIS},
    {"name": "enter already in space, no modules, non-ASCII name", "kind": "Enter", "sessionId": "1",
     "solarSystemId": 30000142, "how": 1, "stationId": "0", "ship": dict(BARE, name="イビス")},
    {"name": "enter accepted", "kind": "EnterReply", "sessionId": "1790646377000015", "ok": True},
    {"name": "enter refused", "kind": "EnterReply", "sessionId": "1790646377000015", "ok": False,
     "reason": "UNKNOWN_STATION 60015249"},
    {"name": "leave docked", "kind": "Leave", "sessionId": "1790646377000015", "why": 0},
    {"name": "leave logged off", "kind": "Leave", "sessionId": "1790646377000015", "why": 1},
    {"name": "bind", "kind": "Bind", "sessionId": "1790646377000015"},
    {"name": "update state", "kind": "UpdateState", "sessionId": "1790646377000015"},
    {"name": "command with one argument", "kind": "Command", "sessionId": "1790646377000015", "command": 1,
     "args": [0.5]},
    {"name": "command with three arguments", "kind": "Command", "sessionId": "1790646377000015", "command": 2,
     "args": [-0.8191520442889917, 0.0, 0.5735764363510462]},
    {"name": "command with none", "kind": "Command", "sessionId": "1790646377000015", "command": 3, "args": []},
    {"name": "dock command", "kind": "Command", "sessionId": "1790646377000015", "command": 4, "args": [60015249.0]},
    {"name": "follow command", "kind": "Command", "sessionId": "1790646377000015", "command": 5,
     "args": [60015249.0, 2500.0]},
    {"name": "orbit command", "kind": "Command", "sessionId": "1790646377000015", "command": 6,
     "args": [40009076.0, 5000.0]},
    {"name": "align command", "kind": "Command", "sessionId": "1790646377000015", "command": 7,
     "args": [60015249.0]},
    {"name": "warp command", "kind": "Command", "sessionId": "1790646377000015", "command": 8,
     "args": [40491233.0, 30000.0]},
    {"name": "jump command", "kind": "Command", "sessionId": "1790646377000015", "command": 9,
     "args": [50016630.0, 50016839.0]},
    {"name": "add target command", "kind": "Command", "sessionId": "1790646377000015", "command": 10,
     "args": [9988400002151.0]},
    {"name": "cancel add target command", "kind": "Command", "sessionId": "1790646377000015", "command": 11,
     "args": [9988400002151.0]},
    {"name": "remove targets command", "kind": "Command", "sessionId": "1790646377000015", "command": 12,
     "args": [9988400002151.0, 60015249.0]},
    {"name": "clear targets command", "kind": "Command", "sessionId": "1790646377000015", "command": 13,
     "args": []},
    {"name": "get targets command", "kind": "Command", "sessionId": "1790646377000015", "command": 14, "args": []},
    {"name": "get targeters command", "kind": "Command", "sessionId": "1790646377000015", "command": 15, "args": []},
    {"name": "deactivate command", "kind": "Command", "sessionId": "1790646377000015", "command": 16,
     "args": [9988400001941.0]},
    {"name": "new ship numbers", "kind": "ShipSpec", "sessionId": "1790646377000015", "ship": IBIS},
    {"name": "notification", "kind": "Notify", "sessionId": "1790646377000015", "notification": "OnSetTimeDilation",
     "idType": "clientID", "args": "7e0000000004010100"},
    {"name": "notification without arguments", "kind": "Notify", "sessionId": "1", "notification": "DoDestinyUpdate",
     "idType": "clientID", "args": ""},
    {"name": "heal command", "kind": "Command", "sessionId": "1790646377000015", "command": 18, "args": []},
    {"name": "get condition command", "kind": "Command", "sessionId": "1790646377000015", "command": 19, "args": []},
    {"name": "docked", "kind": "Docked", "sessionId": "1790646377000015", "stationId": "60015249",
     "condition": {"damage": 0.35, "armorDamage": 0.6, "shieldCharge": 0.917525, "charge": 0.65}},
    {"name": "condition", "kind": "Condition", "sessionId": "1790646377000015", "command": 19,
     "condition": {"damage": 0.0, "armorDamage": 0.18, "shieldCharge": 0.7, "charge": 0.65}},
    {"name": "refused", "kind": "Refused", "sessionId": "1790646377000015", "command": 4,
     "reason": "UNKNOWN_TARGET"},
    {"name": "accepted", "kind": "Accepted", "sessionId": "1790646377000015", "command": 4,
     "at": "134351527136350000"},
    {"name": "a movement command accepted", "kind": "Accepted", "sessionId": "1790646377000015", "command": 5,
     "at": "134351527136350000"},
    {"name": "approaching", "kind": "Approaching", "sessionId": "1790646377000015", "command": 4,
     "distance": 15975.3},
    {"name": "jumping", "kind": "Jumping", "sessionId": "1790646377000015", "command": 9,
     "destinationSystem": 30100034, "destinationGate": "50016839"},
    {"name": "activate an afterburner", "kind": "Activate", "sessionId": "1790646377000015",
     "effect": {"moduleId": "9988400001940", "moduleType": 21857, "effectId": 6731, "fx": "effects.Afterburner",
                "durationMs": 10000.0, "capacitorNeed": 5.0, "repeat": 1000, "targetId": "0",
                "on": {"mass": 1663000.0, "maxVelocity": 522.848918, "signatureRadius": 52.0},
                "off": {"mass": 1163000.0, "maxVelocity": 339.25, "signatureRadius": 52.0},
                "onChanges": [{"itemId": "9988400001940", "attributeId": 20, "value": 60.0},
                              {"itemId": "9988400001939", "attributeId": 37, "value": 522.848918}],
                "offChanges": [{"itemId": "9988400001940", "attributeId": 20, "value": 60.0},
                               {"itemId": "9988400001939", "attributeId": 37, "value": 339.25}]}},
    {"name": "activate an effect that changes nothing of the ship", "kind": "Activate", "sessionId": "1",
     "effect": {"moduleId": "9988400001941", "moduleType": 3651, "effectId": 67, "fx": "", "durationMs": 3000.0,
                "capacitorNeed": 0.0, "repeat": 1, "targetId": "9988400002151", "onChanges": [], "offChanges": []}},
    {"name": "targets with a lock going", "kind": "Targets", "sessionId": "1790646377000015", "command": 10,
     "pending": True, "targets": ["60015249"]},
    {"name": "targets none", "kind": "Targets", "sessionId": "1790646377000015", "command": 14, "pending": False,
     "targets": []},
    {"name": "target refused out of range", "kind": "TargetRefused", "sessionId": "1790646377000015", "command": 10,
     "reason": "TARGET_OUT_OF_RANGE", "targetType": 92885, "desiredRange": 29700.0, "actualDistance": 306288156879.55023},
    {"name": "target refused without figures", "kind": "TargetRefused", "sessionId": "1790646377000015", "command": 10,
     "reason": "TARGET_SELF", "targetType": 0, "desiredRange": 0.0, "actualDistance": 0.0},
    {"name": "jumped", "kind": "Jumped", "sessionId": "1790646377000015", "fromSystem": 30100032,
     "fromGate": "50016630", "toSystem": 30100034, "toGate": "50016839"},
    {"name": "destroyed with a wreck of the scene's own", "kind": "Destroyed", "sessionId": "4611686018427387905",
     "shipId": "980050000077", "wreckId": "4300000000000001", "position": [311989835983.1308, -13069154020.916222, 156646308895.76187],
     "direction": [0.6159, 0.01306, 0.78769], "radius": 47.0, "killerShipId": "9988400001939",
     "wreckKind": {"typeId": 26506, "name": "Caldari Frigate Wreck", "graphicId": 3117}},
    {"name": "destroyed player's ship, nobody to blame, no wreck", "kind": "Destroyed", "sessionId": "1790646377000015",
     "shipId": "9988400001939", "wreckId": "0", "position": [0.0, 0.0, 0.0], "direction": [1.0, 0.0, 0.0],
     "radius": 47.0, "killerShipId": "0", "wreckKind": {"typeId": 26506, "name": "Caldari Frigate Wreck", "graphicId": 0}},
    {"name": "destroyed with no kind of wreck to leave", "kind": "Destroyed", "sessionId": "1790646377000016",
     "shipId": "9988400001940", "wreckId": "0", "position": [1.0, 2.0, 3.0], "direction": [0.0, 1.0, 0.0],
     "radius": 12.5, "killerShipId": "0"},
    {"name": "spawn of an NPC with a laser, a wreck and a tree", "kind": "Spawn", "requestId": "7", "solarSystemId": 30100032,
     "ship": dict(BARE, shipId="980050000077", characterId="0", ownerId="1000125", categoryId=11, groupId=25, typeId=597),
     "anchorShipId": "0", "position": [1000500.0, 100.0, 100.0], "velocity": [0.0, 0.0, 0.0], "direction": [1.0, 0.0, 0.0], "behavior": "belt rat",
     "identity": {"hostileResponseThreshold": -11, "friendlyResponseThreshold": 11, "aggressionRange": 15000.0,
                  "weapons": [{"moduleId": "980050000077", "effectId": 10, "cycleMs": 4000.0, "capacitorNeed": 0.0,
                               "weaponTypeId": 5678, "fx": "effects.Laser", "rawDamage": [0.0, 0.0, 6.0, 4.0],
                               "optimalRange": 20000.0, "falloff": 10000.0, "trackingSpeed": 0.4, "signatureResolution": 40.0}],
                  "wreck": {"typeId": 26506, "name": "Caldari Frigate Wreck", "graphicId": 3117}}},
    {"name": "spawn of a bare NPC: no tree, no weapons, no wreck", "kind": "Spawn", "requestId": "8", "solarSystemId": 30000142,
     "ship": dict(BARE, shipId="980050000078", characterId="0"), "anchorShipId": "9988400001939", "position": [1.0, 2.0, 3.0], "velocity": [4.0, 5.0, 6.0],
     "direction": [0.0, 0.0, 1.0], "behavior": "",
     "identity": {"hostileResponseThreshold": -11, "friendlyResponseThreshold": 11, "aggressionRange": 0.0, "weapons": [], "hull": True}},
    {"name": "spawn done", "kind": "SpawnReply", "requestId": "7", "ok": True, "session": "4611686018427387905"},
    {"name": "spawn refused", "kind": "SpawnReply", "requestId": "8", "ok": False, "reason": "NO_SCENE 30000142"},
    {"name": "destroy for a pilot to loot", "kind": "Destroy", "shipId": "980050000077", "characterId": "140000001",
     "corporationId": "1000006", "allianceId": "0", "warFactionId": "500001"},
    {"name": "respawn of a capsule where the ship was", "kind": "Respawn", "sessionId": "1790646377000015",
     "ship": dict(IBIS, shipId="9988400002416", typeId=670, groupId=29, name="Capsule", modules=[]),
     "position": [311989835983.1308, -13069154020.916222, 156646308895.76187], "direction": [0.6159, 0.01306, 0.78769]},
    {"name": "wreck the host made, to put in space", "kind": "PlaceWreck", "solarSystemId": 30100032,
     "wreckId": "9988400002417", "wreck": {"typeId": 26506, "name": "Caldari Frigate Wreck", "graphicId": 3117},
     "position": [311989835983.1308, -13069154020.916222, 156646308895.76187], "direction": [0.6159, 0.01306, 0.78769],
     "radius": 47.0, "launcherId": "9988400002411", "characterId": "140000394", "corporationId": "1000006",
     "allianceId": "0", "warFactionId": "500001", "isEmpty": False},
    {"name": "wreck of no kind of the reference's graphic", "kind": "PlaceWreck", "solarSystemId": 30000142,
     "wreckId": "9988400002420", "wreck": {"typeId": 26507, "name": "Amarr Frigate Wreck", "graphicId": 0},
     "position": [0.0, 0.0, 0.0], "direction": [1.0, 0.0, 0.0], "radius": 30.0, "launcherId": "9988400002400",
     "characterId": "0", "corporationId": "0", "allianceId": "0", "warFactionId": "0", "isEmpty": True},
    {"name": "destroy for nobody", "kind": "Destroy", "shipId": "4200000000000001", "characterId": "0",
     "corporationId": "0", "allianceId": "0", "warFactionId": "0"},
]
for case in valid:
    case["hex"] = build(case).hex()

# each of these is refused for the stated reason
good = build(valid[2])  # a good Enter
invalid = [
    {"name": "unknown kind", "reason": "unknown-kind", "hex": "01000000ff"},
    {"name": "kind zero", "reason": "unknown-kind", "hex": "0100000000"},
    {"name": "empty frame", "reason": "empty", "hex": "00000000"},
    {"name": "hello truncated", "reason": "truncated", "hex": "02000000" + "0101"},
    {"name": "hello trailing bytes", "reason": "trailing", "hex": "04000000" + "01" + "0100" + "00"},
    {"name": "enter with an unknown way in", "reason": "bad-value",
     "hex": (good[:4] + good[4:17] + bytes([7]) + good[18:]).hex()},
    {"name": "enter cut short", "reason": "truncated", "hex": (struct.pack("<I", 20) + good[4:24]).hex()},
    {"name": "enter reply with an unknown status", "reason": "bad-value",
     "hex": (struct.pack("<I", 12) + bytes([13]) + u64(1) + bytes([2]) + s("")).hex()},
    {"name": "text that is not UTF-8", "reason": "utf8",
     "hex": (struct.pack("<I", 1 + 8 + 1 + 2 + 1) + bytes([11]) + u64(1) + bytes([4]) + struct.pack("<H", 1) + b"\xff").hex()},
    {"name": "command that promises more arguments than it carries", "reason": "truncated",
     "hex": (struct.pack("<I", 1 + 8 + 2) + bytes([7]) + u64(1) + bytes([1, 2])).hex()},
]

os.makedirs(os.path.dirname(OUT), exist_ok=True)
with open(OUT, "w", encoding="utf-8") as f:
    json.dump({"note": "Generated by tools/dev/gen-simlink-fixtures.py. Frames include their u32 length prefix.",
               "valid": valid, "invalid": invalid}, f, indent=1, ensure_ascii=False)
    f.write("\n")
print(f"{len(valid)} valid, {len(invalid)} invalid -> {OUT}")

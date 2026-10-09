#!/usr/bin/env python3
"""Writes oracle/fixtures/simlink/messages.json: the frames between the host and the simulation of docs/design/adr/0007, byte for byte.

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
         "Destroyed": 21, "Spawn": 22, "SpawnReply": 23, "Destroy": 24, "Respawn": 25, "PlaceWreck": 26, "ChargeSpent": 27,
         "PlaceContainer": 28, "ContainerPlaced": 29, "ChangeWreck": 30, "RemoveWreck": 31, "Measured": 32,
         "AdjustCharge": 33, "ChargeAdjusted": 34, "Arrived": 35, "LaunchDrone": 36, "DroneCommand": 37, "DroneResults": 38,
         "DroneScooped": 39, "DroneReleased": 40, "ModuleWorkCycle": 51, "ModuleWorkResult": 52, "ModuleEnded": 53, "LaunchFighter": 54, "FighterOrder": 55, "FighterResults": 56, "FighterLanded": 57, "FighterReleased": 58, "FighterGotoPoint": 59, "FighterSize": 60, "UpdateEffect": 61,
         "EcmBurst": 62}


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
    body += struct.pack("<6d", *m["sensorStrength"], m["ecmResistance"], m["energyWarfareResistance"])
    return body


def vitals(v):
    r = v["resonances"]
    # the armor's pin: a fitted Polarized weapon holds the armor's resonances at one (one byte, 0 or 1)
    return (struct.pack("<15d", v["shieldCapacity"], v["armorHp"], v["structureHp"], *r["shield"], *r["armor"], *r["structure"])
            + bytes([int(v.get("armorPinned", False))]))


def condition(c):
    return struct.pack("<4d", c["damage"], c["armorDamage"], c["shieldCharge"], c["charge"])


def numbers(n):
    return struct.pack("<3d", n["mass"], n["maxVelocity"], n["signatureRadius"])


def changes(items):
    return struct.pack("<I", len(items)) + b"".join(
        i64(c["itemId"]) + struct.pack("<id", c["attributeId"], c["value"]) for c in items)


def dot_bytes(d):
    # a breacher pod's damage over time (protocol 42): a flag, then its duration, its cap per tick and its percentage per tick
    return bytes([1]) + struct.pack("<3d", d["durationMs"], d["maxDamagePerTick"], d["maxHpPercentPerTick"]) if d else bytes([0])


def turret(t):
    body = struct.pack("<i", t["weaponTypeId"]) + s(t["fx"])
    body += struct.pack("<8d", *t["rawDamage"], t["optimalRange"], t["falloff"], t["trackingSpeed"], t["signatureResolution"])
    c = t.get("charge")
    # the charge's reloads byte (protocol 38, FP-C8): the gun reloads from its hold when its rounds run out
    body += bytes([0]) if not c else bytes([1]) + struct.pack("<iiqq", c["typeId"], c["flag"], c["rounds"], c["perCycle"]) + bytes([1 if c.get("reloads") else 0])
    m = t.get("missile")
    body += bytes([0]) if not m else (bytes([1]) + struct.pack("<ii", m["chargeTypeId"], m["groupId"]) + s(m["name"])
                                      + struct.pack("<10d", m["radius"], m["mass"], m["agility"], m["hitPoints"], m["maxVelocity"], m["flightTimeMs"],
                                                    m["explosionRadius"], m["explosionVelocity"], m["damageReductionFactor"], m["damageReductionSensitivity"]) + dot_bytes(m.get("dot")))
    # a precursor weapon's damage ramp: the bonus a cycle adds and the most it reaches (absent for the others)
    p = t.get("spool")
    body += bytes([1]) + struct.pack("<2d", p["perCycle"], p["max"]) if p else bytes([0])
    # a vorton's explosion (its cloud and velocity, the damage reduction) and its arc (the range from the target, the ships in all)
    a = t.get("aoe")
    body += bytes([1]) + struct.pack("<4d", a["explosionRadius"], a["explosionVelocity"], a["damageReductionFactor"],
                                     a["damageReductionSensitivity"]) if a else bytes([0])
    c = t.get("arc")
    body += bytes([1]) + struct.pack("<di", c["range"], c["targets"]) if c else bytes([0])
    # whether the weapon requires Gunnery: a tracking disruptor or computer reaches only such a weapon (FP-10)
    return body + bytes([1 if t.get("requiresGunnery", False) else 0])


LAYERS = {"shield": 0, "armor": 1, "structure": 2}
FAMILIES = {"ore": 0, "ice": 1, "gas": 2}


def deployable(d):
    return struct.pack("<ii", d["chargeTypeId"], d["groupId"]) + s(d["name"]) + struct.pack("<d", d["radius"])


def action(a):
    kind = a["kind"]
    if kind == "smartbomb":
        return bytes([2]) + struct.pack("<d", a["range"]) + struct.pack("<4d", *a["damage"]) + bytes([1 if a["hurtsPilots"] else 0])
    if kind == "repair":
        return bytes([0, LAYERS[a["layer"]], 1 if a["when"] == "end" else 0]) + struct.pack("<d", a["amount"])
    if kind == "ecmBurst":
        return bytes([3]) + struct.pack("<d", a["range"]) + struct.pack("<4d", *a["strength"])
    # the SimAction tag 5 (protocol 34): a cloak and whether it is warp-safe (a covert ops cloak carries the ship into warp; a standard one holds it)
    if kind == "cloak":
        return bytes([5, 1 if a.get("warpSafe") else 0])
    # the SimAction tags 16 to 20 (protocol 34): a warp disruption bubble on the ship, the immunity of a nullifier, a stabilizer's scramble status, an interdiction
    # probe in space, and a bomb that goes off where it is (kind 0 damage, 1 ECM, 2 neutralisation); a deployable's ball is its charge, its group, its name and radius
    if kind == "warpBubble":
        return bytes([16]) + struct.pack("<d", a["range"])
    if kind == "warpImmunity":
        return bytes([17])
    if kind == "scrambleStatus":
        return bytes([18]) + struct.pack("<d", a["delta"])
    # the SimAction tag 22 (protocol 34): a defender launcher's cycle, its defender missile (the charge's flight, as a turret's missile is) and four damages
    if kind == "defender":
        m = a["missile"]
        return (bytes([22]) + struct.pack("<ii", m["chargeTypeId"], m["groupId"]) + s(m["name"])
                + struct.pack("<10d", m["radius"], m["mass"], m["agility"], m["hitPoints"], m["maxVelocity"], m["flightTimeMs"],
                              m["explosionRadius"], m["explosionVelocity"], m["damageReductionFactor"], m["damageReductionSensitivity"])
                + dot_bytes(None) + struct.pack("<4d", *a["damage"]))
    if kind == "stasisBurst":
        return bytes([21]) + struct.pack("<3d", a["range"], a["durationMs"], a["speedFactor"])
    if kind == "warpProbe":
        return bytes([19]) + deployable(a["ball"]) + struct.pack("<2d", a["range"], a["lifetimeMs"])
    # a cynosural field (tag 27): the field's ball and its lifetime (the field type's explosionDelay); it is lit at the end of the generator's cycle
    if kind == "cynoField":
        return bytes([27]) + deployable(a["ball"]) + struct.pack("<d", a["lifetimeMs"])
    if kind == "bomb":
        return (bytes([20]) + deployable(a["ball"]) + bytes([a["bombKind"]]) + struct.pack("<2d", a["lifetimeMs"], a["blastRadius"])
                + struct.pack("<4d", *a["damage"]) + struct.pack("<4d", *a["ecm"]) + struct.pack("<d", a["neutralize"]))
    # the SimAction tags 7-11 (ADR 0013): a mining laser's cycle, a salvager's, and three effects whose packages have not landed (refused at activation)
    if kind == "mining":
        return bytes([7, FAMILIES[a["family"]]]) + struct.pack("<5d", a["miningAmountM3"], a["wasteMultiplier"], a["wasteProbability"], a["critProbability"], a["critMultiplier"])
    if kind == "salvage":
        return bytes([8]) + struct.pack("<d", a["accessBonus"])
    # the SimAction tag 26 (protocol 40): a scanner's cycle, a cargo (0) or ship (1) scan, and its optimal range and falloff (metres; no falloff for a cargo scan)
    if kind == "scan":
        return bytes([26, 0 if a["cargo"] else 1]) + struct.pack("<2d", a["optimalMeters"], a["falloffMeters"])
    # the SimAction tag 9 (protocol 30): the beam's speed, the hold distance, and the module's reach (the range a target is out of reach past)
    if kind == "tractor":
        return bytes([9]) + struct.pack("<3d", a["velocity"], a["hold"], a["range"])
    if kind == "passiveLock":
        return bytes([10])
    if kind == "autoTarget":
        return bytes([11]) + struct.pack("<d", a["range"])
    # the SimAction tag 13 (ADR 0013 epoch 25): a doomsday's strike, an area hit pulse by pulse after a warning, with the windows of the ship that fires it
    if kind == "strike":
        return (bytes([13, a["shape"]]) + struct.pack("<3d", *a["direction"]) + struct.pack("<4d", a["length"], a["radius"], a["warningMs"], a["pulseMs"])
                + struct.pack("<i", a["pulses"]) + struct.pack("<4d", *a["damage"])
                + struct.pack("<6d", a["signatureRadius"], a["immobileMs"], a["noJumpMs"], a["neutAmount"], a["neutRadius"], a["neutSignatureRadius"])
                # protocol 28: the disruption a pulse leaves (ms), then a sweep: a flag and the end of the axis (zeros without a sweep)
                + struct.pack("<d", a.get("debuffMs", 0.0))
                + bytes([1 if a.get("sweepEnd") else 0]) + struct.pack("<3d", *(a.get("sweepEnd") or [0.0, 0.0, 0.0]))
                # protocol 39: a lance's beacon, a flag then its offset from the firing ship (zeros without a beacon)
                + bytes([1 if a.get("beacon") else 0]) + struct.pack("<3d", *(a.get("beacon") or [0.0, 0.0, 0.0])))
    # the SimAction tag 14 (ADR 0013 epoch 26): a titan doomsday, one hit on the locked target after a delay, the ship held for its windows
    if kind == "doomsday":
        return bytes([14]) + struct.pack("<d", a["delayMs"]) + struct.pack("<4d", *a["damage"]) + struct.pack("<2d", a["immobileMs"], a["noJumpMs"])
    # the SimAction tag 23 (protocol 36): a kamikaze squadron's detonation, its damage (four kinds) and the range it detonates at (metres)
    if kind == "kamikaze":
        return bytes([23]) + struct.pack("<4d", *a["damage"]) + struct.pack("<d", a["rangeMeters"])
    # the SimAction tag 24 (protocol 36): a fighter's missiles: the damage (four kinds), the reach (metres) and the explosion (radius, velocity, reduction factor,
    # reduction sensitivity), scaled by the application factor for the target
    if kind == "missile":
        return bytes([24]) + struct.pack("<4d", *a["damage"]) + struct.pack("<5d", a["reachMeters"], a["explosionRadius"], a["explosionVelocity"],
                                                                              a["reductionFactor"], a["reductionSensitivity"])
    # the SimAction tag 25 (protocol 37): a fighter's LaunchBomb blast: the centre (metres), the radius (metres) and the four damages (EM, thermal, kinetic, explosive)
    if kind == "blast":
        return bytes([25]) + struct.pack("<3d", *a["centre"]) + struct.pack("<d", a["radiusMeters"]) + struct.pack("<4d", *a["damage"])
    # the SimAction tag 15 (protocol 29): the Gravitational Transportation Field Oscillator's field, a sphere strike (shape 4) whose body follows the tag, then the
    # offset of the destination from the firing ship, the warp block of the held capitals (ms), and the ship ids moved and held (a u32 count, then each as i64)
    if kind == "field":
        strike = action(dict(a["strike"], kind="strike"))[1:]
        ids = lambda values: struct.pack("<I", len(values)) + b"".join(struct.pack("<q", v) for v in values)
        return (bytes([15]) + strike + struct.pack("<3d", *a["destination"]) + struct.pack("<d", a["warpBlockMs"])
                + ids(a["moved"]) + ids(a["held"]))
    if kind == "resistanceShift":
        # a reactive armor hardener: the seed (em, thermal, kinetic, explosive), the most a kind gives up a cycle, the attributes it tells
        return bytes([12]) + struct.pack("<4d", *a["seed"]) + struct.pack("<d", a["shift"]) + struct.pack("<4i", *a["attributeIds"])
    if kind == "charged":
        body = bytes([4]) + struct.pack("<iiqq", a["chargeTypeId"], a["chargeFlag"], a["chargePerCycle"], a["charges"])
        body += action(a["action"])
        fallback = a.get("fallback")
        body += bytes([1 if fallback else 0])
        if fallback:
            body += action(fallback)
        return body

    what = a["what"]
    body = bytes([1, 1 if a["when"] == "end" else 0]) + struct.pack("<dd", a["optimal"], a["falloff"]) + s(a["jam"])
    body += bytes([{"repair": 0, "transfer": 1, "neutralize": 2, "drain": 3, "web": 4, "paint": 5, "damp": 6, "scramble": 7, "jam": 8, "disrupt": 9, "guidanceDisrupt": 10}[what]])
    if what == "repair":
        body += bytes([LAYERS[a["layer"]]])
    if what in ("repair", "transfer", "neutralize", "drain"):
        body += struct.pack("<d", a["amount"])
    if what in ("neutralize", "drain"):
        body += struct.pack("<d", a["signatureResolution"])
    if what in ("web", "paint"):
        body += struct.pack("<d", a["percent"])
    if what == "damp":
        body += struct.pack(
            "<6d",
            a["rangePercent"], a["resolutionPercent"],
            a.get("gravimetricStrengthPercent", 0.0), a.get("ladarStrengthPercent", 0.0),
            a.get("magnetometricStrengthPercent", 0.0), a.get("radarStrengthPercent", 0.0))
    if what == "scramble":
        body += struct.pack("<d", a["strength"]) + bytes([1 if a["blocksMwd"] else 0])
    if what == "jam":
        body += struct.pack("<4d", *a["strength"])
    if what == "disrupt":
        body += struct.pack("<ddd", a["rangePercent"], a["falloffPercent"], a["trackingPercent"])
    if what == "guidanceDisrupt":
        body += struct.pack("<dddd", a["velocityPercent"], a["delayPercent"], a["aoeVelocityPercent"], a["aoeCloudPercent"])
    return body


def effect(e):
    body = i64(e["moduleId"]) + struct.pack("<ii", e["moduleType"], e["effectId"]) + s(e["fx"])
    body += struct.pack("<ddi", e["durationMs"], e["capacitorNeed"], e["repeat"]) + i64(e["targetId"])
    on, off, gun, act, rules = e.get("on"), e.get("off"), e.get("turret"), e.get("action"), e.get("rules")
    # bit 16: the effect burns fuel (the host is asked for the fuel of each cycle that starts, ModuleWorkCycle with command 9); bit 32: the module rules (protocol 33)
    body += bytes([(1 if on else 0) | (2 if off else 0) | (4 if gun else 0) | (8 if act else 0) | (16 if e.get("burnsFuel") else 0) | (32 if rules else 0)])
    for n in (on, off):
        if n:
            body += numbers(n)
    body += changes(e["onChanges"]) + changes(e["offChanges"])
    body = body + turret(gun) if gun else body
    if act:
        body += action(act)
    if rules:
        # the module rules, after the action: the group and its limit (763), the rule bits (1 early deactivation 906, 2 needs an active industrial core 3265,
        # 4 is an industrial core), and the reactivation delay (669, ms)
        bits = (1 if rules["disallowEarlyDeactivation"] else 0) | (2 if rules["requiresIndustrialCore"] else 0) | (4 if rules["providesIndustrialCore"] else 0)
        body += struct.pack("<iiBddd", rules["groupId"], rules["maxGroupActive"], bits, rules["reactivationDelayMs"], rules.get("maxVelocityLimit", 0.0), rules.get("minVelocityLimit", 0.0))
    return body


def weapon(w):
    return i64(w["moduleId"]) + struct.pack("<idd", w["effectId"], w["cycleMs"], w["capacitorNeed"]) + turret(w)


# what the host sends for a drone with no weapon: no module, no turret
EMPTY_WEAPON = {"moduleId": 0, "effectId": 0, "cycleMs": 0.0, "capacitorNeed": 0.0, "weaponTypeId": 0, "fx": "", "requiresGunnery": False,
                "rawDamage": [0.0, 0.0, 0.0, 0.0], "optimalRange": 0.0, "falloff": 0.0, "trackingSpeed": 0.0,
                "signatureResolution": 0.0}


def identity(i):
    body = struct.pack("<qqd", i["hostileResponseThreshold"], i["friendlyResponseThreshold"], i["aggressionRange"])
    body += struct.pack("<I", len(i["weapons"]))
    for w in i["weapons"]:
        body += weapon(w)
    return body + wreck_kind(i.get("wreck")) + bytes([1 if i.get("hull") else 0, 1 if i.get("hostWreck") else 0])


def wreck_kind(wreck):
    if wreck:
        return bytes([1]) + struct.pack("<i", wreck["typeId"]) + s(wreck["name"]) + struct.pack("<i", wreck["graphicId"])
    return bytes([0])


def build(m):
    k = m["kind"]
    if k == "Hello":
        return frame(k, struct.pack("<H", m["version"]))
    if k == "ModuleWorkCycle":
        # a mining laser's or a salvager's cycle: the numbers, then the waste, rounding and crit rolls the simulation drew
        return frame(k, u64(m["sessionId"]) + i64(m["characterId"]) + i64(m["shipId"]) + struct.pack("<i", m["solarSystemId"])
                     + i64(m["moduleId"]) + i64(m["targetId"]) + u64(m["cycleId"]) + bytes([m["command"]])
                     + struct.pack("<9d", m["miningAmountM3"], m["wasteMultiplier"], m["wasteProbability"], m["critProbability"],
                                   m["critMultiplier"], m["accessDifficultyBonus"], m["wasteRoll"], m["roundingRoll"], m["critRoll"]))
    if k == "ModuleWorkResult":
        return frame(k, u64(m["sessionId"]) + i64(m["moduleId"]) + i64(m["targetId"]) + u64(m["cycleId"])
                     + bytes([1 if m["accepted"] else 0]) + s(m["reason"]) + i64(m["unitsRemaining"]) + i64(m["deliveredUnits"])
                     + bytes([1 if m["complete"] else 0]))
    if k == "ModuleEnded":
        return frame(k, u64(m["sessionId"]) + i64(m["moduleId"]) + bytes([m["reason"]]))
    if k == "HelloAck":
        return frame(k, struct.pack("<HI", m["version"], m["pollHz"]))
    if k == "Enter":
        return frame(k, u64(m["sessionId"]) + struct.pack("<iB", m["solarSystemId"], m["how"]) + i64(m["stationId"])
                       + ship(m["ship"])
                       + (b"\x01" + struct.pack("<3d", *m["position"]) + b"\x01" + struct.pack("<3d", *m["direction"]) if m["how"] == 1 else b""))
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
    if k == "ChargeSpent":
        return frame(k, u64(m["sessionId"]) + i64(m["moduleId"]) + i64(m["roundsLeft"]))
    # protocol 38 (FP-C4b, FP-C5): a refresh of a running effect: the cycle, the capacitor a cycle costs, the turret (a flag, then the turret) and the changes
    if k == "UpdateEffect":
        t = m.get("turret")
        return frame(k, u64(m["sessionId"]) + i64(m["moduleId"]) + struct.pack("<2d", m["durationMs"], m["capacitorNeed"])
                     + (bytes([0]) if not t else bytes([1]) + turret(t)) + changes(m["changes"]))
    if k == "AdjustCharge":
        add = m["op"] == "add"
        return frame(k, u64(m["requestId"]) + u64(m["sessionId"]) + i64(m["moduleId"]) + struct.pack("<i", m["chargeType"])
                     + bytes([0 if add else 1]) + i64(m["capacity"] if add else m["quantity"]) + i64(m["available"] if add else "0"))
    if k == "ChargeAdjusted":
        return frame(k, u64(m["requestId"]) + bytes([1 if m["running"] else 0]) + i64(m["before"]) + i64(m["after"]))
    if k == "Respawn":
        return frame(k, u64(m["sessionId"]) + ship(m["ship"]) + struct.pack("<6d", *m["position"], *m["direction"]))
    if k == "PlaceWreck":
        return frame(k, struct.pack("<i", m["solarSystemId"]) + i64(m["wreckId"]) + struct.pack("<i", m["groupId"]) + wreck_kind(m["wreck"])
                     + struct.pack("<7d", *m["position"], *m["direction"], m["radius"]) + i64(m["launcherId"])
                     + i64(m["characterId"]) + i64(m["corporationId"]) + i64(m["allianceId"]) + i64(m["warFactionId"])
                     + bytes([1 if m["isEmpty"] else 0]))
    if k == "PlaceContainer":
        return frame(k, u64(m["sessionId"]) + i64(m["containerId"]) + wreck_kind(m["container"]) + struct.pack("<d", m["radius"]) + i64(m["launcherId"])
                     + i64(m["characterId"]) + i64(m["corporationId"]) + i64(m["allianceId"]) + i64(m["warFactionId"]) + struct.pack("<d", m["distance"]))
    if k == "ContainerPlaced":
        return frame(k, u64(m["sessionId"]) + i64(m["containerId"]) + struct.pack("<6d", *m["position"], *m["direction"]))
    if k == "Arrived":
        return frame(k, u64(m["sessionId"]) + i64(m["beltId"]) + struct.pack("<3d", *m["position"]))
    if k == "ChangeWreck":
        return frame(k, u64(m["sessionId"]) + i64(m["wreckId"]) + bytes([1 if m["isEmpty"] else 0]))
    if k == "RemoveWreck":
        return frame(k, i64(m["wreckId"]) + struct.pack("<i", m["effect"]))
    if k == "Measured":
        return frame(k, u64(m["sessionId"]) + bytes([m["command"]]) + (bytes([1]) + struct.pack("<d", m["distance"]) if m.get("distance") is not None else bytes([0])))
    if k == "Spawn":
        return frame(k, u64(m["requestId"]) + struct.pack("<i", m["solarSystemId"]) + ship(m["ship"]) + i64(m["anchorShipId"])
                     + struct.pack("<9d", *m["position"], *m["velocity"], *m["direction"]) + s(m["behavior"]) + identity(m["identity"]))
    if k == "SpawnReply":
        return frame(k, u64(m["requestId"]) + (bytes([0]) + u64(m["session"]) if m["ok"] else bytes([1]) + s(m["reason"])))
    if k == "LaunchDrone":
        # the work profile (mining, salvage) is zero for a drone that does neither; the remote effect (a web, a repair, a jam)
        # is a flag, then the effect's id, its name in the destiny stream, its cycle and how long it lands, and the action it does
        work = struct.pack("<5d", m.get("miningAmountM3", 0.0), m.get("workCycleMs", 0.0), m.get("miningWasteMultiplier", 0.0),
                           m.get("miningWasteProbability", 0.0), m.get("salvageAccessBonus", 0.0))
        e = m.get("effect")
        remote = (bytes([1]) + struct.pack("<i", e["effectId"]) + s(e["fx"]) + struct.pack("<dd", e["cycleMs"], e["lastingMs"]) + action(e["action"])
                  if e else bytes([0]))
        # a drone without a weapon still carries the empty weapon record (module 0, no turret), as the host sends it
        weapon_record = weapon(m["weapon"]) if m.get("weapon") else weapon(EMPTY_WEAPON)
        body = (u64(m["requestId"]) + u64(m["sessionId"]) + ship(m["ship"]) + weapon_record
                + struct.pack("<iddd", m["graphicId"], m["orbitDistance"], m["attackRange"], m["cruiseFraction"]) + work + remote)
        return frame(k, body)
    if k == "DroneCommand":
        return frame(k, u64(m["sessionId"]) + bytes([m["command"]]) + i64(m["targetId"]) + struct.pack("<I", len(m["droneIds"]))
                     + b"".join(i64(d) for d in m["droneIds"]) + struct.pack("<d", m["controlRange"]))
    if k == "DroneResults":
        return frame(k, u64(m["sessionId"]) + bytes([m["command"]]) + struct.pack("<I", len(m["results"]))
                     + b"".join(i64(r["droneId"]) + s(r["reason"]) for r in m["results"]))
    if k == "DroneScooped":
        return frame(k, u64(m["sessionId"]) + i64(m["droneId"]))
    if k == "DroneReleased":
        return frame(k, u64(m["sessionId"]) + i64(m["droneId"]) + struct.pack("<3d", *m["position"]))
    if k == "LaunchFighter":
        # the squadron's ship, the graphic of its type, and its cruise fraction (no weapon, no work, no effect: a squadron is not a drone)
        return frame(k, u64(m["requestId"]) + u64(m["sessionId"]) + ship(m["ship"])
                     + struct.pack("<idi", m["graphicId"], m["cruiseFraction"], m["squadronSize"]))
    if k == "FighterOrder":
        return frame(k, u64(m["sessionId"]) + bytes([m["command"]]) + i64(m["targetId"]) + struct.pack("<I", len(m["fighterIds"]))
                     + b"".join(i64(f) for f in m["fighterIds"]) + struct.pack("<d", m["range"]))
    if k == "FighterSize":
        return frame(k, u64(m["sessionId"]) + i64(m["fighterId"]) + struct.pack("<i", m["size"]))
    if k == "FighterGotoPoint":
        return frame(k, u64(m["sessionId"]) + struct.pack("<I", len(m["fighterIds"]))
                     + b"".join(i64(f) for f in m["fighterIds"]) + struct.pack("<3d", *m["point"]))
    if k == "FighterResults":
        return frame(k, u64(m["sessionId"]) + bytes([m["command"]]) + struct.pack("<I", len(m["results"]))
                     + b"".join(i64(r["fighterId"]) + s(r["reason"]) for r in m["results"]))
    if k == "FighterLanded":
        return frame(k, u64(m["sessionId"]) + i64(m["fighterId"]))
    if k == "FighterReleased":
        return frame(k, u64(m["sessionId"]) + i64(m["fighterId"]) + struct.pack("<3d", *m["position"]))
    # protocol 39 (FP-C7): an ECM burst's ship and each ship it reached, with whether its roll jammed it (a flag byte, 0 or 1)
    if k == "EcmBurst":
        return frame(k, u64(m["sessionId"]) + i64(m["shipId"]) + struct.pack("<I", len(m["reached"]))
                     + b"".join(i64(r["shipId"]) + bytes([1 if r["jammed"] else 0]) for r in m["reached"]))
    if k == "Destroy":
        return frame(k, i64(m["shipId"]) + i64(m["characterId"]) + i64(m["corporationId"]) + i64(m["allianceId"]) + i64(m["warFactionId"]))
    raise ValueError(k)


IBIS = {"shipId": "9988400001939", "characterId": "140000342", "typeId": 601, "groupId": 237, "categoryId": 6,
        "name": "Ibis", "ownerId": "140000342", "corporationId": 1000006, "allianceId": 0, "warFactionId": 0,
        "securityStatus": 0.0, "radius": 47.0, "mass": 1163000.0, "maxVelocity": 339.25, "agility": 4.0185,
        "warpSpeedAu": 3.0, "shieldRechargeMs": 860000.0, "maxTargetRange": 29700.0, "scanResolution": 660.0,
        "signatureRadius": 28.0, "maxLockedTargets": 4, "capacitorCapacity": 149.5, "capacitorRechargeS": 55.25,
        "sensorStrength": [0.0, 0.0, 0.0, 6.0], "ecmResistance": 1.0, "energyWarfareResistance": 0.25,
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
    {"name": "hello", "kind": "Hello", "version": 42},
    {"name": "hello ack", "kind": "HelloAck", "version": 42, "pollHz": 4},
    {"name": "enter by undocking", "kind": "Enter", "sessionId": "1790646377000015", "solarSystemId": 30100032,
     "how": 0, "stationId": "60015249", "ship": IBIS},
    {"name": "enter already in space, no modules, non-ASCII name", "kind": "Enter", "sessionId": "1",
     "solarSystemId": 30000142, "how": 1, "stationId": "0", "ship": dict(BARE, name="イビス"),
     "position": [100.0, 200.0, 300.0], "direction": [0.0, 0.0, 1.0]},
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
    {"name": "prime login ballpark visuals", "kind": "Command", "sessionId": "1790646377000015", "command": 24, "args": []},
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
    {"name": "activate an afterburner with its propulsion group's limit", "kind": "Activate", "sessionId": "1790646377000015",
     "effect": {"moduleId": "9988400001940", "moduleType": 21857, "effectId": 6731, "fx": "effects.Afterburner",
                "durationMs": 10000.0, "capacitorNeed": 5.0, "repeat": 1000, "targetId": "0",
                "onChanges": [], "offChanges": [],
                "rules": {"groupId": 46, "maxGroupActive": 1, "disallowEarlyDeactivation": False, "requiresIndustrialCore": False,
                          "providesIndustrialCore": False, "reactivationDelayMs": 0.0}}},
    {"name": "activate a compressor that needs a running industrial core, with a cloak's early deactivation and reactivation", "kind": "Activate",
     "sessionId": "1790646377000015",
     "effect": {"moduleId": "9988400001950", "moduleType": 4174, "effectId": 6731, "fx": "", "durationMs": 300000.0, "capacitorNeed": 0.0,
                "repeat": 1, "targetId": "0", "onChanges": [], "offChanges": [],
                "rules": {"groupId": 4174, "maxGroupActive": 0, "disallowEarlyDeactivation": True, "requiresIndustrialCore": True,
                          "providesIndustrialCore": False, "reactivationDelayMs": 180000.0}}},
    {"name": "activate a cynosural field generator, which starts up to 500 m/s (WP19)", "kind": "Activate", "sessionId": "1790646377000015",
     "effect": {"moduleId": "9988400001951", "moduleType": 21096, "effectId": 6731, "fx": "", "durationMs": 60000.0, "capacitorNeed": 0.0,
                "repeat": 1, "targetId": "0", "onChanges": [], "offChanges": [],
                "rules": {"groupId": 0, "maxGroupActive": 0, "disallowEarlyDeactivation": True, "requiresIndustrialCore": False,
                          "providesIndustrialCore": False, "reactivationDelayMs": 0.0, "maxVelocityLimit": 500.0, "minVelocityLimit": 0.0}}},
    {"name": "activate a bomb launcher, which starts only while moving (WP19)", "kind": "Activate", "sessionId": "1790646377000015",
     "effect": {"moduleId": "9988400001952", "moduleType": 4256, "effectId": 6731, "fx": "", "durationMs": 10000.0, "capacitorNeed": 0.0,
                "repeat": 1, "targetId": "0", "onChanges": [], "offChanges": [],
                "rules": {"groupId": 0, "maxGroupActive": 0, "disallowEarlyDeactivation": False, "requiresIndustrialCore": False,
                          "providesIndustrialCore": False, "reactivationDelayMs": 0.0, "maxVelocityLimit": 0.0, "minVelocityLimit": 1.0}}},
    {"name": "activate an effect that changes nothing of the ship", "kind": "Activate", "sessionId": "1",
     "effect": {"moduleId": "9988400001941", "moduleType": 3651, "effectId": 67, "fx": "", "durationMs": 3000.0,
                "capacitorNeed": 0.0, "repeat": 1, "targetId": "9988400002151", "onChanges": [], "offChanges": []}},
    {"name": "activate a gun at a rat", "kind": "Activate", "sessionId": "1790646377000015",
     "effect": {"moduleId": "9988400002665", "moduleType": 3638, "effectId": 10, "fx": "effects.Laser",
                "durationMs": 1692.8, "capacitorNeed": 0.9, "repeat": 1000, "targetId": "980050000105",
                "turret": {"weaponTypeId": 3638, "requiresGunnery": True, "fx": "effects.Laser", "rawDamage": [0.0, 2.5, 3.75, 0.0],
                           "optimalRange": 5280.0, "falloff": 2000.0, "trackingSpeed": 0.5, "signatureResolution": 40000.0},
                "onChanges": [{"itemId": "9988400002665", "attributeId": 6, "value": 0.9},
                              {"itemId": "9988400002665", "attributeId": 51, "value": 1692.8},
                              {"itemId": "9988400002665", "attributeId": 54, "value": 5280.0}],
                "offChanges": [{"itemId": "9988400002665", "attributeId": 6, "value": 0.9},
                               {"itemId": "9988400002665", "attributeId": 51, "value": 1692.8},
                               {"itemId": "9988400002665", "attributeId": 54, "value": 5280.0}]}},
    {"name": "activate an armor repairer", "kind": "Activate", "sessionId": "1790646377000015",
     "effect": {"moduleId": "9988400002801", "moduleType": 523, "effectId": 27, "fx": "effects.ArmorRepair",
                "durationMs": 6000.0, "capacitorNeed": 20.0, "repeat": 1000, "targetId": "0",
                "action": {"kind": "repair", "layer": "armor", "amount": 69.0, "when": "end"},
                "onChanges": [{"itemId": "9988400002801", "attributeId": 6, "value": 20.0},
                              {"itemId": "9988400002801", "attributeId": 73, "value": 6000.0}],
                "offChanges": [{"itemId": "9988400002801", "attributeId": 6, "value": 20.0},
                               {"itemId": "9988400002801", "attributeId": 73, "value": 6000.0}]}},
    {"name": "activate a shield booster", "kind": "Activate", "sessionId": "1790646377000015",
     "effect": {"moduleId": "9988400002802", "moduleType": 399, "effectId": 4, "fx": "effects.ShieldBoosting",
                "durationMs": 2000.0, "capacitorNeed": 20.0, "repeat": 1000, "targetId": "0",
                "action": {"kind": "repair", "layer": "shield", "amount": 26.0, "when": "start"},
                "onChanges": [], "offChanges": []}},
    {"name": "activate a remote armor repairer", "kind": "Activate", "sessionId": "1790646377000015",
     "effect": {"moduleId": "9988400002803", "moduleType": 3520, "effectId": 6186, "fx": "effects.RemoteArmorRepair",
                "durationMs": 5000.0, "capacitorNeed": 12.0, "repeat": 1000, "targetId": "9988400003001",
                "action": {"kind": "remote", "what": "repair", "layer": "armor", "amount": 66.0, "optimal": 7500.0,
                           "falloff": 5000.0, "when": "end", "jam": "remoteArmorRepair"},
                "onChanges": [], "offChanges": []}},
    {"name": "activate an energy neutralizer", "kind": "Activate", "sessionId": "1790646377000015",
     "effect": {"moduleId": "9988400002804", "moduleType": 12076, "effectId": 6187, "fx": "effects.EnergyNeutralizer",
                "durationMs": 6000.0, "capacitorNeed": 30.0, "repeat": 1000, "targetId": "9988400003001",
                "action": {"kind": "remote", "what": "neutralize", "amount": 90.0, "signatureResolution": 8000.0,
                           "optimal": 5000.0, "falloff": 3000.0, "when": "start", "jam": "ewEnergyNeut"},
                "onChanges": [], "offChanges": []}},
    {"name": "activate an energy nosferatu", "kind": "Activate", "sessionId": "1790646377000015",
     "effect": {"moduleId": "9988400002805", "moduleType": 12263, "effectId": 6188, "fx": "effects.EnergyVampire",
                "durationMs": 5000.0, "capacitorNeed": 0.0, "repeat": 1000, "targetId": "9988400003001",
                "action": {"kind": "remote", "what": "drain", "amount": 12.0, "signatureResolution": 0.0,
                           "optimal": 6000.0, "falloff": 2000.0, "when": "end", "jam": "ewEnergyVampire"},
                "onChanges": [], "offChanges": []}},
    {"name": "activate a remote capacitor transmitter", "kind": "Activate", "sessionId": "1790646377000015",
     "effect": {"moduleId": "9988400002806", "moduleType": 6586, "effectId": 6184, "fx": "effects.EnergyTransfer",
                "durationMs": 5000.0, "capacitorNeed": 28.0, "repeat": 1000, "targetId": "9988400003001",
                "action": {"kind": "remote", "what": "transfer", "amount": 60.0, "optimal": 7000.0,
                           "falloff": 5000.0, "when": "start", "jam": "energyTransfer"},
                "onChanges": [], "offChanges": []}},
    {"name": "activate a stasis webifier", "kind": "Activate", "sessionId": "1790646377000015",
     "effect": {"moduleId": "9988400002807", "moduleType": 526, "effectId": 6426, "fx": "effects.ModifyTargetSpeed",
                "durationMs": 5000.0, "capacitorNeed": 5.0, "repeat": 1000, "targetId": "9988400003001",
                "action": {"kind": "remote", "what": "web", "percent": -50.0, "optimal": 10000.0, "falloff": 0.0,
                           "when": "start", "jam": "webify"},
                "onChanges": [], "offChanges": []}},
    {"name": "activate a target painter", "kind": "Activate", "sessionId": "1790646377000015",
     "effect": {"moduleId": "9988400002808", "moduleType": 12709, "effectId": 6425, "fx": "effects.TargetPaint",
                "durationMs": 5000.0, "capacitorNeed": 10.0, "repeat": 1000, "targetId": "9988400003001",
                "action": {"kind": "remote", "what": "paint", "percent": 25.0, "optimal": 30000.0, "falloff": 75000.0,
                           "when": "start", "jam": "ewTargetPaint"},
                "onChanges": [], "offChanges": []}},
    {"name": "activate a remote sensor dampener", "kind": "Activate", "sessionId": "1790646377000015",
     "effect": {"moduleId": "9988400002809", "moduleType": 1968, "effectId": 6422, "fx": "effects.SensorDampening",
                "durationMs": 10000.0, "capacitorNeed": 30.0, "repeat": 1000, "targetId": "9988400003001",
                "action": {"kind": "remote", "what": "damp", "rangePercent": -13.7, "resolutionPercent": -13.7,
                           "optimal": 25000.0, "falloff": 50000.0, "when": "start", "jam": "ewRemoteSensorDamp"},
                "onChanges": [], "offChanges": []}},
    {"name": "activate a remote sensor booster", "kind": "Activate", "sessionId": "1790646377000015",
     "effect": {"moduleId": "9988400002811", "moduleType": 1963, "effectId": 6427, "fx": "effects.RemoteSensorBoost",
                "durationMs": 10000.0, "capacitorNeed": 30.0, "repeat": 1000, "targetId": "9988400003001",
                "action": {"kind": "remote", "what": "damp", "rangePercent": 10.0, "resolutionPercent": 15.0,
                           "gravimetricStrengthPercent": 20.0, "ladarStrengthPercent": 25.0,
                           "magnetometricStrengthPercent": 30.0, "radarStrengthPercent": 35.0,
                           "optimal": 25000.0, "falloff": 50000.0, "when": "start", "jam": "remoteSensorBoost"},
                "onChanges": [], "offChanges": []}},
    {"name": "activate a warp scrambler", "kind": "Activate", "sessionId": "1790646377000015",
     "effect": {"moduleId": "9988400002810", "moduleType": 447, "effectId": 5934, "fx": "effects.WarpScramble",
                "durationMs": 5000.0, "capacitorNeed": 5.0, "repeat": 1000, "targetId": "9988400003001",
                "action": {"kind": "remote", "what": "scramble", "strength": 2.0, "blocksMwd": True, "optimal": 7500.0,
                           "falloff": 0.0, "when": "start", "jam": "warpScramblerMWD"},
                "onChanges": [], "offChanges": []}},
    {"name": "activate a smartbomb", "kind": "Activate", "sessionId": "1790646377000015",
     "effect": {"moduleId": "9988400002813", "moduleType": 1563, "effectId": 38, "fx": "effects.EmpWave",
                "durationMs": 10000.0, "capacitorNeed": 30.0, "repeat": 1000, "targetId": "0",
                "action": {"kind": "smartbomb", "range": 3000.0, "damage": [50.0, 0.0, 0.0, 0.0], "hurtsPilots": True},
                "onChanges": [], "offChanges": []}},
    {"name": "activate a cloak that is not warp-safe", "kind": "Activate", "sessionId": "1790646377000015",
     "effect": {"moduleId": "9988400002813", "moduleType": 11577, "effectId": 197, "fx": "effects.Cloaking",
                "durationMs": 10000.0, "capacitorNeed": 30.0, "repeat": 1000, "targetId": "0",
                "action": {"kind": "cloak", "warpSafe": False}, "onChanges": [], "offChanges": []}},
    {"name": "activate a field generator's bubble", "kind": "Activate", "sessionId": "1790646377000015",
     "effect": {"moduleId": "9988400002813", "moduleType": 28654, "effectId": 1,
                "fx": "effects.WarpDisruptFieldGenerating",
                "durationMs": 30000.0, "capacitorNeed": 150.0, "repeat": 1000, "targetId": "0",
                "action": {"kind": "warpBubble", "range": 16000.0}, "onChanges": [], "offChanges": []}},
    {"name": "activate a nullifier's immunity", "kind": "Activate", "sessionId": "1790646377000015",
     "effect": {"moduleId": "9988400002813", "moduleType": 58919, "effectId": 1, "fx": "",
                "durationMs": 10000.0, "capacitorNeed": 0.0, "repeat": 1, "targetId": "0",
                "action": {"kind": "warpImmunity"}, "onChanges": [], "offChanges": []}},
    {"name": "activate a stabilizer's scramble status", "kind": "Activate", "sessionId": "1790646377000015",
     "effect": {"moduleId": "9988400002813", "moduleType": 10998, "effectId": 1, "fx": "",
                "durationMs": 10000.0, "capacitorNeed": 0.0, "repeat": 1, "targetId": "0",
                "action": {"kind": "scrambleStatus", "delta": -2.0}, "onChanges": [], "offChanges": []}},
    {"name": "activate a defender launcher", "kind": "Activate", "sessionId": "1790646377000015",
     "effect": {"moduleId": "9988400002813", "moduleType": 44102, "effectId": 1, "fx": "",
                "durationMs": 10000.0, "capacitorNeed": 50.0, "repeat": 1, "targetId": "0",
                "action": {"kind": "defender", "missile": {"chargeTypeId": 32782, "groupId": 88, "name": "Defender Missile I", "radius": 1.0,
                           "mass": 1.0, "agility": 1.0, "hitPoints": 5.0, "maxVelocity": 10000.0, "flightTimeMs": 3000.0,
                           "explosionRadius": 10.0, "explosionVelocity": 400.0, "damageReductionFactor": 0.1, "damageReductionSensitivity": 5.5},
                           "damage": [200.0, 200.0, 200.0, 200.0]},
                "onChanges": [], "offChanges": []}},
    {"name": "activate a stasis webification burst", "kind": "Activate", "sessionId": "1790646377000015",
     "effect": {"moduleId": "9988400002813", "moduleType": 22782, "effectId": 1, "fx": "effects.TorpedoDeployment",
                "durationMs": 10000.0, "capacitorNeed": 30.0, "repeat": 1, "targetId": "0",
                "action": {"kind": "stasisBurst", "range": 15000.0, "durationMs": 30000.0, "speedFactor": -20.0},
                "onChanges": [], "offChanges": []}},
    {"name": "activate an interdiction probe", "kind": "Activate", "sessionId": "1790646377000015",
     "effect": {"moduleId": "9988400002813", "moduleType": 22782, "effectId": 1, "fx": "effects.TorpedoDeployment",
                "durationMs": 10000.0, "capacitorNeed": 30.0, "repeat": 1, "targetId": "0",
                "action": {"kind": "warpProbe", "ball": {"chargeTypeId": 22778, "groupId": 548, "name": "Warp Disrupt Probe", "radius": 40.0}, "range": 20000.0, "lifetimeMs": 120000.0},
                "onChanges": [], "offChanges": []}},
    {"name": "activate a bomb", "kind": "Activate", "sessionId": "1790646377000015",
     "effect": {"moduleId": "9988400002813", "moduleType": 4256, "effectId": 1, "fx": "effects.MissileDeployment",
                "durationMs": 10000.0, "capacitorNeed": 30.0, "repeat": 1, "targetId": "0",
                "action": {"kind": "bomb", "ball": {"chargeTypeId": 27920, "groupId": 90, "name": "Electron Bomb", "radius": 40.0}, "bombKind": 0, "lifetimeMs": 12000.0, "blastRadius": 400.0,
                           "damage": [5800.0, 0.0, 0.0, 0.0], "ecm": [0.0, 0.0, 0.0, 0.0], "neutralize": 0.0},
                "onChanges": [], "offChanges": []}},
    {"name": "activate a cynosural field generator (a field of 21094, lit at the end of its cycle, ten minutes)", "kind": "Activate", "sessionId": "1790646377000015",
     "effect": {"moduleId": "9988400002813", "moduleType": 21096, "effectId": 2857, "fx": "effects.CynoGenerator",
                "durationMs": 600000.0, "capacitorNeed": 0.0, "repeat": 1, "targetId": "0",
                "action": {"kind": "cynoField", "ball": {"chargeTypeId": 21094, "groupId": 2020, "name": "Cynosural Field", "radius": 250.0}, "lifetimeMs": 600000.0},
                "onChanges": [], "offChanges": []}},
    {"name": "activate an ECM burst", "kind": "Activate", "sessionId": "1790646377000015",
     "effect": {"moduleId": "9988400002813", "moduleType": 580, "effectId": 38, "fx": "effects.EcmBurstJammer",
                "durationMs": 10000.0, "capacitorNeed": 30.0, "repeat": 1000, "targetId": "0",
                "action": {"kind": "ecmBurst", "range": 3000.0, "strength": [6.0, 6.0, 6.0, 6.0]},
                "onChanges": [], "offChanges": []}},
    {"name": "activate a charged ancillary repair", "kind": "Activate", "sessionId": "1790646377000015",
     "effect": {"moduleId": "9988400002813", "moduleType": 580, "effectId": 38, "fx": "effects.EcmBurstJammer",
                "durationMs": 10000.0, "capacitorNeed": 30.0, "repeat": 1000, "targetId": "0",
                "action": {"kind": "charged", "chargeTypeId": 91001, "chargeFlag": 27,
                           "chargePerCycle": 1, "charges": 2,
                           "action": {"kind": "repair", "layer": "armor", "amount": 10.0, "when": "end"},
                           "fallback": {"kind": "repair", "layer": "armor", "amount": 5.0, "when": "end"}},
                "onChanges": [], "offChanges": []}},
    {"name": "activate a tracking disruptor", "kind": "Activate", "sessionId": "1790646377000015",
     "effect": {"moduleId": "9988400002812", "moduleType": 2108, "effectId": 6424, "fx": "effects.TrackingDisruption",
                "durationMs": 10000.0, "capacitorNeed": 15.0, "repeat": 1000, "targetId": "9988400003001",
                "action": {"kind": "remote", "what": "disrupt", "rangePercent": -15.3, "falloffPercent": -15.3,
                           "trackingPercent": -15.3, "optimal": 40000.0, "falloff": 20000.0, "when": "start",
                           "jam": "ewTrackingDisrupt"},
                "onChanges": [], "offChanges": []}},
    {"name": "activate a guidance disruptor", "kind": "Activate", "sessionId": "1790646377000015",
     "effect": {"moduleId": "9988400002810", "moduleType": 5321, "effectId": 6423, "fx": "effects.ElectronicAttributeModifyTarget",
                "durationMs": 10000.0, "capacitorNeed": 15.0, "repeat": 1000, "targetId": "9988400003001",
                "action": {"kind": "remote", "what": "guidanceDisrupt", "velocityPercent": -8.5, "delayPercent": -8.5,
                           "aoeVelocityPercent": -11.0, "aoeCloudPercent": 11.0, "optimal": 40000.0, "falloff": 20000.0,
                           "when": "start", "jam": "ewGuidanceDisrupt"},
                "onChanges": [], "offChanges": []}},
    {"name": "activate an ECM jammer", "kind": "Activate", "sessionId": "1790646377000015",
     "effect": {"moduleId": "9988400002811", "moduleType": 1957, "effectId": 6470, "fx": "effects.ElectronicAttributeModifyTarget",
                "durationMs": 20000.0, "capacitorNeed": 40.0, "repeat": 1000, "targetId": "9988400003001",
                "action": {"kind": "remote", "what": "jam", "strength": [3.6, 0.0, 0.0, 0.0], "optimal": 20000.0,
                           "falloff": 20000.0, "when": "start", "jam": ""},
                "onChanges": [], "offChanges": []}},
    {"name": "activate a railgun that fires antimatter", "kind": "Activate", "sessionId": "1790646377000015",
     "effect": {"moduleId": "9988400002719", "moduleType": 10678, "effectId": 34, "fx": "effects.ProjectileFired",
                "durationMs": 2750.8, "capacitorNeed": 1.3545, "repeat": 1000, "targetId": "980050000108",
                "turret": {"weaponTypeId": 10678, "requiresGunnery": True, "fx": "effects.ProjectileFired", "rawDamage": [0.0, 9.05, 12.67, 0.0],
                           "optimalRange": 5940.0, "falloff": 5250.0, "trackingSpeed": 0.098, "signatureResolution": 40000.0,
                           "charge": {"typeId": 222, "flag": 28, "rounds": 80, "perCycle": 1}},
                "onChanges": [{"itemId": "9988400002719", "attributeId": 6, "value": 1.3545},
                              {"itemId": "9988400002719", "attributeId": 51, "value": 2750.8},
                              {"itemId": "9988400002719", "attributeId": 54, "value": 5940.0}],
                "offChanges": [{"itemId": "9988400002719", "attributeId": 6, "value": 1.3545},
                               {"itemId": "9988400002719", "attributeId": 51, "value": 2750.8},
                               {"itemId": "9988400002719", "attributeId": 54, "value": 5940.0}]}},
    {"name": "activate a rocket launcher that fires scourge rockets", "kind": "Activate", "sessionId": "1790646377000015",
     "effect": {"moduleId": "9988400003564", "moduleType": 10629, "effectId": 101, "fx": "effects.MissileDeployment",
                "durationMs": 4900.0, "capacitorNeed": 0.0, "repeat": 1000, "targetId": "980050000111",
                "turret": {"weaponTypeId": 10629, "requiresGunnery": False, "fx": "effects.MissileDeployment", "rawDamage": [0.0, 0.0, 38.115, 0.0],
                           "optimalRange": 4500.0, "falloff": 0.0, "trackingSpeed": 0.0, "signatureResolution": 40000.0,
                           "charge": {"typeId": 266, "flag": 28, "rounds": 3, "perCycle": 1},
                           "missile": {"chargeTypeId": 266, "groupId": 387, "name": "Scourge Rocket", "radius": 300.0, "mass": 100.0,
                                       "agility": 5.220125693558398, "hitPoints": 90.0, "maxVelocity": 2250.0, "flightTimeMs": 2000.0,
                                       "explosionRadius": 20.0, "explosionVelocity": 150.0, "damageReductionFactor": 0.644,
                                       "damageReductionSensitivity": 5.5}},
                "onChanges": [{"itemId": "9988400003564", "attributeId": 51, "value": 4900.0}],
                "offChanges": [{"itemId": "9988400003564", "attributeId": 51, "value": 4900.0}]}},
    {"name": "activate a breacher pod launcher whose pod starts a damage over time", "kind": "Activate", "sessionId": "1790646377000015",
     "effect": {"moduleId": "9988400003600", "moduleType": 85084, "effectId": 101, "fx": "effects.MissileDeployment",
                "durationMs": 20000.0, "capacitorNeed": 0.0, "repeat": 1000, "targetId": "980050000111",
                "turret": {"weaponTypeId": 85084, "requiresGunnery": False, "fx": "effects.MissileDeployment", "rawDamage": [0.0, 0.0, 0.0, 0.0],
                           "optimalRange": 4000.0, "falloff": 0.0, "trackingSpeed": 0.0, "signatureResolution": 40000.0,
                           "charge": {"typeId": 85088, "flag": 28, "rounds": 4, "perCycle": 1},
                           "missile": {"chargeTypeId": 85088, "groupId": 4808, "name": "SCARAB Breacher Pod S", "radius": 1.0, "mass": 1.0,
                                       "agility": 1.0, "hitPoints": 0.0, "maxVelocity": 2000.0, "flightTimeMs": 2000.0,
                                       "explosionRadius": 35.0, "explosionVelocity": 2000.0, "damageReductionFactor": 1.0,
                                       "damageReductionSensitivity": 5.5,
                                       "dot": {"durationMs": 40000.0, "maxDamagePerTick": 200.0, "maxHpPercentPerTick": 0.6}}},
                "onChanges": [{"itemId": "9988400003600", "attributeId": 51, "value": 20000.0}],
                "offChanges": [{"itemId": "9988400003600", "attributeId": 51, "value": 20000.0}]}},
    {"name": "activate a precursor weapon that builds its damage up", "kind": "Activate", "sessionId": "1790646377000015",
     "effect": {"moduleId": "9988400002720", "moduleType": 47272, "effectId": 6995, "fx": "effects.Laser",
                "durationMs": 3500.0, "capacitorNeed": 4.5, "repeat": 1000, "targetId": "980050000108",
                "turret": {"weaponTypeId": 47272, "requiresGunnery": True, "fx": "effects.Laser", "rawDamage": [0.0, 0.0, 2.5, 0.0],
                           "optimalRange": 6000.0, "falloff": 0.0, "trackingSpeed": 410.0, "signatureResolution": 40000.0,
                           "spool": {"perCycle": 0.07, "max": 2.125}},
                "onChanges": [{"itemId": "9988400002720", "attributeId": 6, "value": 4.5},
                              {"itemId": "9988400002720", "attributeId": 51, "value": 3500.0},
                              {"itemId": "9988400002720", "attributeId": 54, "value": 6000.0}],
                "offChanges": [{"itemId": "9988400002720", "attributeId": 6, "value": 4.5},
                               {"itemId": "9988400002720", "attributeId": 51, "value": 3500.0},
                               {"itemId": "9988400002720", "attributeId": 54, "value": 6000.0}]}},
    {"name": "activate a vorton projector whose explosion needs no roll and whose arc reaches ten ships", "kind": "Activate",
     "sessionId": "1790646377000015",
     "effect": {"moduleId": "9988400002721", "moduleType": 54739, "effectId": 8037, "fx": "effects.AttackMode",
                "durationMs": 6000.0, "capacitorNeed": 7.0, "repeat": 1000, "targetId": "980050000108",
                "turret": {"weaponTypeId": 54739, "requiresGunnery": False, "fx": "effects.AttackMode", "rawDamage": [116.0, 0.0, 17.0, 0.0],
                           "optimalRange": 22464.0, "falloff": 0.0, "trackingSpeed": 0.0, "signatureResolution": 40000.0,
                           "charge": {"typeId": 54769, "flag": 11, "rounds": 1000, "perCycle": 1},
                           "aoe": {"explosionRadius": 43.0, "explosionVelocity": 190.0, "damageReductionFactor": 0.4,
                                   "damageReductionSensitivity": 5.5},
                           "arc": {"range": 10000.0, "targets": 10}},
                "onChanges": [{"itemId": "9988400002721", "attributeId": 6, "value": 7.0},
                              {"itemId": "9988400002721", "attributeId": 51, "value": 6000.0},
                              {"itemId": "9988400002721", "attributeId": 54, "value": 22464.0}],
                "offChanges": [{"itemId": "9988400002721", "attributeId": 6, "value": 7.0},
                               {"itemId": "9988400002721", "attributeId": 51, "value": 6000.0},
                               {"itemId": "9988400002721", "attributeId": 54, "value": 22464.0}]}},
    {"name": "rounds spent", "kind": "ChargeSpent", "sessionId": "1790646377000015", "moduleId": "9988400002719", "roundsLeft": "79"},
    {"name": "rounds loaded into a gun that runs", "kind": "AdjustCharge", "requestId": "41", "sessionId": "1790646377000015", "moduleId": "9988400002719",
     "chargeType": 222, "op": "add", "capacity": "80", "available": "40"},
    {"name": "rounds taken out of a gun that runs", "kind": "AdjustCharge", "requestId": "42", "sessionId": "1790646377000015", "moduleId": "9988400002719",
     "chargeType": 0, "op": "remove", "quantity": "75"},
    {"name": "all the rounds taken out of a gun", "kind": "AdjustCharge", "requestId": "43", "sessionId": "1790646377000015", "moduleId": "9988400002719",
     "chargeType": 0, "op": "remove", "quantity": "0"},
    {"name": "a refresh of a running gun: its cycle, its capacitor and its numbers, with the attributes told", "kind": "UpdateEffect", "sessionId": "1790646377000015",
     "moduleId": "9988400002719", "durationMs": 4500.0, "capacitorNeed": 2.5,
     "turret": {"weaponTypeId": 3638, "fx": "effects.Laser", "rawDamage": [0.0, 3.0, 4.5, 0.0], "optimalRange": 5000.0, "falloff": 1200.0,
                "trackingSpeed": 0.1, "signatureResolution": 40000.0, "requiresGunnery": True,
                "charge": {"typeId": 222, "flag": 28, "rounds": 80, "perCycle": 1, "reloads": True}},
     "changes": [{"itemId": "9988400002719", "attributeId": 6, "value": 2.5}, {"itemId": "9988400002719", "attributeId": 51, "value": 4500.0}]},
    {"name": "a refresh of a running module with no gun", "kind": "UpdateEffect", "sessionId": "1790646377000015",
     "moduleId": "9988400002721", "durationMs": 3000.0, "capacitorNeed": 0.0, "changes": []},
    {"name": "rounds of a gun that runs changed", "kind": "ChargeAdjusted", "requestId": "41", "running": True, "before": "3", "after": "80"},
    {"name": "rounds of a gun that does not run", "kind": "ChargeAdjusted", "requestId": "44", "running": False, "before": "0", "after": "0"},
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
    {"name": "new ship numbers with the armor pinned by a polarized weapon", "kind": "ShipSpec", "sessionId": "1790646377000015",
     "ship": dict(IBIS, vitals=dict(IBIS["vitals"], armorPinned=True,
                                    resonances={"shield": [0.0, 0.8, 0.6, 0.5], "armor": [1.0, 1.0, 1.0, 1.0],
                                                "structure": [0.33, 0.33, 0.33, 0.33]}))},
    {"name": "activate a reactive armor hardener", "kind": "Activate", "sessionId": "1790646377000015",
     "effect": {"moduleId": "9988400002810", "moduleType": 4403, "effectId": 4928, "fx": "effects.AdaptiveArmorHardener",
                "durationMs": 10000.0, "capacitorNeed": 42.0, "repeat": 1, "targetId": "0",
                "action": {"kind": "resistanceShift", "seed": [0.85, 0.85, 0.85, 0.85], "shift": 0.06,
                           "attributeIds": [267, 270, 269, 268]},
                "onChanges": [{"itemId": "9988400002810", "attributeId": 6, "value": 42.0},
                              {"itemId": "9988400002810", "attributeId": 73, "value": 10000.0},
                              {"itemId": "9988400002810", "attributeId": 267, "value": 0.425},
                              {"itemId": "9988400002810", "attributeId": 270, "value": 0.2975}],
                "offChanges": [{"itemId": "9988400002810", "attributeId": 6, "value": 42.0},
                               {"itemId": "9988400002810", "attributeId": 73, "value": 10000.0}]}},
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
                               "weaponTypeId": 5678, "fx": "effects.Laser", "requiresGunnery": True, "rawDamage": [0.0, 0.0, 6.0, 4.0],
                               "optimalRange": 20000.0, "falloff": 10000.0, "trackingSpeed": 0.4, "signatureResolution": 40.0}],
                  "wreck": {"typeId": 26506, "name": "Caldari Frigate Wreck", "graphicId": 3117}}},
    {"name": "spawn of a bare NPC: no tree, no weapons, no wreck", "kind": "Spawn", "requestId": "8", "solarSystemId": 30000142,
     "ship": dict(BARE, shipId="980050000078", characterId="0"), "anchorShipId": "9988400001939", "position": [1.0, 2.0, 3.0], "velocity": [4.0, 5.0, 6.0],
     "direction": [0.0, 0.0, 1.0], "behavior": "",
     "identity": {"hostileResponseThreshold": -11, "friendlyResponseThreshold": 11, "aggressionRange": 0.0, "weapons": [], "hull": True}},
    {"name": "spawn of an NPC whose wreck the host makes", "kind": "Spawn", "requestId": "9", "solarSystemId": 30100032,
     "ship": dict(BARE, shipId="980050000079", characterId="0", ownerId="1000125", categoryId=11, groupId=25, typeId=597),
     "anchorShipId": "0", "position": [1000500.0, 100.0, 100.0], "velocity": [0.0, 0.0, 0.0], "direction": [1.0, 0.0, 0.0], "behavior": "",
     "identity": {"hostileResponseThreshold": -11, "friendlyResponseThreshold": 11, "aggressionRange": 0.0, "weapons": [],
                  "wreck": {"typeId": 26506, "name": "Caldari Frigate Wreck", "graphicId": 3117}, "hostWreck": True}},
    {"name": "spawn done", "kind": "SpawnReply", "requestId": "7", "ok": True, "session": "4611686018427387905"},
    {"name": "spawn refused", "kind": "SpawnReply", "requestId": "8", "ok": False, "reason": "NO_SCENE 30000142"},
    {"name": "launch of a drone", "kind": "LaunchDrone", "requestId": "11", "sessionId": "1790646377000015",
     "ship": dict(BARE, shipId="9988400003716", characterId="0", ownerId="140000342", categoryId=18, groupId=100, typeId=2454, name="Hobgoblin I"),
     "weapon": {"moduleId": "9988400003716", "effectId": 10, "cycleMs": 4000.0, "capacitorNeed": 0.0, "weaponTypeId": 2454, "fx": "effects.Laser", "requiresGunnery": True,
                "rawDamage": [0.0, 32.0, 0.0, 0.0], "optimalRange": 2100.0, "falloff": 2000.0, "trackingSpeed": 1.815, "signatureResolution": 25.0},
     "graphicId": 1099, "orbitDistance": 700.0, "attackRange": 2800.0, "cruiseFraction": 0.2357142857142857},
    {"name": "launch of a web drone", "kind": "LaunchDrone", "requestId": "12", "sessionId": "1790646377000015",
     "ship": dict(BARE, shipId="9988400003720", characterId="0", ownerId="140000342", categoryId=18, groupId=641, typeId=23731, name="Warrior SW-300"),
     "weapon": None, "graphicId": 1124, "orbitDistance": 500.0, "attackRange": 10000.0, "cruiseFraction": 0.25,
     "effect": {"effectId": 6690, "fx": "effects.ModifyTargetSpeed", "cycleMs": 5000.0, "lastingMs": 5000.0,
                "action": {"kind": "remote", "what": "web", "percent": -5.0, "optimal": 5000.0, "falloff": 0.0, "when": "start", "jam": "webify"}}},
    {"name": "order to engage", "kind": "DroneCommand", "sessionId": "1790646377000015", "command": 1, "targetId": "980050000077",
     "droneIds": ["9988400003716", "9988400003717"], "controlRange": 40_000.0},
    {"name": "order to go home", "kind": "DroneCommand", "sessionId": "1790646377000015", "command": 2, "targetId": "0", "droneIds": ["9988400003716"], "controlRange": 20_000.0},
    {"name": "what the drones did with an order", "kind": "DroneResults", "sessionId": "1790646377000015", "command": 1,
     "results": [{"droneId": "9988400003716", "reason": ""}, {"droneId": "9988400003717", "reason": "NOT_CONTROLLED"}]},
    {"name": "a drone scooped into the bay", "kind": "DroneScooped", "sessionId": "1790646377000015", "droneId": "9988400003716"},
    {"name": "a drone let go", "kind": "DroneReleased", "sessionId": "1790646377000015", "droneId": "9988400003716",
     "position": [311989835983.1308, -13069154020.916222, 156646308895.76187]},
    {"name": "launch of a fighter squadron", "kind": "LaunchFighter", "requestId": "13", "sessionId": "1790646377000015",
     "ship": dict(IBIS, shipId="9988400004100", typeId=23055, groupId=1652, name="Templar I", modules=[]),
     "graphicId": 1200, "cruiseFraction": 0.5, "squadronSize": 6},
    {"name": "a squadron of the pilot's fighter tube lost a member", "kind": "FighterSize", "sessionId": "1790646377000015",
     "fighterId": "9988400004100", "size": 5},
    {"name": "a fighter order to orbit a ship", "kind": "FighterOrder", "sessionId": "1790646377000015", "command": 1,
     "targetId": "980050000077", "fighterIds": ["9988400004100"], "range": 800.0},
    {"name": "what the fighters did with an order", "kind": "FighterResults", "sessionId": "1790646377000015", "command": 4,
     "results": [{"fighterId": "9988400004100", "reason": ""}, {"fighterId": "9988400004101", "reason": "NOT_CONTROLLED"}]},
    {"name": "a fighter landed in its tube", "kind": "FighterLanded", "sessionId": "1790646377000015", "fighterId": "9988400004100"},
    {"name": "a fighter let go", "kind": "FighterReleased", "sessionId": "1790646377000015", "fighterId": "9988400004100",
     "position": [311989835983.1308, -13069154020.916222, 156646308895.76187]},
    {"name": "a fighter sent to a point", "kind": "FighterGotoPoint", "sessionId": "1790646377000015",
     "fighterIds": ["9988400004100"], "point": [311989835983.1308, -13069154020.916222, 156646308895.76187]},
    {"name": "an ECM burst that jammed one ship it reached and not another", "kind": "EcmBurst", "sessionId": "1790646377000015",
     "shipId": "9988400001939", "reached": [{"shipId": "9988400004100", "jammed": True}, {"shipId": "9988400004101", "jammed": False}]},
    {"name": "destroy for a pilot to loot", "kind": "Destroy", "shipId": "980050000077", "characterId": "140000001",
     "corporationId": "1000006", "allianceId": "0", "warFactionId": "500001"},
    {"name": "respawn of a capsule where the ship was", "kind": "Respawn", "sessionId": "1790646377000015",
     "ship": dict(IBIS, shipId="9988400002416", typeId=670, groupId=29, name="Capsule", modules=[]),
     "position": [311989835983.1308, -13069154020.916222, 156646308895.76187], "direction": [0.6159, 0.01306, 0.78769]},
    {"name": "wreck the host made, to put in space", "kind": "PlaceWreck", "solarSystemId": 30100032,
     "wreckId": "9988400002417", "groupId": 186, "wreck": {"typeId": 26506, "name": "Caldari Frigate Wreck", "graphicId": 3117},
     "position": [311989835983.1308, -13069154020.916222, 156646308895.76187], "direction": [0.6159, 0.01306, 0.78769],
     "radius": 47.0, "launcherId": "9988400002411", "characterId": "140000394", "corporationId": "1000006",
     "allianceId": "0", "warFactionId": "500001", "isEmpty": False},
    {"name": "wreck of no kind of the reference's graphic", "kind": "PlaceWreck", "solarSystemId": 30000142,
     "wreckId": "9988400002420", "groupId": 14, "wreck": {"typeId": 26507, "name": "Amarr Frigate Wreck", "graphicId": 0},
     "position": [0.0, 0.0, 0.0], "direction": [1.0, 0.0, 0.0], "radius": 30.0, "launcherId": "9988400002400",
     "characterId": "0", "corporationId": "0", "allianceId": "0", "warFactionId": "0", "isEmpty": True},
    {"name": "can for cargo a pilot jettisons", "kind": "PlaceContainer", "sessionId": "1790646377000015", "containerId": "9988400002948",
     "container": {"typeId": 23, "name": "Cargo Container", "graphicId": 1176}, "radius": 14.0, "launcherId": "9988400002940",
     "characterId": "140000431", "corporationId": "1000006", "allianceId": "0", "warFactionId": "0", "distance": 275.0},
    {"name": "can that is in space", "kind": "ContainerPlaced", "sessionId": "1790646377000015", "containerId": "9988400002948",
     "position": [311989835983.1308, -13069154020.916222, 156646308895.76187], "direction": [0.6159, 0.01306, 0.78769]},
    {"name": "player's warp ended at a belt", "kind": "Arrived", "sessionId": "1790646377000015", "beltId": "40009256",
     "position": [311989835983.1308, -13069154020.916222, 156646308895.76187]},
    {"name": "wreck with something to take", "kind": "ChangeWreck", "sessionId": "1790646377000015", "wreckId": "9988400002948", "isEmpty": False},
    {"name": "wreck with nothing to take", "kind": "ChangeWreck", "sessionId": "1790646377000015", "wreckId": "9988400002417", "isEmpty": True},
    {"name": "can taken out of space with the destruction effect", "kind": "RemoveWreck", "wreckId": "9988400002948", "effect": 3},
    {"name": "wreck taken out of space without one", "kind": "RemoveWreck", "wreckId": "9988400002417", "effect": 0},
    {"name": "how far a can is", "kind": "Measured", "sessionId": "1790646377000015", "command": 20, "distance": 261.0},
    {"name": "how far a can that is not there is", "kind": "Measured", "sessionId": "1790646377000015", "command": 20, "distance": None},
    {"name": "destroy for nobody", "kind": "Destroy", "shipId": "4200000000000001", "characterId": "0",
     "corporationId": "0", "allianceId": "0", "warFactionId": "0"},
    {"name": "module work cycle of a mining laser", "kind": "ModuleWorkCycle", "sessionId": "1790646377000015",
     "characterId": "140000342", "shipId": "9988400001939", "solarSystemId": 30100032, "moduleId": "9988400002813",
     "targetId": "1000000000001", "cycleId": "7301444403200000001", "command": 7, "miningAmountM3": 20.0,
     "wasteMultiplier": 1.0, "wasteProbability": 0.1, "critProbability": 0.05, "critMultiplier": 2.0,
     "accessDifficultyBonus": 0.0, "wasteRoll": 0.25, "roundingRoll": 0.5, "critRoll": 0.75},
    {"name": "module work cycle of a salvager", "kind": "ModuleWorkCycle", "sessionId": "1790646377000015",
     "characterId": "140000342", "shipId": "9988400001939", "solarSystemId": 30100032, "moduleId": "9988400002813",
     "targetId": "1000000000002", "cycleId": "7301444403200000002", "command": 8, "miningAmountM3": 0.0,
     "wasteMultiplier": 0.0, "wasteProbability": 0.0, "critProbability": 0.0, "critMultiplier": 0.0,
     "accessDifficultyBonus": 5.0, "wasteRoll": 0.0, "roundingRoll": 0.0, "critRoll": 0.9},
    {"name": "module work cycle asking the fuel of a cycle", "kind": "ModuleWorkCycle", "sessionId": "1790646377000015",
     "characterId": "140000342", "shipId": "9988400001939", "solarSystemId": 30100032, "moduleId": "9988400002810",
     "targetId": "0", "cycleId": "7301444403200000003", "command": 9, "miningAmountM3": 0.0,
     "wasteMultiplier": 0.0, "wasteProbability": 0.0, "critProbability": 0.0, "critMultiplier": 0.0,
     "accessDifficultyBonus": 0.0, "wasteRoll": 0.0, "roundingRoll": 0.0, "critRoll": 0.0},
    {"name": "module work result accepted", "kind": "ModuleWorkResult", "sessionId": "1790646377000015",
     "moduleId": "9988400002813", "targetId": "1000000000001", "cycleId": "7301444403200000001", "accepted": True,
     "reason": "", "unitsRemaining": "980", "deliveredUnits": "20", "complete": False},
    {"name": "module work result refused", "kind": "ModuleWorkResult", "sessionId": "1790646377000015",
     "moduleId": "9988400002813", "targetId": "1000000000001", "cycleId": "7301444403200000003", "accepted": False,
     "reason": "NO_TARGET", "unitsRemaining": "0", "deliveredUnits": "0", "complete": True},
    {"name": "module ended when its cycles are done", "kind": "ModuleEnded", "sessionId": "1790646377000015",
     "moduleId": "9988400002813", "reason": 1},
    {"name": "module ended when its target is lost", "kind": "ModuleEnded", "sessionId": "1790646377000015",
     "moduleId": "9988400002813", "reason": 2},
    {"name": "activate a mining laser", "kind": "Activate", "sessionId": "1790646377000015",
     "effect": {"moduleId": "9988400002813", "moduleType": 17912, "effectId": 4025, "fx": "effects.MiningLaser",
                "durationMs": 2000.0, "capacitorNeed": 5.0, "repeat": 1000, "targetId": "1000000000001",
                "action": {"kind": "mining", "family": "ore", "miningAmountM3": 20.0, "wasteMultiplier": 1.0,
                           "wasteProbability": 0.1, "critProbability": 0.05, "critMultiplier": 2.0},
                "onChanges": [], "offChanges": []}},
    {"name": "activate a siege module that burns fuel", "kind": "Activate", "sessionId": "1790646377000015",
     "effect": {"moduleId": "9988400002810", "moduleType": 20280, "effectId": 6582, "fx": "effects.SiegeMode",
                "durationMs": 300000.0, "capacitorNeed": 0.0, "repeat": 1, "targetId": "0",
                "burnsFuel": True, "onChanges": [], "offChanges": []}},
    {"name": "activate a salvager", "kind": "Activate", "sessionId": "1790646377000015",
     "effect": {"moduleId": "9988400002813", "moduleType": 25861, "effectId": 6, "fx": "effects.Salvaging",
                "durationMs": 10000.0, "capacitorNeed": 20.0, "repeat": 1000, "targetId": "1000000000002",
                "action": {"kind": "salvage", "accessBonus": 10.0}, "onChanges": [], "offChanges": []}},
    {"name": "activate a tractor beam", "kind": "Activate", "sessionId": "1790646377000015",
     "effect": {"moduleId": "9988400002813", "moduleType": 24348, "effectId": 9, "fx": "effects.TractorBeam",
                "durationMs": 5000.0, "capacitorNeed": 5.0, "repeat": 1000, "targetId": "1000000000002",
                "action": {"kind": "tractor", "velocity": 500.0, "hold": 500.0, "range": 20000.0}, "onChanges": [], "offChanges": []}},
    # the SimAction tag 26 (protocol 40): a Cargo Scanner I (50 km, a hard cutoff) and a Ship Scanner I (50 km, 30 km of falloff), each locked on a ship
    {"name": "activate a cargo scanner (Cargo Scanner I: 50 km, no falloff)", "kind": "Activate", "sessionId": "1790646377000015",
     "effect": {"moduleId": "9988400002813", "moduleType": 442, "effectId": 47, "fx": "effects.CargoScan",
                "durationMs": 5000.0, "capacitorNeed": 4.0, "repeat": 1, "targetId": "1000000000002", "burnsFuel": False,
                "action": {"kind": "scan", "cargo": True, "optimalMeters": 50000.0, "falloffMeters": 0.0}, "onChanges": [], "offChanges": []}},
    {"name": "activate a ship scanner (Ship Scanner I: 50 km, 30 km falloff)", "kind": "Activate", "sessionId": "1790646377000015",
     "effect": {"moduleId": "9988400002813", "moduleType": 443, "effectId": 46, "fx": "effects.ShipScan",
                "durationMs": 2000.0, "capacitorNeed": 5.0, "repeat": 1, "targetId": "1000000000002", "burnsFuel": False,
                "action": {"kind": "scan", "cargo": False, "optimalMeters": 50000.0, "falloffMeters": 30000.0}, "onChanges": [], "offChanges": []}},
    {"name": "activate a passive targeter", "kind": "Activate", "sessionId": "1790646377000015",
     "effect": {"moduleId": "9988400002813", "moduleType": 1100, "effectId": 10, "fx": "effects.TargetPassively",
                "durationMs": 1000.0, "capacitorNeed": 0.0, "repeat": 1000, "targetId": "1000000000002",
                "action": {"kind": "passiveLock"}, "onChanges": [], "offChanges": []}},
    {"name": "activate a kamikaze squadron (Shadow: four kinds, detonates at 500 m)", "kind": "Activate", "sessionId": "1790646377000015",
     "effect": {"moduleId": "9988400004102", "moduleType": 2948, "effectId": 6554, "fx": "",
                "durationMs": 10000.0, "capacitorNeed": 0.0, "repeat": 1, "targetId": "980050000077", "burnsFuel": False,
                "action": {"kind": "kamikaze", "damage": [4000.0, 0.0, 6000.0, 0.0], "rangeMeters": 500.0},
                "onChanges": [], "offChanges": []}},
    {"name": "activate a fighter's missiles (Templar: a missile reaching 20 km)", "kind": "Activate", "sessionId": "1790646377000015",
     "effect": {"moduleId": "9988400004103", "moduleType": 23055, "effectId": 6441, "fx": "",
                "durationMs": 6000.0, "capacitorNeed": 0.0, "repeat": 1, "targetId": "980050000077", "burnsFuel": False,
                "action": {"kind": "missile", "damage": [1500.0, 0.0, 1500.0, 0.0], "reachMeters": 20000.0, "explosionRadius": 40.0,
                           "explosionVelocity": 135.0, "reductionFactor": 0.4, "reductionSensitivity": 5.5},
                "onChanges": [], "offChanges": []}},
    {"name": "activate a fighter's bomb (a Hornet-class squadron's Micro Electron Bomb, 15 km, a minute)", "kind": "Activate", "sessionId": "1790646377000015",
     "effect": {"moduleId": "9988400004104", "moduleType": 40362, "effectId": 6485, "fx": "",
                "durationMs": 60000.0, "capacitorNeed": 0.0, "repeat": 1, "targetId": "0", "burnsFuel": False,
                "action": {"kind": "blast", "centre": [12000.0, -3000.0, 45000.0], "radiusMeters": 15000.0, "damage": [640.0, 0.0, 0.0, 0.0]},
                "onChanges": [], "offChanges": []}},
    {"name": "activate a doomsday strike (Bosonic Field Generator)", "kind": "Activate", "sessionId": "1790646377000015",
     "effect": {"moduleId": "9988400002814", "moduleType": 40633, "effectId": 6473, "fx": "",
                "durationMs": 300000.0, "capacitorNeed": 30000.0, "repeat": 1, "targetId": "0", "burnsFuel": True,
                "action": {"kind": "strike", "shape": 3, "direction": [0.6, 0.0, 0.8], "length": 30000.0, "radius": 0.0,
                           "warningMs": 10000.0, "pulseMs": 1000.0, "pulses": 21, "damage": [10000.0, 10000.0, 10000.0, 10000.0],
                           "signatureRadius": 10000.0, "immobileMs": 30000.0, "noJumpMs": 300000.0,
                           "neutAmount": 30000.0, "neutRadius": 10000.0, "neutSignatureRadius": 20000.0},
                "onChanges": [], "offChanges": []}},
    {"name": "activate a reaper sweep (Divine Harvest)", "kind": "Activate", "sessionId": "1790646377000015",
     "effect": {"moduleId": "9988400002816", "moduleType": 40632, "effectId": 6201, "fx": "",
                "durationMs": 300000.0, "capacitorNeed": 35000.0, "repeat": 1, "targetId": "0", "burnsFuel": True,
                "action": {"kind": "strike", "shape": 1, "direction": [0.6, 0.0, 0.8], "length": 30000.0, "radius": 5000.0,
                           "warningMs": 10000.0, "pulseMs": 1000.0, "pulses": 11, "damage": [100000.0, 0.0, 0.0, 0.0],
                           "signatureRadius": 10000.0, "immobileMs": 30000.0, "noJumpMs": 300000.0,
                           "neutAmount": 35000.0, "neutRadius": 10000.0, "neutSignatureRadius": 20000.0,
                           "debuffMs": 0.0, "sweepEnd": [-20000.0, 0.0, 28000.0]},
                "onChanges": [], "offChanges": []}},
    {"name": "activate a disruptive lance (Atgeir)", "kind": "Activate", "sessionId": "1790646377000015",
     "effect": {"moduleId": "9988400002817", "moduleType": 77398, "effectId": 11691, "fx": "",
                "durationMs": 300000.0, "capacitorNeed": 30000.0, "repeat": 1, "targetId": "0", "burnsFuel": True,
                "action": {"kind": "strike", "shape": 1, "direction": [1.0, 0.0, 0.0], "length": 50000.0, "radius": 2500.0,
                           "warningMs": 15000.0, "pulseMs": 1000.0, "pulses": 16, "damage": [0.0, 0.0, 0.0, 17000.0],
                           "signatureRadius": 20000.0, "immobileMs": 30000.0, "noJumpMs": 120000.0,
                           "neutAmount": 31000.0, "neutRadius": 10000.0, "neutSignatureRadius": 20000.0,
                           "debuffMs": 60000.0, "beacon": [4000.0, 0.0, 0.0]},
                "onChanges": [], "offChanges": []}},
    {"name": "activate a titan doomsday (Judgment)", "kind": "Activate", "sessionId": "1790646377000015",
     "effect": {"moduleId": "9988400002815", "moduleType": 24550, "effectId": 4489, "fx": "",
                "durationMs": 300000.0, "capacitorNeed": 35000.0, "repeat": 1, "targetId": "1000000000002", "burnsFuel": True,
                "action": {"kind": "doomsday", "delayMs": 9000.0, "damage": [2400000.0, 0.0, 0.0, 0.0],
                           "immobileMs": 30000.0, "noJumpMs": 300000.0},
                "onChanges": [], "offChanges": []}},
    {"name": "activate the Gravitational Transportation Field Oscillator (HOG)", "kind": "Activate", "sessionId": "1790646377000015",
     "effect": {"moduleId": "9988400002818", "moduleType": 40634, "effectId": 6474, "fx": "",
                "durationMs": 300000.0, "capacitorNeed": 30000.0, "repeat": 1, "targetId": "0", "burnsFuel": True,
                "action": {"kind": "field",
                           "strike": {"shape": 4, "direction": [0.6, 0.0, 0.8], "length": 40000.0, "radius": 10000.0,
                                      "warningMs": 15000.0, "pulseMs": 1000.0, "pulses": 1, "damage": [0.0, 0.0, 0.0, 0.0],
                                      "signatureRadius": 0.0, "immobileMs": 30000.0, "noJumpMs": 300000.0,
                                      "neutAmount": 30000.0, "neutRadius": 10000.0, "neutSignatureRadius": 20000.0},
                           "destination": [1000.0, 0.0, -2000.0], "warpBlockMs": 60000.0,
                           "moved": [1000000000003], "held": [1000000000004]},
                "onChanges": [], "offChanges": []}},
    {"name": "activate an auto-targeting system", "kind": "Activate", "sessionId": "1790646377000015",
     "effect": {"moduleId": "9988400002813", "moduleType": 1101, "effectId": 11, "fx": "effects.TargetHostiles",
                "durationMs": 1000.0, "capacitorNeed": 0.0, "repeat": 1000, "targetId": "0",
                "action": {"kind": "autoTarget", "range": 50000.0}, "onChanges": [], "offChanges": []}},
]
for case in valid:
    case["hex"] = build(case).hex()

# each of these is refused for the stated reason
good = build(valid[2])  # a good Enter
precursor = next(c for c in valid if c["name"] == "activate a precursor weapon that builds its damage up")
negative_spool = dict(precursor, effect=dict(precursor["effect"], turret=dict(precursor["effect"]["turret"], spool={"perCycle": -0.07, "max": 2.125})))
vorton = next(c for c in valid if c["name"].startswith("activate a vorton projector"))
negative_radius = dict(vorton, effect=dict(vorton["effect"], turret=dict(vorton["effect"]["turret"],
                                                                         aoe=dict(vorton["effect"]["turret"]["aoe"], explosionRadius=-43.0))))
negative_targets = dict(vorton, effect=dict(vorton["effect"], turret=dict(vorton["effect"]["turret"],
                                                                          arc={"range": 10000.0, "targets": -1})))
hardener = next(c for c in valid if c["name"] == "activate a reactive armor hardener")
hardener_seed_high = dict(hardener, effect=dict(hardener["effect"], action=dict(hardener["effect"]["action"], seed=[0.85, 1.5, 0.85, 0.85])))
hardener_shift_none = dict(hardener, effect=dict(hardener["effect"], action=dict(hardener["effect"]["action"], shift=0.0)))
pinned_flag_two = {"kind": "ShipSpec", "sessionId": "1790646377000015", "ship": dict(IBIS, vitals=dict(IBIS["vitals"], armorPinned=2))}
def named(name):
    return next(c for c in valid if c["name"] == name)


invalid = [
    {"name": "unknown kind", "reason": "unknown-kind", "hex": "01000000ff"},
    {"name": "kind zero", "reason": "unknown-kind", "hex": "0100000000"},
    {"name": "empty frame", "reason": "empty", "hex": "00000000"},
    {"name": "hello truncated", "reason": "truncated", "hex": "02000000" + "0101"},
    {"name": "hello trailing bytes", "reason": "trailing", "hex": "04000000" + "01" + "0100" + "00"},
    {"name": "enter with an unknown way in", "reason": "bad-value",
     "hex": (good[:4] + good[4:17] + bytes([7]) + good[18:]).hex()},
    {"name": "enter cut short", "reason": "truncated", "hex": (struct.pack("<I", 20) + good[4:24]).hex()},
    {"name": "precursor spool with a negative bonus per cycle", "reason": "bad-value", "hex": build(negative_spool).hex()},
    {"name": "vorton explosion with a negative radius", "reason": "bad-value", "hex": build(negative_radius).hex()},
    {"name": "vorton arc with a negative number of targets", "reason": "bad-value", "hex": build(negative_targets).hex()},
    {"name": "reactive armor hardener with a seed above one", "reason": "bad-value", "hex": build(hardener_seed_high).hex()},
    {"name": "reactive armor hardener that shifts by nothing", "reason": "bad-value", "hex": build(hardener_shift_none).hex()},
    {"name": "armor pinned flag that is not a flag", "reason": "bad-value", "hex": build(pinned_flag_two).hex()},
    {"name": "kamikaze with a negative range", "reason": "bad-value",
     "hex": build(dict(named("activate a kamikaze squadron (Shadow: four kinds, detonates at 500 m)"), effect=dict(named("activate a kamikaze squadron (Shadow: four kinds, detonates at 500 m)")["effect"],
                       action=dict(named("activate a kamikaze squadron (Shadow: four kinds, detonates at 500 m)")["effect"]["action"], rangeMeters=-1.0)))).hex()},
    {"name": "fighter missile with a negative explosion radius", "reason": "bad-value",
     "hex": build(dict(named("activate a fighter's missiles (Templar: a missile reaching 20 km)"), effect=dict(named("activate a fighter's missiles (Templar: a missile reaching 20 km)")["effect"],
                       action=dict(named("activate a fighter's missiles (Templar: a missile reaching 20 km)")["effect"]["action"], explosionRadius=-40.0)))).hex()},
    {"name": "fighter bomb with no radius", "reason": "bad-value",
     "hex": build(dict(named("activate a fighter's bomb (a Hornet-class squadron's Micro Electron Bomb, 15 km, a minute)"), effect=dict(named("activate a fighter's bomb (a Hornet-class squadron's Micro Electron Bomb, 15 km, a minute)")["effect"],
                       action=dict(named("activate a fighter's bomb (a Hornet-class squadron's Micro Electron Bomb, 15 km, a minute)")["effect"]["action"], radiusMeters=0.0)))).hex()},
    {"name": "fighter bomb with a negative damage", "reason": "bad-value",
     "hex": build(dict(named("activate a fighter's bomb (a Hornet-class squadron's Micro Electron Bomb, 15 km, a minute)"), effect=dict(named("activate a fighter's bomb (a Hornet-class squadron's Micro Electron Bomb, 15 km, a minute)")["effect"],
                       action=dict(named("activate a fighter's bomb (a Hornet-class squadron's Micro Electron Bomb, 15 km, a minute)")["effect"]["action"], damage=[-640.0, 0.0, 0.0, 0.0])))).hex()},
    {"name": "strike with an aim that is not a unit vector", "reason": "bad-value",
     "hex": build(dict(named("activate a doomsday strike (Bosonic Field Generator)"), effect=dict(named("activate a doomsday strike (Bosonic Field Generator)")["effect"],
                       action=dict(named("activate a doomsday strike (Bosonic Field Generator)")["effect"]["action"], direction=[0.6, 0.0, 0.9])))).hex()},
    {"name": "a sweep that ends on the firing ship", "reason": "bad-value",
     "hex": build(dict(named("activate a reaper sweep (Divine Harvest)"), effect=dict(named("activate a reaper sweep (Divine Harvest)")["effect"],
                       action=dict(named("activate a reaper sweep (Divine Harvest)")["effect"]["action"], sweepEnd=[0.0, 0.0, 0.0])))).hex()},
    {"name": "strike in a shape the simulation does not run", "reason": "bad-value",
     "hex": build(dict(named("activate a doomsday strike (Bosonic Field Generator)"), effect=dict(named("activate a doomsday strike (Bosonic Field Generator)")["effect"],
                       action=dict(named("activate a doomsday strike (Bosonic Field Generator)")["effect"]["action"], shape=2)))).hex()},
    {"name": "titan doomsday with a negative delay", "reason": "bad-value",
     "hex": build(dict(named("activate a titan doomsday (Judgment)"), effect=dict(named("activate a titan doomsday (Judgment)")["effect"],
                       action=dict(named("activate a titan doomsday (Judgment)")["effect"]["action"], delayMs=-1.0)))).hex()},
    {"name": "enter reply with an unknown status", "reason": "bad-value",
     "hex": (struct.pack("<I", 12) + bytes([13]) + u64(1) + bytes([2]) + s("")).hex()},
    {"name": "text that is not UTF-8", "reason": "utf8",
     "hex": (struct.pack("<I", 1 + 8 + 1 + 2 + 1) + bytes([11]) + u64(1) + bytes([4]) + struct.pack("<H", 1) + b"\xff").hex()},
    {"name": "command that promises more arguments than it carries", "reason": "truncated",
     "hex": (struct.pack("<I", 1 + 8 + 2) + bytes([7]) + u64(1) + bytes([1, 2])).hex()},
    {"name": "refresh of a running effect with no cycle", "reason": "bad-value",
     "hex": build(dict(named("a refresh of a running gun: its cycle, its capacitor and its numbers, with the attributes told"), durationMs=0.0)).hex()},
    {"name": "refresh of a running effect with a negative capacitor need", "reason": "bad-value",
     "hex": build(dict(named("a refresh of a running gun: its cycle, its capacitor and its numbers, with the attributes told"), capacitorNeed=-1.0)).hex()},
    {"name": "module work cycle with no cycle", "reason": "bad-value",
     "hex": build(dict(named("module work cycle of a mining laser"), cycleId="0")).hex()},
    {"name": "module work cycle with a roll of one", "reason": "bad-value",
     "hex": build(dict(named("module work cycle of a mining laser"), wasteRoll=1.0)).hex()},
    {"name": "module work cycle of an unknown command", "reason": "bad-value",
     "hex": build(dict(named("module work cycle of a mining laser"), command=12)).hex()},
    {"name": "scan with no optimal range", "reason": "bad-value",
     "hex": build(dict(named("activate a cargo scanner (Cargo Scanner I: 50 km, no falloff)"), effect=dict(named("activate a cargo scanner (Cargo Scanner I: 50 km, no falloff)")["effect"],
                       action=dict(named("activate a cargo scanner (Cargo Scanner I: 50 km, no falloff)")["effect"]["action"], optimalMeters=0.0)))).hex()},
    {"name": "ship scan with a negative falloff", "reason": "bad-value",
     "hex": build(dict(named("activate a ship scanner (Ship Scanner I: 50 km, 30 km falloff)"), effect=dict(named("activate a ship scanner (Ship Scanner I: 50 km, 30 km falloff)")["effect"],
                       action=dict(named("activate a ship scanner (Ship Scanner I: 50 km, 30 km falloff)")["effect"]["action"], falloffMeters=-1.0)))).hex()},
    {"name": "module work cycle with a probability above one", "reason": "bad-value",
     "hex": build(dict(named("module work cycle of a mining laser"), critProbability=1.5)).hex()},
    {"name": "module work cycle cut short", "reason": "truncated",
     "hex": (struct.pack("<I", 1 + 8 + 8) + bytes([51]) + u64(1) + u64(2)).hex()},
    {"name": "fuel cycle with mining numbers", "reason": "bad-value",
     "hex": build(dict(named("module work cycle asking the fuel of a cycle"), miningAmountM3=20.0)).hex()},
    {"name": "module ended with an unknown reason", "reason": "bad-value",
     "hex": build(dict(named("module ended when its target is lost"), reason=9)).hex()},
    {"name": "ECM burst with a jam flag that is neither 0 nor 1", "reason": "bad-value",
     "hex": frame("EcmBurst", u64(1) + i64(9) + struct.pack("<I", 1) + i64(10) + bytes([2])).hex()},
    {"name": "ECM burst that promises two ships and carries one", "reason": "truncated",
     "hex": frame("EcmBurst", u64(1) + i64(9) + struct.pack("<I", 2) + i64(10) + bytes([1])).hex()},
    {"name": "ECM burst cut short before its ship", "reason": "truncated",
     "hex": frame("EcmBurst", u64(1) + bytes([0, 0, 0])).hex()},
    {"name": "warp probe with no lifetime", "reason": "bad-value",
     "hex": build(dict(named("activate an interdiction probe"), effect=dict(named("activate an interdiction probe")["effect"],
                       action=dict(named("activate an interdiction probe")["effect"]["action"], lifetimeMs=0.0)))).hex()},
    {"name": "bomb of an unknown kind", "reason": "bad-value",
     "hex": build(dict(named("activate a bomb"), effect=dict(named("activate a bomb")["effect"],
                       action=dict(named("activate a bomb")["effect"]["action"], bombKind=7)))).hex()},
    {"name": "mining action with a crit chance above one", "reason": "bad-value",
     "hex": build(dict(named("activate a mining laser"), effect=dict(named("activate a mining laser")["effect"],
                       action=dict(named("activate a mining laser")["effect"]["action"], critProbability=1.5)))).hex()},
    {"name": "mining action with an unknown family", "reason": "bad-value",
     "hex": (lambda frame: frame[:len(frame) - 41] + bytes([9]) + frame[len(frame) - 40:])(build(named("activate a mining laser")))
            .hex()},
]

os.makedirs(os.path.dirname(OUT), exist_ok=True)
with open(OUT, "w", encoding="utf-8") as f:
    json.dump({"note": "Generated by tools/dev/gen-simlink-fixtures.py. Frames include their u32 length prefix.",
               "valid": valid, "invalid": invalid}, f, indent=1, ensure_ascii=False)
    f.write("\n")
print(f"{len(valid)} valid, {len(invalid)} invalid -> {OUT}")

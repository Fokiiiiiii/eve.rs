"""A pilot loads a gun with ammunition, unloads it and fires it until the rounds are spent, compared with what the reference was recorded doing.

    EVE_LIVE_SLOW=1 EVE_LIVE_HOST=host:port [EVE_LIVE_SERVER=reference|eve.rs] pytest tests/test_ammo.py

`oracle/fixtures/scenarios/ammo.json` (scripts/record_character_scenarios.py --only ammo) holds one character docked with a gun that takes charges (the
Civilian Miner is taken out and a 125mm Railgun I fitted: `/giveitem`, `/unfit me`, `/fit me`): ammunition given, the gun loaded, loaded again, unloaded,
loaded, and loaded with another kind of charge (the first goes back to the hangar); then the ship asked for with and without the charge, and in space with a
rat beside it the gun fired until its three rounds of iron are spent and fired again with none. The replay does the same on any server and compares what the
client hears, event by event (the ids of the stacks and of the charges are per run: they are compared by the order they first appear in), and what the gun
does in space (the effect going on and off, the rounds the slot tells, the moments, the combat log). What the rat does is its own (DEVIATIONS #53) and what a
shot does is a roll. It needs the simulation and two minutes, so it runs only with EVE_LIVE_SLOW=1.
"""

from __future__ import annotations

import copy
import os
import re
import uuid

import pytest

from evecli import marshal as m
from evecli.describe import describe, differences
from tests import dbseed
from tests import test_login_to_character as base
from tests.test_others import watch
from tests.test_target import compare_answer, plain_race, timing
from tests.test_undock import STATION, SYSTEM, arrive, args_of, compare_events, ids, number, outcome
from tests.test_weapon import effect_of, fx_actions, shots_of, slims_in

AMMO = base.load("ammo.json")
pytestmark = [
    pytest.mark.skipif(not base.LIVE, reason="set EVE_LIVE_HOST=host:port to test against a running server"),
    pytest.mark.skipif(os.environ.get("EVE_LIVE_SLOW") != "1", reason="two minutes: set EVE_LIVE_SLOW=1"),
]

RAILGUN, ANTIMATTER, IRON = 10678, 222, 215
AFTERBURNER, GATLING, MINER = 21857, 3638, 3651  # the modules the server fits the character's Ibis with (in the recorded slots 19, 27 and 28)
HULLS = (601, dbseed.COCKROACH)  # the recording's Ibis and the test ship: a hull's own entry is the hull's and is not compared
# the gun's range and capacitor need: the test ship's hull scales them (to 5/6 of the recording's Ibis), so they are not compared (DEVIATIONS #59)
HULL_SCALED = (6, 54)
# the moments an event may come after or before the recording's
TOLERANCE = 0.8
# the ids of items (stacks, charges) of the recording (10**12 and more) and of this run (from the 1.99 billions) are told apart by the order they appear in
ITEM_IDS = (1_990_000_000, 10**15)


def canon(described, table: dict):
    """A description with the ids of items replaced by the order they first appear in (`#1`, `#2`, ...): the ids are per run and what the client reads is which
    event is about which item."""
    if isinstance(described, list):
        return [canon(v, table) for v in described]
    if isinstance(described, dict):
        if "$long" in described and isinstance(described["$long"], int) and ITEM_IDS[0] <= described["$long"] < ITEM_IDS[1]:
            return table.setdefault(described["$long"], f"#{len(table) + 1}")
        return {k: canon(v, table) for k, v in described.items()}
    if isinstance(described, int) and not isinstance(described, bool) and ITEM_IDS[0] <= described < ITEM_IDS[1]:
        return table.setdefault(described, f"#{len(table) + 1}")
    if isinstance(described, float):
        return round(described, 9)  # two servers work a product out in their own order: the last bits are not what the client reads
    return described


def hull_free(events: list) -> list:
    """The events without the lines that the hull scales (the gun's range and capacitor need, HULL_SCALED); an event that holds only those is left out."""
    out = []
    for event in events:
        if event["name"] == "OnModuleAttributeChanges":
            try:
                lines = event["args"]["$tuple"][1]["$tuple"][1]["$tuple"][0]
            except (KeyError, TypeError):
                lines = None
            if lines is not None:
                kept = [line for line in lines if line["$tuple"][3] not in HULL_SCALED]
                if not kept:
                    continue
                event = copy.deepcopy(event)
                event["args"]["$tuple"][1]["$tuple"][1]["$tuple"][0] = kept
        out.append(event)
    return out


def canon_events(events: list, table: dict, mapping: dict | None = None) -> list:
    """The events with the ids of the recording changed to this run's where they are known, and the ids of items by the order they appear in."""
    return [{**e, "args": canon(ids(e["args"], mapping or {}), table)} for e in hull_free(events)]


def tolerated(answer: dict, drop: tuple = ()) -> dict:
    """The recorded `GetAllInfo` with what the deviations settle: the drone limits are not on the ship (DEVIATIONS #12), the trade premium of the character is what its
    skills make of it (#11: 0.9), and `drop` are attributes of the gun that are not compared."""
    answer = copy.deepcopy(answer)
    kv = dict(answer["args"]["$dict"])
    for _, item in kv["shipInfo"]["$dict"]:
        fields = dict(item["args"]["$dict"])
        attributes = fields["attributes"]
        attributes["$dict"] = [e for e in attributes["$dict"] if e[0] not in (352, 458)]
        if drop and fields.get("invItem") and fields["invItem"]["$row"]["typeID"] == RAILGUN:
            attributes["$dict"] = [e for e in attributes["$dict"] if e[0] not in drop]
    if kv["shipModifiedCharAttribs"]:
        for entry in dict(kv["shipModifiedCharAttribs"]["args"]["$dict"])["attributes"]["$dict"]:
            if entry[0] == 443:
                entry[1] = 0.9
    return answer


def without(answer: dict, drop: tuple) -> dict:
    """Our `GetAllInfo` without the attributes of the gun that are not compared."""
    answer = copy.deepcopy(answer)
    for _, item in dict(answer["args"]["$dict"])["shipInfo"]["$dict"]:
        fields = dict(item["args"]["$dict"])
        if fields.get("invItem") and fields["invItem"]["$row"]["typeID"] == RAILGUN:
            fields["attributes"]["$dict"] = [e for e in fields["attributes"]["$dict"] if e[0] not in drop]
    return answer


def is_hull(entry) -> bool:
    """Whether a `shipInfo` entry is the hull itself (its item row's type is the recording's or the test ship's)."""
    row = dict(entry["args"]["$dict"]).get("invItem") if isinstance(entry, dict) and entry.get("args") else None
    return row is not None and row["$row"]["typeID"] in HULLS


def modules_only(answer: dict) -> dict:
    """What a `GetAllInfo` answer says of the fitted modules (rows, attributes, charges): the hull's own entry and its state are not compared (DEVIATIONS: the
    test ship is a Cockroach where the recording's was an Ibis)."""
    answer = copy.deepcopy(answer)
    fields = [[k, v] for k, v in answer["args"]["$dict"] if k != "shipState"]
    for field in fields:
        if field[0] == "shipInfo":
            field[1]["$dict"] = [e for e in field[1]["$dict"] if not is_hull(e[1])]
    answer["args"]["$dict"] = fields
    return answer


def of_the_slot(item, gun: int, ship: int) -> bool:
    """Whether the item a line of `OnModuleAttributeChanges` is about is the gun or the charge in its slot, which the client knows by `(ship, flag, type)`."""
    if isinstance(item, dict) and "$tuple" in item:
        return number(item["$tuple"][0]) == ship
    return number(item) == gun


def of_the_gun(events: list, gun: int, ship: int) -> list:
    """What the gun makes the pilot's client hear in space: the effect going on and off, the special effect that plays it and the numbers of the module and of
    the rounds in its slot."""
    out = []
    for event in events:
        if event["name"] == "OnGodmaShipEffect" and number(effect_of(event)[0]) == gun:
            out.append(event)
        elif event["name"] == "OnModuleAttributeChanges":
            try:
                lines = event["args"]["$tuple"][1]["$tuple"][1]["$tuple"][0]
            except (KeyError, TypeError):
                continue
            kept = [line for line in lines if line["$tuple"][3] not in HULL_SCALED]
            if kept and all(of_the_slot(line["$tuple"][2], gun, ship) for line in kept):
                event = copy.deepcopy(event)
                event["args"]["$tuple"][1]["$tuple"][1]["$tuple"][0] = kept
                out.append(event)
        elif event["name"] == "DoDestinyUpdate" and (played := fx_actions(event, gun)):
            # what the shot does to the rat is a roll: only the effect that plays the shot is compared
            kept = copy.deepcopy(event)
            kept["args"]["$tuple"][1]["$tuple"][1]["$tuple"][0] = played
            out.append(kept)
    return out


def test_a_pilot_loads_and_fires_a_gun_as_it_did_in_the_reference():
    race = AMMO["races"][0]
    roles = race["roles"]
    run = uuid.uuid4().hex[:10]
    problems: list[str] = []
    steps = {step["label"]: step for step in race["steps"]}
    seen_ids: dict = {}
    seen_recorded: dict = {}
    with base.connect(f"walk-ammo-{run}") as who:
        character, ship, modules, mapping = arrive(who, plain_race({"pilot": race}, "pilot"), run)
        # the test ship is the Cockroach (seeded through dbseed); the modules the server fitted stay in their slots and are matched by type
        dbseed.set_hull(ship, dbseed.COCKROACH)
        mapping.update({race["roles"]["fitted"][str(t)]: dbseed.items_of(character, t)[0] for t in (AFTERBURNER, GATLING)})
        mapping[race["moduleIDs"][2]] = dbseed.items_of(character, MINER)[0]
        known = dict(mapping)  # the ids both sides have from the start: the character, the ship and the modules it had
        only = {"events": who}
        slash = lambda command: (lambda: who.call("slash", "SlashCmd", command))  # noqa: E731

        def docked_step(label: str, call, seconds: float = 1.5, events: bool = True):
            """A step in the station: the answer and every event the client hears, in order (`events=False`: the answer only)."""
            raw, error = outcome(call)
            problems.extend(compare_answer(label, steps[label], raw, error, mapping))
            got = watch(only, seconds)["events"]
            if events:
                recorded = canon_events(steps[label]["events"], seen_recorded, known)
                problems.extend(compare_events(recorded, canon_events(got, seen_ids), label, {}))
            return raw

        def info_step(label: str, call, seconds: float, values: bool = True):
            """`GetAllInfo`: the same answer with the ids told apart by the order they appear in (`values=False`: without the cycle and the capacitor need of the gun), and the events after it."""
            raw, error = outcome(call)
            if error is not None:
                problems.append(f"{label}: error {base.brief(error)}")
            else:
                drop = HULL_SCALED if values else (6, 51, 54)
                recorded = canon(base.blank_times(ids(modules_only(tolerated(steps[label]["ok"], drop)), known)), seen_recorded)
                ours = canon(base.blank_times(modules_only(without(describe(raw), drop) if drop else describe(raw))), seen_ids)
                problems.extend(differences(recorded, ours, label))
            got = watch(only, seconds)["events"]
            problems.extend(compare_events(canon_events(steps[label]["events"], seen_recorded, known), canon_events(got, seen_ids), label, {}))
            return raw

        def rows():
            manager = who.call("invbroker", "MachoBindObject", (STATION, 15), None)[0]
            return [row.fields() for row in who.call_bound(who.call_bound(manager, "GetInventory", 10004), "List")]

        def stacks() -> dict:
            return {int(r["typeID"]): int(r["itemID"]) for r in rows()}

        docked_step("the pilot is given antimatter", slash("/giveitem Antimatter Charge S 500"))
        # the game master's commands that make the setup tell the client in their own ways (DEVIATIONS #58): only their answers are compared
        docked_step("the pilot takes the miner out", slash("/unfit me Civilian Miner"), events=False)
        docked_step("the pilot fits a railgun", slash("/fit me 125mm Railgun I"), events=False)

        bound = who.call("dogmaIM", "MachoBindObject", (STATION, 15), None)
        label = "the pilot asks for its ship with the railgun empty"
        # the reference tells the cycle and the capacitor need of a gun just fitted without the skills of the pilot (3250 and 1.505, and 2750.8 and 1.3545 with the
        # charge in it): the shape of its answer is compared, not the values (DEVIATIONS #58)
        info = info_step(label, lambda: who.call_bound(bound[0], "GetAllInfo", True, True, None), 1.0, values=False)
        ship_info = {k: v for k, v in describe(info)["args"]["$dict"]}["shipInfo"]["$dict"]
        fitted = {}
        for key, value in ship_info:
            if isinstance(key, dict) and "$tuple" in key:
                continue
            row = {k: v for k, v in value["args"]["$dict"]}["invItem"]["$row"]
            fitted[row["typeID"]] = int(key["$long"] if isinstance(key, dict) else key)
        if RAILGUN not in fitted:
            raise AssertionError("the railgun is not fitted: " + "; ".join(problems) + f" (fitted: {sorted(fitted)})")
        gun = fitted[RAILGUN]
        antimatter = stacks()[ANTIMATTER]
        mapping[roles["gun"]] = gun
        mapping[roles["antimatter"]] = antimatter

        load = lambda charge: (lambda: who.call_bound(bound[0], "LoadAmmo", ship, m.List([gun]), m.List([charge]), STATION))  # noqa: E731
        docked_step("the pilot loads the railgun", load(antimatter), 2.0)
        docked_step("the pilot loads the railgun again", load(antimatter), 2.0)
        docked_step("the pilot lists its hangar with the railgun loaded", lambda: rows_call(who), 0.5)
        info_step("the pilot asks for its ship with the railgun loaded", lambda: who.call_bound(bound[0], "GetAllInfo", True, True, None), 1.5)
        docked_step("the pilot unloads the railgun", lambda: who.call_bound(bound[0], "UnloadAmmo", ship, m.List([gun]), STATION, None), 2.0)
        docked_step("the pilot loads the railgun for the flight", load(antimatter), 2.0)
        docked_step("the pilot is given three rounds of iron", slash("/giveitem Iron Charge S 3"))
        iron = stacks()[IRON]
        mapping[roles["iron"]] = iron
        docked_step("the pilot loads the iron in the place of the antimatter", load(iron), 2.0)

        if os.environ.get("EVE_AMMO_DOCKED_ONLY") == "1":
            assert problems == [], "\n".join(problems)
            return
        # in space: the gun fires until the rounds are spent
        service = who.call("ship", "MachoBindObject", (STATION, 15), None)
        who.call_bound(service[0], "Undock", ship, False)
        watch(only, 2.5)
        ballpark = who.call("beyonce", "MachoBindObject", SYSTEM, None)
        watch(only, 3.0)
        dogma = who.call("dogmaIM", "MachoBindObject", (SYSTEM, 5), None)
        who.call_bound(dogma[0], "GetAllInfo", True, True, None)
        watch(only, 1.0)
        watch(only, 45.0, until=lambda seen: any(e["name"] == "OnInvulnCancelled" for e in seen["events"]))
        # the Cockroach flies about 1.6 km/s (the recording's Ibis 339 m/s): it is stopped, so the rat the server places from the ship is beside it
        who.call_bound(ballpark[0], "CmdStop")
        watch(only, 20.0)
        raw_text = who.call("slash", "SlashCmd", "/npc 1 Blood Visionary")
        spawn = watch(only, 8.0)["events"]
        text = getattr(raw_text, "value", raw_text)
        text = text.decode() if isinstance(text, bytes) else str(text)
        assert text.startswith("Spawned 1 hull from Blood Visionary"), text
        rat = next((item for item, _ in slims_in(spawn) if item != ship), 0)
        assert rat, "the rat did not come into space"
        mapping[roles["rat"]] = rat
        who.call_bound(dogma[0], "AddTarget", rat)

        def locked(seen):
            try:
                return rat in [number(t) for t in who.call_bound(dogma[0], "GetTargets")]
            except Exception:  # noqa: BLE001
                return False

        watch(only, 20.0, until=lambda seen: locked(seen))
        who.call("slash", "SlashCmd", "/heal")
        watch(only, 1.0)

        stopped = lambda seen: any(e["name"] == "OnGodmaShipEffect" and effect_of(e)[3] == 0 for e in seen)  # noqa: E731

        def space_step(label: str, call, seconds: float, until=None) -> None:
            raw, error = outcome(call)
            problems.extend(compare_answer(label, steps[label], raw, error, mapping))
            got = watch(only, seconds, (lambda seen: until(seen["events"])) if until else None)["events"]
            theirs, ours = [e for e in steps[label]["events"] if e["name"] != "OnInvulnCancelled"], [e for e in got if e["name"] != "OnInvulnCancelled"]
            recorded_gun, our_gun = of_the_gun(theirs, roles["gun"], int(race["shipID"])), of_the_gun(ours, gun, ship)
            problems.extend(compare_events(recorded_gun, our_gun, label, mapping))
            problems.extend(timing(recorded_gun, our_gun, label, TOLERANCE, ("OnGodmaShipEffect", "DoDestinyUpdate")))
            recorded_shots, our_shots = shots_of(theirs), shots_of(got)
            if abs(len(recorded_shots) - len(our_shots)) > 1:
                problems.append(f"{label}: {len(our_shots)} shots in the combat log in place of {len(recorded_shots)}")
            for index, (a, b) in enumerate(zip(recorded_shots, our_shots)):
                if list(a) != list(b):
                    problems.append(f"{label}: shot {index} has {list(b)} in place of {list(a)}")
                elif number(a["weapon"]) != number(b["weapon"]) or number(b["target"]) != mapping.get(number(a["target"]), number(a["target"])):
                    problems.append(f"{label}: shot {index} of {number(b['weapon'])} at {number(b['target'])}")

        space_step("the pilot fires the railgun until the iron is spent", lambda: who.call_bound(dogma[0], "Activate", gun, "projectileFired", rat, 1000), 20.0, until=stopped)
        space_step("the pilot fires the railgun with nothing in it", lambda: who.call_bound(dogma[0], "Activate", gun, "projectileFired", rat, 1000), 2.0)
    assert problems == [], "\n".join(problems)


def rows_call(who):
    manager = who.call("invbroker", "MachoBindObject", (STATION, 15), None)[0]
    return who.call_bound(who.call_bound(manager, "GetInventory", 10004), "List")

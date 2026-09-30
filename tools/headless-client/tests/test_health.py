"""A ship that is damaged, asked for, flown damaged and mended, compared with what the reference was recorded doing.

    EVE_LIVE_SLOW=1 EVE_LIVE_HOST=host:port [EVE_LIVE_SERVER=reference|eve.rs] pytest tests/test_health.py

`oracle/fixtures/scenarios/health.json` (scripts/record_character_scenarios.py --only health) holds one character that damages its ship in the hangar
(`/dmg`, the reference's only way to hurt a ship on demand), asks for the ship (`GetAllInfo`), mends it (`/heal`), damages it again, undocks, asks for the
ship in space, waits, tries to damage it in space and mends it there. Each step holds the answer (or the error) and what the client was sent, with the
seconds each arrived at. The replay does the same on any server and compares: the answers, the numbers of the ship and of its modules in
`GetAllInfo` (and the state rows next to them), the events and their order, and the damage state of the ship in the stream. What the reference's own NPC
did to the ship once its undocking protection was over (`OnDamageMessage`, the shield charge streaming as it recovers, the missiles) is not compared:
eve.rs has no NPCs yet, and those events are the evidence for the slice that has. It takes a minute, so it runs only with EVE_LIVE_SLOW=1.
"""

from __future__ import annotations

import os
import uuid

import pytest

from evecli import marshal as m
from evecli.describe import describe
from tests import test_login_to_character as base
from tests.test_others import watch
from tests.test_undock import STATION, SYSTEM, arrive, compare_events, ids, outcome

HEALTH = base.load("health.json")
pytestmark = [
    pytest.mark.skipif(not base.LIVE, reason="set EVE_LIVE_HOST=host:port to test against a running server"),
    pytest.mark.skipif(os.environ.get("EVE_LIVE_SLOW") != "1", reason="a minute: set EVE_LIVE_SLOW=1"),
]

# the attributes of the ship and its modules that say what they have taken
HEALTH_ATTRIBUTES = (3, 18, 264, 266)
TOLERANCE = 1e-6


def num(value):
    return value["$long"] if isinstance(value, dict) and "$long" in value else value


def entries(dictionary: dict) -> list:
    return dictionary["$dict"]


def keyval_fields(described: dict) -> dict:
    return {k: v for k, v in entries(described["args"])}


def health_view(answer: dict, mapping: dict) -> dict:
    """What `GetAllInfo` says of what the ship and its modules have taken: their attributes, and the state rows next to them, by the ids of this run."""
    info = keyval_fields(answer)
    view: dict = {"attributes": {}, "state": {}}
    for key, value in entries(info["shipInfo"]):
        item = mapping.get(num(key), num(key))
        attributes = {num(k): num(v) for k, v in entries(keyval_fields(value)["attributes"])}
        view["attributes"][item] = {a: attributes[a] for a in HEALTH_ATTRIBUTES if a in attributes}
    for key, value in entries(info["shipState"]["$tuple"][0]):
        row = value["$row"]
        view["state"][mapping.get(num(key), num(key))] = {k: row[k] for k in ("damage", "charge", "armorDamage", "shieldCharge", "incapacitated", "online")}
    return view


def compare_health(label: str, recorded: dict, ours: dict, mapping: dict) -> list[str]:
    a, b = health_view(recorded, mapping), health_view(ours, {})
    out: list[str] = []
    for part in ("attributes", "state"):
        if set(a[part]) != set(b[part]):
            out.append(f"{label}: the {part} are of {sorted(b[part])} in place of {sorted(a[part])}")
            continue
        for item in a[part]:
            for key, expected in a[part][item].items():
                got = b[part][item].get(key)
                same = abs(got - expected) <= TOLERANCE if isinstance(expected, float) and isinstance(got, (int, float)) else got == expected
                if not same:
                    out.append(f"{label}: {part} of {item}: {key} is {got} in place of {expected}")
    return out


def without_the_undocking_protection(events: list) -> list:
    return [e for e in events if e["name"] != "OnInvulnCancelled"]


def ships_own_health(events: list, ship: int) -> list:
    """The events that are about this ship's health and nothing else: the attributes told to its pilot and its damage state in the stream. What the
    reference's NPCs do around the ship is left out."""
    kept = []
    for event in events:
        if event["name"] == "OnModuleAttributeChanges":
            kept.append(event)
        elif event["name"] == "DoDestinyUpdate":
            try:
                actions = event["args"]["$tuple"][1]["$tuple"][1]["$tuple"][0]
            except (KeyError, TypeError):
                continue
            names = [a["$tuple"][1]["$tuple"][0] for a in actions]
            if names and set(names) == {"OnDamageStateChange"} and all(num(a["$tuple"][1]["$tuple"][1]["$tuple"][0]) == ship for a in actions):
                kept.append(event)
    return kept


def own_damage_state(events: list, ship: int, mapping: dict):
    """The ship's damage state `(shield, armor, structure)` in the first `SetState` of these events."""
    def find(node):
        if isinstance(node, dict):
            if "$dict" in node:
                for key, value in node["$dict"]:
                    if num(key) == ship and isinstance(value, dict) and "$tuple" in value and len(value["$tuple"]) == 3:
                        return value["$tuple"]
                for _, value in node["$dict"]:
                    found = find(value)
                    if found:
                        return found
            for value in node.values():
                found = find(value)
                if found:
                    return found
        elif isinstance(node, list):
            for value in node:
                found = find(value)
                if found:
                    return found
        return None

    for event in events:
        if event["name"] == "DoDestinyUpdate":
            found = find(event["args"])
            if found:
                (shield, _tau, _time), armor, structure = found[0]["$tuple"], found[1], found[2]
                return (shield, armor, structure)
    return None


def test_a_ship_is_damaged_flown_hurt_and_mended_as_it_was_in_the_reference():
    race = HEALTH["races"][0]
    run = uuid.uuid4().hex[:10]
    problems: list[str] = []
    steps = {step["label"]: step for step in race["steps"]}
    with base.connect(f"walk-health-{run}") as who:
        _, ship, modules, mapping = arrive(who, race, run)
        only = {"events": who}
        station_dogma = who.call("dogmaIM", "MachoBindObject", (STATION, 15), None)
        watch(only, 0.5)

        def step(label: str, call, seconds: float = 1.5, compare=("answer", "events")) -> list:
            raw, error = outcome(call)
            recorded = steps[label]
            if "answer" in compare:
                if error is not None:
                    problems.append(f"{label}: error {base.brief(error)} in place of the answer {base.brief(recorded.get('ok'))}")
                elif isinstance(recorded.get("ok"), str):
                    if describe(raw) != recorded["ok"]:
                        problems.append(f"{label}: answered {describe(raw)!r} in place of {recorded['ok']!r}")
                elif "GetAllInfo" in label or "asks for" in label:
                    problems.extend(compare_health(label, recorded["ok"], describe(raw), mapping))
            got = watch(only, seconds)["events"]
            if "events" in compare:
                theirs = without_the_undocking_protection(recorded["events"])
                problems.extend(compare_events(theirs, without_the_undocking_protection(got), label, mapping))
            return got

        info = lambda: who.call_bound(station_dogma[0], "GetAllInfo", True, True, None)  # noqa: E731
        slash = lambda command: (lambda: who.call("slash", "SlashCmd", command))  # noqa: E731
        step("the pilot asks for its ship", info)
        step("light damage in the hangar", slash("/dmg light"))
        step("the pilot asks for its damaged ship", info)
        step("heavy damage in the hangar", slash("/dmg heavy"))
        step("the pilot asks for its badly damaged ship", info)
        step("a heal in the hangar", slash("/heal"))
        step("the pilot asks for its healed ship", info)
        step("heavy damage before the undock", slash("/dmg heavy"))

        service = who.call("ship", "MachoBindObject", (STATION, 15), None)
        step("the damaged ship undocks", lambda: who.call_bound(service[0], "Undock", ship, False, onlineModules=m.List(modules)), 2.5, compare=())
        ballpark = who.call("beyonce", "MachoBindObject", SYSTEM, None)
        got = watch(only, 3.0)["events"]
        # the ship leaves the station with its shield full and the armor and hull it had: the damage state the client is given for it
        wanted = own_damage_state(steps["the pilot binds the ballpark"]["events"], int(race["shipID"]), mapping)
        told = own_damage_state(got, ship, mapping)
        if told is None or wanted is None or tuple(map(round6, told)) != tuple(map(round6, wanted)):
            problems.append(f"the pilot binds the ballpark: the damage state of the ship is {told} in place of {wanted}")

        space = who.call("dogmaIM", "MachoBindObject", (SYSTEM, 5), None)
        watch(only, 1.0)
        step("the pilot asks for its damaged ship in space", lambda: who.call_bound(space[0], "GetAllInfo", True, True, None), 1.0, compare=("answer",))
        # nothing is told of a shield and a capacitor that are full
        quiet = watch(only, 12.0)["events"]
        if [e for e in quiet if e["name"] == "OnModuleAttributeChanges"]:
            problems.append("the shield and the capacitor recharge: the pilot was told a charge, and both were full")
        step("damage in space", slash("/dmg light"), 1.0, compare=("answer",))
        healed = step("a heal in space", slash("/heal"), 2.0, compare=("answer",))
        theirs = ships_own_health(without_the_undocking_protection(steps["a heal in space"]["events"]), int(race["shipID"]))
        ours = ships_own_health(without_the_undocking_protection(healed), ship)
        problems.extend(compare_events(theirs, ours, "a heal in space", mapping))
    assert problems == [], "\n".join(problems[:40])


def round6(value):
    return round(float(value), 6)

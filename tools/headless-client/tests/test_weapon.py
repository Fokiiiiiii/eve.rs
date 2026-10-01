"""A pilot fires its gun at a rat, compared with what the reference was recorded doing.

    EVE_LIVE_SLOW=1 EVE_LIVE_HOST=host:port [EVE_LIVE_SERVER=reference|eve.rs] pytest tests/test_weapon.py

`oracle/fixtures/scenarios/weapon.json` (scripts/record_character_scenarios.py --only weapon) holds one character in space with a rat beside it (`/npc`, the
only target a pilot may shoot in the empire with the safety on): the pilot waits out its undocking protection, calls the rat, fires its gun (the Civilian
Gatling Railgun, which needs no charge) at it before it has locked it, locks it, fires, fires again, stops the gun (twice), fires and lets the lock go while
the gun runs. The replay does the same on any server and compares what the gun does: the answers and the errors, the events of the module (the effect
going on and off with its numbers, the special effect of the shot) and their order, the moments, and the combat log of the shots the pilot fired. What the
rat does is its own (the reference's AI is not the simulation's trees: DEVIATIONS #53) and what a shot does is a roll, so a shot's quality and damage are
compared by their keys and not their values. It needs the simulation and a minute and a half, so it runs only with EVE_LIVE_SLOW=1.
"""

from __future__ import annotations

import os
import re
import uuid

import pytest

from evecli.describe import describe
from tests import test_login_to_character as base
from tests.test_others import watch
from tests.test_target import compare_answer, plain_race, timing
from tests.test_undock import STATION, SYSTEM, arrive, args_of, compare_events, number, outcome

WEAPON = base.load("weapon.json")
pytestmark = [
    pytest.mark.skipif(not base.LIVE, reason="set EVE_LIVE_HOST=host:port to test against a running server"),
    pytest.mark.skipif(os.environ.get("EVE_LIVE_SLOW") != "1", reason="a minute and a half: set EVE_LIVE_SLOW=1"),
]

# the moments an event may come after or before the recording's: a cycle is a little under two seconds, and this side works in quarters of a second
TOLERANCE = 0.8


def changes_of(event: dict):
    """The `(item, attribute, new, old)` lines of an `OnModuleAttributeChanges`."""
    try:
        lines = event["args"]["$tuple"][1]["$tuple"][1]["$tuple"][0]
    except (KeyError, TypeError):
        return []
    out = []
    for line in lines:
        t = line["$tuple"]
        out.append((number(t[2]), t[3], t[5], t[6]))
    return out


def effect_of(event: dict):
    """The tuple an `OnGodmaShipEffect` carries."""
    return args_of(event)["$tuple"]


def fx_actions(event: dict, module: int):
    """The `OnSpecialFX` actions of a `DoDestinyUpdate` that play the effect of this module (the arguments are `(ship, module, type, target, charge, effect, ...)`)."""
    try:
        actions = event["args"]["$tuple"][1]["$tuple"][1]["$tuple"][0]
    except (KeyError, TypeError):
        return []
    return [a for a in actions if a["$tuple"][1]["$tuple"][0] == "OnSpecialFX" and number(a["$tuple"][1]["$tuple"][1]["$tuple"][1]) == module]


def of_the_gun(events: list, gun: int) -> list:
    """What the gun makes the pilot's client hear: the effect going on and off, the special effect that plays it and the numbers of the module it tells."""
    out = []
    for event in events:
        if event["name"] == "OnGodmaShipEffect" and number(effect_of(event)[0]) == gun:
            out.append(event)
        elif event["name"] == "OnModuleAttributeChanges" and changes_of(event) and all(item == gun for item, _, _, _ in changes_of(event)):
            out.append(event)
        elif event["name"] == "DoDestinyUpdate" and fx_actions(event, gun):
            out.append(event)
    return out


def shots_of(events: list) -> list:
    """The combat-log messages of the shots the pilot fired (`attackType` `me`), as `{key: value}`."""
    out = []
    for event in events:
        if event["name"] == "OnDamageMessage":
            message = {k: v for k, v in args_of(event)["$tuple"][0]["$dict"]}
            if message.get("attackType") == "me":
                out.append(message)
    return out


def slims_in(events: list) -> list:
    """The `(itemID, typeID)` of every slim item an event holds, in order."""
    found = []
    for event in events:
        for item, kind in re.findall(r'\["itemID", \{"\$long": (\d+)\}\], \["typeID", (\d+)\]', str(event).replace("'", '"')):
            found.append((int(item), int(kind)))
    return found


def test_a_pilot_fires_its_gun_as_it_did_in_the_reference():
    race = WEAPON["races"][0]
    run = uuid.uuid4().hex[:10]
    problems: list[str] = []
    steps = {step["label"]: step for step in race["steps"]}
    with base.connect(f"walk-weapon-{run}") as who:
        _, ship, modules, mapping = arrive(who, plain_race({"pilot": race}, "pilot"), run)
        gun = modules[1]
        mapping[race["modules"]["gun"]] = gun
        only = {"events": who}

        def step(label: str, call, seconds: float = 2.0, until=None) -> list:
            raw, error = outcome(call)
            problems.extend(compare_answer(label, steps[label], raw, error, mapping))
            got = watch(only, seconds, (lambda seen: until(seen["events"])) if until else None)["events"]
            theirs, ours = [e for e in steps[label]["events"] if e["name"] != "OnInvulnCancelled"], [e for e in got if e["name"] != "OnInvulnCancelled"]
            # the gun's events: the same, in the same order, at the moments of the recording
            recorded_gun, our_gun = of_the_gun(theirs, race["modules"]["gun"]), of_the_gun(ours, gun)
            problems.extend(compare_events(recorded_gun, our_gun, f"{label}", mapping))
            problems.extend(timing(recorded_gun, our_gun, label, TOLERANCE, ("OnGodmaShipEffect", "DoDestinyUpdate")))
            # the shots the pilot fired: as many as the recording's, give or take the one at the edge of the step, of the same weapon at the same target
            recorded_shots, our_shots = shots_of(theirs), shots_of(got)
            if abs(len(recorded_shots) - len(our_shots)) > 1:
                problems.append(f"{label}: {len(our_shots)} shots in the combat log in place of {len(recorded_shots)}")
            for index, (a, b) in enumerate(zip(recorded_shots, our_shots)):
                if list(a) != list(b):
                    problems.append(f"{label}: shot {index} has {list(b)} in place of {list(a)}")
                elif (number(a["weapon"]), number(b["weapon"])) != (3638, 3638) or number(b["target"]) != mapping.get(number(a["target"]), number(a["target"])):
                    problems.append(f"{label}: shot {index} of {number(b['weapon'])} at {number(b['target'])}")
                elif list(a["damageAttributes"]["$dict"]) and [k for k, _ in a["damageAttributes"]["$dict"]] != [k for k, _ in b["damageAttributes"]["$dict"]]:
                    problems.append(f"{label}: shot {index} damages {[k for k, _ in b['damageAttributes']['$dict']]}")
            return got

        service = who.call("ship", "MachoBindObject", (STATION, 15), None)
        watch(only, 0.5)
        who.call_bound(service[0], "Undock", ship, False)
        watch(only, 2.5)
        who.call("beyonce", "MachoBindObject", SYSTEM, None)
        watch(only, 3.0)
        dogma = who.call("dogmaIM", "MachoBindObject", (SYSTEM, 5), None)
        who.call_bound(dogma[0], "GetAllInfo", True, True, None)
        watch(only, 1.0)
        watch(only, 45.0, until=lambda seen: any(e["name"] == "OnInvulnCancelled" for e in seen["events"]))

        slash = lambda command: (lambda: who.call("slash", "SlashCmd", command))  # noqa: E731
        activate = lambda target: (lambda: who.call_bound(dogma[0], "Activate", gun, "targetAttack", target, 1000))  # noqa: E731
        stopped = lambda seen: any(e["name"] == "OnGodmaShipEffect" and effect_of(e)[3] == 0 for e in seen)  # noqa: E731
        raw = who.call("slash", "SlashCmd", "/npc 1 Blood Visionary")
        spawn = watch(only, 8.0)["events"]
        text = getattr(raw, "value", raw)
        text = text.decode() if isinstance(text, bytes) else str(text)
        assert text.startswith("Spawned 1 hull from Blood Visionary"), text
        npc = next((item for item, _ in slims_in(spawn) if item != ship), 0)
        assert npc, "the rat did not come into space"
        mapping[race["npcID"]] = npc

        step("the pilot fires the gun at the rat before locking it", activate(npc), 2.0)

        def locked(seen):
            try:
                return npc in [number(t) for t in who.call_bound(dogma[0], "GetTargets")]
            except Exception:  # noqa: BLE001
                return False

        who.call_bound(dogma[0], "AddTarget", npc)
        watch(only, 20.0, until=lambda seen: locked(seen))
        step("the pilot heals", slash("/heal"), 1.0)
        step("the pilot fires the gun at the rat", activate(npc), 14.0)
        step("the pilot fires the gun again", activate(npc), 1.0)
        step("the pilot stops the gun", lambda: who.call_bound(dogma[0], "Deactivate", gun, "targetAttack"), 6.0, until=stopped)
        step("the pilot stops the gun again", lambda: who.call_bound(dogma[0], "Deactivate", gun, "targetAttack"), 1.5)
        step("the pilot heals again", slash("/heal"), 1.0)
        step("the pilot fires the gun to lose the lock", activate(npc), 4.0)
        step("the pilot lets the lock go while the gun runs", lambda: who.call_bound(dogma[0], "RemoveTarget", npc), 8.0, until=stopped)
    assert problems == [], "\n".join(problems[:40])

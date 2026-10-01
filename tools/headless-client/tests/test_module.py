"""Two ships in one system, one working its afterburner, compared with what the reference was recorded doing.

    EVE_LIVE_SLOW=1 EVE_LIVE_HOST=host:port [EVE_LIVE_SERVER=reference|eve.rs] pytest tests/test_module.py

`oracle/fixtures/scenarios/module.json` (scripts/record_character_scenarios.py --only module) holds two characters by the same station: the watcher
undocks and binds the ballpark, then the actor undocks, binds, is given the dogma location of the system, flies at full speed, starts its afterburner
(twice), stops it (twice) and tries its gun and its miner where they cannot be used. Each step holds what the actor was sent (`events`) and what the
watcher was sent (`seen`), with the seconds each arrived at. The replay does the same on any server and compares: the answers and the errors, the
events and their order, the destiny updates, and the moments. What a server may do differently is in docs/compat/DEVIATIONS.md: the capacitor's charge
is told at the reference's cadence but is compared by its values and not sample by sample; the reference's refusal of the miner is its own (there is no rock
to work on), so that step is compared as an error only. The refusal of the gun is the pilot's safety, which eve.rs ports (`WeaponSafety`). It takes a minute, but the ships wait out their undocking protection first,
so it runs only with EVE_LIVE_SLOW=1.
"""

from __future__ import annotations

import os
import time
import uuid

import pytest

from evecli.describe import describe, shape
from tests import test_login_to_character as base
from tests.test_others import watch
from tests.test_target import compare_answer, only, plain_race, timing
from tests.test_undock import STATION, SYSTEM, arrive, compare_events, outcome

MODULE = base.load("module.json")
pytestmark = [
    pytest.mark.skipif(not base.LIVE, reason="set EVE_LIVE_HOST=host:port to test against a running server"),
    pytest.mark.skipif(os.environ.get("EVE_LIVE_SLOW") != "1", reason="a minute and a half: set EVE_LIVE_SLOW=1"),
]

# the moments an event may come after or before the recording's: a cycle is ten seconds long, and this side works in quarters of a second
TOLERANCE = 0.8
CAPACITOR = 18
# the steps whose error is the reference's own (a miner that has no rock to work on): only that they are refused is compared
REFUSED_ANYWAY = ("the actor works the miner on the watcher",)


def changes_of(event: dict):
    """The `(item, attribute, new, old)` lines of an `OnModuleAttributeChanges`."""
    try:
        lines = event["args"]["$tuple"][1]["$tuple"][1]["$tuple"][0]
    except (KeyError, TypeError):
        return []
    out = []
    for line in lines:
        t = line["$tuple"]
        item = t[2]["$long"] if isinstance(t[2], dict) else t[2]
        out.append((item, t[3], t[5], t[6]))
    return out


def is_capacitor(event: dict) -> bool:
    lines = changes_of(event) if event["name"] == "OnModuleAttributeChanges" else []
    return bool(lines) and all(attribute == CAPACITOR for _, attribute, _, _ in lines)


def without_capacitor(events: list) -> list:
    """The events without the stream of the capacitor's charge, and without the attribute the reference tells of a module that has nothing to do with
    the effect (its miner's range, told again at every start and stop)."""
    return [e for e in events if not is_capacitor(e) and not (e["name"] == "OnModuleAttributeChanges" and [a for _, a, _, _ in changes_of(e)] == [54])]


def capacitor_values(events: list) -> list:
    return [(e["at"], new, old) for e in events if is_capacitor(e) for _, _, new, old in changes_of(e)]


def test_a_ship_works_its_afterburner_as_it_did_in_the_reference():
    race = MODULE["races"][0]
    run = uuid.uuid4().hex[:10]
    problems: list[str] = []
    steps = {step["label"]: step for step in race["steps"]}
    with base.connect(f"walk-module-a-{run}") as a, base.connect(f"walk-module-b-{run}") as b:
        _, a_ship, _, a_mapping = arrive(a, plain_race(race, "watcher"), run)
        _, b_ship, b_modules, b_mapping = arrive(b, plain_race(race, "actor"), run)
        mapping = {**a_mapping, **b_mapping}
        both = {"events": b, "seen": a}

        def step(label: str, call, seconds: float = 2.0, until=None) -> dict:
            raw, error = outcome(call)
            if label in REFUSED_ANYWAY:
                if error is None:
                    problems.append(f"{label}: answered {base.brief(describe(raw))} in place of an error")
            else:
                problems.extend(compare_answer(label, steps[label], raw, error, mapping))
            got = watch(both, seconds, until)
            for who, key in (("the actor", "events"), ("the watcher", "seen")):
                theirs = [e for e in steps[label][key] if e["name"] != "OnInvulnCancelled"]
                ours = [e for e in got[key] if e["name"] != "OnInvulnCancelled"]
                problems.extend(compare_events(without_capacitor(theirs), without_capacitor(ours), f"{label} ({who})", mapping))
                problems.extend(timing(without_capacitor(theirs), without_capacitor(ours), f"{label} ({who})", TOLERANCE, ("OnGodmaShipEffect", "DoDestinyUpdate")))
            # the charge of the capacitor: it is told, and its values follow the reference's curve
            theirs, ours = capacitor_values(steps[label]["events"]), capacitor_values(got["events"])
            if abs(len(theirs) - len(ours)) > 2:
                problems.append(f"{label}: the capacitor's charge was told {len(ours)} times in place of {len(theirs)}")
            for index, ((_, new_r, _), (_, new_o, _)) in enumerate(zip(theirs, ours)):
                if abs(new_r - new_o) > 0.5:
                    problems.append(f"{label}: capacitor charge {index} is {new_o} in place of {new_r}")
                    break
            return got

        a_service = a.call("ship", "MachoBindObject", (STATION, 15), None)
        b_service = b.call("ship", "MachoBindObject", (STATION, 15), None)
        watch(both, 0.5)
        a.call_bound(a_service[0], "Undock", a_ship, False)
        a_ballpark = a.call("beyonce", "MachoBindObject", SYSTEM, None)
        watch(both, 2.5)
        b.call_bound(b_service[0], "Undock", b_ship, False)
        b_ballpark = b.call("beyonce", "MachoBindObject", SYSTEM, None)
        watch(both, 3.0)
        a_dogma = a.call("dogmaIM", "MachoBindObject", (SYSTEM, 5), None)
        b_dogma = b.call("dogmaIM", "MachoBindObject", (SYSTEM, 5), None)
        b.call_bound(b_dogma[0], "GetAllInfo", True, True, None)
        # the modules in the order the recorded ship had them: the afterburner, the gun, the miner
        afterburner, gun, miner = b_modules[0], b_modules[1], b_modules[2]
        watch(both, 45.0, until=lambda seen: any(e["name"] == "OnInvulnCancelled" for e in seen["events"]))

        fly = lambda name, *args: (lambda: b.call_bound(b_ballpark[0], name, *args))  # noqa: E731
        activate = lambda module, effect, target=None: (lambda: b.call_bound(b_dogma[0], "Activate", module, effect, target, 1000))  # noqa: E731
        stopped = lambda seen: any(e["name"] == "OnGodmaShipEffect" and e["args"]["$tuple"][1]["$tuple"][1]["$tuple"][3] == 0 for e in seen["events"])  # noqa: E731
        step("the actor flies at full speed", fly("CmdGotoDirection", 1.0, 0.0, 0.0), 3.0)
        step("the actor starts the afterburner", activate(afterburner, "moduleBonusAfterburner"), 13.0)
        step("the actor starts the afterburner again", activate(afterburner, "moduleBonusAfterburner"), 1.0)
        step("the actor stops the afterburner", lambda: b.call_bound(b_dogma[0], "Deactivate", afterburner, "moduleBonusAfterburner"), 12.0, until=stopped)
        step("the actor stops the afterburner again", lambda: b.call_bound(b_dogma[0], "Deactivate", afterburner, "moduleBonusAfterburner"), 2.0)
        step("the actor fires the gun with no target", activate(gun, "targetAttack"), 2.0)
        step("the actor locks the watcher", lambda: b.call_bound(b_dogma[0], "AddTarget", a_ship), 8.0, until=lambda seen: any(e["name"] == "OnTarget" for e in seen["events"]))
        step("the actor fires the gun at the watcher without a charge", activate(gun, "targetAttack", a_ship), 3.0)
        step("the actor works the miner on the watcher", activate(miner, "miningLaser", a_ship), 3.0)
        step("the actor works the miner with no target", activate(miner, "miningLaser"), 2.0)
    assert problems == [], "\n".join(problems[:40])

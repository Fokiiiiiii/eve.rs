"""A pilot attacks another in a high security system: its combat timers, its flag and what the others are told, compared with what the reference was recorded doing.

    EVE_LIVE_SLOW=1 EVE_LIVE_HOST=host:port [EVE_LIVE_SERVER=reference|eve.rs] pytest tests/test_aggression.py

`oracle/fixtures/scenarios/aggression.json` (scripts/record_character_scenarios.py --only aggression) holds two characters in space by the same station: the aggressor locks
the victim, fires its gun with the safety full (the client is refused), makes the safety none and fires again. The replay does the same on any server and compares what the
answers and the crimewatch notifications are, step by step: the weapons, PvP and criminal timers (`OnWeaponsTimerUpdate`, `OnPvpTimerUpdate`, `OnCriminalTimerUpdate`) the aggressor is told,
`OnSystemCriminalFlagUpdates` both are told, what the crime costs in security status (the attribute change of the pilot's ship, `OnSecurityStatusUpdate`, the slim item shown again to both) and the
states each asks for afterwards. What else a crime brings (a kill right, CONCORD) is not compared yet. It needs the
simulation and a minute: EVE_LIVE_SLOW=1.
"""

from __future__ import annotations

import json
import os
import uuid

import pytest

from evecli.describe import describe, differences
from tests import test_login_to_character as base
from tests.test_others import watch
from tests.test_projected import fitted
from tests.test_target import compare_answer, only, plain_race
from tests.test_undock import STATION, SYSTEM, arrive, compare_events, ids, outcome

AGGRESSION = base.load("aggression.json")
GUN = 3638
FILETIME_SECOND = 10_000_000
pytestmark = [
    pytest.mark.skipif(not base.LIVE, reason="set EVE_LIVE_HOST=host:port to test against a running server"),
    pytest.mark.skipif(os.environ.get("EVE_LIVE_SLOW") != "1", reason="a minute: set EVE_LIVE_SLOW=1"),
]

SECURITY_STATUS_ATTRIBUTE = 2610
NAMED = only("OnWeaponsTimerUpdate", "OnPvpTimerUpdate", "OnNpcTimerUpdate", "OnCriminalTimerUpdate", "OnDisapprovalTimerUpdate", "OnSystemCriminalFlagUpdates", "OnSecurityStatusUpdate")


def changes_security_status(event: dict) -> bool:
    """Whether an `OnModuleAttributeChanges` is about the pilot's security status (the other attributes of a fight are not crimewatch's)."""
    stack = [event["args"]]
    while stack:
        value = stack.pop()
        if isinstance(value, dict):
            items = value.get("$tuple")
            if isinstance(items, list) and len(items) > 3 and items[0] == "OnModuleAttributeChange" and items[3] == SECURITY_STATUS_ATTRIBUTE:
                return True
            stack.extend(value.values())
        elif isinstance(value, list):
            stack.extend(value)
    return False


def CRIMEWATCH(events: list) -> list:
    """The crimewatch events of a step: the timers, the flags, the pilot's security status and the attribute that carries it."""
    named = NAMED(events)
    return [e for e in events if e in named or (e["name"] == "OnModuleAttributeChanges" and changes_security_status(e))]


def slim_changes(events: list) -> int:
    """How many times the slim item of a ship was told again (what a change of security status does for those who see the ship)."""
    return sum(1 for e in events if e["name"] == "DoDestinyUpdate" and "OnSlimItemChange" in json.dumps(e["args"]))


def mine(described, keep: set):
    """A description with the flagged pilots of other runs left out of every set (a flag lasts a quarter of an hour, in a system every run starts in)."""
    if isinstance(described, list):
        return [mine(v, keep) for v in described]
    if isinstance(described, dict):
        out = {k: mine(v, keep) for k, v in described.items()}
        if described.get("$objectex", {}).get("$tuple", [None])[0] == {"$token": "__builtin__.set"}:
            out["list"] = [i for i in described["list"] if i in keep]
            out["$objectex"] = {"$tuple": [{"$token": "__builtin__.set"}, {"$tuple": [out["list"]]}]}
        return out
    return described


def flags_of(events: list, keep: set) -> list:
    """The crimewatch events, with the pilots of other runs left out of the flag updates (an update that is only about them is no event of this run)."""
    kept = []
    for event in events:
        if event["name"] == "OnSystemCriminalFlagUpdates":
            lists = [{"$tuple": [i for i in group["$tuple"] if i in keep]} for group in event["args"]["$tuple"][1]["$tuple"][1]["$tuple"]]
            if not any(group["$tuple"] for group in lists):
                continue
            event = {**event, "args": {"$tuple": [event["args"]["$tuple"][0], {"$tuple": [event["args"]["$tuple"][1]["$tuple"][0], {"$tuple": lists}]}]}}
        kept.append(event)
    return kept


def timer_expiries(states: dict) -> list:
    """The expiry of each of the five timers of a described `GetClientStates` answer (`None` while idle)."""
    out = []
    for timer in states["$tuple"][0]["$tuple"]:
        expiry = timer["$tuple"][1]
        out.append(expiry["$long"] if isinstance(expiry, dict) else expiry)
    return out


def test_an_attack_on_another_pilot_starts_the_timers_and_flags_the_attacker_as_the_reference_did():
    race = AGGRESSION["races"][0]
    run = uuid.uuid4().hex[:10]
    problems: list[str] = []
    steps = {step["label"]: step for step in race["steps"]}
    with base.connect(f"walk-aggression-a-{run}") as a, base.connect(f"walk-aggression-b-{run}") as b:
        _, a_ship, _, a_mapping = arrive(a, plain_race(race, "victim"), run)
        b_character, b_ship, _, b_mapping = arrive(b, plain_race(race, "aggressor"), run)
        mapping = {**a_mapping, **b_mapping}
        recorded_pilots = {int(race["victim"]["characterID"]), int(race["aggressor"]["characterID"])}
        pilots = {mapping[c] for c in recorded_pilots}
        both = {"events": b, "seen": a}

        def compared(label: str, got: dict) -> None:
            """What the aggressor and the victim were sent in a step, against what was recorded (only the crimewatch notifications)."""
            for who, key in (("the aggressor", "events"), ("the victim", "seen")):
                theirs = flags_of(CRIMEWATCH(steps[label][key]), recorded_pilots)
                ours = flags_of(CRIMEWATCH(got[key]), pilots)
                problems.extend(compare_events(theirs, ours, f"{label} ({who})", mapping))
                if slim_changes(steps[label][key]) != slim_changes(got[key]):
                    problems.append(f"{label} ({who}): the slim item was told {slim_changes(got[key])} times in place of {slim_changes(steps[label][key])}")

        def step(label: str, call, seconds: float = 2.0, until=None, exact: bool = False) -> object:
            """A call and what it was sent; the answer is compared by shape, or (the states) value by value with only the times left out."""
            raw, error = outcome(call)
            if exact and error is None:
                problems.extend(differences(base.blank_times(mine(ids(steps[label]["ok"], mapping), pilots)), base.blank_times(mine(describe(raw), pilots)), label))
            else:
                problems.extend(compare_answer(label, steps[label], raw, error, mapping))
            compared(label, watch(both, seconds, until))
            return raw

        a_service = a.call("ship", "MachoBindObject", (STATION, 15), None)
        b_service = b.call("ship", "MachoBindObject", (STATION, 15), None)
        watch(both, 0.5)
        a.call_bound(a_service[0], "Undock", a_ship, False)
        watch(both, 2.0)
        a.call("beyonce", "MachoBindObject", SYSTEM, None)
        watch(both, 2.5)
        b.call_bound(b_service[0], "Undock", b_ship, False)
        watch(both, 2.5)
        b.call("beyonce", "MachoBindObject", SYSTEM, None)
        watch(both, 3.0)
        a_dogma = a.call("dogmaIM", "MachoBindObject", (SYSTEM, 5), None)
        b_dogma = b.call("dogmaIM", "MachoBindObject", (SYSTEM, 5), None)
        watch(both, 1.0)
        a_crime = a.call("crimewatch", "MachoBindObject", (SYSTEM, 5), ("GetClientStates", (), None))
        b_crime = b.call("crimewatch", "MachoBindObject", (SYSTEM, 5), ("GetClientStates", (), None))
        recorded = steps["both bind crimewatch in space and ask for their states"]
        problems.extend(differences(base.blank_times(mine(recorded["ok"], recorded_pilots)), base.blank_times(mine(describe([a_crime[1], b_crime[1]]), pilots)), "both bind crimewatch in space and ask for their states"))
        watch(both, 1.0)
        gun = fitted(b, b.call("dogmaIM", "MachoBindObject", (SYSTEM, 5), None)[0])[GUN]

        # a ship that has just undocked cannot be locked until its protection ends (thirty seconds)
        watch(both, 45.0, until=lambda seen: any(e["name"] == "OnInvulnCancelled" for e in seen["seen"]))
        watch(both, 8.0, until=lambda seen: any(e["name"] == "OnInvulnCancelled" for e in seen["events"]))
        raw, error = outcome(lambda: b.call_bound(b_dogma[0], "AddTarget", a_ship))
        assert error is None, base.brief(error)
        watch(both, 20.0, until=lambda seen: any(e["name"] == "OnTarget" for e in seen["events"]))

        fire = lambda: b.call_bound(b_dogma[0], "Activate", gun, "targetAttack", a_ship, 1000)  # noqa: E731
        step("the aggressor fires at the victim with the safety full", fire, 2.0)
        step("the aggressor asks for its states with nothing started", lambda: b.call_bound(b_crime[0], "GetClientStates"), 1.0, exact=True)
        step("the aggressor makes its ship safety none", lambda: b.call_bound(b_crime[0], "SetSafetyLevel", 0), 1.0, exact=True)
        step("the aggressor fires at the victim with the safety none", fire, 4.0)
        states = describe(step("the aggressor asks for its states with the timers running", lambda: b.call_bound(b_crime[0], "GetClientStates"), 1.0, exact=True))
        step("the victim asks for its states while the aggressor is flagged", lambda: a.call_bound(a_crime[0], "GetClientStates"), 8.0, exact=True)
        step("the aggressor asks for its states after the response", lambda: b.call_bound(b_crime[0], "GetClientStates"), 1.0, exact=True)

        # the durations the reference had: weapons a minute, pvp and criminal a quarter of an hour (the times themselves are the run's)
        weapons, pvp, npc, criminal, disapproval = timer_expiries(states)
        assert npc is None and disapproval is None, states
        assert (pvp - weapons) // FILETIME_SECOND == 840, (weapons, pvp)
        assert criminal == pvp, (criminal, pvp)
    assert problems == [], "\n".join(problems[:40])

"""Two ships in one system, compared with what the reference was recorded doing.

    EVE_LIVE_SLOW=1 EVE_LIVE_HOST=host:port [EVE_LIVE_SERVER=reference|eve.rs] pytest tests/test_others.py

`oracle/fixtures/scenarios/others.json` (scripts/record_character_scenarios.py --only others) holds two characters in the same system: the watcher
undocks and binds the ballpark, then the actor undocks, binds, is given orders, stops, docks, undocks again and warps to a planet. Each step holds
what the actor was sent (`events`) and what the watcher was sent (`seen`), with the seconds each arrived at. The replay does the same on any server
and compares both. What a server may do differently is in docs/reference/compat/DEVIATIONS.md: the reference's ambient additions to a ballpark and the
stamps of a session (its updates for the watcher can be a stamp behind the actor's). The warp takes a minute and a half, so this runs only with
EVE_LIVE_SLOW=1.
"""

from __future__ import annotations

import os
import time
import uuid

import pytest

from evecli.describe import describe, differences, shape
from evecli.observe import events_of, wait_quietly
from tests import test_login_to_character as base
from tests.test_undock import STATION, SYSTEM, actions_of, arrive, compare_events, ids, outcome

OTHERS = base.load("others.json")
pytestmark = [
    pytest.mark.skipif(not base.LIVE, reason="set EVE_LIVE_HOST=host:port to test against a running server"),
    pytest.mark.skipif(os.environ.get("EVE_LIVE_SLOW") != "1", reason="four minutes: set EVE_LIVE_SLOW=1"),
]


def watch(clients: dict, seconds: float, until=None) -> dict:
    """The events each client is sent in `seconds` (or until `until(all so far)`), with the seconds since the start each arrived at."""
    started = time.time()
    out: dict = {name: [] for name in clients}
    while time.time() - started < seconds:
        for name, client in clients.items():
            for event in events_of(client):
                event["at"] = round(time.time() - started, 2)
                out[name].append(event)
        if until is not None and until(out):
            break
        time.sleep(0.25)
    return out


def names(event: dict) -> list:
    if event["name"] != "DoDestinyUpdate":
        return []
    try:
        actions, _ = actions_of(event)
    except (KeyError, TypeError):
        return []
    return [action[1] for action in actions]


def not_time_dependent(events: list) -> list:
    """The events without the end of the watcher's own undocking protection (thirty seconds after it undocked, whatever else is going on)."""
    return [e for e in events if e["name"] != "OnInvulnCancelled"]


def in_order(events: list) -> list:
    return sorted(events, key=lambda e: e.get("at", 0.0))


def without_noise(events: list) -> list:
    """The events without the state that was asked for and the balls the reference adds to a ballpark and takes out again."""
    return [e for e in events if names(e) not in (["SetState"],) and "AddBalls2" not in names(e)]


def removed_by(event: dict, mapping: dict) -> list:
    """The ships a `PackagedAction` of `RemoveBalls` takes out (the reference's own additions to the ballpark left out)."""
    actions, _ = actions_of(event)
    packaged = actions[0][2]["$marshalled"]["$tuple"][0]["$tuple"][1]["$tuple"][1]["$tuple"][0]
    return [mapping.get(i["$long"] if isinstance(i, dict) else i, i["$long"] if isinstance(i, dict) else i) for i in packaged]


def plain_race(race: dict, who: str) -> dict:
    """One of the two characters as the helpers of test_undock know a recorded character."""
    return {"create": race[who]["create"], "characterID": race[who]["characterID"], "shipID": race[who]["shipID"],
            "moduleIDs": [race[who]["shipID"] + 1, race[who]["shipID"] + 2, race[who]["shipID"] + 3]}


def test_two_ships_see_each_other_as_they_did_in_the_reference():
    race = OTHERS["races"][0]
    run = uuid.uuid4().hex[:10]
    problems: list[str] = []
    steps = {step["label"]: step for step in race["steps"]}
    planet = race["planetID"]
    with base.connect(f"walk-others-a-{run}") as a, base.connect(f"walk-others-b-{run}") as b:
        _, a_ship, _, a_mapping = arrive(a, plain_race(race, "watcher"), run)
        _, b_ship, _, b_mapping = arrive(b, plain_race(race, "actor"), run)
        mapping = {**a_mapping, **b_mapping}
        both = {"events": b, "seen": a}

        def compared(label: str, got: dict, **filters) -> None:
            """What the actor and the watcher were sent in a step, against what was recorded."""
            for who, key in (("the actor", "events"), ("the watcher", "seen")):
                # the end of the watcher's undocking protection comes thirty seconds after it undocked, whichever step that falls in
                keep = filters.get(key, not_time_dependent if key == "seen" else (lambda events: events))
                problems.extend(compare_events(keep(steps[label][key]), keep(got[key]), f"{label} ({who})", mapping))

        def step(label: str, call, seconds: float = 2.0, until=None, **filters) -> dict:
            recorded = steps[label]
            raw, error = outcome(call)
            if error is not None:
                problems.append(f"{label}: error {base.brief(error)}")
            elif "ok" in recorded and shape(describe(raw)) != shape(recorded["ok"]):
                problems.append(f"{label}: answered {base.brief(describe(raw))} in place of {base.brief(recorded['ok'])}")
            got = watch(both, seconds, until)
            compared(label, got, **filters)
            return got

        a_service = a.call("ship", "MachoBindObject", (STATION, 15), None)
        b_service = b.call("ship", "MachoBindObject", (STATION, 15), None)
        watch(both, 0.5)
        step("the watcher undocks", lambda: a.call_bound(a_service[0], "Undock", a_ship, False))
        a_ballpark = a.call("beyonce", "MachoBindObject", SYSTEM, None)
        compared("the watcher binds the ballpark", watch(both, 2.5))

        step("the actor undocks", lambda: b.call_bound(b_service[0], "Undock", b_ship, False), 2.5)
        b_ballpark = b.call("beyonce", "MachoBindObject", SYSTEM, None)
        compared("the actor binds the ballpark", watch(both, 3.0))

        fly = lambda name, *args: (lambda: b.call_bound(b_ballpark[0], name, *args))  # noqa: E731
        step("the actor goes in a direction", fly("CmdGotoDirection", 1.0, 0.0, 0.0))
        step("the actor slows down", fly("CmdSetSpeedFraction", 0.5))
        step("the actor orbits the station", fly("CmdOrbit", STATION, 5000))
        step("the actor stops", fly("CmdStop"))
        step("the watcher asks for the state", lambda: a.call_bound(a_ballpark[0], "UpdateStateRequest"))
        step("the actor docks", fly("CmdDock", STATION), 9.0, until=lambda seen: any(e["name"] == "$sessionchange" for e in seen["events"]))
        step("the watcher asks for the state again", lambda: a.call_bound(a_ballpark[0], "UpdateStateRequest"))

        # the actor goes out again and warps off; the watcher stays where it is
        b_service = b.call("ship", "MachoBindObject", (STATION, 15), None)
        watch(both, 0.5)
        raw, error = outcome(lambda: b.call_bound(b_service[0], "Undock", b_ship, False))
        if error is not None:
            problems.append(f"the actor undocks again: error {base.brief(error)}")
        again = watch(both, 2.5)
        compared("the actor undocks again", again, seen=not_time_dependent)
        b_ballpark = b.call("beyonce", "MachoBindObject", SYSTEM, None)
        compared("the actor binds the ballpark again", watch(both, 3.0))

        where = "the actor warps to a planet"
        recorded = steps[where]
        started = time.time()
        raw, error = outcome(lambda: b.call_bound(b_ballpark[0], "CmdWarpToStuff", "item", planet, minRange=0))
        if error is not None:
            problems.append(f"{where}: error {base.brief(error)}")
        got: dict = {"events": [], "seen": []}
        while time.time() - started < 200 and not any("Stop" in names(e) for e in got["events"]):
            seen = watch(both, 3.0)
            for key in got:
                for event in seen[key]:
                    event["at"] = round(event["at"] + (time.time() - started) - 3.0, 2)
                got[key] += not_time_dependent([e for e in seen[key] if names(e) != ["SetState"]])

        # what the actor was sent: the update that starts the warp, the ships it loses sight of, the dropout and the rest
        theirs = in_order(not_time_dependent(recorded["events"]))
        ours = in_order(got["events"])
        order = lambda events: [tuple(names(e)) or (e["name"],) for e in events if names(e) != ["PackagedAction"]]  # noqa: E731
        if order(ours) != order(theirs):
            problems.append(f"{where}: the actor was sent {order(ours)} in place of {order(theirs)}")
        lost = [e for e in ours if names(e) == ["PackagedAction"]]
        lost_theirs = [e for e in theirs if names(e) == ["PackagedAction"]]
        if len(lost) != 1 or len(lost_theirs) != 1:
            problems.append(f"{where}: {len(lost)} removals in place of {len(lost_theirs)}")
        else:
            ships = {a_ship, b_ship}
            wanted = [i for i in removed_by(lost_theirs[0], mapping) if i in ships]
            lost_ours = [i for i in removed_by(lost[0], mapping) if i in ships]
            if lost_ours != wanted:
                problems.append(f"{where}: the actor lost sight of {lost_ours} in place of {wanted}")
            if abs(lost[0]["at"] - lost_theirs[0]["at"]) > 2.5:
                problems.append(f"{where}: the actor lost sight after {lost[0]['at']} s, in the recording after {lost_theirs[0]['at']} s")

        # what the watcher was sent: a turn at once, the warp when the ship enters it, and the ship taken out of view
        theirs = in_order(recorded["seen"])
        ours = in_order(got["seen"])
        if [tuple(names(e)) for e in ours] != [tuple(names(e)) for e in theirs]:
            problems.append(f"{where}: the watcher was sent {[names(e) for e in ours]} in place of {[names(e) for e in theirs]}")
        else:
            expected = actions_of(theirs[1])[0]
            actual = actions_of(ours[1])[0]
            problems += differences(base.blank_times(ids([[a[1], a[2]] for a in expected], mapping)), base.blank_times([[a[1], a[2]] for a in actual]), f"{where} (the watcher): the warp")
            if [a[1] for a in actions_of(ours[0])[0]] != ["GotoDirection"]:
                problems.append(f"{where}: the watcher's first update is {names(ours[0])}")
            expected_gone = [i for i in removed_by(theirs[2], mapping) if i in ships]
            actual_gone = [i for i in removed_by(ours[2], mapping) if i in ships]
            if actual_gone != expected_gone:
                problems.append(f"{where}: the watcher lost sight of {actual_gone} in place of {expected_gone}")
            for index, tolerance in ((1, 2.0), (2, 2.5)):
                if abs(ours[index]["at"] - theirs[index]["at"]) > tolerance:
                    problems.append(f"{where} (the watcher): event {index} after {ours[index]['at']} s, in the recording after {theirs[index]['at']} s")

        step("the watcher asks for the state after the warp", lambda: a.call_bound(a_ballpark[0], "UpdateStateRequest"))
    assert problems == [], "\n".join(problems[:40])

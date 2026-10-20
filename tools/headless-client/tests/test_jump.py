"""A ship warps to a stargate and jumps, compared with what the reference was recorded doing.

    EVE_LIVE_SLOW=1 EVE_LIVE_HOST=host:port [EVE_LIVE_SERVER=reference|eve.rs] pytest tests/test_jump.py

`oracle/fixtures/scenarios/jump.json` (scripts/record_character_scenarios.py --only jump) holds a new character's undock, `CmdWarpToStuff` to the
stargate of its system, `CmdStargateJump` with every event the client was sent while the ship went (with the seconds each arrived at), and the
ballpark of the next system bound and asked for its state. The warp takes a minute and a half, so this runs only with EVE_LIVE_SLOW=1.

What a server may do differently (docs/reference/compat/DEVIATIONS.md): where the ship lands by the gate and where it appears in the next system (both are
random in the reference, inside the same bounds), the customs offices, props, ambient ships and gate guns the reference adds to a system's
ballpark, the position update the reference sends 0.5 s after the jump-out and the balls it adds a few seconds after the bind, and the ship
arriving cloaked (the reference's balls carry the jump cloak; eve.rs tells the client with `OnJumpCloakUpdated` only).
"""

from __future__ import annotations

import os
import time
import uuid

import pytest

from evecli.describe import describe, differences, shape
from evecli.observe import timed_events, wait_quietly
from tests import test_login_to_character as base
from tests.test_undock import (
    STATION,
    SYSTEM,
    actions_of,
    args_of,
    collapse,
    compare_events,
    compare_state,
    arrive,
    ids,
    keyval,
    number,
    outcome,
)

JUMP = base.load("jump.json")
pytestmark = [
    pytest.mark.skipif(not base.LIVE, reason="set EVE_LIVE_HOST=host:port to test against a running server"),
    pytest.mark.skipif(os.environ.get("EVE_LIVE_SLOW") != "1", reason="two minutes: set EVE_LIVE_SLOW=1"),
]

# how far the second of an event may be from the recording's: the reference times the handoff by its own clock and the stamp it sent the effect at
TOLERANCE = 1.6
# ids the reference adds to a system's ballpark that no static data holds: its ambient ships (98005…) and gate guns and customs offices
AMBIENT = range(980_050_000_000, 980_060_000_000)

def names(event: dict) -> list:
    if event["name"] != "DoDestinyUpdate":
        return []
    try:
        actions, _ = actions_of(event)
    except (KeyError, TypeError):
        return []
    return [action[1] for action in actions]

def in_order(events: list) -> list:
    return sorted(events, key=lambda e: e.get("at", 0.0))

def without_noise(events: list) -> list:
    """The events without the state the polling asks for and the balls the reference takes out of the ballpark or adds to it in warp."""
    return [e for e in events if names(e) not in (["SetState"], ["PackagedAction"]) and "AddBalls2" not in names(e)]

def milestones(events: list) -> dict:
    out: dict = {}
    for event in events:
        for name in names(event):
            if name == "SetBallMassive" and "dropout" not in out:
                out["dropout"] = event["at"]
            if name == "Stop":
                out["rest"] = event["at"]
    return out

def slim_of(extras: list, ship_id) -> list:
    """The entries of an `AddBalls2` (the slim item and the damage state) that are the ship's own: the reference adds its ambient ships to the
    ball list of the ship that arrives, so the first update of the bind carries eleven of them in place of one."""
    return [e for e in extras if number({k: v for k, v in e["$tuple"][0]["$dict"]}["itemID"]) == ship_id]

def ship_actions(event: dict, ship_id) -> list:
    """The actions of an update that are the ship's own (what the reference sends for every ambient ship too is left out)."""
    actions, _ = actions_of(event)
    return [a for a in actions if number(a[2]["$tuple"][0]) == ship_id]

def compare_bound_state(recorded: dict, ours: dict, where: str, mapping: dict) -> list[str]:
    """`SetState` of a system: as test_undock.compare_state, leaving out the ambient ships the reference adds to it."""
    def without_ambient(described: dict) -> dict:
        state = described["$tuple"][0]
        fields = [[k, [s for s in v if number(keyval(s)["itemID"]) not in AMBIENT] if k == "slims" else v] for k, v in state["args"]["$dict"]]
        return {"$tuple": [{**state, "args": {"$dict": fields}}, *described["$tuple"][1:]]}

    # (a reference that is replayed against itself has ambient ships of its own, with ids of this run)
    return compare_state(without_ambient(recorded), without_ambient(ours), where, mapping)

def test_a_ship_warps_to_a_gate_and_jumps_as_the_reference_did():
    race = JUMP["races"][0]
    run = uuid.uuid4().hex[:10]
    problems: list[str] = []
    steps = {step["label"]: step for step in race["steps"]}
    gate, next_gate, next_system = race["stargateID"], race["nextStargateID"], race["nextSolarSystemID"]
    with base.connect(f"walk-jump-{run}") as who:
        character, ship_id, _, mapping = arrive(who, race, run)
        ship = who.call("ship", "MachoBindObject", (STATION, 15), None)
        wait_quietly(who, 0.5)
        raw, error = outcome(lambda: who.call_bound(ship[0], "Undock", ship_id, False))
        assert error is None, base.brief(error)
        problems += compare_events(steps["undock"]["events"], wait_quietly(who, 2.0), "undock", mapping)
        ballpark = who.call("beyonce", "MachoBindObject", SYSTEM, None)
        problems += compare_events(steps["bind the ballpark"]["events"], wait_quietly(who, 2.5), "bind the ballpark", mapping)

        # the warp: to a random point by the gate, so what is compared is the shape of the update and when the ship drops out and comes to rest
        where = "warp to the stargate"
        recorded = steps[where]
        started = time.time()
        raw, error = outcome(lambda: who.call_bound(ballpark[0], "CmdWarpToStuff", "item", gate, minRange=0))
        if error is not None:
            problems.append(f"{where}: error {base.brief(error)}")
        events = timed_events(who, 1.0)
        while time.time() - started < 200 and not any("Stop" in names(e) for e in events):
            who.call_bound(ballpark[0], "UpdateStateRequest")
            seen = timed_events(who, 3.0)
            for event in seen:
                event["at"] = round(event["at"] + (time.time() - started) - 3.0, 2)
            events += seen
        ours, theirs = without_noise(in_order(events)), without_noise(in_order(recorded["events"]))
        first_ours, first_theirs = ours[0], theirs[0]
        if names(first_ours) != names(first_theirs):
            problems.append(f"{where}: first update {names(first_ours)} in place of {names(first_theirs)}")
        else:
            for (_, name, expected), (_, _, actual) in zip(actions_of(first_theirs)[0], actions_of(first_ours)[0]):
                if name == "WarpTo":  # the point is random: the shape and the warp factor are what the client works its warp out from
                    if shape(ids(expected, mapping)) != shape(actual) or expected["$tuple"][5] != actual["$tuple"][5]:
                        problems.append(f"{where}.WarpTo: {base.brief(actual)} in place of {base.brief(expected)}")
                else:
                    problems += differences(base.blank_times(ids(expected, mapping)), base.blank_times(actual), f"{where}.{name}")
        got, expected = milestones(ours), milestones(theirs)
        for name in ("dropout", "rest"):
            if name not in got:
                problems.append(f"{where}: the ship never came to {name}")
            elif abs(got[name] - expected[name]) > 3.0:
                problems.append(f"{where}: {name} after {got[name]} s, in the recording after {expected[name]} s")

        # the state at the gate: the same bodies as recorded (the ambient ships and the like left out)
        raw, error = outcome(lambda: who.call_bound(ballpark[0], "UpdateStateRequest"))
        state = [e for e in timed_events(who, 2.0) if names(e) == ["SetState"]]
        if not state:
            problems.append("look at the state: no state")
        else:
            recorded_state = [a for a in actions_of(steps["look at the state"]["events"][0])[0] if a[1] == "SetState"][0]
            ours_state = [a for a in actions_of(state[0])[0] if a[1] == "SetState"][0]
            problems += compare_bound_state(recorded_state[2], ours_state[2], "look at the state", mapping)

        # the jump
        where = "jump"
        recorded = steps[where]
        jumped_at = time.time()
        raw, error = outcome(lambda: who.call_bound(ballpark[0], "CmdStargateJump", gate, next_gate, ship_id))
        if error is not None:
            problems.append(f"{where}: error {base.brief(error)}")
        else:
            answer = describe(raw)
            if shape(answer) != shape(recorded["ok"]):
                problems.append(f"{where}: answered {base.brief(answer)} in place of {base.brief(recorded['ok'])}")
        seen = timed_events(who, 12.0, lambda e: any(x["name"] == "$sessionchange" for x in e))
        # the reference sends the ball's position again half a second after the effect (DEVIATIONS #24)
        not_the_resync = lambda e: names(e) != ["SetBallPosition", "SetBallVelocity", "SetSpeedFraction", "Stop"]  # noqa: E731
        problems += compare_events([e for e in recorded["events"] if not_the_resync(e)], [e for e in seen if not_the_resync(e)], where, mapping)
        moment = lambda events, label: next((e["at"] for e in events if e["name"] == label), None)  # noqa: E731
        change_ours, change_theirs = moment(seen, "$sessionchange"), moment(recorded["events"], "$sessionchange")
        if change_ours is None:
            problems.append(f"{where}: the session did not change")
        elif abs(change_ours - change_theirs) > TOLERANCE:
            problems.append(f"{where}: the session changed after {change_ours} s, in the recording after {change_theirs} s")

        # the ballpark of the next system
        where = "bind the ballpark of the next system"
        recorded = steps[where]
        raw, error = outcome(lambda: who.call("beyonce", "MachoBindObject", next_system, None))
        if error is not None:
            problems.append(f"{where}: error {base.brief(error)}")
            assert problems == [], "\n".join(problems[:40])
        landed = raw
        bound = wait_quietly(who, 4.0)
        theirs = collapse(recorded["events"])[:5]
        ours = collapse(bound)[:5]
        if [(e["name"], e["idType"]) for e in ours] != [(e["name"], e["idType"]) for e in theirs]:
            problems.append(f"{where}: events {[e['name'] for e in ours]} in place of {[e['name'] for e in theirs]}")
        else:
            for index, (a, b) in enumerate(zip(theirs, ours)):
                if a["name"] != "DoDestinyUpdate":
                    problems += differences(base.blank_times(ids(args_of(a), mapping)), base.blank_times(args_of(b)), f"{where}: event {index} {a['name']}")
                    continue
                (actions_a, wait_a), (actions_b, wait_b) = actions_of(a), actions_of(b)
                if wait_a != wait_b:
                    problems.append(f"{where}: event {index} wait {wait_b} in place of {wait_a}")
                if actions_a[0][1] == "AddBalls2":
                    theirs_slim = slim_of(ids(actions_a[0][2], mapping)["$tuple"][0]["$tuple"][1], ship_id)
                    ours_slim = slim_of(actions_b[0][2]["$tuple"][0]["$tuple"][1], ship_id)
                    if len(theirs_slim) != 1 or len(ours_slim) != 1:
                        problems.append(f"{where}: event {index} AddBalls2 holds {len(ours_slim)} slim items of the ship, the recording {len(theirs_slim)}")
                    else:
                        problems += differences(base.blank_times(theirs_slim), base.blank_times(ours_slim), f"{where}: event {index} AddBalls2.slims")
                elif actions_a[0][1] == "SetState":
                    problems += compare_bound_state(actions_a[0][2], actions_b[0][2], f"{where}: event {index} SetState", mapping)
                else:  # the numbers: the ship's own (the reference adds those of its ambient ships)
                    mine = [(n, differences(base.blank_times(ids(x, mapping)), base.blank_times(y), n)) for (_, n, x), (_, _, y) in zip(ship_actions(a, race["shipID"]), ship_actions(b, ship_id))]
                    if [n for n, _ in mine] != ["SetBallAgility", "SetBallMass", "SetMaxSpeed"]:
                        problems.append(f"{where}: event {index} the ship's numbers {[n for n, _ in mine]}")
                    for _, found in mine:
                        problems += found

        raw, error = outcome(lambda: who.call_bound(landed[0], "UpdateStateRequest"))
        state = [e for e in timed_events(who, 3.0) if names(e) == ["SetState"]]
        if not state:
            problems.append("look at the state there: no state")
        else:
            recorded_state = [a for a in actions_of(steps["look at the state there"]["events"][0])[0] if a[1] == "SetState"][0]
            ours_state = [a for a in actions_of(state[0])[0] if a[1] == "SetState"][0]
            problems += compare_bound_state(recorded_state[2], ours_state[2], "look at the state there", mapping)
    assert problems == [], "\n".join(problems[:40])

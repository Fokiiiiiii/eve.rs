"""A ship warps to a stargate and jumps, compared with what the reference was recorded doing.

    EVE_LIVE_SLOW=1 EVE_LIVE_HOST=host:port [EVE_LIVE_SERVER=reference|eve.rs] pytest tests/test_jump.py

`oracle/fixtures/scenarios/jump.json` (scripts/record_character_scenarios.py --only jump) holds a new character's undock, `CmdWarpToStuff` to the
stargate of its system, `CmdStargateJump` with every event the client was sent while the ship went (with the seconds each arrived at), and the
ballpark of the next system bound and asked for its state. The warp takes a minute and a half, so this runs only with EVE_LIVE_SLOW=1.

What a server may do differently (docs/reference/compat/DEVIATIONS.md): where the ship lands by the gate and where it appears in the next system (both are
random in the reference, inside the same bounds), the customs offices, props, ambient ships and gate guns the reference adds to a system's
ballpark, the position update the reference sends 0.5 s after the jump-out and the balls it adds a few seconds after the bind, and the ship
arriving cloaked. The Host sends the owner's jump cloak timer, while the ballpark `SetState` carries the native cloak mode;
destination observers receive the ship only after its cloak ends or the pilot moves.
"""

from __future__ import annotations

import copy
import os
import time
import uuid

import pytest

from evecli import marshal as m
from evecli.describe import describe, differences, shape
from evecli.observe import timed_events, wait_quietly
from tests import test_login_to_character as base
from tests.test_undock import (
    STATION,
    SYSTEM,
    actions_of,
    args_of,
    collapse,
    compare_actions,
    compare_destiny_updates,
    compare_events,
    compare_events_by_source,
    compare_add_balls,
    compare_state,
    arrive,
    add_ball_slim,
    ids,
    keyval,
    number,
    online_modules_wire,
    outcome,
)

JUMP = base.load("jump.json")
REFERENCE = base.REFERENCE
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
    """Exclude polled state, actionless ticks, and non-ship balls from both runs."""
    # V24 Michelle.FlushState logs and returns immediately for an empty state.
    return [
        event for event in events
        if names(event)
        and names(event) not in (["SetState"], ["PackagedAction"])
        and "AddBalls2" not in names(event)
    ]

def milestones(events: list) -> dict:
    out: dict = {}
    for event in events:
        for name in names(event):
            if name == "SetBallMassive" and "dropout" not in out:
                out["dropout"] = event["at"]
            if name == "Stop":
                out["rest"] = event["at"]
    return out

def compare_bound_events(recorded: list[dict], ours: list[dict], ship_id: int, mapping: dict, where: str) -> list[str]:
    """Compare the first system state and the player's actions, independent of Destiny packet boundaries."""
    out: list[str] = []
    core_notices = ("OnSetTimeDilation", "DoSimClockRebase")
    expected_notices = [event for event in recorded if event["name"] in core_notices]
    actual_notices = [event for event in ours if event["name"] in core_notices]
    if [(event["name"], event["idType"]) for event in expected_notices] != [
        (event["name"], event["idType"]) for event in actual_notices
    ]:
        out.append(
            f"{where}: core notices {[event['name'] for event in actual_notices]} "
            f"in place of {[event['name'] for event in expected_notices]}"
        )
    else:
        for index, (expected, actual) in enumerate(zip(expected_notices, actual_notices)):
            out += compare_events([expected], [actual], f"{where}: notice {index} {expected['name']}", mapping)

    def destiny(events: list[dict]) -> list[list]:
        return [actions_of(event)[0] for event in events if event["name"] == "DoDestinyUpdate"]

    expected_updates, actual_updates = destiny(recorded), destiny(ours)
    expected_state_packets = [
        actions_of(event) for event in recorded
        if event["name"] == "DoDestinyUpdate" and any(action[1] == "SetState" for action in actions_of(event)[0])
    ]
    actual_state_packets = [
        actions_of(event) for event in ours
        if event["name"] == "DoDestinyUpdate" and any(action[1] == "SetState" for action in actions_of(event)[0])
    ]

    def wait_contract(events: list[dict], state_packets: list[tuple], side: str) -> None:
        if len(state_packets) != 1:
            out.append(f"{where}: {side} SetState packet count {len(state_packets)}")
            return
        wait = state_packets[0][1]
        if side == "eve.rs" and not wait:
            out.append(f"{where}: eve.rs SetState must wait for its separate same-stamp follow-up")
        if not wait:
            return
        state_stamps = {
            number(action[0])
            for action in state_packets[0][0]
            if action[1] == "SetState"
        }
        state_index = next(
            index for index, event in enumerate(events)
            if event["name"] == "DoDestinyUpdate"
            and any(action[1] == "SetState" for action in actions_of(event)[0])
        )
        released = any(
            event["name"] == "DoDestinyUpdate"
            and not actions_of(event)[1]
            and any(number(action[0]) in state_stamps for action in actions_of(event)[0])
            for event in events[state_index + 1:]
        )
        if not released:
            out.append(f"{where}: {side} waited SetState has no same-stamp update to release it")

    wait_contract(recorded, expected_state_packets, "EVEJS")
    wait_contract(ours, actual_state_packets, "eve.rs")
    expected_states = [action for update in expected_updates for action in update if action[1] == "SetState"]
    actual_states = [action for update in actual_updates for action in update if action[1] == "SetState"]
    if len(expected_states) != 1 or len(actual_states) != 1:
        out.append(f"{where}: SetState count {len(actual_states)} in place of {len(expected_states)}")
    else:
        out += compare_bound_state(expected_states[0][2], actual_states[0][2], f"{where}.SetState", mapping)

    def selected(actions: list[list], *, map_recorded_ids: bool) -> list[tuple[int, str, dict]]:
        return [
            action for action in actions
            if action[1] != "SetState"
            and action[1] != "AddBalls2"
            and action[2].get("$tuple")
            and (
                mapping.get(number(action[2]["$tuple"][0]), number(action[2]["$tuple"][0]))
                if map_recorded_ids else number(action[2]["$tuple"][0])
            ) == ship_id
        ]

    expected_ship = selected([action for update in expected_updates for action in update], map_recorded_ids=True)
    actual_ship = selected([action for update in actual_updates for action in update], map_recorded_ids=False)
    out += compare_actions(expected_ship, actual_ship, f"{where}.ship", mapping)

    expected_balls = [action for update in expected_updates for action in update if action[1] == "AddBalls2"]
    actual_balls = [action for update in actual_updates for action in update if action[1] == "AddBalls2"]
    if len(expected_balls) != len(actual_balls):
        out.append(f"{where}: AddBalls2 count {len(actual_balls)} in place of {len(expected_balls)}")
    else:
        for index, (expected, actual) in enumerate(zip(expected_balls, actual_balls)):
            out += compare_add_balls(expected[2], actual[2], f"{where}.AddBalls2[{index}]", mapping)
    return out

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
        character, ship_id, _, mapping, online_modules = arrive(
            who, race, run, skip_tutorial=True, include_online_modules=True,
        )
        ship = who.call("ship", "MachoBindObject", (STATION, 15), None)
        wait_quietly(who, 0.5)
        raw, error = outcome(lambda: who.call_bound(ship[0], "Undock", m.Long(ship_id), False, onlineModules=online_modules_wire(online_modules)))
        assert error is None, base.brief(error)
        # Before the first ballpark bind, V24 Michelle.DoDestinyUpdate drops the stream. Compare
        # those server messages with the equivalent updates the client can first consume at bind.
        undock_events = wait_quietly(who, 2.0)
        problems += compare_events(
            [event for event in steps["undock"]["events"] if event["name"] != "DoDestinyUpdate"],
            [event for event in undock_events if event["name"] != "DoDestinyUpdate"],
            "undock",
            mapping,
        )
        ballpark = who.call("beyonce", "MachoBindObject", SYSTEM, None)
        bind_events = wait_quietly(who, 2.5)
        problems += compare_events(
            [event for event in steps["bind the ballpark"]["events"] if event["name"] != "DoDestinyUpdate"],
            [event for event in bind_events if event["name"] != "DoDestinyUpdate"],
            "bind the ballpark",
            mapping,
        )
        problems += compare_destiny_updates(
            [event for step in (steps["undock"], steps["bind the ballpark"]) for event in step["events"] if event["name"] == "DoDestinyUpdate"],
            [event for event in (*undock_events, *bind_events) if event["name"] == "DoDestinyUpdate"],
            "undock and bind the ballpark",
            mapping,
        )

        # the warp: to a random point by the gate, so what is compared is the shape of the update and when the ship drops out and comes to rest
        where = "warp to the stargate"
        recorded = steps[where]
        started = time.time()
        raw, error = outcome(lambda: who.call_bound(ballpark[0], "CmdWarpToStuff", "item", gate, minRange=0))
        if error is not None:
            problems.append(f"{where}: error {base.brief(error)}")
        events = timed_events(who, 1.0)
        def terminal_warp_event(event: dict) -> bool:
            # EveJS 261108.1 completes this warp with SetBallMassive and no Stop; eve.rs sends the
            # terminal SetBallPosition/SetBallVelocity/SetSpeedFraction/Stop update consumed by V24.
            return "Stop" in names(event) if not REFERENCE else "SetBallMassive" in names(event)

        while time.time() - started < 200 and not any(terminal_warp_event(event) for event in events):
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
            if name not in expected:
                continue
            if name not in got:
                problems.append(f"{where}: the ship never came to {name}")
            elif abs(got[name] - expected[name]) > 4.0:
                problems.append(f"{where}: {name} after {got[name]} s, in the recording after {expected[name]} s")
        if not REFERENCE and "rest" not in got:
            problems.append(f"{where}: eve.rs did not send the V24 terminal stop update")

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
        raw, error = outcome(lambda: who.call_bound(ballpark[0], "CmdStargateJump", gate, next_gate, m.Long(ship_id)))
        if error is not None:
            problems.append(f"{where}: error {base.brief(error)}")
        # V24's gateJumpSvc.py:124 passes CmdStargateJump to PerformSessionChange and ignores its return value;
        # compare the call's success and the subsequent ordered session/destiny events below.
        seen = timed_events(who, 12.0, lambda e: any(x["name"] == "$sessionchange" for x in e))
        # the reference sends the ball's position again half a second after the effect (DEVIATIONS #24)
        not_the_resync = lambda e: names(e) != ["SetBallPosition", "SetBallVelocity", "SetSpeedFraction", "Stop"]  # noqa: E731
        # Jump-queue UI and Destiny's JumpOut presentation have separate V24 handlers. Preserve order
        # within each stream while allowing their independent notifications to interleave.
        problems += compare_events_by_source(
            [e for e in recorded["events"] if not_the_resync(e)],
            [e for e in seen if not_the_resync(e)],
            where,
            mapping,
        )
        moment = lambda events, label: next((e["at"] for e in events if e["name"] == label), None)  # noqa: E731
        change_ours, change_theirs = moment(seen, "$sessionchange"), moment(recorded["events"], "$sessionchange")
        if change_ours is None:
            problems.append(f"{where}: the session did not change")
        elif abs(change_ours - change_theirs) > TOLERANCE:
            problems.append(f"{where}: the session changed after {change_ours} s, in the recording after {change_theirs} s")
        if who.session is None:
            problems.append(f"{where}: no client session snapshot after the change")
        else:
            current = who.session.session_init
            for key, expected in (
                ("solarsystemid", next_system),
                ("solarsystemid2", next_system),
                ("locationid", next_system),
                ("stationid", None),
                ("structureid", None),
            ):
                if current.get(key) != expected:
                    problems.append(f"{where}: session {key} is {current.get(key)!r}, expected {expected!r}")

        # the ballpark of the next system
        where = "bind the ballpark of the next system"
        recorded = steps[where]
        raw, error = outcome(lambda: who.call("beyonce", "MachoBindObject", next_system, None))
        if error is not None:
            problems.append(f"{where}: error {base.brief(error)}")
            assert problems == [], "\n".join(problems[:40])
        else:
            answer = describe(raw)
            if shape(answer) != shape(recorded["ok"]):
                problems.append(f"{where}: answered {base.brief(answer)} in place of {base.brief(recorded['ok'])}")
        landed = raw
        bound = wait_quietly(who, 4.0)
        problems += compare_bound_events(recorded["events"], bound, ship_id, mapping, where)

        raw, error = outcome(lambda: who.call_bound(landed[0], "UpdateStateRequest"))
        state = [e for e in timed_events(who, 3.0) if names(e) == ["SetState"]]
        if not state:
            problems.append("look at the state there: no state")
        else:
            recorded_state = [a for a in actions_of(steps["look at the state there"]["events"][0])[0] if a[1] == "SetState"][0]
            ours_state = [a for a in actions_of(state[0])[0] if a[1] == "SetState"][0]
            problems += compare_bound_state(recorded_state[2], ours_state[2], "look at the state there", mapping)
    assert problems == [], "\n".join(problems[:40])


def _jump_from_station(who, race: dict, run: str, ship_id: int, modules: list[tuple[int, int]]):
    """Run the complete undock, gate warp and jump path for a second client."""
    ship = who.call("ship", "MachoBindObject", (STATION, 15), None)
    raw, error = outcome(
        lambda: who.call_bound(
            ship[0], "Undock", m.Long(ship_id), False, onlineModules=online_modules_wire(modules)
        )
    )
    assert error is None, base.brief(error)
    wait_quietly(who, 2.0)

    ballpark = who.call("beyonce", "MachoBindObject", SYSTEM, None)
    wait_quietly(who, 2.5)
    gate = race["stargateID"]
    next_gate = race["nextStargateID"]
    next_system = race["nextSolarSystemID"]
    _, error = outcome(lambda: who.call_bound(ballpark[0], "CmdWarpToStuff", "item", gate, minRange=0))
    assert error is None, base.brief(error)

    started = time.time()
    events = timed_events(who, 1.0)

    def warp_finished(event: dict) -> bool:
        return any(name in names(event) for name in ("Stop", "SetBallMassive"))

    while time.time() - started < 200 and not any(warp_finished(event) for event in events):
        who.call_bound(ballpark[0], "UpdateStateRequest")
        events.extend(timed_events(who, 3.0))
    assert any(warp_finished(event) for event in events), f"{run}: gate warp did not finish"

    _, error = outcome(
        lambda: who.call_bound(ballpark[0], "CmdStargateJump", gate, next_gate, m.Long(ship_id))
    )
    assert error is None, base.brief(error)
    jump_events = wait_quietly(
        who, 15.0, lambda seen: any(event["name"] == "$sessionchange" for event in seen)
    )
    assert any(event["name"] == "$sessionchange" for event in jump_events), f"{run}: no jump session change"
    landed = who.call("beyonce", "MachoBindObject", next_system, None)
    wait_quietly(who, 3.0)
    return landed


def test_a_destination_observer_sees_the_jumping_ship_after_movement_cancels_its_cloak():
    """A gate-cloaked arrival stays hidden until a valid movement command cancels its cloak."""
    race = JUMP["races"][0]
    run = uuid.uuid4().hex[:10]
    def named_race(label: str) -> dict:
        named = copy.deepcopy(race)
        parts = named["create"]["call"][2][0].split(" ", 1)
        named["create"]["call"][2][0] = f"{label} {parts[1]}"
        return named

    def has_traveler_add(events: list[dict]) -> bool:
        return any(
            event["name"] == "DoDestinyUpdate"
            and any(
                action[1] == "AddBalls2"
                and any(
                    number(keyval(add_ball_slim(row))["itemID"]) == traveler_ship
                    for row in action[2]["$tuple"][0]["$tuple"][1]
                )
                for action in actions_of(event)[0]
            )
            for event in events
        )

    with base.connect(f"jump-observer-{run}") as observer, base.connect(f"jump-traveler-{run}") as traveler:
        _, observer_ship, _, _, observer_modules = arrive(
            observer, named_race("Obs"), run, skip_tutorial=True, include_online_modules=True
        )
        _, traveler_ship, _, _, traveler_modules = arrive(
            traveler, named_race("Trav"), run, skip_tutorial=True, include_online_modules=True
        )

        # The observer first performs the same real gate transition, then binds the
        # destination ballpark and remains connected while the other pilot arrives.
        _jump_from_station(observer, race, run, observer_ship, observer_modules)
        traveler_park = _jump_from_station(traveler, race, run, traveler_ship, traveler_modules)
        hidden = wait_quietly(observer, 8.0)
        _, error = outcome(lambda: traveler.call_bound(traveler_park[0], "CmdSetSpeedFraction", 1.0))
        assert error is None, base.brief(error)
        owner_events = wait_quietly(traveler, 2.0)
        revealed = wait_quietly(observer, 10.0, until=has_traveler_add)

    arriving_ships = []
    jump_effects = []
    for event in hidden:
        if event["name"] != "DoDestinyUpdate":
            continue
        actions, _ = actions_of(event)
        for _, name, args in actions:
            if name == "AddBalls2":
                for row in args["$tuple"][0]["$tuple"][1]:
                    slim = add_ball_slim(row)
                    if number(keyval(slim)["itemID"]) == traveler_ship:
                        arriving_ships.append(slim)
            elif name == "OnSpecialFX":
                values = args["$tuple"]
                if number(values[0]) == traveler_ship and values[5] == "effects.JumpIn":
                    jump_effects.append(values)

    assert not arriving_ships, f"observer saw a gate-cloaked ship in AddBalls2 before it moved: {arriving_ships}"
    if not REFERENCE:
        assert not jump_effects, f"eve.rs sent JumpIn before adding the arriving ball: {jump_effects}"

    cloak_notices = [
        event["name"]
        for event in owner_events
        if event["name"] in ("OnJumpCloakCancelled", "OnInvulnCancelled")
    ]
    assert cloak_notices == ["OnJumpCloakCancelled", "OnInvulnCancelled"], cloak_notices

    visible_rows = [
        add_ball_slim(row)
        for event in revealed
        if event["name"] == "DoDestinyUpdate"
        for action in actions_of(event)[0]
        if action[1] == "AddBalls2"
        for row in action[2]["$tuple"][0]["$tuple"][1]
        if number(keyval(add_ball_slim(row))["itemID"]) == traveler_ship
    ]
    assert len(visible_rows) == 1, f"observer did not receive exactly one arriving ship after cloak cancellation: {visible_rows}"

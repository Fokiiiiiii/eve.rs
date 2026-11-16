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
import math
import time
import uuid
from collections import Counter
from copy import deepcopy

import pytest

from evecli.describe import describe, differences, destiny_state_fields, shape
from evecli.observe import timed_events, wait_quietly
from tests import test_login_to_character as base
from tests.test_undock import (
    STATION,
    SYSTEM,
    actions_of,
    args_of,
    compare_events_by_source,
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
DESTINATION_DUPLICATE_SHIP_STATS = frozenset({"SetBallAgility", "SetBallMass", "SetMaxSpeed"})
HEAT_ATTRIBUTES = {1175, 1176, 1177}  # SDE dogmaAttributes: heatHi, heatMed, heatLow
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

def slim_of(extras: list, ship_id) -> list:
    """The entries of an `AddBalls2` (the slim item and the damage state) that are the ship's own: the reference adds its ambient ships to the
    ball list of the ship that arrives, so the first update of the bind carries eleven of them in place of one."""
    def slim(entry):
        # Michelle.AddBalls2 unpacks each entry as (slimItemDict, damageState)
        # when it is a tuple, and also accepts a bare slimItemDict. Keep the
        # comparison on the client-visible contents, not the Python container
        # wrapper used to encode them.
        parts = sequence(entry)
        return parts[0] if parts else entry

    return [item for item in extras if number(keyval(slim(item))["itemID"]) == ship_id]

def sequence(value):
    if isinstance(value, list):
        return value
    if isinstance(value, dict) and isinstance(value.get("$tuple"), list):
        return value["$tuple"]
    return None

def compare_bound_state(recorded: dict, ours: dict, where: str, mapping: dict) -> list[str]:
    """Compare the bound state, accounting for EveJS ambient ships and scenery-only sentry props."""
    def state_fields(described: dict) -> dict:
        return keyval(described["$tuple"][0])

    expected_state, actual_state = state_fields(recorded), state_fields(ours)
    expected_sentries = Counter(
        (number(keyval(slim)["groupID"]), number(keyval(slim)["categoryID"]), number(keyval(slim)["typeID"]))
        for slim in expected_state["slims"] if number(keyval(slim).get("groupID", 0)) == 99
    )
    actual_sentries = Counter(
        (number(keyval(slim)["groupID"]), number(keyval(slim)["categoryID"]), number(keyval(slim)["typeID"]))
        for slim in actual_state["slims"] if number(keyval(slim).get("groupID", 0)) == 99
    )
    out: list[str] = []
    if expected_sentries != actual_sentries:
        out.append(f"{where}: SDE sentry group/category/type counts {actual_sentries} in place of {expected_sentries}")

    expected_ego = number(expected_state["ego"])
    actual_ego = number(actual_state["ego"])
    expected_ball_id = mapping.get(expected_ego, expected_ego)
    if actual_ego != expected_ball_id:
        out.append(f"{where}: SetState ego {actual_ego} in place of {expected_ball_id}")
    expected_ball = None
    for label, fields, item_id in (("recorded", expected_state, expected_ego), ("eve.rs", actual_state, actual_ego)):
        decoded = destiny_state_fields(fields["state"])
        if decoded is None or decoded.get("decodeError"):
            out.append(f"{where}: could not decode {label} Destiny state: {None if decoded is None else decoded.get('decodeError')}")
            continue
        ball = next((
            ball for ball in decoded["balls"]
            if (mapping.get(number(ball["itemID"]), number(ball["itemID"])) == actual_ego if label == "recorded" else number(ball["itemID"]) == item_id)
        ), None)
        if ball is None:
            out.append(f"{where}: {label} SetState has no free-ball record for ship {item_id}")
            continue
        if not all(field in ball for field in ("mass", "maxVelocity", "inertia")):
            out.append(f"{where}: {label} ship state omits mass, max velocity or inertia: {ball}")
            continue
        if label == "recorded":
            expected_ball = ball
        elif expected_ball is not None:
            for field in ("mass", "maxVelocity", "inertia"):
                if not math.isclose(float(expected_ball[field]), float(ball[field]), rel_tol=1e-6, abs_tol=1e-4):
                    out.append(f"{where}: ship {field} {ball[field]} in place of {expected_ball[field]} in SetState")

    def without_ambient_and_sentries(described: dict) -> dict:
        copy = deepcopy(described)
        state = copy["$tuple"][0]
        fields = []
        for key, value in state["args"]["$dict"]:
            if key == "slims":
                value = [
                    slim for slim in value
                    if number(keyval(slim)["itemID"]) not in AMBIENT
                    and number(keyval(slim).get("groupID", 0)) != 99
                ]
            fields.append([key, value])
        state["args"]["$dict"] = fields
        return copy

    clean_mapping = {**mapping, "_sentry_slims": {}}
    out += compare_state(
        without_ambient_and_sentries(recorded),
        without_ambient_and_sentries(ours),
        where,
        clean_mapping,
    )
    return out

def compare_heat_reset(events: list[dict], character_id: int, ship_id: int) -> list[str]:
    """Check the jump's explicit Dogma heat reset; V24 applies these heat attributes to loaded modules."""
    notices = [event for event in events if event["name"] == "OnModuleAttributeChanges"]
    if len(notices) != 1:
        return [f"jump destination bind: {len(notices)} module-attribute notices in place of the heat reset"]
    outer = sequence(args_of(notices[0]))
    changes = sequence(outer[0]) if outer else None
    if changes is None:
        return ["jump destination bind: malformed OnModuleAttributeChanges payload"]
    found: dict[int, list] = {}
    for value in changes:
        change = sequence(value)
        if change is None or len(change) != 8:
            return [f"jump destination bind: malformed module-attribute change {value}"]
        found[number(change[3])] = change
    if set(found) != HEAT_ATTRIBUTES:
        return [f"jump destination bind: heat attributes {sorted(found)} in place of {sorted(HEAT_ATTRIBUTES)}"]
    for attribute_id, change in found.items():
        if change[0] != "OnModuleAttributeChange" or number(change[1]) != character_id or number(change[2]) != ship_id:
            return [f"jump destination bind: heat attribute {attribute_id} names the wrong owner or ship"]
        if float(change[5]) != 0.0 or change[6] is not None:
            return [f"jump destination bind: heat attribute {attribute_id} was {change[5]!r} (old {change[6]!r}), expected reset to zero"]
        if change[4] != change[7]:
            return [f"jump destination bind: heat attribute {attribute_id} has inconsistent event timestamps"]
    return []

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
        undock_events = wait_quietly(who, 2.0)
        ballpark = who.call("beyonce", "MachoBindObject", SYSTEM, None)
        bind_events = wait_quietly(who, 2.5)
        # Destiny actions can cross the RPC wait boundary: the client's FlushState groups them by stamp,
        # not by the command that happened to be in flight when the notification arrived.
        problems += compare_events_by_source(
            steps["undock"]["events"] + steps["bind the ballpark"]["events"],
            undock_events + bind_events,
            "undock and bind the source ballpark",
            mapping,
            state_comparator=compare_bound_state,
        )

        # the gate warp drops out close enough to jump before the ship comes to rest; the jump update itself stops the ball
        where = "warp to the stargate"
        recorded = steps[where]
        started = time.time()
        raw, error = outcome(lambda: who.call_bound(ballpark[0], "CmdWarpToStuff", "item", gate, minRange=0))
        if error is not None:
            problems.append(f"{where}: error {base.brief(error)}")
        events = timed_events(who, 1.0)
        while time.time() - started < 200 and "dropout" not in milestones(events):
            who.call_bound(ballpark[0], "UpdateStateRequest")
            seen = timed_events(who, 3.0)
            for event in seen:
                event["at"] = round(event["at"] + (time.time() - started) - 3.0, 2)
            events += seen
        ours, theirs = without_noise(in_order(events)), without_noise(in_order(recorded["events"]))
        if not ours or not theirs:
            problems.append(f"{where}: no destiny update was recorded or received")
        else:
            first_ours, first_theirs = ours[0], theirs[0]
            if names(first_ours) != names(first_theirs):
                problems.append(f"{where}: first update {names(first_ours)} in place of {names(first_theirs)}")
            else:
                expected_actions, actual_actions = actions_of(first_theirs)[0], actions_of(first_ours)[0]
                for expected_action, actual_action in zip(expected_actions, actual_actions):
                    _, name, expected = expected_action
                    _, _, actual = actual_action
                    if name == "WarpTo":  # the point is random: the shape and the warp factor are what the client works its warp out from
                        expected_warp, actual_warp = expected["$tuple"], actual["$tuple"]
                        if shape(ids(expected, mapping)) != shape(actual) or number(expected_warp[5]) != number(actual_warp[5]):
                            problems.append(f"{where}.WarpTo: {base.brief(actual)} in place of {base.brief(expected)}")
                        if not 0.0 <= float(actual_warp[4]) <= 2_500.0:
                            problems.append(f"{where}.WarpTo: stop distance {actual_warp[4]} is outside the gate's 2,500 m landing scatter")
                    else:
                        problems += differences(base.blank_times(ids(expected, mapping)), base.blank_times(actual), f"{where}.{name}")
        got, expected = milestones(ours), milestones(theirs)
        if "dropout" not in expected:
            problems.append(f"{where}: the EveJS recording has no warp dropout")
        for name, expected_at in expected.items():
            if name not in got:
                problems.append(f"{where}: the ship never reached the recorded {name} milestone")
            # This lane only runs when the client can issue the gate jump after warp dropout; the recording has no rest Stop here.
            elif abs(got[name] - expected_at) > 4.0:
                problems.append(f"{where}: {name} after {got[name]} s, in the recording after {expected_at} s")

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
        if not any("Stop" in names(event) for event in seen):
            problems.append(f"{where}: the gate jump did not stop the ship")
        # the reference sends the ball's position again half a second after the effect (DEVIATIONS #24)
        not_the_resync = lambda e: names(e) != ["SetBallPosition", "SetBallVelocity", "SetSpeedFraction", "Stop"]  # noqa: E731
        problems += compare_events_by_source(
            [e for e in recorded["events"] if not_the_resync(e)],
            [e for e in seen if not_the_resync(e)],
            where,
            mapping,
            state_comparator=compare_bound_state,
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
        problems += compare_heat_reset(bound, character, ship_id)
        reference_bound = [event for event in recorded["events"] if event["name"] != "OnModuleAttributeChanges"]
        actual_bound = [event for event in bound if event["name"] != "OnModuleAttributeChanges"]
        problems += compare_events_by_source(
            reference_bound,
            actual_bound,
            where,
            mapping,
            state_comparator=compare_bound_state,
            ignored_actions=DESTINATION_DUPLICATE_SHIP_STATS,
        )

        raw, error = outcome(lambda: who.call_bound(landed[0], "UpdateStateRequest"))
        state = [e for e in timed_events(who, 3.0) if names(e) == ["SetState"]]
        if not state:
            problems.append("look at the state there: no state")
        else:
            recorded_state = [a for a in actions_of(steps["look at the state there"]["events"][0])[0] if a[1] == "SetState"][0]
            ours_state = [a for a in actions_of(state[0])[0] if a[1] == "SetState"][0]
            problems += compare_bound_state(recorded_state[2], ours_state[2], "look at the state there", mapping)
    assert problems == [], "\n".join(problems[:40])

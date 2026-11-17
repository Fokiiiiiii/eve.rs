"""A ship warps to a planet, compared with what the reference was recorded doing.

    EVE_LIVE_SLOW=1 EVE_LIVE_HOST=host:port [EVE_LIVE_SERVER=reference|eve.rs] pytest tests/test_warp.py

`oracle/fixtures/scenarios/warp.json` (scripts/record_character_scenarios.py --only warp) holds a new character's undock, `CmdWarpToStuff` to
a planet two AU away with every event the client was sent while the ship went (with the seconds each arrived at), the same order given again
by the planet and one to a place that is not there. The update that starts the warp is compared value for value (the point the client warps
in at is worked out from the planet's place and id, and the warp factor from the ship's warp speed); when the ship drops out and when it comes
to rest, within a couple of seconds of the recording. What the reference adds (the customs offices and props it takes out of the ballpark
while the ship is in warp, DEVIATIONS.md #16) is left out. It takes a minute and a half, so it runs only with EVE_LIVE_SLOW=1.
"""

from __future__ import annotations

import copy
import math
import os
import time
import uuid

import pytest

from evecli.describe import describe, destiny_state_fields
from evecli.observe import timed_events, wait_quietly
from tests import test_login_to_character as base
from tests.test_undock import (
    STATION, SYSTEM, UNDOCK, actions_of, arrive,
    client_updates_after_ballpark_bind, compare_actions, compare_destiny_updates,
    compare_events, keyval, number, outcome,
)

WARP = base.load("warp.json")
pytestmark = [
    pytest.mark.skipif(not base.LIVE, reason="set EVE_LIVE_HOST=host:port to test against a running server"),
    pytest.mark.skipif(os.environ.get("EVE_LIVE_SLOW") != "1", reason="a minute and a half: set EVE_LIVE_SLOW=1"),
]

WAITS = {"bind the ship service": 0.5, "undock": 2.0, "bind the ballpark": 2.5}
# how far the seconds of a milestone may be from the recording's: both servers work them out from a ship that lines up as the physics of the
# integrator says, and a tick of theirs is a second
TOLERANCE = 3.0
WARP_ORDER = ["OnSpecialFX", "SetSpeedFraction", "WarpTo"]
RECORDED_WARP_ORDER = ["OnSpecialFX", "SetSpeedFraction", "WarpTo"]
WARP_EXIT_ACTIONS = frozenset({"SetBallMassive", "SetBallPosition", "SetBallVelocity", "SetSpeedFraction", "Stop"})

def names(event: dict) -> list:
    if event["name"] != "DoDestinyUpdate":
        return []
    try:
        actions, _ = actions_of(event)
    except (KeyError, TypeError):
        return []
    return [action[1] for action in actions]

def milestones(events: list) -> dict:
    """The seconds after the call at which the ship dropped out (the massless updates) and came to rest (the stop), from the events."""
    out: dict = {}
    for event in events:
        for name in names(event):
            if name == "SetBallMassive" and "dropout" not in out:
                out["dropout"] = event["at"]
            if name == "Stop":
                out["rest"] = event["at"]
    return out

def without_ballpark_noise(events: list) -> list:
    """The events without the state the polling asks for and its packaged-action wrapper."""
    keep = []
    for event in events:
        if names(event) in (["SetState"], ["PackagedAction"]):
            continue
        event = copy.deepcopy(event)
        keep.append(event)
    return keep

def in_order(events: list) -> list:
    return sorted(events, key=lambda e: e.get("at", 0.0))

def tuple_items(value):
    return value.get("$tuple") if isinstance(value, dict) and "$tuple" in value else value

def compare_warp_start(recorded: dict, actual: dict, where: str, ship_id: int, mapping: dict) -> list[str]:
    """Check the recorded EveJS warp order and its values against the live stream."""
    expected, expected_wait = actions_of(recorded)
    observed, observed_wait = actions_of(actual)
    out: list[str] = []
    if [action[1] for action in expected] != RECORDED_WARP_ORDER:
        return [f"{where}: recorded warp order {[action[1] for action in expected]} differs from the known legacy fixture"]
    if [action[1] for action in observed] != WARP_ORDER:
        return [f"{where}: warp order {[action[1] for action in observed]} in place of {WARP_ORDER}"]
    if expected_wait != observed_wait:
        out.append(f"{where}: wait flag {observed_wait} in place of {expected_wait}")
    if len({action[0] for action in expected}) != 1 or len({action[0] for action in observed}) != 1:
        out.append(f"{where}: warp order actions do not share one destiny stamp")

    expected_by_name = {action[1]: action for action in expected}
    observed_by_name = {action[1]: action for action in observed}
    out += compare_actions(
        [expected_by_name[name] for name in WARP_ORDER[:2]],
        [observed_by_name[name] for name in WARP_ORDER[:2]],
        f"{where} (FX and speed)", mapping,
    )

    expected_warp = tuple_items(expected_by_name["WarpTo"][2])
    observed_warp = tuple_items(observed_by_name["WarpTo"][2])
    if len(expected_warp) != 6 or len(observed_warp) != 6:
        return out + [f"{where}: WarpTo argument counts {len(observed_warp)} in place of {len(expected_warp)}"]
    expected_ship = mapping.get(number(expected_warp[0]), number(expected_warp[0]))
    if number(observed_warp[0]) != ship_id or expected_ship != ship_id:
        out.append(f"{where}: WarpTo did not name ship {ship_id}")
    if [number(value) for value in observed_warp[4:]] != [number(value) for value in expected_warp[4:]]:
        out.append(f"{where}: WarpTo stop distance or warp factor changed")
    distance = math.dist(
        [number(value) for value in observed_warp[1:4]],
        [number(value) for value in expected_warp[1:4]],
    )
    # Each EveJS run jitters the target independently inside a 2,500 m radius, so two valid points can be 5,000 m apart.
    if distance > 5_000:
        out.append(f"{where}: WarpTo landing point differs by {distance:.1f} m, beyond the 5,000 m pairwise jitter bound")
    return out

def own_warp_actions(events: list[dict], names_to_keep: frozenset[str], ship_id: int, mapping: dict) -> list[tuple[dict, int, str, list]]:
    out = []
    for event in events:
        if event["name"] != "DoDestinyUpdate":
            continue
        for stamp, name, args in actions_of(event)[0]:
            if name not in names_to_keep:
                continue
            values = tuple_items(args)
            if not values:
                continue
            item_id = number(values[0])
            if mapping.get(item_id, item_id) == ship_id:
                out.append((event, stamp, name, values))
    return out

def compare_warp_exit(recorded: list[dict], actual: list[dict], ship_id: int, mapping: dict, reference: bool) -> list[str]:
    """Check dropout and arrival actions separately from EveJS's historical event capture."""
    out: list[str] = []
    recorded_massless = own_warp_actions(recorded, frozenset({"SetBallMassive"}), ship_id, mapping)
    actual_massless = own_warp_actions(actual, frozenset({"SetBallMassive"}), ship_id, mapping)
    if len(recorded_massless) != 1 or recorded_massless[0][3][1:] != [0]:
        out.append(f"warp to a planet: recording has unexpected massless actions {[(name, values[1:]) for _, _, name, values in recorded_massless]}")
    expected_massless_count = 1 if reference else 2
    if len(actual_massless) != expected_massless_count or any(values[1:] != [0] for _, _, _, values in actual_massless):
        out.append(f"warp to a planet: own-ship massless actions {[(name, values[1:]) for _, _, name, values in actual_massless]} in place of {expected_massless_count} SetBallMassive(ship, 0) actions")
    elif not reference and actual_massless[1][1] - actual_massless[0][1] != 2:
        out.append(f"warp to a planet: second massless stamp is {actual_massless[1][1] - actual_massless[0][1]} after the first, expected 2")

    terminal = own_warp_actions(actual, frozenset({"SetBallPosition", "SetBallVelocity", "SetSpeedFraction", "Stop"}), ship_id, mapping)
    terminal_names = [name for _, _, name, _ in terminal]
    expected_terminal = [] if reference else ["SetBallPosition", "SetBallVelocity", "SetSpeedFraction", "Stop"]
    if terminal_names != expected_terminal:
        out.append(f"warp to a planet: own-ship arrival actions {terminal_names} in place of {expected_terminal}")
    elif terminal:
        if len({stamp for _, stamp, _, _ in terminal}) != 1:
            out.append(f"warp to a planet: own-ship arrival actions do not share one Destiny stamp")
        position, velocity, speed, stop = (values for _, _, _, values in terminal)
        if len(position) != 4 or not all(math.isfinite(number(value)) for value in position[1:]):
            out.append(f"warp to a planet: final SetBallPosition has invalid coordinates {position}")
        if len(velocity) != 4 or not all(math.isfinite(number(value)) for value in velocity[1:]) or math.sqrt(sum(number(value) ** 2 for value in velocity[1:])) > 0.02:
            out.append(f"warp to a planet: final SetBallVelocity is not at rest {velocity}")
        if len(speed) != 2 or number(speed[1]) != 0.0:
            out.append(f"warp to a planet: final SetSpeedFraction is not zero {speed}")
        if len(stop) != 1:
            out.append(f"warp to a planet: final Stop has unexpected arguments {stop}")
    return out

def state_ball_ids(events: list[dict]) -> set[int] | None:
    ids: set[int] = set()
    for event in events:
        if event["name"] != "DoDestinyUpdate":
            continue
        for _, name, args in actions_of(event)[0]:
            if name != "SetState":
                continue
            state = keyval(args["$tuple"][0]).get("state")
            decoded = destiny_state_fields(state)
            if decoded is None or decoded.get("decodeError"):
                return None
            ids.update(ball["itemID"] for ball in decoded["balls"])
    return ids or None

def compare_ballpark_removals(
    recorded_bind: list[dict], actual_bind: list[dict], recorded_warp: list[dict], actual_warp: list[dict],
) -> list[str]:
    """Check each RemoveBalls update against its own SetState; the systems contain different warp-only props."""
    out: list[str] = []
    recorded_state, actual_state = state_ball_ids(recorded_bind), state_ball_ids(actual_bind)
    rows = []
    for side, events, known_ids in (
        ("recording", recorded_warp, recorded_state),
        ("eve.rs", actual_warp, actual_state),
    ):
        removed = []
        for event in events:
            if event["name"] != "DoDestinyUpdate":
                continue
            for stamp, name, args in actions_of(event)[0]:
                if name != "RemoveBalls":
                    continue
                values = tuple_items(args)
                if len(values) != 1 or not isinstance(tuple_items(values[0]), list):
                    out.append(f"warp to a planet: {side} RemoveBalls has invalid arguments {values}")
                    ids = []
                else:
                    ids = [number(item) for item in tuple_items(values[0])]
                    if not ids:
                        out.append(f"warp to a planet: {side} RemoveBalls is empty")
                removed.append((event, stamp, actions_of(event)[1], ids))
        if known_ids is None:
            out.append(f"warp to a planet: {side} initial SetState ball IDs could not be decoded")
        if len(removed) != 1:
            out.append(f"warp to a planet: {side} sent {len(removed)} RemoveBalls actions, expected one")
        elif known_ids is not None:
            event, _, _, ids = removed[0]
            missing = sorted(set(ids) - known_ids)
            if missing:
                out.append(f"warp to a planet: {side} removed balls absent from its initial SetState: {missing}")
        rows.append(removed)
    if len(rows[0]) == len(rows[1]) == 1:
        expected, observed = rows[0][0], rows[1][0]
        if expected[2] != observed[2]:
            out.append(f"warp to a planet: RemoveBalls wait flag {observed[2]} in place of {expected[2]}")
        if abs(expected[0]["at"] - observed[0]["at"]) > TOLERANCE:
            out.append(f"warp to a planet: RemoveBalls at {observed[0]['at']} s, in the recording at {expected[0]['at']} s")
    return out

@pytest.mark.parametrize("race", WARP["races"], ids=lambda r: f"race-{r['raceID']}")
def test_a_ship_warps_to_a_planet_as_the_reference_did(race):
    run = uuid.uuid4().hex[:10]
    problems: list[str] = []
    steps = {step["label"]: step for step in race["steps"]}
    recent = next(r for r in UNDOCK["races"] if r["raceID"] == race["raceID"])
    recent_steps = {step["label"]: step for step in recent["steps"]}
    planet = race["planetID"]
    with base.connect(f"walk-warp-{run}") as who:
        _, ship_id, _, mapping = arrive(who, race, run, skip_tutorial=base.REFERENCE)
        ship = who.call("ship", "MachoBindObject", (STATION, 15), None)
        wait_quietly(who, 0.5)
        raw, error = outcome(lambda: who.call_bound(ship[0], "Undock", ship_id, False))
        assert error is None, base.brief(error)
        undock_events = wait_quietly(who, 2.0)
        problems += compare_events(
            [event for event in steps["undock"]["events"] if event["name"] != "DoDestinyUpdate"],
            [event for event in undock_events if event["name"] != "DoDestinyUpdate"],
            "undock",
            mapping,
        )
        ballpark = who.call("beyonce", "MachoBindObject", SYSTEM, None)
        bind_events = wait_quietly(who, 2.5)
        reference_mapping = {
            **mapping,
            int(recent["characterID"]): mapping[int(race["characterID"])],
            int(recent["shipID"]): ship_id,
            **{int(old): mapping[int(original)] for old, original in zip(recent["moduleIDs"], race["moduleIDs"])},
        }
        problems += compare_destiny_updates(
            client_updates_after_ballpark_bind([
                event
                for event in recent_steps["undock"]["events"] + recent_steps["bind the ballpark"]["events"]
                if event["name"] == "DoDestinyUpdate"
            ]),
            client_updates_after_ballpark_bind([
                event
                for event in undock_events + bind_events
                if event["name"] == "DoDestinyUpdate"
            ]),
            "initial destiny updates",
            reference_mapping,
        )
        mapping.update(reference_mapping)
        problems += compare_events(
            [e for e in steps["bind the ballpark"]["events"] if e["name"] != "DoDestinyUpdate"],
            [e for e in bind_events if e["name"] != "DoDestinyUpdate"], "bind the ballpark", mapping,
        )

        # the warp: the answer, then every event until the ship stops (asking for the state every three seconds, as the recording did)
        where = "warp to a planet"
        recorded = steps[where]
        started = time.time()
        raw, error = outcome(lambda: who.call_bound(ballpark[0], "CmdWarpToStuff", "item", planet, minRange=0))
        if error is not None:
            problems.append(f"{where}: error {base.brief(error)}")
        elif "error" in recorded or describe(raw) != recorded["ok"]:
            problems.append(f"{where}: answered {base.brief(describe(raw))} in place of {recorded['ok']}")
        else:
            assert who.call("achievementTrackerMgr", "HasEverWarped") is True
        events = timed_events(who, 1.0)
        while time.time() - started < 150 and not any(
            "Stop" in names(e) or (base.REFERENCE and "SetBallMassive" in names(e)) for e in events
        ):
            who.call_bound(ballpark[0], "UpdateStateRequest")
            seen = timed_events(who, 3.0)
            for event in seen:
                event["at"] = round(event["at"] + (time.time() - started) - 3.0, 2)
            events += seen
        ours = without_ballpark_noise(in_order(events))
        theirs = without_ballpark_noise(in_order(recorded["events"]))
        reference_warp_events = [event for event in theirs if "WarpTo" in names(event)]
        actual_warp_events = [event for event in ours if "WarpTo" in names(event)]
        if len(reference_warp_events) != 1 or len(actual_warp_events) != 1:
            problems.append(f"{where}: {len(actual_warp_events)} warp-order updates in place of {len(reference_warp_events)}")
        else:
            problems += compare_warp_start(reference_warp_events[0], actual_warp_events[0], where, ship_id, mapping)
        recorded_after = [event for event in theirs if "WarpTo" not in names(event)]
        actual_after = [event for event in ours if "WarpTo" not in names(event)]
        problems += compare_warp_exit(recorded_after, actual_after, ship_id, mapping, base.REFERENCE)
        problems += compare_ballpark_removals(
            steps["bind the ballpark"]["events"], bind_events, recorded_after, actual_after,
        )
        problems += compare_destiny_updates(
            [event for event in recorded_after if event["name"] == "DoDestinyUpdate"],
            [event for event in actual_after if event["name"] == "DoDestinyUpdate"],
            f"{where} (after the warp order)",
            mapping,
            ignored_actions=WARP_EXIT_ACTIONS | {"RemoveBalls"},
        )
        problems += compare_events(
            [event for event in recorded_after if event["name"] != "DoDestinyUpdate"],
            [event for event in actual_after if event["name"] != "DoDestinyUpdate"],
            f"{where} (notifications after the warp order)",
            mapping,
        )
        got, expected = milestones(ours), milestones(theirs)
        for name in (("dropout",) if base.REFERENCE else ("dropout", "rest")):
            if name not in got:
                problems.append(f"{where}: the ship never came to {name}")
            elif name in expected and abs(got[name] - expected[name]) > TOLERANCE:
                problems.append(f"{where}: {name} after {got[name]} s, in the recording after {expected[name]} s")
            # The reference recording ends after the warp dropout and has no Stop action. Keep
            # requiring eve.rs to tell the client that the ship has come to rest above.

        # the ship stands by the planet: another warp there is a message, as is one to what is not there
        for label, target in (("warp to it again, in warp range", planet), ("a warp to what is not there", 99999999)):
            recorded = steps[label]
            raw, error = outcome(lambda: who.call_bound(ballpark[0], "CmdWarpToStuff", "item", target, minRange=0))
            timed_events(who, 1.0)
            if error is None:
                problems.append(f"{label}: answered {base.brief(describe(raw))} in place of the message")
            elif error != recorded["error"]["payload"]:
                problems.append(f"{label}: error {base.brief(error)} in place of {base.brief(recorded['error']['payload'])}")
    assert problems == [], "\n".join(problems[:40])

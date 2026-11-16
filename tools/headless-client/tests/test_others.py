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
import math
import time
import uuid

import pytest

from evecli.describe import describe, differences, shape
from evecli.observe import events_of, wait_quietly
from tests import test_login_to_character as base
from tests.test_undock import (
    STATION,
    SYSTEM,
    actions_of,
    arrive,
    compare_actions,
    compare_events,
    compare_events_by_source,
    ids,
    number,
    outcome,
)

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

def hold_still(who, ballpark, seconds: float = 10.0) -> None:
    """The pilot's ship is stopped before a rat is called (`CmdStop`, then the events of the stop are watched): the server places the rat from where the
    ship is when the rat is called, and a ship that still flies (the Ibis at 339 m/s) is past its lock range (29.7 km: AddTarget refuses with
    TargetNotWithinRangeGeneric) by the time the rat is there."""
    who.call_bound(ballpark, "CmdStop")
    watch({"events": who}, seconds)

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

def destiny_actions(events: list) -> list:
    return [action for event in events if event["name"] == "DoDestinyUpdate" for action in actions_of(event)[0]]

def tuple_items(value):
    return value.get("$tuple") if isinstance(value, dict) and "$tuple" in value else value

def compare_ordered_actions(recorded: list, actual: list, where: str, mapping: dict) -> list[str]:
    """Compare action order, same-stamp grouping, and payloads without requiring the same elapsed tick gaps."""
    def normalize(actions: list) -> list:
        out = []
        group = -1
        previous = None
        for stamp, name, arguments in actions:
            if stamp != previous:
                group += 1
                previous = stamp
            out.append((group, name, arguments))
        return out
    return compare_actions(normalize(recorded), normalize(actual), where, mapping)

def compare_warp_order(recorded: tuple, actual: tuple, where: str, ship_id: int, mapping: dict) -> list[str]:
    """The celestial landing point may differ by EveJS's documented 2,500 m warp jitter."""
    expected, observed = tuple_items(recorded[2]), tuple_items(actual[2])
    if len(expected) != 6 or len(observed) != 6:
        return [f"{where}: WarpTo argument counts {len(observed)} in place of {len(expected)}"]
    expected_ship = mapping.get(number(expected[0]), number(expected[0]))
    if number(observed[0]) != ship_id or expected_ship != ship_id:
        return [f"{where}: WarpTo did not name ship {ship_id}"]
    if [number(value) for value in observed[4:]] != [number(value) for value in expected[4:]]:
        return [f"{where}: WarpTo stop distance or warp factor changed"]
    distance = math.dist([number(value) for value in observed[1:4]], [number(value) for value in expected[1:4]])
    if distance > 2_500:
        return [f"{where}: WarpTo landing point differs by {distance:.1f} m, beyond EveJS's 2,500 m jitter"]
    return []

def without_noise(events: list) -> list:
    """The events without the state that was asked for and the balls the reference adds to a ballpark and takes out again."""
    return [e for e in events if names(e) not in (["SetState"],) and "AddBalls2" not in names(e)]

def removed_by(event: dict, mapping: dict) -> list:
    """The ships the unwrapped `RemoveBalls` action takes out (reference-only ballpark additions are left out)."""
    actions, _ = actions_of(event)
    removals = [args["$tuple"][0] for _, name, args in actions if name == "RemoveBalls"]
    if len(removals) != 1:
        raise AssertionError(f"expected one unwrapped RemoveBalls action, got {[name for _, name, _ in actions]}")
    return [mapping.get(i["$long"] if isinstance(i, dict) else i, i["$long"] if isinstance(i, dict) else i) for i in removals[0]]

def compare_bootstrap(recorded: list, actual: list, where: str, mapping: dict) -> list[str]:
    """Compare the client's ordered bootstrap groups while allowing different elapsed ticks between groups."""
    def groups(events: list) -> list[tuple[list, bool]]:
        by_stamp: dict[int, dict] = {}
        for event in events:
            actions, wait = actions_of(event)
            by_update_stamp: dict[int, list] = {}
            for action in actions:
                by_update_stamp.setdefault(action[0], []).append(action)
            for stamp in sorted(by_update_stamp):
                if stamp in by_stamp:
                    by_stamp[stamp]["actions"].extend(by_update_stamp[stamp])
                    by_stamp[stamp]["wait"] = False
                else:
                    by_stamp[stamp] = {"actions": by_update_stamp[stamp], "wait": bool(wait)}
                    wait = False
        return [(entry["actions"], entry["wait"]) for _, entry in sorted(by_stamp.items())]

    expected, observed = groups(recorded), groups(actual)
    out = []
    expected_waits = [wait for _, wait in expected]
    observed_waits = [wait for _, wait in observed]
    if observed_waits != expected_waits:
        out.append(f"{where}: wait groups {observed_waits} in place of {expected_waits}")

    def with_ordered_stamps(groups: list[tuple[list, bool]]) -> list:
        return [
            (group_index, name, arguments)
            for group_index, (actions, _) in enumerate(groups)
            for _, name, arguments in actions
        ]

    # Michelle groups and applies updates by their stamps, but does not require a fixed number of
    # ticks between the undock prime and SetState. Compare each same-stamp group and its order.
    out.extend(compare_actions(
        with_ordered_stamps(expected),
        with_ordered_stamps(observed),
        where,
        mapping,
    ))
    return out

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
        ships = {a_ship, b_ship}
        both = {"events": b, "seen": a}
        bootstrap_labels = {
            "the watcher undocks",
            "the watcher binds the ballpark",
            "the actor undocks",
            "the actor binds the ballpark",
            "the actor undocks again",
            "the actor binds the ballpark again",
        }
        bootstrap_destiny = {key: {"recorded": [], "actual": []} for key in ("events", "seen")}

        def compared(label: str, got: dict, **filters) -> None:
            """What the actor and the watcher were sent in a step, against what was recorded."""
            for who, key in (("the actor", "events"), ("the watcher", "seen")):

                keep = filters.get(key, not_time_dependent if key == "seen" else (lambda events: events))
                recorded = keep(steps[label][key])
                actual = keep(got[key])
                if label in bootstrap_labels:
                    # The reference primes an undocking ship before the client binds the ballpark;
                    # eve.rs delivers the same ordered prime while binding, before the RPC reply.
                    # Michelle processes Destiny asynchronously, so compare the whole bootstrap across both calls.
                    bootstrap_destiny[key]["recorded"].extend(e for e in recorded if e["name"] == "DoDestinyUpdate")
                    bootstrap_destiny[key]["actual"].extend(e for e in actual if e["name"] == "DoDestinyUpdate")
                    recorded_session = [e for e in recorded if e["name"] != "DoDestinyUpdate"]
                    actual_session = [e for e in actual if e["name"] != "DoDestinyUpdate"]
                    problems.extend(compare_events(recorded_session, actual_session, f"{label} ({who})", mapping))
                elif label == "the actor docks" and key == "seen":
                    # EveJS 0.12.9 leaves the actor's Ball in this Observer's park during
                    # a dock. V24's RemoveBalls applies to this Observer's ballpark too;
                    # require exactly the actor's ID, then compare the rest of the batch.
                    removed = [e for e in actual if "RemoveBalls" in names(e)]
                    if len(removed) != 1:
                        problems.append(f"{label} ({who}): {len(removed)} RemoveBalls updates, expected one for the docked ship")
                    else:
                        removed_ids = removed_by(removed[0], mapping)
                        if removed_ids != [b_ship]:
                            problems.append(f"{label} ({who}): RemoveBalls removed {removed_ids}, expected only {b_ship}")
                    recorded_destiny = [e for e in recorded if e["name"] == "DoDestinyUpdate"]
                    actual_destiny = [e for e in actual if e["name"] == "DoDestinyUpdate"]
                    recorded_actions = [action for event in recorded_destiny for action in actions_of(event)[0]]
                    actual_actions = [action for event in actual_destiny for action in actions_of(event)[0] if action[1] != "RemoveBalls"]
                    problems.extend(compare_actions(recorded_actions, actual_actions, f"{label} ({who}) (simulation)", mapping))
                    problems.extend(compare_events(
                        [e for e in recorded if e["name"] != "DoDestinyUpdate"],
                        [e for e in actual if e["name"] != "DoDestinyUpdate"],
                        f"{label} ({who}) (session)",
                        mapping,
                    ))
                else:
                    problems.extend(compare_events_by_source(recorded, actual, f"{label} ({who})", mapping))

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
        for key, who in (("events", "the actor"), ("seen", "the watcher")):
            startup = bootstrap_destiny[key]
            problems.extend(compare_bootstrap(startup["recorded"], startup["actual"], f"ballpark bootstrap ({who})", mapping))
            startup["recorded"].clear()
            startup["actual"].clear()

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
        for key, who in (("events", "the actor"), ("seen", "the watcher")):
            startup = bootstrap_destiny[key]
            problems.extend(compare_bootstrap(startup["recorded"], startup["actual"], f"ballpark bootstrap ({who})", mapping))

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

        # The order goes to each current observer once. The server then sends the dropout and
        # authoritative rest state; EveJS's recorded scenario stops after the first massless action.
        theirs = in_order(not_time_dependent(recorded["events"]))
        ours = in_order(got["events"])
        actor_actions = destiny_actions(ours)
        reference_actions = destiny_actions(theirs)
        actor_names = [action[1] for action in actor_actions]
        reference_names = [action[1] for action in reference_actions]
        if actor_names[:len(reference_names)] != reference_names:
            problems.append(f"{where}: the actor was sent {actor_names} in place of the recorded prefix {reference_names}")
        extra_actions = actor_actions[len(reference_names):]
        extra_names = [action[1] for action in extra_actions]
        expected_rest = ["SetBallMassive", "SetBallPosition", "SetBallVelocity", "SetSpeedFraction", "Stop"]
        if extra_names != expected_rest:
            problems.append(f"{where}: authoritative warp rest updates are {extra_names}, expected {expected_rest}")

        reference_common = [action for action in reference_actions if action[1] not in {"RemoveBalls", "WarpTo"}]
        actor_common = [action for action in actor_actions if action[1] not in {"RemoveBalls", "WarpTo"}][:len(reference_common)]
        problems += compare_ordered_actions(reference_common, actor_common, f"{where} (the actor's shared warp/dropout updates)", mapping)
        reference_warp = next((action for action in reference_actions if action[1] == "WarpTo"), None)
        actor_warp = next((action for action in actor_actions if action[1] == "WarpTo"), None)
        if reference_warp is None or actor_warp is None:
            problems.append(f"{where}: WarpTo count is {int(actor_warp is not None)} in place of {int(reference_warp is not None)}")
        else:
            problems.extend(compare_warp_order(reference_warp, actor_warp, f"{where} (the actor)", b_ship, mapping))

        lost = [e for e in ours if "RemoveBalls" in names(e)]
        lost_theirs = [e for e in theirs if "RemoveBalls" in names(e)]
        if len(lost) != 1 or len(lost_theirs) != 1:
            problems.append(f"{where}: {len(lost)} actor RemoveBalls updates in place of {len(lost_theirs)}")
        else:
            wanted = [i for i in removed_by(lost_theirs[0], mapping) if i in ships]
            lost_ours = [i for i in removed_by(lost[0], mapping) if i in ships]
            if lost_ours != wanted:
                problems.append(f"{where}: the actor lost sight of {lost_ours} in place of {wanted}")
            if abs(lost[0]["at"] - lost_theirs[0]["at"]) > 2.5:
                problems.append(f"{where}: the actor lost sight after {lost[0]['at']} s, in the recording after {lost_theirs[0]['at']} s")

        massless = [action for action in actor_actions if action[1] == "SetBallMassive"]
        rest = extra_actions[1:]
        if len(massless) != 2 or any(number(tuple_items(action[2])[0]) != b_ship or number(tuple_items(action[2])[1]) != 0 for action in massless):
            problems.append(f"{where}: massless notifications are not the two zero-mass updates for ship {b_ship}")
        elif massless[1][0] - massless[0][0] != 2:
            problems.append(f"{where}: the two zero-mass updates have stamp gap {massless[1][0] - massless[0][0]}, expected 2")
        if [action[1] for action in rest] != expected_rest[1:]:
            problems.append(f"{where}: rest actions { [action[1] for action in rest] }")
        elif all(len(tuple_items(action[2])) >= 2 for action in rest[:3]):
            position = tuple_items(rest[0][2])
            velocity = tuple_items(rest[1][2])
            warp_to = next((tuple_items(action[2]) for action in actor_actions if action[1] == "WarpTo"), None)
            if number(position[0]) != b_ship or number(velocity[0]) != b_ship or number(tuple_items(rest[2][2])[0]) != b_ship or number(tuple_items(rest[3][2])[0]) != b_ship:
                problems.append(f"{where}: the rest updates do not name the actor's ship {b_ship}")
            elif warp_to is not None:
                landing = [number(value) for value in position[1:4]]
                destination = [number(value) for value in warp_to[1:4]]
                if math.dist(landing, destination) >= 2_000:
                    problems.append(f"{where}: authoritative rest position is not within 2 km of WarpTo destination")
                speed = math.sqrt(sum(number(value) ** 2 for value in velocity[1:4]))
                if speed > 0.01 or number(tuple_items(rest[2][2])[1]) != 0.0:
                    problems.append(f"{where}: the ship did not rest at zero speed (velocity={speed})")

        # what the watcher was sent: one warp order and the ship taken out of view
        theirs = in_order(recorded["seen"])
        ours = in_order(got["seen"])
        if [tuple(names(e)) for e in ours] != [tuple(names(e)) for e in theirs]:
            problems.append(f"{where}: the watcher was sent {[names(e) for e in ours]} in place of {[names(e) for e in theirs]}")
        else:
            expected = actions_of(theirs[0])[0]
            actual = actions_of(ours[0])[0]
            expected_warp = next((action for action in expected if action[1] == "WarpTo"), None)
            actual_warp = next((action for action in actual if action[1] == "WarpTo"), None)
            if expected_warp is None or actual_warp is None:
                problems.append(f"{where}: watcher WarpTo count is {int(actual_warp is not None)} in place of {int(expected_warp is not None)}")
            else:
                    problems.extend(compare_warp_order(expected_warp, actual_warp, f"{where} (the watcher)", b_ship, mapping))
            if [a[1] for a in actions_of(ours[0])[0]] != ["OnSpecialFX", "SetSpeedFraction", "WarpTo"]:
                problems.append(f"{where}: the watcher did not receive one warp order: {names(ours[0])}")
            expected_gone = [i for i in removed_by(theirs[1], mapping) if i in ships]
            actual_gone = [i for i in removed_by(ours[1], mapping) if i in ships]
            if actual_gone != expected_gone:
                problems.append(f"{where}: the watcher lost sight of {actual_gone} in place of {expected_gone}")
            for index, tolerance in ((0, 2.0), (1, 2.5)):
                if abs(ours[index]["at"] - theirs[index]["at"]) > tolerance:
                    problems.append(f"{where} (the watcher): event {index} after {ours[index]['at']} s, in the recording after {theirs[index]['at']} s")

        step("the watcher asks for the state after the warp", lambda: a.call_bound(a_ballpark[0], "UpdateStateRequest"))
    assert problems == [], "\n".join(problems[:40])

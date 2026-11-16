"""Two ships in one system, one locking the other, compared with what the reference was recorded doing.

    EVE_LIVE_SLOW=1 EVE_LIVE_HOST=host:port [EVE_LIVE_SERVER=reference|eve.rs] pytest tests/test_target.py

`oracle/fixtures/scenarios/target.json` (scripts/record_character_scenarios.py --only target) holds two characters by the same station: the watcher
undocks and binds the ballpark, then the actor undocks, binds, and is given the dogma location of the system. Each step holds what the actor was
sent (`events`) and what the watcher was sent (`seen`), with the seconds each arrived at, and the answer or the error of the call. The replay
does the same on any server and compares. What a server may do differently is in docs/reference/compat/DEVIATIONS.md; the seconds a lock takes are the
ship's own numbers, so they are compared within a second, not to the hundredth. The warp and the docking are long (a minute and a half, and
the protection of a ship that has just undocked is thirty seconds), so this runs only with EVE_LIVE_SLOW=1.
"""

from __future__ import annotations

import os
import time
import uuid

import pytest

from evecli import marshal as m
from evecli.describe import describe, differences, shape
from evecli.observe import _notification_call_args, events_of
from tests import test_login_to_character as base
from tests.test_others import names, watch
from tests.test_undock import (
    STATION,
    SYSTEM,
    arrive,
    compare_destiny_updates,
    compare_events,
    compare_events_by_source,
    ids,
    outcome,
)
from tests.test_movement import distance_from_surface

TARGET = base.load("target.json")
pytestmark = [
    pytest.mark.skipif(not base.LIVE, reason="set EVE_LIVE_HOST=host:port to test against a running server"),
    pytest.mark.skipif(os.environ.get("EVE_LIVE_SLOW") != "1", reason="four minutes: set EVE_LIVE_SLOW=1"),
]

# how far apart (in seconds) an event may come from the same one in the recording: a lock takes the time the ship's own numbers give, and this
# side sees the world a quarter of a second at a time
LOCK_TOLERANCE = 1.0
EVENT_SAMPLE_INTERVAL = 0.25

def plain_race(race: dict, who: str) -> dict:
    return {"create": race[who]["create"], "characterID": race[who]["characterID"], "shipID": race[who]["shipID"],
            "moduleIDs": [race[who]["shipID"] + 1, race[who]["shipID"] + 2, race[who]["shipID"] + 3]}

def only(*wanted: str):
    """Keep direct notifications and dogma batches that contain one of these client events."""
    return lambda events: [e for e in events if e["name"] in wanted or any(name in wanted for name in dogma_event_names(e))]

def sequence(value):
    if isinstance(value, list):
        return value
    if isinstance(value, dict) and isinstance(value.get("$tuple"), list):
        return value["$tuple"]
    return None

def dogma_event_names(event: dict) -> list[str]:
    if event["name"] == "OnTarget":
        return ["OnTarget"]
    if event["name"] != "OnMultiEvent":
        return []
    args = _notification_call_args(event["args"])
    pairs = sequence(args[0]) if args else None
    found = []
    for pair in pairs or []:
        timed = sequence(pair)
        words = sequence(timed[0]) if timed else None
        if words and isinstance(words[0], str):
            found.append(words[0])
    return found

def user_error(payload: dict | None):
    """`(name, values)` of a user error, from its described payload."""
    try:
        name, values = payload["$objectex"]["$tuple"][1]["$tuple"]
    except (KeyError, TypeError):
        return None
    return name, {k: v for k, v in values["$dict"]}

def compare_answer(label: str, recorded: dict, got: dict | None, error: dict | None, mapping: dict, exact: tuple = ()) -> list[str]:
    """What the call answered: the same shape, or the same user error with the same values where a value is the same on every server."""
    if "error" in recorded:
        expected = user_error(recorded["error"]["payload"])
        if error is None:
            return [f"{label}: answered {base.brief(got)} in place of the error {expected[0] if expected else recorded['error']}"]
        actual = user_error(error)
        if actual is None or expected is None:
            return [f"{label}: error {base.brief(error)} in place of {base.brief(recorded['error']['payload'])}"]
        problems = []
        if actual[0] != expected[0]:
            return [f"{label}: error {actual[0]} in place of {expected[0]}"]
        if list(actual[1]) != list(expected[1]):
            problems.append(f"{label}: error values {list(actual[1])} in place of {list(expected[1])}")
        else:
            for key in expected[1]:
                if key in exact or key in ("target", "notify", "targetName", "limit"):
                    problems += differences(expected[1][key], actual[1][key], f"{label}: {actual[0]}.{key}")
                elif shape(expected[1][key]) != shape(actual[1][key]) and key != "actualDistance":
                    problems.append(f"{label}: {actual[0]}.{key} is {actual[1][key]} in place of {expected[1][key]}")
        return problems
    if error is not None:
        return [f"{label}: error {base.brief(error)} in place of the answer {base.brief(recorded.get('ok'))}"]
    # the ids of ships are per run (and a long only where they do not fit 32 bits, which is not the same on every server)
    if "ok" in recorded and shape(describe(got)) != shape(ids(recorded["ok"], mapping)):
        return [f"{label}: answered {base.brief(describe(got))} in place of {base.brief(ids(recorded['ok'], mapping))}"]
    return []

def timing(recorded: list[dict], got: list[dict], where: str, tolerance: float, wanted=("OnTarget",)) -> list[str]:
    """The seconds each event of these names arrived at, against the recording."""
    theirs = [e for e in recorded if e["name"] in wanted or any(name in wanted for name in dogma_event_names(e))]
    ours = [e for e in got if e["name"] in wanted or any(name in wanted for name in dogma_event_names(e))]
    out = []
    for index, (a, b) in enumerate(zip(theirs, ours)):
        # Both the recorder and replay timestamp after draining events in a 0.25 s polling loop. Treat each
        # timestamp as an observation interval so poll phase alone cannot turn a one-second boundary into a failure.
        if abs(a["at"] - b["at"]) > tolerance + EVENT_SAMPLE_INTERVAL:
            out.append(f"{where}: event {index} {a['name']} after {b['at']} s, in the recording after {a['at']} s")
    return out

def test_a_ship_locks_others_as_it_did_in_the_reference():
    race = TARGET["races"][0]
    run = uuid.uuid4().hex[:10]
    problems: list[str] = []
    steps = {step["label"]: step for step in race["steps"]}
    planet = race["planetID"]
    with base.connect(f"walk-target-a-{run}") as a, base.connect(f"walk-target-b-{run}") as b:
        _, a_ship, _, a_mapping = arrive(a, plain_race(race, "watcher"), run)
        _, b_ship, _, b_mapping = arrive(b, plain_race(race, "actor"), run)
        mapping = {**a_mapping, **b_mapping}
        both = {"events": b, "seen": a}
        bootstrap_labels = {
            "the watcher undocks",
            "the watcher binds the ballpark",
            "the actor undocks",
            "the actor binds the ballpark",
        }
        bootstrap_destiny = {"events": {"recorded": [], "actual": []}, "seen": {"recorded": [], "actual": []}}

        def everything(events: list) -> list:
            return events

        def compared(
            label: str,
            got: dict,
            keep_actor=everything,
            keep_watcher=everything,
            tolerance: float = LOCK_TOLERANCE,
            compare_timing: bool = True,
        ) -> None:
            """What the actor and the watcher were sent in a step, against what was recorded (the end of a ship's protection comes thirty
            seconds after it undocked, whichever step that falls in, so it is left out unless the step is about it)."""
            for who, key, keep in (("the actor", "events", keep_actor), ("the watcher", "seen", keep_watcher)):
                theirs = keep([e for e in steps[label][key] if e["name"] != "OnInvulnCancelled" or label.startswith("the actor locks")])
                ours = keep([e for e in got[key] if e["name"] != "OnInvulnCancelled" or label.startswith("the actor locks")])
                if label in bootstrap_labels:
                    # The reference primes an undocking ship before the client binds the ballpark;
                    # eve.rs delivers the same ordered prime while binding, before the RPC reply.
                    # Michelle processes Destiny asynchronously and the session transition opens
                    # the inflight view; compare the complete bootstrap stream across both calls.
                    bootstrap_destiny[key]["recorded"].extend(e for e in theirs if e["name"] == "DoDestinyUpdate")
                    bootstrap_destiny[key]["actual"].extend(e for e in ours if e["name"] == "DoDestinyUpdate")
                    recorded_session = [e for e in theirs if e["name"] != "DoDestinyUpdate"]
                    actual_session = [e for e in ours if e["name"] != "DoDestinyUpdate"]
                    problems.extend(compare_events(recorded_session, actual_session, f"{label} ({who})", mapping))
                    if compare_timing:
                        problems.extend(timing(recorded_session, actual_session, f"{label} ({who})", tolerance))
                else:
                    problems.extend(compare_events_by_source(theirs, ours, f"{label} ({who})", mapping))
                    if compare_timing:
                        problems.extend(timing(theirs, ours, f"{label} ({who})", tolerance))

        def step(label: str, call, seconds: float = 2.0, until=None, exact: tuple = (), **filters) -> dict:
            raw, error = outcome(call)
            problems.extend(compare_answer(label, steps[label], raw, error, mapping, exact))
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
            problems.extend(compare_destiny_updates(
                startup["recorded"], startup["actual"], f"ballpark bootstrap ({who})", mapping
            ))
        a_dogma = a.call("dogmaIM", "MachoBindObject", (SYSTEM, 5), None)
        b_dogma = b.call("dogmaIM", "MachoBindObject", (SYSTEM, 5), None)
        if shape(describe([a_dogma, b_dogma])) != shape(steps["both bind dogma in space"]["ok"]):
            problems.append(f"both bind dogma in space: {base.brief(describe([a_dogma, b_dogma]))}")
        watch(both, 1.0)

        lock = lambda ship: (lambda: b.call_bound(b_dogma[0], "AddTarget", ship))  # noqa: E731
        got_lock = lambda seen: any("OnTarget" in dogma_event_names(e) for e in seen["events"])  # noqa: E731
        watcher_free = lambda seen: any(e["name"] == "OnInvulnCancelled" for e in seen["seen"])  # noqa: E731

        # a ship that has just undocked cannot be locked
        watch(both, 40.0, until=watcher_free)
        step("the actor locks itself while it is protected", lock(b_ship))
        step("the actor locks the watcher while it is protected", lock(a_ship), 20.0, until=got_lock)
        step("the actor unlocks the watcher it locked while protected", lambda: b.call_bound(b_dogma[0], "RemoveTarget", a_ship), 2.0)
        watch(both, 6.0)

        step("the actor asks what it has locked", lambda: b.call_bound(b_dogma[0], "GetTargets"))
        step("the watcher asks what has locked it", lambda: a.call_bound(a_dogma[0], "GetTargeters"))
        step("the actor locks itself", lock(b_ship))
        step("the actor locks a planet", lock(planet))
        step("the actor locks something that is not there", lock(99999999))
        step("the actor locks the watcher", lock(a_ship), 20.0, until=got_lock)
        step("the actor asks what it has locked with the watcher locked", lambda: b.call_bound(b_dogma[0], "GetTargets"))
        step("the watcher asks what has locked it with the actor locking", lambda: a.call_bound(a_dogma[0], "GetTargeters"))
        step("the actor locks the watcher again", lock(a_ship), 2.0)
        step("the actor unlocks the watcher", lambda: b.call_bound(b_dogma[0], "RemoveTarget", a_ship), 2.0)
        step("the actor locks the watcher to give up", lock(a_ship), 1.5)
        step("the actor gives up the lock", lambda: b.call_bound(b_dogma[0], "CancelAddTarget", a_ship), 2.0)
        step("the actor locks the station", lock(STATION), 20.0, until=got_lock)
        step("the actor locks the watcher after the station", lock(a_ship), 20.0, until=got_lock)
        step("the actor unlocks the station with a list", lambda: b.call_bound(b_dogma[0], "RemoveTargets", m.List([STATION])), 2.0)
        step("the actor locks the station again", lock(STATION), 20.0, until=got_lock)
        step("the actor clears its targets", lambda: b.call_bound(b_dogma[0], "ClearTargets"), 2.0)
        step("the actor asks what it has locked after clearing", lambda: b.call_bound(b_dogma[0], "GetTargets"))

        # a ship that is locked docks: the one that locked it is told it is gone
        step("the actor locks the watcher before it docks", lock(a_ship), 20.0, until=got_lock)
        # The recorded reference reaches this call after about 17 km of travel. The lock delays and sim cadence
        # vary by server, so wait for the same out-of-range precondition instead of tying docking to wall-clock steps.
        started = time.time()
        distance = None
        while time.time() - started < 420:
            a.call_bound(a_ballpark[0], "UpdateStateRequest")
            distance = distance_from_surface(watch({"seen": a}, 2.0)["seen"], a_ship)
            if distance is not None and distance > 6000:
                break
        assert distance is not None and distance > 6000, f"the watcher never left the station docking range: {distance}"
        step("the watcher docks", lambda: a.call_bound(a_ballpark[0], "CmdDock", STATION), 9.0,
             until=lambda seen: any(e["name"] == "$sessionchange" for e in seen["seen"]),
             keep_actor=only("OnTarget"), keep_watcher=only("$sessionchange"))
        step("the actor asks what it has locked after the watcher docked", lambda: b.call_bound(b_dogma[0], "GetTargets"))

        # the actor warps with the station locked, tries to lock in warp and, when it is far away, from there
        step("the actor locks the station before warping", lock(STATION), 20.0, until=got_lock)
        where = "the actor warps to a planet"
        raw, error = outcome(lambda: b.call_bound(b_ballpark[0], "CmdWarpToStuff", "item", planet, minRange=0))
        problems.extend(compare_answer(where, steps[where], raw, error, mapping))
        warp_events = watch(both, 14.0)
        # Collision settlement changes the velocity before this warp. EveJS's matching collision seam is still a
        # no-op, so its recorded target notice time cannot be reused as an absolute timing oracle. The dedicated
        # warp walk compares the WarpTo payload; here require its update to precede the exact OnTarget notification
        # on both client streams.
        for key, who in (("events", "the actor"), ("seen", "the watcher")):
            recorded_warp = [event for event in steps[where][key] if "WarpTo" in names(event)]
            actual_warp_positions = [i for i, event in enumerate(warp_events[key]) if "WarpTo" in names(event)]
            actual_target_positions = [
                i for i, event in enumerate(warp_events[key]) if "OnTarget" in dogma_event_names(event)
            ]
            if not recorded_warp or not actual_warp_positions:
                problems.append(f"{where} ({who}): no WarpTo update was received")
            elif not actual_target_positions or min(actual_warp_positions) >= min(actual_target_positions):
                problems.append(f"{where} ({who}): target notification did not follow the WarpTo update")
        compared(
            where,
            warp_events,
            keep_actor=only("OnTarget"),
            keep_watcher=only("OnTarget"),
            compare_timing=False,
        )
        step("the actor locks the station in warp", lock(STATION), 2.0, keep_actor=only("OnTarget"), keep_watcher=only("OnTarget"))
        started = time.time()
        arrived = False
        while time.time() - started < 200 and not arrived:
            for event in watch(both, 3.0)["events"]:
                if event["name"] == "DoDestinyUpdate" and "Stop" in names(event):
                    arrived = True
        assert arrived, "the actor never came out of warp"
        watch(both, 1.0)
        step("the actor locks the station from far away", lock(STATION), 2.0, exact=("desiredRange",),
             keep_actor=only("OnTarget"), keep_watcher=only("OnTarget"))
    assert problems == [], "\n".join(problems[:40])

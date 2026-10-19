"""A ship in space is told to align, orbit, follow and stop, and asked to dock from too far, compared with what the reference was recorded doing.

    EVE_LIVE_HOST=host:port [EVE_LIVE_SERVER=reference|eve.rs] pytest tests/test_movement.py

`oracle/fixtures/scenarios/movement.json` holds each order (`CmdAlignTo`, `CmdOrbit`, `CmdFollowBall`, `CmdStop`, `CmdSetSpeedFraction`), what the
reference answered and what the client was sent while it waited; `approach.json` a ship that flies out of docking range and is told to dock
(`scripts/record_character_scenarios.py --only movement|approach`). The events are compared as in tests/test_undock.py. What a server may do
differently is listed in docs/reference/compat/DEVIATIONS.md: where the ship appears (#15) and how it comes to rest at the station (#17).

Skipped unless EVE_LIVE_HOST is set. The approach test takes about five minutes (the station's bounding sphere is 70 km across, so the ship has
to fly a long way to be out of range) and runs only with EVE_LIVE_SLOW=1.
"""

from __future__ import annotations

import os
import time
import uuid

import pytest

from evecli.describe import describe, shape
from evecli.observe import ego_state, timed_events, wait_quietly
from tests import test_login_to_character as base
from tests.test_undock import STATION, SYSTEM, arrive, compare_answer, compare_events, outcome

MOVEMENT = base.load("movement.json")
APPROACH = base.load("approach.json")
pytestmark = pytest.mark.skipif(not base.LIVE, reason="set EVE_LIVE_HOST=host:port to test against a running server")
SLOW = pytest.mark.skipif(os.environ.get("EVE_LIVE_SLOW") != "1", reason="five minutes: set EVE_LIVE_SLOW=1")

NOWHERE = 99999999
# each order of movement.json by its label: the call on the ballpark
ORDERS = {
    "align to the station": ("CmdAlignTo", STATION),
    "align to what is not there": ("CmdAlignTo", NOWHERE),
    "orbit the station": ("CmdOrbit", STATION, 5000),
    "orbit it again": ("CmdOrbit", STATION, 5000),
    "orbit closer": ("CmdOrbit", STATION, 2500.7),
    "follow the station": ("CmdFollowBall", STATION, 3000),
    "follow it again": ("CmdFollowBall", STATION, 3000),
    "stop": ("CmdStop",),
    "follow from a standstill": ("CmdFollowBall", STATION, 1000),
    "speed fraction": ("CmdSetSpeedFraction", 0.0),
    "align from a standstill": ("CmdAlignTo", STATION),
    "follow what is not there": ("CmdFollowBall", NOWHERE, 500),
}
# how long the recorder waited after each call
WAITS = {"bind the ship service": 0.5, "undock": 2.0, "bind the ballpark": 2.5}

# the station of the approach recording: its centre and the radius of its bounding sphere (what a ship's distance is measured from)
STATION_POSITION = (311989870775.0, -13069156275.0, 156646281447.0)
STATION_RADIUS = 70000.0
DOCKING_RANGE = 2500.0


@pytest.mark.parametrize("race", MOVEMENT["races"], ids=lambda r: f"race-{r['raceID']}")
def test_orders_in_space_make_the_updates_the_reference_sent(race):
    run = uuid.uuid4().hex[:10]
    problems: list[str] = []
    with base.connect(f"walk-movement-{run}") as who:
        _, ship_id, _, mapping = arrive(who, race, run)
        ship = ballpark = None
        for step in race["steps"]:
            label = step["label"]
            where = f"race {race['raceID']} {label}"
            if label == "bind the ship service":
                raw, error = outcome(lambda: who.call("ship", "MachoBindObject", (STATION, 15), None))
                ship = raw
            elif label == "undock":
                raw, error = outcome(lambda: who.call_bound(ship[0], "Undock", ship_id, False))
            elif label == "bind the ballpark":
                raw, error = outcome(lambda: who.call("beyonce", "MachoBindObject", SYSTEM, None))
                ballpark = raw
            else:
                name, *args = ORDERS[label]
                raw, error = outcome(lambda: who.call_bound(ballpark[0], name, *args))
            if error is not None:
                problems.append(f"{where}: error {base.brief(error)}")
                continue
            events = wait_quietly(who, WAITS.get(label, 1.5))
            if "ok" in step:
                problems += compare_answer(label, step["ok"], describe(raw), where)
            problems += compare_events(step["events"], events, where, mapping)
    assert problems == [], "\n".join(problems[:40])


def distance_from_surface(events: list, ship_id: int):
    """How far off the station's surface the ship is, from the state among the events (None when they hold none)."""
    state = ego_state(events, ship_id)
    if state is None:
        return None
    (x, y, z), radius = state
    gap = sum((a - b) ** 2 for a, b in zip((x, y, z), STATION_POSITION)) ** 0.5
    return gap - radius - STATION_RADIUS


@SLOW
@pytest.mark.parametrize("race", APPROACH["races"], ids=lambda r: f"race-{r['raceID']}")
def test_a_dock_from_beyond_the_range_sends_the_ship_to_the_station_and_a_second_asks_docks_it(race):
    run = uuid.uuid4().hex[:10]
    problems: list[str] = []
    steps = {step["label"]: step for step in race["steps"]}
    with base.connect(f"walk-approach-{run}") as who:
        _, ship_id, _, mapping = arrive(who, race, run)
        ship = who.call("ship", "MachoBindObject", (STATION, 15), None)
        wait_quietly(who, 0.5)
        raw, error = outcome(lambda: who.call_bound(ship[0], "Undock", ship_id, False))
        assert error is None, base.brief(error)
        problems += compare_events(steps["undock"]["events"], wait_quietly(who, 2.0), "undock", mapping)
        ballpark = who.call("beyonce", "MachoBindObject", SYSTEM, None)
        problems += compare_events(steps["bind the ballpark"]["events"], wait_quietly(who, 2.5), "bind the ballpark", mapping)

        # the ship leaves at full speed: ask for the state until it is out of the docking range
        started = time.time()
        distance = None
        while time.time() - started < 420:
            who.call_bound(ballpark[0], "UpdateStateRequest")
            distance = distance_from_surface(timed_events(who, 15.0), ship_id)
            if distance is not None and distance > 6000:
                break
        assert distance is not None and distance > DOCKING_RANGE, f"the ship never left the docking range: {distance}"

        # a dock from here is an error that says how far it is, and the ship goes to the station on its own
        where = "dock from beyond the range"
        recorded = steps[where]
        raw, error = outcome(lambda: who.call_bound(ballpark[0], "CmdDock", STATION))
        events = timed_events(who, 1.0)
        if error is None:
            problems.append(f"{where}: answered {base.brief(describe(raw))} in place of the user error")
        elif shape(error) != shape(recorded["error"]["payload"]):
            problems.append(f"{where}: error {base.brief(error)} in place of {base.brief(recorded['error']['payload'])}")
        problems += compare_events(recorded["events"], events, where, mapping)

        # it gets there without being asked again, and comes to rest within docking range
        readings = []
        called = time.time()
        while time.time() - called < 150:
            who.call_bound(ballpark[0], "UpdateStateRequest")
            reading = distance_from_surface(timed_events(who, 4.0), ship_id)
            if reading is None:
                continue
            readings.append(reading)
            if len(readings) >= 2 and abs(readings[-1] - readings[-2]) < 20 and reading < 5000:
                break
        recorded_last = steps["the approach"]["readings"][-1]["distanceFromSurface"]
        assert readings and readings[-1] <= DOCKING_RANGE, f"the ship comes to rest {readings[-1:]} m off the surface (the reference: {recorded_last:.0f} m): {[round(r) for r in readings]}"

        # asked again from within the range, it docks, and the client is told what it was told in the reference
        where = "dock again, in range"
        recorded = steps[where]
        raw, error = outcome(lambda: who.call_bound(ballpark[0], "CmdDock", STATION))
        if error is not None:
            problems.append(f"{where}: error {base.brief(error)}")
        else:
            events = wait_quietly(who, 15.0, lambda seen: any(e["name"] == "$sessionchange" for e in seen))
            problems += compare_answer("dock", recorded["ok"], describe(raw), where)
            problems += compare_events(recorded["events"], events, where, mapping)
    assert problems == [], "\n".join(problems[:40])

"""A ship in space is told to align, orbit, follow and stop, and docks after one out-of-range station request.

    EVE_LIVE_HOST=host:port [EVE_LIVE_SERVER=reference|eve.rs] pytest tests/test_movement.py

`oracle/fixtures/scenarios/movement.json` holds each order (`CmdAlignTo`, `CmdOrbit`, `CmdFollowBall`, `CmdStop`, `CmdSetSpeedFraction`) and what
the client was sent while it waited. The older `approach.json` records a second `CmdDock` from an earlier reference build, so this test uses its
out-of-range call only for the `DockingApproach` error shape. The current EveJS station approach sends `GotoPoint`; eve.rs retains its documented
`FollowBall` motion, and both must accept the dock on arrival without another command.

Skipped unless EVE_LIVE_HOST is set. The approach test takes about five minutes (the station's bounding sphere is 70 km across, so the ship has
to fly a long way to be out of range) and runs only with EVE_LIVE_SLOW=1.
"""

from __future__ import annotations

import os
import re
import time
import uuid

import pytest

from evecli.describe import describe, shape
from evecli.observe import ego_state, timed_events, wait_quietly
from evecli import marshal as m
from tests import test_login_to_character as base
from tests.test_undock import UNDOCK, STATION, SYSTEM, actions_of, arrive, compare_answer, compare_destiny_updates, compare_events, keyval, number, outcome

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
# the station of the historical approach recording: its centre and radius
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
        expected_destiny_updates = []
        actual_destiny_updates = []
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
            expected_destiny_updates.extend(event for event in step["events"] if event["name"] == "DoDestinyUpdate")
            actual_destiny_updates.extend(event for event in events if event["name"] == "DoDestinyUpdate")
            problems += compare_events(
                [event for event in step["events"] if event["name"] != "DoDestinyUpdate"],
                [event for event in events if event["name"] != "DoDestinyUpdate"],
                where,
                mapping,
            )
        problems += compare_destiny_updates(expected_destiny_updates, actual_destiny_updates, f"race {race['raceID']} destiny updates", mapping)
    assert problems == [], "\n".join(problems[:40])

def distance_from_surface(events: list, ship_id: int):
    """How far off the station's surface the ship is, from the state among the events (None when they hold none)."""
    state = ego_state(events, ship_id)
    if state is None:
        return None
    (x, y, z), radius = state
    gap = sum((a - b) ** 2 for a, b in zip((x, y, z), STATION_POSITION)) ** 0.5
    return gap - radius - STATION_RADIUS

def arrive_for_docking(who, race: dict, run: str) -> tuple[int, int, list, dict]:
    """Create the fixture pilot and read its starter ship from the station hangar."""
    recorded_run = re.search(r"(\d{6,})$", race["create"]["call"][2][0]).group(1)
    service, method, *args = [
        race["create"]["call"][0],
        race["create"]["call"][1],
        *[base.revive(value, run, {}, recorded_run) for value in race["create"]["call"][2]],
    ]
    kind, made = base.attempt(who, service, method, *args)
    assert kind == "ok", base.brief(made)
    character = int(made)
    assert base.attempt(who, "charUnboundMgr", "SelectCharacterID", character, None, False) == ("ok", None)

    manager = who.call("invbroker", "MachoBindObject", (STATION, 15), None)[0]
    hangar = who.call_bound(manager, "GetInventory", 10004)
    rows = who.call_bound(hangar, "List")
    if isinstance(rows, m.ObjectEx):
        header = m.plain(rows.header)
        assert isinstance(header, tuple) and header[0] == "__builtin__.set", (
            f"unexpected ObjectEx inventory reply header: {header!r}"
        )
        assert len(header) == 2 and len(header[1]) == 1, f"unexpected built-in set header: {header!r}"
        rows = header[1][0]
    assert isinstance(rows, (m.List, list)), f"unexpected station hangar List reply: {type(rows).__name__}"
    assert rows, "new character's station hangar contains its starter ship"
    first_row = rows[0]
    fields = first_row.fields() if isinstance(first_row, m.PackedRow) else first_row
    assert isinstance(fields, dict), f"unexpected station hangar row: {type(first_row).__name__}"
    assert {"itemID", "typeID", "ownerID", "locationID", "flagID", "quantity"} <= fields.keys(), (
        f"station hangar row is missing required columns: {fields.keys()}"
    )
    ship_id = int(fields["itemID"])

    info = who.call("dogmaIM", "MachoBindObject", (STATION, 15), ("GetAllInfo", (True, True, None), None))
    fitted = [number(key) for key, _ in keyval(describe(info[1]))["shipInfo"]["$dict"]][1:]
    mapping = {
        int(race["characterID"]): character,
        int(race["shipID"]): ship_id,
        **dict(zip(race["moduleIDs"], fitted)),
    }
    wait_quietly(who, 1.0)
    return character, ship_id, fitted, mapping

@SLOW
@pytest.mark.parametrize("race", APPROACH["races"], ids=lambda r: f"race-{r['raceID']}")
def test_a_station_dock_from_beyond_the_range_is_accepted_on_arrival_without_a_second_command(race):
    run = uuid.uuid4().hex[:10]
    problems: list[str] = []
    steps = {step["label"]: step for step in race["steps"]}
    with base.connect(f"walk-approach-{run}") as who:
        _, ship_id, _, mapping = arrive_for_docking(who, race, run)
        ship = who.call("ship", "MachoBindObject", (STATION, 15), None)
        wait_quietly(who, 0.5)
        raw, error = outcome(lambda: who.call_bound(ship[0], "Undock", ship_id, False))
        assert error is None, base.brief(error)
        # The old trace has different live Destiny tick counts; compare its stable session events and inspect current ball state below.
        undock_events = wait_quietly(who, 2.0)
        problems += compare_events(
            [event for event in steps["undock"]["events"] if event["name"] != "DoDestinyUpdate"],
            [event for event in undock_events if event["name"] != "DoDestinyUpdate"],
            "undock session events",
            mapping,
        )
        ballpark = who.call("beyonce", "MachoBindObject", SYSTEM, None)
        bind_events = wait_quietly(who, 2.5)
        problems += compare_events(
            [event for event in steps["bind the ballpark"]["events"] if event["name"] != "DoDestinyUpdate"],
            [event for event in bind_events if event["name"] != "DoDestinyUpdate"],
            "bind the ballpark session events",
            mapping,
        )

        # the ship leaves at full speed: ask for the state until it is out of the docking range
        started = time.time()
        distance = None
        while time.time() - started < 420:
            who.call_bound(ballpark[0], "UpdateStateRequest")
            distance = distance_from_surface(timed_events(who, 15.0), ship_id)
            if distance is not None and distance > 6000:
                break
        assert distance is not None and distance > DOCKING_RANGE, f"the ship never left the docking range: {distance}"

        # A dock from here returns DockingApproach and starts the server's station approach.
        where = "dock from beyond the range"
        recorded = steps[where]
        raw, error = outcome(lambda: who.call_bound(ballpark[0], "CmdDock", STATION))
        if error is None:
            problems.append(f"{where}: answered {base.brief(describe(raw))} in place of the user error")
        elif shape(error) != shape(recorded["error"]["payload"]):
            problems.append(f"{where}: error {base.brief(error)} in place of {base.brief(recorded['error']['payload'])}")
        approach_events = wait_quietly(who, 1.0)
        approach_actions = [
            name
            for event in approach_events
            if event["name"] == "DoDestinyUpdate"
            for _, name, _ in actions_of(event)[0]
        ]
        expected_approach = "GotoPoint" if base.REFERENCE else "FollowBall"
        assert expected_approach in approach_actions, f"{where}: expected {expected_approach}, got {approach_actions}"

        # The server completes the accepted dock after its normal delay; the client does not issue another CmdDock.
        arrival_events = wait_quietly(
            who,
            210.0,
            lambda seen: any(event["name"] == "OnDockingFinished" for event in seen),
        )
        events = approach_events + arrival_events
        accepted = next((i for i, event in enumerate(events) if event["name"] == "OnDockingAccepted"), None)
        changed = next((i for i, event in enumerate(events) if event["name"] == "$sessionchange"), None)
        finished = next((i for i, event in enumerate(events) if event["name"] == "OnDockingFinished"), None)
        assert accepted is not None, "the station accepts the dock when the approach arrives"
        assert changed is not None, "the Host moves the client into the station"
        assert finished is not None, "the client receives docking completion"
        assert accepted < changed < finished, "acceptance, session transition and completion keep their client-visible order"
        recorded_docking = next(
            step["events"]
            for step in UNDOCK["races"][0]["steps"]
            if step["label"] == "dock"
        )
        for name in ("OnDockingAccepted", "$sessionchange", "OnDockingFinished"):
            expected = next(event for event in recorded_docking if event["name"] == name)
            actual = next(event for event in events if event["name"] == name)
            problems += compare_events([expected], [actual], f"arrival {name}", {})
    assert problems == [], "\n".join(problems[:40])

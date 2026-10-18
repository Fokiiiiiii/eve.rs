"""A pilot takes a scout complex of an occupied system in space and captures it for its faction (slice 1, the live capture walk).

    EVE_LIVE_SLOW=1 EVE_LIVE_HOST=host:port EVE_PG_URL=... pytest tests/test_fw_capture_walk.py

The pilot (the owner's test ship, the Cockroach, with the QA Smartbomb) undocks in Manifest (30100032) and flies the stargates to the nearest occupied
system that has an open scout complex (the complexes are spawned by the server's tick). It enlists in the occupier's enemy (facWarMgr.JoinFactionAsCharacter),
warps to the complex's acceleration gate and takes it (keeper.ActivateAccelerationGate), then moves to 18 km or more from the gate (the smartbomb's
surface rule, DEVIATIONS "Smartbomb near a station or stargate") and inside the complex's 30 km presence. The smartbomb clears the defenders; the capture
timer then runs while the pilot stays in the complex. When the scout is captured the system's attacker points rise by its 50 victory points, the pilot is
told OnFacWarDungeonCompleted and OnVictoryPointStateUpdated, fwVictoryPointMgr shows the points, and the pilot's statistics (facWarMgr.GetStats_Character) hold the 50 points. The server's state is read through the run's database
(tests/fw_complex_live.py). Run it through tools/mitm to keep the capture.
"""

from __future__ import annotations

import json
import os
import re
import time
import uuid

import pytest

from evecli import marshal as m
from evecli import packet
from evecli.describe import describe
from evecli.observe import wait_quietly
from tests import fw_complex_live as fw
from tests import test_login_to_character as base
from tests.test_death import DEATH
from tests.test_fw_complex_walk import _states
from tests.test_mission_walk import fly, in_range, seed_cockroach, until_stopped
from tests.test_others import watch
from tests.test_target import plain_race
from tests.test_undock import STATION, SYSTEM, arrive, outcome

SDE_DIR = os.environ.get("EVE_SDE_DIR", "")
SCOUT_VICTORY_POINTS = 50  # the wiki's Scout victory points (FwComplexes.Specs)
OPEN_LIMIT_S = 180.0  # the first tick after the start opens the complexes; a fresh server needs a few seconds
DEFENDERS_LIMIT_S = 900.0  # the smartbomb clears the defenders (the room's waves)
CAPTURE_LIMIT_S = 1200.0  # the capture timer: 300 s of the pilot alone in the complex, ticked every 10 s

pytestmark = [
    pytest.mark.skipif(not base.LIVE, reason="set EVE_LIVE_HOST=host:port to test against a running server"),
    pytest.mark.skipif(os.environ.get("EVE_LIVE_SLOW") != "1", reason="several minutes: set EVE_LIVE_SLOW=1"),
    pytest.mark.skipif(any(name not in os.environ for name in ("EVE_FACTIONWARFARE_SCHEMA", "EVE_PSQL_URL", "EVE_SDE_DIR", "EVE_INVENTORY_SCHEMA")),
                       reason="needs the run's factionwarfare schema, its database and the SDE (tools/dev/run-eve-live.sh)"),
]


def wait_for_open_scouts() -> set[int]:
    started = time.time()
    while True:
        systems = set(fw.systems_with_open_scout())
        if systems or time.time() - started > OPEN_LIMIT_S:
            return systems
        time.sleep(5.0)


def warp_to_gate(who, ballpark, gate: int, limit: float = 90.0) -> None:
    """Warps to the complex's gate. The gate joins the scene when its room is placed (the server's reconcile, every ten seconds, once the pilot's
    system has its scene), so a warp soon after the jump is refused until the gate is there: it is asked again until it is."""
    started = time.time()
    while True:
        try:
            who.call_bound(ballpark[0], "CmdWarpToStuff", "item", gate, minRange=0)
            return
        except packet.MachoError as error:
            if time.time() - started > limit:
                raise AssertionError(f"the warp to the gate {gate} was refused for {int(limit)} s (record: {fw.deployed_record(gate)!r}): "
                                     f"{repr(describe(error.payload))[:600]}") from error
            watch({"events": who}, 5.0)


def activate_gate(who, gate: int, limit: float = 90.0) -> None:
    """Takes the gate into the room (keeper.ActivateAccelerationGate). The pilot must be at the gate: a walk that is still under way is refused
    as a denied target until the warp ends, so the walk asks again until it is accepted."""
    started = time.time()
    while True:
        try:
            who.call("keeper", "ActivateAccelerationGate", gate)
            return
        except packet.MachoError as error:
            if time.time() - started > limit:
                raise AssertionError(f"the gate {gate} was refused for {int(limit)} s: {repr(describe(error.payload))[:600]}") from error
            watch({"events": who}, 5.0)


def test_a_pilot_takes_a_scout_complex_in_space_and_captures_it_for_its_faction():
    run = uuid.uuid4().hex[:10]
    occupied = fw.occupied_systems()
    targets = wait_for_open_scouts()
    assert targets, "no open scout complex on the server after the start"
    hops = fw.route_to_nearest(SYSTEM, targets, SDE_DIR)
    target = hops[-1][2]
    warzone, occupier = occupied[target]
    enemy = fw.enemy_of(warzone, occupier)
    complex_id = fw.open_scout(target)
    points_before = fw.attacker_points(target)
    print("TARGET", target, "occupier", occupier, "enemy", enemy, "complex", complex_id, "hops", len(hops), "points before", points_before)

    race = plain_race(DEATH["races"][0], "victim")
    with base.connect(f"walk-fw-capture-{run}") as who:
        character, ship_id, _, _ = arrive(who, race, run)
        bomb = seed_cockroach(ship_id)
        _, error = outcome(lambda: who.call("facWarMgr", "JoinFactionAsCharacter", enemy))
        assert error is None, f"enlisting in the enemy faction {enemy}: {base.brief(error)}"
        service = who.call("ship", "MachoBindObject", (STATION, 15), None)
        who.call_bound(service[0], "Undock", ship_id, False)
        wait_quietly(who, 2.0)
        ballpark, dogma = fly(who, ship_id, SYSTEM, hops)
        # the site starts with the pilot in the system (the server's tick): its gate is the site's
        instance = fw.wait_for_site(complex_id)
        gate = fw.gate_of(instance)
        print("SITE", instance, "GATE", gate)

        # the complex's gate: warp to it and take it into the room
        warp_to_gate(who, ballpark, gate)
        until_stopped(who)
        activate_gate(who, gate)
        wait_quietly(who, 8.0)

        # 18 km from the gate or more, still inside the complex's 30 km presence: the pilot flies 12 s towards the site
        who.call_bound(ballpark[0], "CmdGotoDirection", -1.0, 0.0, 0.0)
        wait_quietly(who, 12.0)  # about 15 km at the ship's 1,265 m/s: from 15 km out to about 4 km out, inside the 30 km presence and 18 km from the gate
        who.call_bound(ballpark[0], "CmdStop")
        until_stopped(who)

        _, error = outcome(lambda: who.call_bound(dogma[0], "SetModuleOnline", ship_id, bomb))
        assert error is None, f"bringing the smartbomb online: {base.brief(error)}"
        in_range(who, lambda: who.call_bound(dogma[0], "Activate", bomb, "empWave", None, 1000), "the smartbomb")

        # the defenders go, then the capture timer runs while the pilot is alone in the complex
        started = time.time()
        seen: list[dict] = []
        progress, closed = fw.complex_progress(complex_id)
        while progress == 0 and closed is None and time.time() - started < DEFENDERS_LIMIT_S:
            seen += watch({"events": who}, 8.0)["events"]
            progress, closed = fw.complex_progress(complex_id)
        assert progress != 0 or closed is not None, f"the defenders were not cleared in {int(time.time() - started)} s: {fw.site_state(instance)}"
        print("TIMER_STARTED_AFTER", int(time.time() - started), "s")
        while closed is None and time.time() - started < DEFENDERS_LIMIT_S + CAPTURE_LIMIT_S:
            seen += watch({"events": who}, 8.0)["events"]
            progress, closed = fw.complex_progress(complex_id)
        assert closed is not None, f"complex {complex_id} was not captured: progress {progress} ms after {int(time.time() - started)} s"
        seen += watch({"events": who}, 10.0)["events"]  # the notices are sent after the commit

        points_after = fw.attacker_points(target)
        assert points_after == points_before + SCOUT_VICTORY_POINTS, (points_before, points_after)
        names = [event["name"] for event in seen]
        assert "OnFacWarDungeonCompleted" in names, sorted(set(names))
        assert "OnVictoryPointStateUpdated" in names, sorted(set(names))
        assert names.index("OnFacWarDungeonCompleted") < names.index("OnVictoryPointStateUpdated"), names
        states = dict(_states(who.call("fwVictoryPointMgr", "GetAllVictoryPointStates")))
        assert points_after in states.get(target, []), f"the system's victory point state: {states.get(target)}"
        # the pilot was alone in the complex when the timer ended, so it takes the whole 50 points; its own row and the client's statistics read it back
        # (the pilot may also have passed other complexes on the way, so the rows hold at least this capture)
        row_points = int(fw.psql(f"SELECT COALESCE(SUM(victory_points), 0) FROM {fw.fw('fw_pilot_daily')} WHERE character_id = {character}"))
        assert row_points >= SCOUT_VICTORY_POINTS, (character, row_points)
        stats = m.plain(who.call("facWarMgr", "GetStats_Character"))
        assert stats["vpTotal"] >= SCOUT_VICTORY_POINTS and stats["vpTotal"] == row_points, (stats, row_points)

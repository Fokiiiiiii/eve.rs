"""Two pilots take a battlefield of an occupied system by its acceleration gate and win it for their faction (factional warfare battlefields).

    EVE_LIVE_SLOW=1 EVE_LIVE_HOST=host:port EVE_PG_URL=... EVE_FW_CAPTURE=<the tools/mitm capture file> pytest tests/test_fw_battlefield_walk.py

The battlefield of a system has three capture points (the centre and 80 km either side) and a scoreboard. Its site (a gated room, SDE archetype 68) starts with the
first pilot in the system. Each pilot (the owner's test ship, the Cockroach, with the QA Smartbomb) enlists in the occupier's enemy, flies the stargates to the
system, warps to the battlefield's gate and takes it into the room. The pilots contest the centre, then the GM /tr position tool places them at the two SDE-derived side points 80 km away; each holds one side point. They contest the centre again, the occupier leaves it, and the attacker wins. A point is held by the side whose pilots are within its 30 km range alone: its hold timer runs, and each 20 s tick moves the score one point toward that side.
The battlefield is won at +150 (the attackers): the system's attacker points rise by 2,000, the winners get OnFwScoreboardWon and OnVictoryPointStateUpdated, their
victory points (2,000 split evenly) and their loyalty points (5,000 each, from the militia) are recorded, and the site ends.
The capture (the MITM file named by EVE_FW_CAPTURE) must hold the points' and the scoreboard's slim fields.
"""

from __future__ import annotations

import json
import os
import struct
import subprocess
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
from tests.test_fw_capture_walk import activate_gate, warp_to_gate
from tests.test_mission_walk import seed_cockroach, until_stopped
from tests.test_others import watch
from tests.test_target import plain_race
from tests.test_undock import STATION, SYSTEM, arrive, outcome

SDE_DIR = os.environ.get("EVE_SDE_DIR", "")
CAPTURE = os.environ.get("EVE_FW_CAPTURE", "")
SIDE_POINT_SPACING_METERS = 80_000  # FwBattlefields.PointSpacingMeters
OPEN_LIMIT_S = 180.0
SETTLE_LIMIT_S = 240.0  # the points take their holders on the first tick after the pilots are in place
HOLD_LIMIT_S = 3900.0  # 150 points at one point a 20 s tick: 3,000 s; the limit leaves room for the placement and the notices

pytestmark = [
    pytest.mark.skipif(not base.LIVE, reason="set EVE_LIVE_HOST=host:port to test against a running server"),
    pytest.mark.skipif(os.environ.get("EVE_LIVE_SLOW") != "1", reason="about an hour: set EVE_LIVE_SLOW=1"),
    pytest.mark.skipif(any(name not in os.environ for name in ("EVE_FACTIONWARFARE_SCHEMA", "EVE_PSQL_URL", "EVE_SDE_DIR", "EVE_INVENTORY_SCHEMA",
                                                               "EVE_CONTENT_SCHEMA", "EVE_WALLET_SCHEMA")),
                       reason="needs the run's schemas, its database and the SDE (tools/dev/run-eve-live.sh)"),
    pytest.mark.skipif(not CAPTURE, reason="needs the MITM capture of the run: EVE_FW_CAPTURE=<file of tools/mitm capture>"),
]

def jump_when_out_of_warp(who, ballpark, ship_id: int, gate: int, dest_gate: int, limit: float = 240.0) -> None:
    """Jumps through a stargate once the ship is out of warp: a jump asked while it still warps is refused (IN_WARP), so it is asked again."""
    started = time.time()
    while True:
        try:
            who.call_bound(ballpark[0], "CmdStargateJump", gate, dest_gate, ship_id)
            return
        except packet.MachoError as error:
            if time.time() - started > limit:
                raise AssertionError(f"the jump through {gate} was refused for {int(limit)} s: {repr(describe(error.payload))[:600]}") from error
            watch({"events": who}, 3.0)

def bind_ballpark(who, system: int, limit: float = 60.0):
    """Waits for a system's ballpark after a stargate jump; the session can briefly change systems before the bound object is ready."""
    started = time.monotonic()
    while time.monotonic() - started < limit:
        ballpark = who.call("beyonce", "MachoBindObject", system, None)
        if ballpark is not None:
            return ballpark
        watch({"events": who}, 2.0)
    raise AssertionError(f"the ballpark for system {system} was not ready in {int(limit)} s")

def fly_route(who, ship_id: int, hops) -> tuple:
    """The flight of tests/test_mission_walk.fly with each jump asked again until the ship is out of warp (pilots fly at once, so a stale Stop is no sign)."""
    ballpark = bind_ballpark(who, SYSTEM)
    dogma = None
    for gate, dest_gate, dest_system in hops:
        warp_to_gate(who, ballpark, gate)
        until_stopped(who)
        jump_when_out_of_warp(who, ballpark, ship_id, gate, dest_gate)
        wait_quietly(who, 6.0)
        ballpark = bind_ballpark(who, dest_system)
        dogma = who.call("dogmaIM", "MachoBindObject", (dest_system, 5), None)
        wait_quietly(who, 3.0)
    return ballpark, dogma

def setup_pilot(who, race, run: str, enemy: int, hops) -> tuple[int, int, list, list]:
    """One pilot: its character, enlisted in the enemy, undocked and flown to the battlefield's system. Returns (character, ship, ballpark, dogma)."""
    character, ship_id, _, _ = arrive(who, race, run)
    seed_cockroach(ship_id)
    _, error = outcome(lambda: who.call("facWarMgr", "JoinFactionAsCharacter", enemy))
    assert error is None, f"enlisting in the enemy faction {enemy}: {base.brief(error)}"
    service = who.call("ship", "MachoBindObject", (STATION, 15), None)
    who.call_bound(service[0], "Undock", ship_id, False)
    wait_quietly(who, 2.0)
    ballpark, dogma = fly_route(who, ship_id, hops)
    return character, ship_id, ballpark, dogma

def enter_by_gate(who, ballpark, gate: int) -> None:
    """Warps to the battlefield's gate, stops, and takes the gate into the room (the room's entry is 15 km from the centre point: inside its range)."""
    warp_to_gate(who, ballpark, gate)
    until_stopped(who)
    activate_gate(who, gate)
    wait_quietly(who, 8.0)

def capture_frames() -> list[dict]:
    """The frames the MITM capture of the run holds (one JSON record per line, flushed as they are sent)."""
    with open(CAPTURE, encoding="utf-8", errors="replace") as stream:
        return [json.loads(line) for line in stream if line.strip()]

def ball_frames(frames: list[dict], ball: int, field: str) -> int:
    """The server-to-client frames that name a ball and a slim field: the ball's id as a number, or as the little-endian bytes its destiny buffers carry."""
    little_endian = struct.pack("<q", ball).hex()
    return sum(1 for frame in frames if frame.get("dir") == "s2c" and field in str(frame.get("args", ""))
               and (str(ball) in str(frame.get("args", "")) or little_endian in str(frame.get("args", ""))))

def test_a_pilot_holds_a_battlefield_point_and_the_battlefield_is_won_for_its_faction():
    run = uuid.uuid4().hex[:10]
    battlefields = fw.wait_for_open_battlefields(OPEN_LIMIT_S)
    assert battlefields, "no open battlefield on the server after the start"
    occupied = fw.occupied_systems()
    hops = fw.route_to_nearest(SYSTEM, set(battlefields), SDE_DIR)
    target = hops[-1][2]
    battlefield, occupier, _ = battlefields[target]
    enemy = fw.enemy_of(occupied[target][0], occupier)
    points_before = fw.attacker_points(target)
    point_ids = [fw.point_deployable(battlefield, index) for index in range(3)]
    scoreboard_id = fw.scoreboard_deployable(battlefield)
    print("BATTLEFIELD", battlefield, "SYSTEM", target, "OCCUPIER", occupier, "ENEMY", enemy, "POINTS", point_ids, "SCOREBOARD", scoreboard_id)

    race = plain_race(DEATH["races"][0], "victim")
    with base.connect(f"walk-fw-battlefield-{run}") as who:
        character, ship_id, ballpark, dogma = setup_pilot(who, race, run, enemy, hops)
        print("PILOT", character, "SHIP", ship_id, "IN", target)

        # the site starts with the first pilot in the system: the gate is the site's
        instance = fw.wait_for_battlefield_site(battlefield)
        gate = fw.gate_of(instance)
        print("SITE", instance, "GATE", gate)
        enter_by_gate(who, ballpark, gate)

        # the placement: the points are sent to the pilot's client with their faction info (the capture holds the frames)
        started = time.time()
        while True:
            watch({"events": who}, 5.0)
            frames = capture_frames()
            missing = [index for index, point in enumerate(point_ids) if not ball_frames(frames, point, "component_fwCapturePoint_factionInfo")]
            if not missing or time.time() - started > SETTLE_LIMIT_S:
                break
        assert not missing, f"points {missing} have no faction info in the capture: {[point_ids[i] for i in missing]}"

        # The opposing militia pilot contests the centre. The room's GM /tr pos tool places each pilot at an SDE-derived side point:
        # the points sit 80 km from the origin, outside the room's entry range, and each faction holds one side point.
        with base.connect(f"walk-fw-battlefield-{run}-occupier") as defender:
            defender_race = plain_race(DEATH["races"][0], "watcher")
            _, defender_ship, defender_ballpark, _ = setup_pilot(defender, defender_race, run + "-occupier", occupier, hops)
            enter_by_gate(defender, defender_ballpark, gate)
            watch({"events": who}, 12.0)
            contested_score = fw.battlefield_row(battlefield)[0]
            watch({"events": who}, 25.0)
            assert fw.battlefield_row(battlefield)[0] == contested_score, "the centre capture score advanced while both factions were in point range"

            entry = fw.battlefield_entry(target)
            origin = [entry[0] - fw.ROOM_ENTRY_OFFSET_METERS, entry[1], entry[2]]
            side_positions = {
                1: [origin[0] + SIDE_POINT_SPACING_METERS, origin[1], origin[2]],
                2: [origin[0] - SIDE_POINT_SPACING_METERS, origin[1], origin[2]],
            }
            for client, index in ((who, 1), (defender, 2)):
                position = side_positions[index]
                answer = m.plain(client.call("slash", "SlashCmd", f"/tr me pos={position[0]:.0f},{position[1]:.0f},{position[2]:.0f}"))
                assert "Transported me" in str(answer), f"GM relocation to side point {index} failed: {answer}"

            started = time.monotonic()
            while time.monotonic() - started < SETTLE_LIMIT_S:
                watch({"events": who}, 8.0)
                watch({"events": defender}, 8.0)
                points = fw.battlefield_points(battlefield)
                if points[1][2] == enemy and points[2][2] == occupier:
                    break
            points = fw.battlefield_points(battlefield)
            assert points[1][2] == enemy, f"attacking pilot did not hold side point 1: {points}"
            assert points[2][2] == occupier, f"occupying pilot did not hold side point 2: {points}"

            centre = origin
            for client in (who, defender):
                answer = m.plain(client.call("slash", "SlashCmd", f"/tr me pos={centre[0]:.0f},{centre[1]:.0f},{centre[2]:.0f}"))
                assert "Transported me" in str(answer), f"GM relocation to the centre failed: {answer}"
            watch({"events": who}, 12.0)
            contested_score = fw.battlefield_row(battlefield)[0]
            watch({"events": who}, 25.0)
            assert fw.battlefield_row(battlefield)[0] == contested_score, "the centre capture score advanced during the live contest"
            away = side_positions[2]
            answer = m.plain(defender.call("slash", "SlashCmd", f"/tr me pos={away[0]:.0f},{away[1]:.0f},{away[2]:.0f}"))
            assert "Transported me" in str(answer), f"occupier did not leave the centre: {answer}"

        # the centre point is held by the pilot inside its 30 km range (the room's entry is 15 km from it): the holder changes to the attackers
        started = time.time()
        holder = None
        while time.time() - started < SETTLE_LIMIT_S:
            watch({"events": who}, 8.0)
            holder = fw.battlefield_points(battlefield)[0][2]
            if holder == enemy:
                break
        assert holder == enemy, f"the centre point was not held by the enemy {enemy}: {fw.battlefield_points(battlefield)}"

        # the score moves toward the attackers, one point per 20 s of holding, until the battlefield is won at +150
        samples = []
        started = time.time()
        closed = None
        seen_names: list[str] = []
        while closed is None and time.time() - started < HOLD_LIMIT_S:
            out = watch({"events": who}, 10.0)
            seen_names += [event["name"] for event in out["events"]]
            score, closed, winner = fw.battlefield_row(battlefield)
            samples.append(score)
        assert closed is not None, f"battlefield {battlefield} was not won in {int(HOLD_LIMIT_S)} s: score {samples[-1:] if samples else None}"
        score, closed, winner = fw.battlefield_row(battlefield)
        assert winner == enemy and score == 150, (winner, score)
        assert samples[-1] == 150 and samples == sorted(samples) and samples[0] < samples[-1], f"the score did not rise: {samples[:10]} ... {samples[-5:]}"
        seen_names += [event["name"] for event in watch({"events": who}, 10.0)["events"]]

        # the awards: the system's victory points, the notices to the pilot, its statistics and loyalty points
        assert fw.attacker_points(target) == points_before + 2_000
        assert "OnFwScoreboardWon" in seen_names and "OnVictoryPointStateUpdated" in seen_names, sorted(set(seen_names))
        assert seen_names.index("OnFwScoreboardWon") < seen_names.index("OnVictoryPointStateUpdated"), seen_names
        rows = dict(fw.battlefield_winner_rows(battlefield))
        assert list(rows) == [character] and rows[character] == 2_000, rows
        stats = m.plain(who.call("facWarMgr", "GetStats_Character"))
        assert stats["vpTotal"] == 2_000, (character, stats)
        credits = fw.battlefield_lp(battlefield)
        assert sum(delta for _, delta in credits) == fw.LP_EACH, credits
        assert any(holder_id == character for holder_id, _ in credits), credits

        # the capture holds the slim fields: the centre point's tick countdown, and the scoreboard's score moving
        frames = capture_frames()
        assert ball_frames(frames, point_ids[0], "component_fwCapturePoint_nextPointTime") >= 2, "no tick countdown of the point in the capture"
        assert ball_frames(frames, scoreboard_id, "component_fwScoreboard_score") >= 2, "the scoreboard's score did not update in the capture"

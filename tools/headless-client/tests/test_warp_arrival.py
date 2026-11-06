"""Follow an undock through warp arrival without requesting state during the flight."""

import math
import os
import signal
import struct
import subprocess
import time
import uuid

import pytest

from evecli.describe import buffer_bytes
from evecli.observe import _set_state_fields, wait_quietly
from tests import test_login_to_character as base
from tests.test_undock import STATION, SYSTEM, actions_of, add_ball_slim, arrive, keyval, number
from tests.test_warp import WARP, names

pytestmark = [
    pytest.mark.skipif(not base.LIVE, reason="set EVE_LIVE_HOST to test against a running server"),
    pytest.mark.skipif(os.environ.get("EVE_LIVE_SLOW") != "1", reason="requires a real-time warp"),
]


def ego_ball(events, ship_id):
    for event in reversed(events):
        for action in reversed(event.get("destiny", {}).get("actions", [])):
            if action.get("name") != "SetState":
                continue
            fields = _set_state_fields(action["args"])
            data = buffer_bytes(fields.get("state"))
            if data is None:
                continue
            at = data.find(struct.pack("<q", ship_id), 5)
            if at < 0 or len(data) < at + 99:
                continue
            return (
                data[at + 8],
                struct.unpack_from("<f", data, at + 63)[0],
                struct.unpack_from("<3d", data, at + 67),
                struct.unpack_from("<f", data, at + 91)[0],
                struct.unpack_from("<f", data, at + 95)[0],
            )
    raise AssertionError("no ego ball in SetState")


def wait_for_warp_arrival(who, ballpark, target_id, ship_id, *, kind="item", min_range=0, during_warp=None):
    if kind == "autopilot":
        who.call_bound(ballpark, "CmdWarpToStuffAutopilot", target_id)
    else:
        who.call_bound(ballpark, "CmdWarpToStuff", kind, target_id, minRange=min_range)
    if during_warp is not None:
        during_warp()
    events = wait_quietly(who, 150.0, lambda seen: any("Stop" in names(event) for event in seen))
    arrival = [event for event in events if names(event) == ["SetBallPosition", "SetBallVelocity", "SetSpeedFraction", "Stop"]]
    assert arrival, f"no complete warp arrival update for {target_id}"
    actions, _ = actions_of(arrival[-1])
    assert all(action[2]["$tuple"][0] in (ship_id, {"$long": ship_id}) for action in actions)
    return events


def gate_ids(events):
    gates = set()
    for event in events:
        if event.get("name") != "DoDestinyUpdate":
            continue
        for _, name, args in actions_of(event)[0]:
            if name != "AddBalls2":
                continue
            for entry in args["$tuple"][0]["$tuple"][1]:
                fields = keyval(add_ball_slim(entry))
                if number(fields.get("groupID", 0)) == 10:
                    gates.add(number(fields["itemID"]))
    return gates


def stop_simulation(pid):
    os.kill(pid, signal.SIGTERM)


def restart_simulation(socket_path):
    binary = os.environ["EVE_SIM_BINARY"]
    previous = os.stat(socket_path) if os.path.exists(socket_path) else None
    process = subprocess.Popen([binary], stdout=subprocess.DEVNULL, stderr=subprocess.STDOUT)
    deadline = time.monotonic() + 45.0
    while time.monotonic() < deadline:
        if process.poll() is not None:
            raise AssertionError(f"replacement simulation exited with {process.returncode}")
        try:
            current = os.stat(socket_path)
        except FileNotFoundError:
            pass
        else:
            if previous is None or (current.st_dev, current.st_ino, current.st_ctime_ns) != (
                previous.st_dev,
                previous.st_ino,
                previous.st_ctime_ns,
            ):
                return process
        time.sleep(0.1)
    process.terminate()
    try:
        process.wait(timeout=10.0)
    except subprocess.TimeoutExpired:
        process.kill()
        process.wait(timeout=5.0)
    raise AssertionError("replacement simulation did not open its socket")


def stop_replacement_simulation(process):
    if process.poll() is None:
        process.terminate()
        try:
            process.wait(timeout=10.0)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=5.0)


def test_undock_align_warp_arrival_and_next_order():
    race = WARP["races"][0]
    with base.connect(f"walk-warp-arrival-{uuid.uuid4().hex[:10]}", timeout=120.0) as who:
        _, ship_id, _, _ = arrive(who, race, uuid.uuid4().hex[:10])
        ship = who.call("ship", "MachoBindObject", (STATION, 15), None)
        wait_quietly(who, 0.5)
        who.call_bound(ship[0], "Undock", ship_id, False)
        wait_quietly(who, 2.0)
        ballpark = who.call("beyonce", "MachoBindObject", SYSTEM, None)[0]
        bind_events = wait_quietly(who, 2.5)
        initial = ego_ball(bind_events, ship_id)
        assert initial[1] > 0 and initial[3] > 0, initial

        planet = race["planetID"]
        who.call_bound(ballpark, "CmdAlignTo", planet)
        wait_quietly(who, 1.0)
        wait_for_warp_arrival(who, ballpark, planet, ship_id)
        who.call_bound(ballpark, "UpdateStateRequest")
        landed = ego_ball(wait_quietly(who, 2.0), ship_id)
        assert landed[0] == 2, landed
        assert landed[1] == pytest.approx(initial[1])
        assert landed[3] == pytest.approx(initial[3])
        assert math.sqrt(sum(v * v for v in landed[2])) < 1.0
        assert landed[4] == 0.0

        point = {"x": landed[2][0] + 1_000_000, "y": landed[2][1], "z": landed[2][2]}
        wait_for_warp_arrival(who, ballpark, point, ship_id, kind="position", min_range=25_000)
        who.call_bound(ballpark, "UpdateStateRequest")
        positioned = ego_ball(wait_quietly(who, 2.0), ship_id)
        distance = math.dist(positioned[2], (point["x"], point["y"], point["z"]))
        assert distance == pytest.approx(25_000, abs=1_000), distance

        wait_for_warp_arrival(who, ballpark, STATION, ship_id)
        who.call_bound(ballpark, "CmdDock", STATION)
        docked = wait_quietly(who, 15.0, lambda seen: any(event["name"] == "$sessionchange" for event in seen))
        assert any(event["name"] == "$sessionchange" for event in docked), docked
        assert all(names(e) for e in bind_events if e["name"] == "DoDestinyUpdate"), bind_events


@pytest.mark.timeout(600)
def test_autopilot_warp_to_a_gate_arrives_then_accepts_another_warp_and_dock():
    race = WARP["races"][0]
    with base.connect(f"walk-warp-gate-{uuid.uuid4().hex[:10]}", timeout=120.0) as who:
        _, ship_id, _, _ = arrive(who, race, uuid.uuid4().hex[:10])
        ship = who.call("ship", "MachoBindObject", (STATION, 15), None)
        wait_quietly(who, 0.5)
        who.call_bound(ship[0], "Undock", ship_id, False)
        wait_quietly(who, 2.0)
        ballpark = who.call("beyonce", "MachoBindObject", SYSTEM, None)[0]
        bind_events = wait_quietly(who, 2.5)
        initial = ego_ball(bind_events, ship_id)
        gates = gate_ids(bind_events)
        assert gates, "the bound system state contains no stargate ball"
        gate = max(gates, key=lambda item: math.dist(ego_ball(bind_events, item)[2], initial[2]))
        gate_position = ego_ball(bind_events, gate)[2]
        assert math.dist(gate_position, initial[2]) > 150_000

        def stop_and_align_during_warp():
            who.call_bound(ballpark, "CmdStop")
            who.call_bound(ballpark, "CmdAlignTo", gate)

        wait_for_warp_arrival(who, ballpark, gate, ship_id, kind="autopilot", during_warp=stop_and_align_during_warp)
        wait_for_warp_arrival(who, ballpark, STATION, ship_id)
        who.call_bound(ballpark, "CmdDock", STATION)
        docked = wait_quietly(who, 15.0, lambda seen: any(event["name"] == "$sessionchange" for event in seen))
        assert any(event["name"] == "$sessionchange" for event in docked), docked


def test_sim_link_loss_during_warp_reconnects_and_accepts_warp_and_dock():
    race = WARP["races"][0]
    sim_socket = os.environ["EVE_SIM_SOCKET"]
    sim_pid = int(os.environ["EVE_SIM_PID"])
    replacement = None
    try:
        with base.connect(f"walk-warp-reconnect-{uuid.uuid4().hex[:10]}", timeout=120.0) as who:
            _, ship_id, _, _ = arrive(who, race, uuid.uuid4().hex[:10])
            ship = who.call("ship", "MachoBindObject", (STATION, 15), None)
            wait_quietly(who, 0.5)
            who.call_bound(ship[0], "Undock", ship_id, False)
            wait_quietly(who, 2.0)
            ballpark = who.call("beyonce", "MachoBindObject", SYSTEM, None)[0]
            wait_quietly(who, 2.5)

            planet = race["planetID"]
            who.call_bound(ballpark, "CmdAlignTo", planet)
            wait_quietly(who, 1.0)
            who.call_bound(ballpark, "CmdWarpToStuff", "item", planet, minRange=0)
            underway = wait_quietly(who, 2.0)
            assert any("WarpTo" in names(event) for event in underway), underway
            assert not any("Stop" in names(event) for event in underway), underway

            stop_simulation(sim_pid)
            lost = wait_quietly(who, 30.0, lambda seen: any(event["name"] == "$sessionchange" for event in seen))
            assert any(event["name"] == "$sessionchange" for event in lost), lost
            replacement = restart_simulation(sim_socket)
            wait_quietly(who, 2.0)

            ship = who.call("ship", "MachoBindObject", (STATION, 15), None)
            wait_quietly(who, 0.5)
            who.call_bound(ship[0], "Undock", ship_id, False)
            wait_quietly(who, 2.0)
            ballpark = who.call("beyonce", "MachoBindObject", SYSTEM, None)[0]
            wait_quietly(who, 2.5)

            who.call_bound(ballpark, "CmdAlignTo", planet)
            wait_quietly(who, 1.0)
            wait_for_warp_arrival(who, ballpark, planet, ship_id)
            wait_for_warp_arrival(who, ballpark, STATION, ship_id)
            who.call_bound(ballpark, "CmdDock", STATION)
            docked = wait_quietly(who, 15.0, lambda seen: any(event["name"] == "$sessionchange" for event in seen))
            assert any(event["name"] == "$sessionchange" for event in docked), docked
    finally:
        if replacement is not None:
            stop_replacement_simulation(replacement)

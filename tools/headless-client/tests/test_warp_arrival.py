"""Follow an undock through warp arrival without requesting state during the flight."""

import math
import os
import struct
import uuid

import pytest

from evecli.describe import buffer_bytes
from evecli.observe import _set_state_fields, wait_quietly
from tests import test_login_to_character as base
from tests.test_undock import STATION, SYSTEM, actions_of, arrive
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
        who.call_bound(ballpark, "CmdWarpToStuff", "item", planet, minRange=0)
        events = wait_quietly(who, 150.0, lambda seen: any("Stop" in names(e) for e in seen))
        arrival = [e for e in events if names(e) == ["SetBallPosition", "SetBallVelocity", "SetSpeedFraction", "Stop"]]
        assert arrival, "no complete warp arrival update"
        actions, _ = actions_of(arrival[-1])
        assert all(a[2]["$tuple"][0] in (ship_id, {"$long": ship_id}) for a in actions)

        who.call_bound(ballpark, "UpdateStateRequest")
        landed = ego_ball(wait_quietly(who, 2.0), ship_id)
        assert landed[0] == 2, landed
        assert landed[1] == pytest.approx(initial[1])
        assert landed[3] == pytest.approx(initial[3])
        assert math.sqrt(sum(v * v for v in landed[2])) < 1.0
        assert landed[4] == 0.0

        who.call_bound(ballpark, "CmdOrbit", planet, 5000)
        after = wait_quietly(who, 2.0)
        assert any("Orbit" in names(e) for e in after), after
        assert all(names(e) for e in bind_events if e["name"] == "DoDestinyUpdate"), bind_events

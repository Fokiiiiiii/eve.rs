"""A pilot jumps through a static wormhole and back through its junction end, against a running server (slice 10, the headless check).

    EVE_LIVE_SLOW=1 EVE_LIVE_HOST=host:port EVE_PG_URL=postgres://... pytest tests/test_wormhole_live.py

The world of the server has its statics from the start, but none leads into the station where a new pilot stands. So the test makes
one: a row of the world's table, copied from an active static, whose destination is the station's system. The pilot undocks, warps to
the near end (the simulation takes a jump only within its range), jumps through it with `wormholeMgr.WormholeJump` into the wormhole
system, and jumps back through the far end the world places there. The row is deactivated at the end. The warp takes some seconds,
so this runs only with EVE_LIVE_SLOW=1. Skipped unless EVE_LIVE_HOST and EVE_PG_URL are set.
"""

import os
import time
import uuid

import pytest

from evecli import marshal as m
from evecli import packet as p
from evecli.observe import wait_quietly

from tests import test_login_to_character as base
from tests.test_agents_live import new_pilot, psql, schema
from tests.test_undock import STATION

PG_URL = os.environ.get("EVE_PG_URL", "")
pytestmark = [
    pytest.mark.skipif(not (base.LIVE and PG_URL), reason="set EVE_LIVE_HOST=host:port and EVE_PG_URL to test against a running server"),
    pytest.mark.skipif(os.environ.get("EVE_LIVE_SLOW") != "1", reason="the warp takes seconds: set EVE_LIVE_SLOW=1"),
]

WORMHOLES = os.environ.get("EVE_WORMHOLE_SCHEMA", "wormhole")  # the world's schema: the run's scratch copy, or the default (Eve.Wormholes.WormholeStore.DefaultSchema)
WARP_SECONDS = 20.0  # the warp from the station to the near end, then the time the client gives the server to finish it
JUMP_SECONDS = 120.0
FLY_SECONDS = 25.0  # the flight in from about 12 km at full speed  # the time the simulation takes to put the ship in the other system

def system_of(character: int) -> int:
    return int(psql(f"SELECT solar_system_id FROM {schema('EVE_CHARACTERS_SCHEMA')}.characters WHERE id = {character}"))

def wait_for(predicate, seconds: float) -> bool:
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        if predicate():
            return True
        time.sleep(0.5)
    return predicate()

def jump_when_in_range(who, end: int, seconds: float) -> None:
    """Asks for the jump until the simulation takes it: a refusal (too far) is answered by the warp still going on."""
    deadline = time.monotonic() + seconds
    while True:
        try:
            who.call("wormholeMgr", "WormholeJump", end)
            return
        except p.MachoError:
            if time.monotonic() > deadline:
                raise
            wait_quietly(who, 3.0)

def test_a_pilot_jumps_through_a_static_wormhole_and_back_through_its_far_end():
    run = uuid.uuid4().hex[:10]
    with base.connect(f"wormhole-{run}") as who:
        character = new_pilot(who, run)
        ship = int(psql(f"SELECT ship_id FROM {schema('EVE_CHARACTERS_SCHEMA')}.characters WHERE id = {character}"))
        home = system_of(character)
        # the static of the test: a copy of an active static of the world, leading into the home system; its ends are the connection's id times two
        connection = int(psql(
            f"INSERT INTO {WORMHOLES}.wormhole_connections (source_system_id, destination_system_id, type_id, code, is_static, mass_kg, "
            f"max_stable_mass_kg, created_ms, expires_ms, active, seed) "
            f"SELECT source_system_id, {home}, type_id, 'LIVE', true, mass_kg, max_stable_mass_kg, "
            f"(extract(epoch from now()) * 1000)::bigint, (extract(epoch from now()) * 1000)::bigint + 3600000, true, seed "
            f"FROM {WORMHOLES}.wormhole_connections WHERE active AND is_static ORDER BY connection_id LIMIT 1 RETURNING connection_id"))
        # the copy's source is the junction (its end is the connection's id times two: the near end), its destination is the home system (the far end)
        junction_end, home_end = connection * 2, connection * 2 + 1
        try:
            junction = int(psql(f"SELECT source_system_id FROM {WORMHOLES}.wormhole_connections WHERE connection_id = {connection}"))

            ship_bound = who.call("ship", "MachoBindObject", (STATION, 15), None)
            wait_quietly(who, 0.5)
            who.call_bound(ship_bound[0], "Undock", ship, False, onlineModules=m.List([]))
            wait_quietly(who, 2.0)
            ballpark = who.call("beyonce", "MachoBindObject", home, None)
            wait_quietly(who, 2.5)

            # the home end is a ball of the home system once the pilot undocks: the warp brings the ship in range (the simulation's jump range)
            who.call_bound(ballpark[0], "CmdWarpToStuff", "item", home_end, minRange=0)
            wait_quietly(who, WARP_SECONDS)
            jump_when_in_range(who, home_end, JUMP_SECONDS)
            assert wait_for(lambda: system_of(character) == junction, JUMP_SECONDS), "the pilot did not come out of the wormhole system"

            # the pilot comes out about 12 km from the junction end, outside the jump range: it binds the wormhole system's ballpark, flies to the
            # junction end, and jumps back through it
            # the database shows the pilot in the junction before the arrival sets the session's system: the bind is answered None until
            # the session follows, so it is asked again (the client binds once its session change has come)
            ballpark_j = None
            bind_deadline = time.monotonic() + JUMP_SECONDS
            while ballpark_j is None and time.monotonic() < bind_deadline:
                ballpark_j = who.call("beyonce", "MachoBindObject", junction, None)
                if ballpark_j is None:
                    wait_quietly(who, 0.5)
            assert ballpark_j is not None, "the junction's ballpark did not bind"
            wait_quietly(who, 2.5)
            # a warp needs 150 km (the simulation's minimum), so the pilot flies in: full speed, following the end to its range of zero
            who.call_bound(ballpark_j[0], "CmdSetSpeedFraction", 1.0)
            who.call_bound(ballpark_j[0], "CmdFollowBall", junction_end, 0)
            wait_quietly(who, FLY_SECONDS)
            jump_when_in_range(who, junction_end, JUMP_SECONDS)
            assert wait_for(lambda: system_of(character) == home, JUMP_SECONDS), "the pilot did not come back through the far end"
        finally:
            psql(f"UPDATE {WORMHOLES}.wormhole_connections SET active = false WHERE connection_id = {connection}")

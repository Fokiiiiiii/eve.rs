"""The CRAB walk: a pilot in a Cockroach flies to low-security space, launches a CONCORD Rogue Analysis Beacon from its hold,
links after its 20-second activation, and sees the first Rogue Drone wave after the four-minute link completes. Warp is refused during
the link and the 60-second status tail, then allowed (slice 2; eve.rs only).

    EVE_LIVE_SLOW=1 EVE_LIVE_HOST=host:port EVE_PSQL_URL=... EVE_INVENTORY_SCHEMA=... EVE_SKILLS_SCHEMA=... pytest tests/test_crab_walk.py

The CRAB and the anchoring skill are fixtures put into the run's database (the CRAB in the hold, the skill at level 3), as the mission
walk seeds its ship. Manifest (30100032) is high-security; the route is four gates to 30004988 (security 0.43, SDE mapStargates). The
EVE.js side is not run: its character store is not seeded from the database, so the same fixture cannot be written there.
"""

from __future__ import annotations

import os
import subprocess
import time
import uuid

import pytest

from evecli import marshal as m
from evecli.observe import timed_events, wait_quietly
from tests import test_login_to_character as base
from tests.test_death import DEATH
from tests.test_mission_walk import seed_cockroach, until_stopped
from tests.test_target import plain_race
from tests.test_undock import STATION, SYSTEM, arrive, outcome
from tests.test_weapon import slims_in

pytestmark = [
    pytest.mark.skipif(not base.LIVE, reason="set EVE_LIVE_HOST=host:port to test against a running server"),
    pytest.mark.skipif(os.environ.get("EVE_LIVE_SLOW") != "1", reason="four minutes of link: set EVE_LIVE_SLOW=1"),
    pytest.mark.skipif("EVE_INVENTORY_SCHEMA" not in os.environ or "EVE_PSQL_URL" not in os.environ or "EVE_SKILLS_SCHEMA" not in os.environ,
                       reason="the CRAB and its skill are seeded in the run's database (tools/dev/run-eve-live.sh with EVE_PG_URL)"),
]

CRAB = 60244  # the standard CONCORD Rogue Analysis Beacon (SDE 60244)
ANCHORING = 11584  # the skill a CRAB's launch needs (the wiki's CRAB page; the host checks it at level 3)
LOW_SYSTEM = 30004988  # security 0.43, four gates from Manifest (SDE mapStargates, the shortest route)
LOW_HOPS = [(50017727, 50016629, 30005204), (50003291, 50003399, 30005206), (50003400, 50003410, 30004987), (50003411, 50004520, 30004988)]
WARP_GATE = 50004521  # another gate of LOW_SYSTEM (SDE mapStargates), about 380,000 km from the arrival gate: a warp target the ship can make


def sql(text: str) -> str:
    url = os.environ["EVE_PSQL_URL"]
    return subprocess.run(["psql", url, "-At", "-c", text], check=True, capture_output=True, text=True).stdout.strip()


def seed_crab(ship_id: int) -> int:
    """A fixture, not a server path: a standard CRAB in the ship's cargo (flag 5) and the anchoring skill at level 3 for its owner."""
    inventory, skills = os.environ["EVE_INVENTORY_SCHEMA"], os.environ["EVE_SKILLS_SCHEMA"]
    owner = sql(f"SELECT owner_id FROM {inventory}.items WHERE id = {ship_id}").split()[0]
    sql(f"INSERT INTO {skills}.character_skills (character_id, type_id, level, skill_points) VALUES ({owner}, {ANCHORING}, 3, 45255) "
        f"ON CONFLICT (character_id, type_id) DO UPDATE SET level = 3, skill_points = 45255")
    crab = sql(f"INSERT INTO {inventory}.items (type_id, owner_id, location_id, flag, quantity, singleton) "
               f"VALUES ({CRAB}, {owner}, {ship_id}, 5, 1, true) RETURNING id")
    return int(crab.split()[0])


def fly_from(who, ship_id: int, ballpark):
    """The gates of the low-security route in turn, from the system the pilot is bound to: warp to the gate, jump, bind the next system."""
    for gate, dest_gate, dest_system in LOW_HOPS:
        who.call_bound(ballpark[0], "CmdWarpToStuff", "item", gate, minRange=0)
        until_stopped(who)
        who.call_bound(ballpark[0], "CmdStargateJump", gate, dest_gate, ship_id)
        # the jump lands in the next system a few seconds on: the system binds once the ship is there (checked for a minute)
        for _ in range(12):
            wait_quietly(who, 5.0)
            ballpark, error = outcome(lambda: who.call("beyonce", "MachoBindObject", dest_system, None))
            if error is None and ballpark is not None:
                break
        assert error is None and ballpark is not None, f"the system {dest_system} did not bind: {error!r}"
        wait_quietly(who, 3.0)
    return ballpark


def refused(error) -> bool:
    return error is not None and "warp" in str(error).lower()


def test_a_crab_is_launched_linked_and_holds_the_ship_until_the_link_is_over():
    race = plain_race(DEATH["races"][0], "victim")
    run = uuid.uuid4().hex[:10]
    with base.connect(f"walk-crab-{run}") as who:
        _, ship_id, _, _ = arrive(who, race, run)
        seed_cockroach(ship_id)  # the owner's test ship (the Cockroach is on the CRAB link lists)
        service = who.call("ship", "MachoBindObject", (STATION, 15), None)
        who.call_bound(service[0], "Undock", ship_id, False)
        wait_quietly(who, 2.0)
        bound, error = outcome(lambda: who.call("beyonce", "MachoBindObject", SYSTEM, None))
        assert error is None and bound is not None, f"the system did not bind: {error!r}"
        ballpark = fly_from(who, ship_id, bound)

        # the CRAB is put in the hold once the pilot is in the low-security system (the undock takes the hold's cargo into account)
        crab = seed_crab(ship_id)
        # the launch: the beacon goes out of the hold, and is answered as launched (its activation takes 20 seconds)
        in_space = who.call("ship", "MachoBindObject", (LOW_SYSTEM, 15), None)
        launched, refusals = m.plain(who.call_bound(in_space[0], "LaunchFromShip", [crab]))
        assert len(launched) == 1 and not refusals, f"the CRAB was not launched: {launched!r} {refusals!r}"
        beacon = int(launched[0])  # the beacon's own id in space (the link and its slim item name it)

        # the link starts once the activation delay has passed; its scan and first NPC wave wait until linking completes
        wait_quietly(who, 21.0)
        who.call_bound(ballpark[0], "CallComponentFromClient", beacon, "linkWithShip", "InitiateLink")
        wait_quietly(who, 3.0)
        raw, error = outcome(lambda: who.call_bound(ballpark[0], "CmdWarpToStuff", "item", WARP_GATE, minRange=0))
        assert refused(error), f"a warp during the link was not refused: {error!r} {raw!r}"

        drones: set[int] = set()
        started = time.time()
        while time.time() - started < 270 and len(drones) < 4:
            for item, _ in slims_in(timed_events(who, 3.0)):
                if item not in (ship_id, beacon):
                    drones.add(item)
        assert len(drones) >= 4, f"the first wave brought {len(drones)} NPCs into space after the link completed"
        raw, error = outcome(lambda: who.call_bound(ballpark[0], "CmdWarpToStuff", "item", WARP_GATE, minRange=0))
        assert refused(error), f"a warp during the status tail was not refused: {error!r} {raw!r}"

        # The four-minute link ends; all four statuses clear after the additional minute.
        wait_quietly(who, 60.0 + 15.0)
        raw, error = outcome(lambda: who.call_bound(ballpark[0], "CmdWarpToStuff", "item", WARP_GATE, minRange=0))
        assert error is None, f"the warp after the link status tail was refused: {error!r}"

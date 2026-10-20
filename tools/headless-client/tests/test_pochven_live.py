"""A Cockroach logs into a Pochven system directly in space, and the Triglavian NPCs the simulation spawns there are observed.

    EVE_LIVE_HOST=host:port EVE_PG_URL=postgres://... pytest tests/test_pochven_live.py

Pochven has no station, so the pilot cannot undock into it. The walk seeds the state the server keeps for a pilot in space,
the way a pilot who logged off in Pochven would have it: the character row names the system, the ship is in it with the
in-space flag (0), and a space_items pose lies in the system until a year out (SpaceSessions.GetSessionAttributesAsync and
OnCharacterEnteredAsync). Then the character is selected. Experiment: the walk asserts that the selection is answered and
that the session is put in space; the notifications it is sent are returned for comparison with the reference (tools/mitm).
Skipped unless EVE_LIVE_HOST and EVE_PG_URL are set.
"""

import collections
import json
import os
import re
import time

import pytest

from evecli import marshal as m
from evecli.observe import events_of

from tests.test_agents_live import names, psql, run_id, schema, seed_ship
from tests.test_login_to_character import WALK, connect, revive
from tests.test_login_to_character import LIVE

PG_URL = os.environ.get("EVE_PG_URL", "")

pytestmark = pytest.mark.skipif(not (LIVE and PG_URL), reason="set EVE_LIVE_HOST=host:port and EVE_PG_URL to test against a running server")

POCHVEN_SYSTEM = 30000021  # Pochven, region 10000070 (the SDE systems row): a Triglavian system
POCHVEN_CONSTELLATION = 20000788
POCHVEN_REGION = 10000070
TRIGLAVIAN_TYPES = {"52182", "52923", "56493", "56221", "54577", "54579", "54580"}  # the sim-resident hulls (pochven_population.rs)
COCKROACH_GROUP = 25  # the Cockroach's groupID (SDE types row 11019); its radius is 42 m
# stargate 50016504 of the system (SDE mapObjects__celestials): the sentries stand about 20 km from a gate, so the pilot
# is set 5 km from it, where their balls are in view (the bubble grid is 7.8e6 m a side)
GATE = (-120679911185.50594, -8208806312.394073, -25334939508.729)
EGO = (GATE[0] + 5_000.0, GATE[1], GATE[2])
FAR_FUTURE_MS = 4_102_444_800_000  # 2100-01-01: the pose outlives the walk (the server refuses an expired one)

def seed_pochven_pilot(character_id: int, ship: int) -> None:
    """The pilot's state as a pilot in space in Pochven keeps it (see the module docstring)."""
    items = f"{schema('EVE_INVENTORY_SCHEMA')}.items"
    characters = f"{schema('EVE_CHARACTERS_SCHEMA')}.characters"
    poses = f"{schema('EVE_SPACE_SCHEMA')}.space_items"
    psql(f"UPDATE {items} SET location_id = {POCHVEN_SYSTEM}, flag = 0 WHERE id = {ship}")
    psql(f"UPDATE {characters} SET solar_system_id = {POCHVEN_SYSTEM}, constellation_id = {POCHVEN_CONSTELLATION}, region_id = {POCHVEN_REGION} WHERE id = {character_id}")
    psql(f"INSERT INTO {poses} (item_id, solar_system_id, x, y, z, dx, dy, dz, radius, group_id, launcher_id, character_id, corporation_id, alliance_id, war_faction_id, expires_at_ms) "
         f"SELECT {ship}, {POCHVEN_SYSTEM}, {EGO[0]}, {EGO[1]}, {EGO[2]}, 1, 0, 0, 42, {COCKROACH_GROUP}, 0, {character_id}, corporation_id, 0, 0, {FAR_FUTURE_MS} "
         f"FROM {characters} WHERE id = {character_id}")

def test_a_cockroach_logs_into_a_pochven_system_in_space():
    creation = next(step for step in WALK["steps"] if step["call"][1] == "CreateCharacterWithDoll" and "ok" in step)
    with connect(f"pochven-{run_id()}") as client:
        service, method, described = creation["call"]
        run = run_id()
        character_id = m.plain(client.call(service, method, *[revive(value, run) for value in described]))
        ship = seed_ship(character_id)
        seed_pochven_pilot(character_id, ship)
        client.notifications.clear()

        client.call("charUnboundMgr", "SelectCharacterID", charID=character_id, secondChoiceID=None, skipTutorial=False)

        assert psql(f"SELECT solar_system_id FROM {schema('EVE_CHARACTERS_SCHEMA')}.characters WHERE id = {character_id}") == str(POCHVEN_SYSTEM)
        assert psql(f"SELECT flag FROM {schema('EVE_INVENTORY_SCHEMA')}.items WHERE id = {ship}") == "0"
        ballpark = client.call("beyonce", "MachoBindObject", POCHVEN_SYSTEM, None)  # the bind the recorded walk makes (record_character_scenarios)
        print("POCHVEN_BIND", type(ballpark).__name__)
        time.sleep(25)  # the scene's population is sent once the pilot is in the system: the walk waits for it
        events = events_of(client)
        notes = collections.Counter(event.get("name") for event in events)
        actions = collections.Counter()
        added = []
        for event in events:
            if event.get("name") != "DoDestinyUpdate":
                continue
            for action in (event.get("destiny") or {}).get("actions", []):
                actions[action.get("name")] += 1
                if action.get("name") == "AddBalls2":
                    added.append(json.dumps(action.get("args"))[:400])
        in_pochven = any('30000021' in json.dumps(event) for event in events)
        print("POCHVEN_NOTIFICATIONS", dict(notes))
        print("POCHVEN_DESTINY_ACTIONS", dict(actions))
        print("POCHVEN_SESSION_IN_SYSTEM", in_pochven)
        pairs = collections.Counter()
        for event in events:
            for action in (event.get("destiny") or {}).get("actions", []):
                if action.get("name") == "AddBalls2":
                    pairs.update(re.findall(r'\["typeID", (\d+)\]', json.dumps(action.get("args"))))
        print("POCHVEN_BALL_TYPES", dict(pairs))
        assert TRIGLAVIAN_TYPES & set(pairs), "no Triglavian ball was added to the pilot's scene in the system"

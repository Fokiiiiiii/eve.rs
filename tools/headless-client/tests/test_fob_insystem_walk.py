"""The pirate FOB in its system, live (slice 5): a Cockroach in space in the FOB's hub system fires the QA Smartbomb at the FOB until its shield is
down and the FOB is in its armor reinforcement.

    EVE_LIVE_HOST=host:port EVE_PG_URL=postgres://... EVE_LIVE_FOB_WALK=1 pytest tests/test_fob_insystem_walk.py

The campaign and the FOB are set through the database, the way the walks seed their state: the Angel campaign (warzone 1) is made active with its
empire side at its count, and its FOB structure is made vulnerable at its shield (state 110, the structures table). The pilot is seeded in space in
that system, 150 km from the FOB's centre, with eight QA Smartbombs fitted (250 km fields, 5,000 of each damage type a cycle, against the FOB's
9.6 million shield). The walk watches the ballpark for the FOB's ball (typeID 78260, its Angel Cartel FOB) and polls the structure's state until the
shield is depleted (state 111, the armor reinforcement). Run it through tools/mitm to keep the capture.
"""

import json
import os
import re
import subprocess
import time
import uuid

import pytest

from evecli import marshal as m
from evecli import packet
from evecli.observe import timed_events

from tests.test_agents_live import psql, run_id, schema, seed_ship
from tests.test_login_to_character import LIVE, WALK, connect, revive
from tests.test_mission_walk import QA_SMARTBOMB, seed_cockroach

PG_URL = os.environ.get("EVE_PG_URL", "")
FOB_TYPE = 78260  # the Angel Cartel FOB (SDE types row 78260; the Angel warzone is the client's warzone 1)
ARMOR_REINFORCE = 111  # the structure state of the armor reinforcement (StructureState.ArmorReinforce)
SHIELD_VULNERABLE = 110  # the structure state of the shield stage (StructureState.ShieldVulnerable)
BOMBS = 1  # one QA Smartbomb runs at a time (the server refuses a second of the same effect, EffectAlreadyActive2): 5,333 damage a second
# after the 0.8 resonances, so the FOB's 9.6 million shield takes about 30 minutes of cycles
EGO_OFFSET = 150_000.0  # 150 km from the FOB's centre (its radius is 50 km): inside the bombs' 250 km fields
COCKROACH_GROUP = 25
COCKROACH = 11019
FAR_FUTURE_MS = 4_102_444_800_000
WALK_LIMIT_S = 2_100.0

pytestmark = pytest.mark.skipif(
    not (LIVE and PG_URL and os.environ.get("EVE_LIVE_FOB_WALK") == "1"),
    reason="set EVE_LIVE_HOST, EVE_PG_URL and EVE_LIVE_FOB_WALK=1 (about twenty minutes of smartbomb cycles)",
)


def fit_bombs(ship_id: int, count: int) -> list[int]:
    """The QA Smartbomb of the ship: the mission walk's fixture (the Cockroach with the bomb in a mid slot)."""
    return [seed_cockroach(ship_id)][:count]


def seed_hub_pilot(character_id: int, ship: int, system: int) -> None:
    """The pilot as a pilot in space keeps it: the character row names the system, the ship is in it (flag 0) and a pose lies 150 km from the FOB."""
    items = f"{schema('EVE_INVENTORY_SCHEMA')}.items"
    characters = f"{schema('EVE_CHARACTERS_SCHEMA')}.characters"
    poses = f"{schema('EVE_SPACE_SCHEMA')}.space_items"
    psql(f"UPDATE {items} SET location_id = {system}, flag = 0 WHERE id = {ship}")
    psql(f"UPDATE {characters} SET solar_system_id = {system} WHERE id = {character_id}")
    psql(f"INSERT INTO {poses} (item_id, solar_system_id, x, y, z, dx, dy, dz, radius, group_id, launcher_id, character_id, corporation_id, alliance_id, war_faction_id, expires_at_ms) "
         f"SELECT {ship}, {system}, {EGO_OFFSET}, 0, 0, 1, 0, 0, 42, {COCKROACH_GROUP}, 0, {character_id}, corporation_id, 0, 0, {FAR_FUTURE_MS} "
         f"FROM {characters} WHERE id = {character_id}")


def structure_state(fob: int) -> int:
    return int(psql(f"SELECT state FROM {schema('EVE_STRUCTURES_SCHEMA')}.structures WHERE structure_id = {fob}"))


def test_the_qa_smartbombs_take_the_fobs_shield_in_its_system():
    ins = schema("EVE_INSURGENCY_SCHEMA")
    structures = schema("EVE_STRUCTURES_SCHEMA")
    row = psql(f"SELECT campaign_id, origin_solar_system_id, structure_id FROM {ins}.insurgency_campaigns "
               f"WHERE warzone_id = 1 AND state = 2 ORDER BY campaign_id LIMIT 1")
    assert row, "no forecasting Angel campaign: the server starts one per warzone at boot"
    campaign_id, system, fob = (int(value) for value in row.split("|"))
    assert fob > 0, "the campaign has its FOB"
    now = int(time.time() * 1000)

    # the campaign is active with the empire side at its count, and the FOB is vulnerable at its shield (set through the database)
    psql(f"UPDATE {ins}.insurgency_campaigns SET state = 3, antipirate_points_scored = antipirate_points_required WHERE campaign_id = {campaign_id}")
    psql(f"UPDATE {structures}.structures SET state = {SHIELD_VULNERABLE}, upkeep = 1, state_started_ms = {now}, state_ends_ms = 0 WHERE structure_id = {fob}")

    run = run_id()
    creation = next(step for step in WALK["steps"] if step["call"][1] == "CreateCharacterWithDoll" and "ok" in step)
    with connect(f"fob-{run}") as client:
        service, method, described = creation["call"]
        character_id = m.plain(client.call(service, method, *[revive(value, run) for value in described]))
        ship = seed_ship(character_id)
        seed_hub_pilot(character_id, ship, system)
        bombs = fit_bombs(ship, BOMBS)
        assert len(bombs) == BOMBS, f"only {len(bombs)} mid slots free for the bombs"
        client.notifications.clear()
        client.call("charUnboundMgr", "SelectCharacterID", charID=character_id, secondChoiceID=None, skipTutorial=False)
        ballpark = client.call("beyonce", "MachoBindObject", system, None)
        dogma = client.call("dogmaIM", "MachoBindObject", (system, 5), None)
        for bomb in bombs:
            client.call_bound(dogma[0], "SetModuleOnline", ship, bomb)

        started = time.time()
        fob_seen = False
        refusals = []
        state = SHIELD_VULNERABLE
        last_check = 0.0
        cycles = 0
        while time.time() - started < WALK_LIMIT_S and state != ARMOR_REINFORCE:
            for bomb in bombs:
                try:
                    client.call_bound(dogma[0], "Activate", bomb, "empWave", None, 1000)
                except packet.MachoError as error:  # a refusal is kept for the report; the walk goes on with the others
                    refusals.append(repr(getattr(error, "payload", error))[:160])
            for event in timed_events(client, 3.0):
                text = json.dumps(event, default=str)
                cycles += text.count("EMPWave")  # each cycle of the bomb is one field of the effect on the pilot's scene
                if re.search(r'\["typeID", %d\]' % FOB_TYPE, text):
                    fob_seen = True
            if time.time() - last_check >= 15.0:
                last_check = time.time()
                state = structure_state(fob)
        print("FOB_SEEN", fob_seen, "STATE", state, "SECONDS", int(time.time() - started), "CYCLES", cycles, "REFUSALS", len(refusals), refusals[:3])
        assert fob_seen, "the FOB's ball was not added to the pilot's scene"
        assert state == ARMOR_REINFORCE, f"the FOB's shield is not down after {int(time.time() - started)} s (state {state}, refusals {refusals[:3]})"

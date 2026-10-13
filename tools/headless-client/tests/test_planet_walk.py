"""Planetary interaction, live: a pilot with a command center in the hold places it on a barren planet, adds a storage facility, is
refused a second command center, and reads the planet back (planetMgr, V24.01 clientPlanet.py and planetSvc.py).

    EVE_LIVE_HOST=host:port EVE_PG_URL=postgres://... EVE_LIVE_PI_WALK=1 pytest tests/test_planet_walk.py

The character is created through the login walk; its skills (Interplanetary Consolidation, Command Center Upgrades) and ISK are
seeded through the database, and a command center is put in its ship's hold. The walk checks the item is consumed, the storage
facility is paid for, and the colony is in the database. Run it through tools/mitm to keep the capture.
"""

import os

import pytest

from evecli import marshal as m

from tests import dbseed
from tests.test_agents_live import psql, run_id, schema, seed_ship
from tests.test_login_to_character import LIVE, WALK, connect, revive

PG_URL = os.environ.get("EVE_PG_URL", "")
PLANET = 40486618  # a barren planet of the SDE (sde.mapPlanets, type 2016, radius 2310000 m)
BARREN = 2016
COMMAND_CENTER = 2524  # Barren Command Center
STORAGE = 2541  # Barren Storage Facility (250,000 ISK)
CONSOLIDATION = 2495  # Interplanetary Consolidation
COMMAND_CENTER_UPGRADE = 2505
LATITUDE, LONGITUDE = 0.5, 1.0  # a point on the planet (radians)
STORAGE_PRICE = 250_000

pytestmark = pytest.mark.skipif(
    not (LIVE and PG_URL and os.environ.get("EVE_LIVE_PI_WALK") == "1"),
    reason="set EVE_LIVE_HOST, EVE_PG_URL and EVE_LIVE_PI_WALK=1",
)


def skill(character: int, type_id: int, level: int) -> None:
    psql(f"INSERT INTO {schema('EVE_SKILLS_SCHEMA')}.character_skills (character_id, type_id, level, skill_points) "
         f"VALUES ({character}, {type_id}, {level}, 0) ON CONFLICT (character_id, type_id) DO UPDATE SET level = {level}")


def item_exists(item: int) -> bool:
    return psql(f"SELECT count(*) FROM {schema('EVE_INVENTORY_SCHEMA')}.items WHERE id = {item}") == "1"


def colony_pins(owner: int, planet: int) -> int:
    count = psql(f"SELECT jsonb_array_length(doc->'Pins') FROM {schema('EVE_PLANETS_SCHEMA')}.colonies WHERE owner_id = {owner} AND planet_id = {planet}")
    return int(count or 0)


def test_a_pilot_places_a_command_center_and_a_storage_facility_on_a_barren_planet():
    run = run_id()
    creation = next(step for step in WALK["steps"] if step["call"][1] == "CreateCharacterWithDoll" and "ok" in step)
    with connect(f"pi-{run}") as client:
        service, method, described = creation["call"]
        character_id = m.plain(client.call(service, method, *[revive(value, run) for value in described]))
        ship = seed_ship(character_id)
        skill(character_id, CONSOLIDATION, 1)
        skill(character_id, COMMAND_CENTER_UPGRADE, 5)
        dbseed.set_isk(character_id, 1_000_000)
        command_center = dbseed.new_item(COMMAND_CENTER, owner=character_id, location=ship)
        second_center = dbseed.new_item(COMMAND_CENTER, owner=character_id, location=ship)

        handle = client.call("planetMgr", "MachoBindObject", PLANET, None)[0]
        info = m.plain(client.call_bound(handle, "GetPlanetInfo"))
        assert info["planetTypeID"] == BARREN
        assert info["solarSystemID"] > 0

        # the command center: the hold item is the pin, consumed by the placement
        client.call_bound(handle, "UserUpdateNetwork", [(1, (command_center, COMMAND_CENTER, LATITUDE, LONGITUDE))])
        assert not item_exists(command_center), "the command center item is consumed by the placement"
        assert colony_pins(character_id, PLANET) == 1

        # a second command center on the same planet is refused, and the item stays
        with pytest.raises(Exception):
            client.call_bound(handle, "UserUpdateNetwork", [(1, (second_center, COMMAND_CENTER, 0.6, 1.0))])
        assert item_exists(second_center), "a refused placement consumes nothing"
        assert colony_pins(character_id, PLANET) == 1

        # a storage facility (a temporary id on the client): its item is created and its price charged
        before = dbseed.isk_of(character_id)
        client.call_bound(handle, "UserUpdateNetwork", [(1, ((1, 1), STORAGE, 0.5, 1.01))])
        assert before - dbseed.isk_of(character_id) == STORAGE_PRICE, "the storage facility is paid for"
        assert colony_pins(character_id, PLANET) == 2

        # read back: the colony the client gets is the one in the database
        colony = m.plain(client.call_bound(handle, "GetPlanetInfo"))
        assert len(colony["pins"]) == 2

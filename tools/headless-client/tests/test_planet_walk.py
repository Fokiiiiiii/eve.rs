"""Planetary interaction, live: a pilot with a command center in the hold places it on a barren planet, adds a storage facility, is
refused a second command center, and reads the planet back (planetMgr, V24.01 clientPlanet.py and planetSvc.py).

    EVE_LIVE_HOST=host:port EVE_PG_URL=postgres://... EVE_LIVE_PI_WALK=1 pytest tests/test_planet_walk.py

The character is created through the login walk; its skills (Interplanetary Consolidation, Command Center Upgrades) and ISK are
seeded through the database, and a command center is put in its ship's hold. The walk checks the item is consumed, the storage
facility is paid for, and the colony is in the database. Run it through tools/mitm to keep the capture.
"""

import json
import os
import re
from contextlib import contextmanager

import pytest

from evecli.observe import wait_quietly
from tests.test_fob_insystem_walk import seed_hub_pilot

from evecli import marshal as m

from tests import dbseed
from tests.test_agents_live import psql, run_id, schema, seed_ship
from tests.test_login_to_character import LIVE, WALK, connect, revive

PG_URL = os.environ.get("EVE_PG_URL", "")
PLANET = 40486618  # a barren planet of the SDE (sde.mapPlanets, type 2016, radius 2310000 m)
PLANET_SYSTEM = 31000604  # its solar system (sde.mapPlanets; a wormhole system)
BARREN = 2016
COMMAND_CENTER = 2524  # Barren Command Center
STORAGE = 2541  # Barren Storage Facility (250,000 ISK)
CONSOLIDATION = 2495  # Interplanetary Consolidation
COMMAND_CENTER_UPGRADE = 2505
LATITUDE, LONGITUDE = 0.5, 1.0  # a point on the planet (radians)
STORAGE_PRICE = 250_000
ECU = 2848  # Barren Extraction Control Unit
LAUNCHPAD = 2544  # Barren Launchpad
WATER = 3645  # Water (a P1 commodity: base value 400 ISK per unit, SDE attribute 1640)
AQUEOUS = 2268  # Aqueous Liquids (an R0 resource: base value 5 ISK per unit)
LAUNCH_CONTAINER = 23  # Cargo Container, the can a launch is put in
HANGAR = 4
TICKS_PER_HOUR = 36_000_000_000

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
        client.call("charUnboundMgr", "SelectCharacterID", charID=character_id, secondChoiceID=None, skipTutorial=False)
        ship = seed_ship(character_id)
        skill(character_id, CONSOLIDATION, 1)
        skill(character_id, COMMAND_CENTER_UPGRADE, 5)
        dbseed.set_isk(character_id, 1_000_000)
        command_center = dbseed.new_item(COMMAND_CENTER, owner=character_id, location=ship)
        second_center = dbseed.new_item(COMMAND_CENTER, owner=character_id, location=ship)

        handle = client.call("planetMgr", "MachoBindObject", PLANET, None)[0]
        info = record(client.call_bound(handle, "GetPlanetInfo"))
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
        colony = record(client.call_bound(handle, "GetPlanetInfo"))
        assert len(colony["pins"]) == 2


def record(value):
    """A call's answer as plain data: a util.KeyVal (the client's record) is the dict of its fields, recursively."""
    def unwrap(item):
        if isinstance(item, tuple) and len(item) == 2 and item[0] == "util.KeyVal" and isinstance(item[1], dict):
            return {key: unwrap(field) for key, field in item[1].items()}
        if isinstance(item, dict):
            return {key: unwrap(field) for key, field in item.items()}
        if isinstance(item, (list, tuple)):
            return [unwrap(field) for field in item]
        return item
    return unwrap(m.plain(value))


@contextmanager
def new_pilot(tag: str, system: int | None = None):
    """A character created through the login walk, with its ship; yields (client, character, ship). With a system, the ship is seeded in space there
    before the character is selected (the server brings a selected pilot into space where its rows say)."""
    run = run_id()
    creation = next(step for step in WALK["steps"] if step["call"][1] == "CreateCharacterWithDoll" and "ok" in step)
    with connect(f"{tag}-{run}") as client:
        service, method, described = creation["call"]
        character = m.plain(client.call(service, method, *[revive(value, run) for value in described]))
        ship = seed_ship(character)
        if system is not None:
            seed_hub_pilot(character, ship, system)
        client.call("charUnboundMgr", "SelectCharacterID", charID=character, secondChoiceID=None, skipTutorial=False)
        yield client, character, ship


def colony_doc(owner: int, planet: int) -> dict:
    text = psql(f"SELECT doc::text FROM {schema('EVE_PLANETS_SCHEMA')}.colonies WHERE owner_id = {owner} AND planet_id = {planet}")
    return json.loads(text)


def save_colony_doc(owner: int, planet: int, doc: dict) -> None:
    body = json.dumps(doc)
    psql(f"UPDATE {schema('EVE_PLANETS_SCHEMA')}.colonies SET doc = $json${body}$json$::jsonb WHERE owner_id = {owner} AND planet_id = {planet}")


def move_colony_back(owner: int, planet: int, hours: int) -> None:
    """The colony as it was `hours` ago: every time of it moves back (the lazy simulation then runs that span to now)."""
    doc = colony_doc(owner, planet)
    delta = hours * TICKS_PER_HOUR
    doc["CurrentSimTime"] -= delta
    for pin in doc["Pins"]:
        for key in ("LastRunTime", "InstallTime", "ExpiryTime", "LastLaunchTime"):
            if pin.get(key) is not None:
                pin[key] -= delta
    save_colony_doc(owner, planet, doc)


def set_contents(owner: int, planet: int, pin: int, contents: dict) -> None:
    doc = colony_doc(owner, planet)
    for entry in doc["Pins"]:
        if entry["Id"] == pin:
            entry["Contents"] = {str(k): v for k, v in contents.items()}
    save_colony_doc(owner, planet, doc)


def pin_contents(colony: dict, pin: int) -> dict:
    return {int(k): v for k, v in next(p for p in colony["pins"] if p["id"] == pin)["contents"].items()}


def pin_of_type(colony: dict, type_id: int) -> int:
    return next(p["id"] for p in colony["pins"] if p["typeID"] == type_id)


def ball_of(events: list, item: int, planet: int | None) -> bool:
    """Whether the events tell of the item's ball: its slim item has its id (and, for an orbital, its planet and its category). A destiny update's
    arguments come through as text, so the keys are matched in either quoting."""
    text = json.dumps(events, default=str)
    value = r"[^\]]{0,24}?"  # a number, or a long in its wrapper ({'$long': N})
    if not re.search(r"[\"']itemID[\"'],\s*%s%d\b" % (value, item), text):
        return False
    return planet is None or bool(re.search(r"[\"']planetID[\"'],\s*%s%d\b" % (value, planet), text)
                                  and re.search(r"[\"']categoryID[\"'],\s*46\b", text))


def test_a_colony_runs_in_time_launches_and_trades_at_customs():
    with new_pilot("colony", system=PLANET_SYSTEM) as (client, pilot, ship):
        skill(pilot, CONSOLIDATION, 1)
        skill(pilot, COMMAND_CENTER_UPGRADE, 5)
        dbseed.set_isk(pilot, 10_000_000)
        center = dbseed.new_item(COMMAND_CENTER, owner=pilot, location=ship)
        handle = client.call("planetMgr", "MachoBindObject", PLANET, None)[0]
        info = record(client.call_bound(handle, "GetPlanetInfo"))
        system = info["solarSystemID"]

        # the command center, then its upgrade to level 5 (the launchpad needs its CPU)
        client.call_bound(handle, "UserUpdateNetwork", [(1, (center, COMMAND_CENTER, LATITUDE, LONGITUDE))])
        client.call_bound(handle, "UserUpdateNetwork", [(9, (center, 5))])

        # storage, extraction control unit, launchpad (temporary ids), a link, a head, a program and a route: one batch
        colony = record(client.call_bound(handle, "UserUpdateNetwork", [
            (1, ((1, 1), STORAGE, 0.5, 1.01)),
            (1, ((1, 2), ECU, 0.52, 1.0)),
            (1, ((1, 3), LAUNCHPAD, 0.5, 1.02)),
            (3, ((1, 2), (1, 1), 0)),
            (10, ((1, 2), 0, 0.52, 1.0)),
            (13, ((1, 2), AQUEOUS, 0.01)),
            (6, ((2, 1), [(1, 2), (1, 1)], AQUEOUS, 1)),
        ]))
        storage, ecu, pad = pin_of_type(colony, STORAGE), pin_of_type(colony, ECU), pin_of_type(colony, LAUNCHPAD)
        assert colony["level"] == 5
        assert [route["commodityTypeID"] for route in colony["routes"]] == [AQUEOUS]

        # the colony runs in time: the program's cycles since the install are simulated when it is read
        move_colony_back(pilot, PLANET, 3)
        colony = record(client.call_bound(handle, "GetPlanetInfo"))
        assert pin_contents(colony, storage).get(AQUEOUS, 0) > 0, "the extractor's output reached the storage"

        # the pilot is in the planet's system: the customs office is a ball there (an orbital of the planet, the launchpad window reaches it through it)
        office = 1_200_000_000_000 + PLANET
        assert system == PLANET_SYSTEM, "the planet is in the system the pilot was seeded in"
        client.notifications.clear()
        ballpark = client.call("beyonce", "MachoBindObject", system, None)
        assert ballpark and ballpark[0], "the pilot's ballpark is bound: the pilot is in the system"
        # the ballpark's state is the scene the pilot is given: its balls arrive as notifications after the state is asked for
        client.call_bound(ballpark[0], "UpdateStateRequest")
        assert ball_of(wait_quietly(client, 4.0), office, PLANET), "the customs office is not a ball of the planet in its system"

        # the launch: commodities leave the command center into a can in the system, with the launch row naming it
        set_contents(pilot, PLANET, center, {WATER: 50})
        client.notifications.clear()
        client.call_bound(handle, "UserLaunchCommodities", center, m.Dict([(WATER, 5)]))
        assert pin_contents(record(client.call_bound(handle, "GetPlanetInfo")), center)[WATER] == 45
        can = psql(f"SELECT c.id FROM {schema('EVE_INVENTORY_SCHEMA')}.items c WHERE c.type_id = {LAUNCH_CONTAINER} AND c.owner_id = {pilot} "
                   f"AND c.location_id = {system} AND EXISTS (SELECT 1 FROM {schema('EVE_INVENTORY_SCHEMA')}.items s WHERE s.location_id = c.id AND s.type_id = {WATER} AND s.quantity = 5)")
        assert can, "the launch container holds the launched commodities"
        assert psql(f"SELECT count(*) FROM {schema('EVE_PLANETS_SCHEMA')}.launches WHERE owner_id = {pilot} AND NOT deleted") == "1"
        # the can is in space: its space row (the owner's, a can's group, the launch's expiry) and its ball in the pilot's scene
        assert psql(f"SELECT group_id || '|' || character_id FROM {schema('EVE_SPACE_SCHEMA')}.space_items WHERE item_id = {can}") == f"12|{pilot}"
        # its ball is told to the pilots who can see its place: this pilot is about 1.9e10 m from the planet (the walk's pose), so the ball is not
        # checked here; the simulation's wreck placement is checked in eve-space (the watchers of a placed wreck)
        wait_quietly(client, 2.0)

        # customs: the office is the planet's (its id). The planet is in system 31000604 (wormhole space): the NPC rate outside high-security is 17%
        registry = client.call("planetOrbitalRegistryBroker", "MachoBindObject", system, None)[0]
        rate = record(client.call_bound(registry, "GetTaxRate", office))
        assert abs(rate - 0.17) < 1e-9
        stock = dbseed.new_item(WATER, owner=pilot, location=office, flag=HANGAR, quantity=50)
        customs = client.call("invbroker", "GetInventoryFromId", office)
        before = dbseed.isk_of(pilot)
        client.call_bound(customs, "ImportExportWithPlanet", pad, m.Dict([(stock, 20)]), m.Dict([]), rate)
        assert pin_contents(record(client.call_bound(handle, "GetPlanetInfo")), pad)[WATER] == 20
        assert int(psql(f"SELECT quantity FROM {schema('EVE_INVENTORY_SCHEMA')}.items WHERE id = {stock}")) == 30
        # import fee: 20 x 0.5 (the pad's import rate) x 400 (Water's multiplier) x 0.17
        assert abs((before - dbseed.isk_of(pilot)) - 680) < 0.01

        before = dbseed.isk_of(pilot)
        client.call_bound(customs, "ImportExportWithPlanet", pad, m.Dict([]), m.Dict([(WATER, 5)]), rate)
        assert pin_contents(record(client.call_bound(handle, "GetPlanetInfo")), pad)[WATER] == 15
        assert int(psql(f"SELECT quantity FROM {schema('EVE_INVENTORY_SCHEMA')}.items WHERE id = {stock}")) == 35
        # export fee: 5 x 1.0 x 400 x 0.17
        assert abs((before - dbseed.isk_of(pilot)) - 340) < 0.01

        # a rate that moved since the window was opened is refused, and nothing moves
        with pytest.raises(Exception):
            client.call_bound(customs, "ImportExportWithPlanet", pad, m.Dict([]), m.Dict([(WATER, 1)]), 0.25)
        assert pin_contents(record(client.call_bound(handle, "GetPlanetInfo")), pad)[WATER] == 15


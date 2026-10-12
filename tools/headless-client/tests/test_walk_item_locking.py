"""Item locking walk: a corporation's locked blueprint at a station is listed for the pilot there, and not at another place.

    EVE_LIVE_HOST=host:port tests/... (through tools/dev/run-eve-live.sh so EVE_PG_URL and the schemas are exported)

The lock is seeded the way a director's vote writes it (dbseed.lock_item: corporations.item_locks) for a blueprint in the pilot's station
hangar. The client's itemLocking calls are GetItemsByLocation(locationID), which answers {itemID: KeyVal(itemID, typeID, ownerID,
locationID)}, and GetLockedItemLocations(), which answers the set of locations with a lock. Only eve.rs has the seeded walk: EveJS has no
database to seed, so its side is compared by reading its code (itemLocking).
"""

from __future__ import annotations

import os
import uuid

import pytest

from evecli.describe import describe
from tests import dbseed
from tests import test_login_to_character as base
from tests.test_health import HEALTH
from tests.test_undock import STATION, arrive, number

BLUEPRINT = 681  # Clone Grade Beta Blueprint (category 9): the blueprint the vote locks
ELSEWHERE = 30000142  # Jita: no lock there
pytestmark = [
    pytest.mark.skipif(not base.LIVE, reason="set EVE_LIVE_HOST=host:port to test against a running server"),
    pytest.mark.skipif(os.environ.get("EVE_LIVE_SERVER", "eve.rs") == "reference", reason="seeding needs the database: eve.rs only"),
]


def dict_entries(described) -> list:
    """The (key, value) pairs of a described dict."""
    return described["$dict"]


def all_numbers(described) -> list:
    """Every integer in a described value: the members of a set (the describer writes it as an object with its items inside)."""
    found = []
    if isinstance(described, bool):
        return found
    if isinstance(described, int):
        return [described]
    if isinstance(described, dict):
        if "$long" in described:
            return [int(described["$long"])]
        for child in described.values():
            found += all_numbers(child)
    elif isinstance(described, list):
        for child in described:
            found += all_numbers(child)
    return found


def field(keyval, name: str):
    """A field of a described util.KeyVal."""
    return dict(keyval["args"]["$dict"])[name]


def test_a_corporation_locked_blueprint_is_listed_where_it_is_locked_and_not_elsewhere():
    race = HEALTH["races"][0]
    run = uuid.uuid4().hex[:10]
    with base.connect(f"walk-locks-{run}") as who:
        character, _, _, _ = arrive(who, race, run)
        corporation = dbseed.corporation_of(character)
        blueprint = dbseed.new_item(BLUEPRINT, owner=character, location=STATION, singleton=True, quantity=-1)
        dbseed.lock_item(blueprint, corporation=corporation, type_id=BLUEPRINT, location=STATION)

        here = describe(who.call("itemLocking", "GetItemsByLocation", STATION))
        rows = {number(key): value for key, value in dict_entries(here)}
        assert blueprint in rows, here
        assert number(field(rows[blueprint], "typeID")) == BLUEPRINT
        assert number(field(rows[blueprint], "locationID")) == STATION

        locations = all_numbers(describe(who.call("itemLocking", "GetLockedItemLocations")))
        assert STATION in locations, locations

        elsewhere = describe(who.call("itemLocking", "GetItemsByLocation", ELSEWHERE))
        assert blueprint not in [number(key) for key, _ in dict_entries(elsewhere)], elsewhere

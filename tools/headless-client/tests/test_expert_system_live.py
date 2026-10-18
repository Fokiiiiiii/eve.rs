"""Expert systems of a pilot against a running server (CH-17): the client's Consume and Remove, and the lapse of an expert system.

    EVE_LIVE_HOST=host:port EVE_PG_URL=postgres://... EVE_RUN=... tools/dev/run-eve-live.sh (or pytest tests/test_expert_system_live.py)

A new pilot docked at the start station uses an expert system item ("Free AIR Explorer Expert System", catalogue 63826, three days; it grants
several skills, and the one checked is one the pilot has not trained). The list (GetMyExpertSystems) has it, the client is told the list (OnExpertSystemsUpdated), and the skill it grants is in the skills the client
reads (GetSkills), though the pilot never trained it. Remove takes it off and the skill goes. A system whose time has run out is taken off by the
expiry job, and the client is told (OnExpertSystemExpired). Skipped unless EVE_LIVE_HOST is set.
"""

import os
import time
import uuid

import pytest

from evecli import marshal as m
from evecli.observe import notifications_of
from tests import dbseed
from tests.test_agents_live import new_pilot, psql, schema
from tests.test_login_to_character import connect
from tests.test_undock import STATION

LIVE = os.environ.get("EVE_LIVE_HOST", "")
pytestmark = pytest.mark.skipif(not LIVE, reason="set EVE_LIVE_HOST=host:port to test against a running server")

FREE_AIR_EXPLORER = 63826
# the skills the catalogue grants (EveJS data.json, the row of 63826): a new pilot has trained some of them already
GRANTED_BY_FREE_AIR_EXPLORER = [3327, 3328, 3329, 3330, 3331, 3412, 3431, 3436, 3449, 3452, 3453, 3455, 13278, 21718, 24241, 24242, 25739, 25810, 25811]
EXPIRY_WAIT_SECONDS = 40


def untrained_grant(character: int) -> int:
    """A skill the system grants that the pilot has not trained, so that a grant is the only way it shows."""
    trained = {int(line) for line in psql(f"SELECT type_id FROM {schema('EVE_SKILLS_SCHEMA')}.character_skills WHERE character_id = {character}").split()}
    return next(skill for skill in GRANTED_BY_FREE_AIR_EXPLORER if skill not in trained)


def contains(value, wanted: int) -> bool:
    if isinstance(value, bool):
        return False
    if isinstance(value, int):
        return value == wanted
    if isinstance(value, dict):
        return any(contains(item, wanted) for item in value.keys()) or any(contains(item, wanted) for item in value.values())
    if isinstance(value, (list, tuple)):
        return any(contains(item, wanted) for item in value)
    return False


def wait_for_notification(client, name: str, seconds: float) -> bool:
    deadline = time.monotonic() + seconds
    while True:
        if any(event.get("name") == name for event in notifications_of(client)):
            return True
        if time.monotonic() >= deadline:
            return False
        time.sleep(0.5)


def test_an_expert_system_is_used_listed_granted_removed_and_lapses_as_the_client_sees_it():
    run = uuid.uuid4().hex[:10]
    with connect(f"expert-{run}") as client:
        character = new_pilot(client, run)
        granted = untrained_grant(character)
        assert not contains(m.plain(client.call("skillHandler", "GetSkills")), granted), "the pilot has not trained the granted skill"
        assert m.plain(client.call("expertSystemMgr", "GetMyExpertSystems")) == {}

        item = dbseed.new_item(FREE_AIR_EXPLORER, owner=character, location=STATION, singleton=True)
        client.notifications.clear()
        client.call("expertSystemMgr", "ConsumeExpertSystem", item)
        assert int(psql(f"SELECT count(*) FROM {schema('EVE_INVENTORY_SCHEMA')}.items WHERE id = {item}")) == 0, "the item is used up"
        assert contains(m.plain(client.call("expertSystemMgr", "GetMyExpertSystems")), FREE_AIR_EXPLORER)
        assert contains(m.plain(client.call("skillHandler", "GetSkills")), granted), "the granted skill is in the skills the client reads"
        assert wait_for_notification(client, "OnExpertSystemsUpdated", 3.0)

        # Remove takes it off, and the skill it granted goes with it
        client.notifications.clear()
        client.call("expertSystemMgr", "RemoveMyExpertSystem", FREE_AIR_EXPLORER)
        assert m.plain(client.call("expertSystemMgr", "GetMyExpertSystems")) == {}
        assert not contains(m.plain(client.call("skillHandler", "GetSkills")), granted)
        assert wait_for_notification(client, "OnExpertSystemsUpdated", 3.0)

        # a second one lapses: its time is set to have run out, and the expiry job takes it off and tells the client
        second = dbseed.new_item(FREE_AIR_EXPLORER, owner=character, location=STATION, singleton=True)
        client.call("expertSystemMgr", "ConsumeExpertSystem", second)
        assert contains(m.plain(client.call("expertSystemMgr", "GetMyExpertSystems")), FREE_AIR_EXPLORER)
        psql(f"UPDATE {schema('EVE_SKILLS_SCHEMA')}.character_expert_systems SET expires_at_ms = 0 WHERE character_id = {character}")
        client.notifications.clear()
        assert wait_for_notification(client, "OnExpertSystemExpired", EXPIRY_WAIT_SECONDS), "the expiry job tells the client"
        assert m.plain(client.call("expertSystemMgr", "GetMyExpertSystems")) == {}
        assert int(psql(f"SELECT count(*) FROM {schema('EVE_SKILLS_SCHEMA')}.character_expert_systems WHERE character_id = {character}")) == 0

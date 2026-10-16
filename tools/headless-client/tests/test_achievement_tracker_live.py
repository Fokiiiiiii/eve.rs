"""The achievement tracker against a running server (eve.rs: the client's maps are stored and read back; no fixture).

    EVE_LIVE_HOST=host:port EVE_PG_URL=postgres://... tools/dev/run-eve-live.sh (or pytest tests/test_achievement_tracker_live.py)

A new character reads empty maps. The client sends its completed achievements and event counts (`UpdateClientAchievmentsAndCounters`, at logoff
and as they change); a later login of the same character reads them back, and the database holds the rows.
"""

from __future__ import annotations

import json
import os
import uuid

import pytest

from evecli import marshal as m
from tests.test_agents_live import new_pilot, psql, schema
from tests.test_login_to_character import connect

LIVE = os.environ.get("EVE_LIVE_HOST", "")
PG_URL = os.environ.get("EVE_PG_URL", "")
pytestmark = pytest.mark.skipif(not (LIVE and PG_URL), reason="set EVE_LIVE_HOST=host:port and EVE_PG_URL to test against a running server")

ACHIEVEMENT = 9
FILETIME = 134273341290000000
EVENT, COUNT = "client_openMap", 3


def maps_of(client) -> dict:
    return json.loads(json.dumps(m.plain(client.call("achievementTrackerMgr", "GetCompletedAchievementsAndClientEventCount")), sort_keys=True))


def test_the_achievement_tracker_is_stored_for_the_character_and_read_back_on_the_next_login():
    run = uuid.uuid4().hex[:10]
    name = f"tracker-{run}"
    with connect(name) as client:
        character = new_pilot(client, run)
        fresh = json.dumps(maps_of(client))
        assert "client_activateAutopilot" not in fresh, "no record: empty maps, not the fixture"
        client.call("achievementTrackerMgr", "UpdateClientAchievmentsAndCounters",
                    m.Dict([(ACHIEVEMENT, FILETIME)]), m.Dict([(EVENT, COUNT)]))

    assert psql(f"SELECT event_count FROM {schema('EVE_CHARACTERS_SCHEMA')}.achievement_event_counts WHERE character_id = {character} AND event_name = '{EVENT}'") == str(COUNT)
    assert psql(f"SELECT completed_filetime FROM {schema('EVE_CHARACTERS_SCHEMA')}.achievement_completions WHERE character_id = {character} AND achievement_id = {ACHIEVEMENT}") == str(FILETIME)

    with connect(name) as again:
        again.call("charUnboundMgr", "SelectCharacterID", charID=character, secondChoiceID=None, skipTutorial=False)
        read = maps_of(again)
        assert read["eventDict"] == {EVENT: COUNT} or EVENT in json.dumps(read), read
        assert str(FILETIME) in json.dumps(read) and str(ACHIEVEMENT) in json.dumps(read), read
        assert "client_activateAutopilot" not in json.dumps(read)

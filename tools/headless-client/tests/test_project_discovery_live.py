"""The Project Discovery tutorial and the season reads against a running server (CH-4).

    EVE_LIVE_HOST=host:port EVE_PG_URL=postgres://... EVE_RUN=... tools/dev/run-eve-live.sh (or pytest tests/test_project_discovery_live.py)

A new pilot has no stored Project Discovery tutorial, so the client's startup call `ProjectDiscovery.initialize_tutorial_status` answers False.
The test stores a finished tutorial in the run's scratch characters schema, and the same call then answers True. No season is active, so
`seasonManager.get_season_data_for_character` answers None and `get_season_end_time` answers a filetime. With no challenge definitions the
client reads a zero challenge limit, empty challenge and loot maps, dormant/nonexistent challenge values, refused reward claims, and the
character's empty no-season progress record.
"""

from __future__ import annotations

import os
import uuid

import pytest

from evecli import marshal as m
from tests.test_agents_live import new_pilot, psql, schema
from tests.test_login_to_character import connect

LIVE = os.environ.get("EVE_LIVE_HOST", "")
PG_URL = os.environ.get("EVE_PG_URL", "")
pytestmark = pytest.mark.skipif(not (LIVE and PG_URL), reason="set EVE_LIVE_HOST=host:port and EVE_PG_URL to test against a running server")

def test_the_tutorial_status_follows_the_stored_tutorial_and_no_season_is_active():
    run = uuid.uuid4().hex[:10]
    with connect(f"tutorial-{run}") as client:
        character = new_pilot(client, run)
        status = lambda: m.plain(client.call("ProjectDiscovery", "initialize_tutorial_status"))  # noqa: E731
        assert status() is False, "a pilot with no stored tutorial has not started"

        psql(
            f"INSERT INTO {schema('EVE_CHARACTERS_SCHEMA')}.project_discovery_tutorials (character_id, finished, tutorial_level, updated_ms) "
            f"VALUES ({character}, true, 8, 1)"
        )
        assert status() is True, "the stored finished tutorial is what the client reads at startup"

        assert m.plain(client.call("seasonManager", "get_season_data_for_character")) is None
        assert m.plain(client.call("seasonManager", "select_season", 1)) is None
        end = m.plain(client.call("seasonManager", "get_season_end_time"))
        assert isinstance(end, int) and end > 0

        assert m.plain(client.call("seasonManager", "get_max_active_challenges")) == 0
        assert m.plain(client.call("seasonManager", "fill_up_and_return_challenges_for_character")) == {}
        challenge_id = 1
        assert m.plain(client.call("seasonManager", "get_challenge", challenge_id)) is None
        assert m.plain(client.call("seasonManager", "challenge_is_dormant", challenge_id)) is True
        assert m.plain(client.call("seasonManager", "get_challenge_expiration_date", challenge_id)) is None
        assert m.plain(client.call("seasonManager", "claim_challenge_rewards", challenge_id, "live-test")) is False
        assert m.plain(client.call("seasonManager", "claim_goal_reward", 0)) is False
        assert m.plain(client.call("seasonManager", "get_loot_table_by_typeID", 34)) == []
        name, args = m.plain(client.call("seasonManager", "get_character_progress_by_character_id"))  # util.KeyVal
        assert name == "util.KeyVal"
        progress = dict(args)
        assert progress == {
            "character_id": character,
            "season_id": None,
            "seasonal_points": 0,
            "max_seasonal_points": 1,
            "seasonal_goals": {},
            "next_seasonal_goal": None,
            "last_seasonal_goal": None,
            "challenges": {},
        }

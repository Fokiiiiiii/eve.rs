"""The population cap against a running server: two accounts select into the last room of the starter system, and one is refused (eve.rs).

    EVE_SYSTEM_POPULATION_CAP=1 EVE_LIVE_HOST=host:port EVE_PG_URL=postgres://... tools/dev/run-eve-live.sh (or pytest tests/test_population_cap_live.py)

The host runs with a cap of one character per system (the run's environment). The first account's pilot takes the only room of the starter
system; the second account's pilot, which is created in the same system, is refused with the cap's text and is not placed in the game. The
room is checked and taken in one step, so the refusal does not depend on the two selections being sent one after the other.
"""

from __future__ import annotations

import os
import uuid

import pytest

from evecli import packet as p
from evecli import marshal as m
from tests.test_agents_live import new_pilot, psql, schema
from tests.test_login_to_character import WALK, connect, revive

LIVE = os.environ.get("EVE_LIVE_HOST", "")
PG_URL = os.environ.get("EVE_PG_URL", "")
pytestmark = [
    pytest.mark.skipif(not (LIVE and PG_URL), reason="set EVE_LIVE_HOST=host:port and EVE_PG_URL to test against a running server"),
    pytest.mark.skipif(os.environ.get("EVE_SYSTEM_POPULATION_CAP") != "1", reason="set EVE_SYSTEM_POPULATION_CAP=1 for the host"),
]

def create_only(client, run: str) -> int:
    """A new character (the creation of login_to_character.json), not selected."""
    creation = next(step for step in WALK["steps"] if step["call"][1] == "CreateCharacterWithDoll" and "ok" in step)
    service, method, described = creation["call"]
    return m.plain(client.call(service, method, *[revive(value, run) for value in described]))

def test_the_last_room_of_the_starter_system_admits_one_pilot():
    run = uuid.uuid4().hex[:10]
    with connect(f"cap-first-{run}") as first, connect(f"cap-second-{run}") as second:
        taken = new_pilot(first, run)
        waiting = create_only(second, f"{run}b")
        with pytest.raises(p.MachoError) as refused:
            second.call("charUnboundMgr", "SelectCharacterID", charID=waiting, secondChoiceID=None, skipTutorial=False)
        assert "no room" in repr(refused.value.payload), refused.value.payload
        assert psql(f"SELECT count(*) FROM {schema('EVE_CHARACTERS_SCHEMA')}.characters WHERE id = {taken} AND solar_system_id > 0") == "1"

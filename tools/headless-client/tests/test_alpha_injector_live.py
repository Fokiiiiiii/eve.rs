"""The Alpha clone's Daily Alpha Injector against a running server (eve.rs: the EveJS rule, one use per 11:00 UTC downtime).

    EVE_LIVE_HOST=host:port EVE_PG_URL=postgres://... EVE_RUN=... tools/dev/run-eve-live.sh (or pytest tests/test_alpha_injector_live.py)

A new account is Omega, so the test lapses its subscription in the run's scratch identity schema (an Alpha account). The pilot gets a Daily Alpha
Injector in its station hangar (dbseed), and the client's own calls do the rest: `alphaInjectorMgr.GetNextAvailableInjection` reads 0, then
`skillHandler.InjectSkillpoints` gives 50,000 skill points and the clock reads the next downtime; a second injection in the same downtime is refused.
"""

from __future__ import annotations

import os
import uuid

import pytest

from evecli import marshal as m
from evecli import packet as p
from tests import dbseed
from tests.test_agents_live import new_pilot, psql, schema
from tests.test_login_to_character import connect
from tests.test_undock import STATION

LIVE = os.environ.get("EVE_LIVE_HOST", "")
PG_URL = os.environ.get("EVE_PG_URL", "")

DAILY_ALPHA_INJECTOR = 46375
DOWNTIME_SECONDS_OF_DAY = 11 * 3600
FILETIME_UNIX_EPOCH = 116_444_736_000_000_000
FILETIME_TICKS_PER_SECOND = 10_000_000
pytestmark = pytest.mark.skipif(not (LIVE and PG_URL), reason="set EVE_LIVE_HOST=host:port and EVE_PG_URL to test against a running server")


def test_an_alpha_account_injects_once_per_downtime_and_the_clock_shows_the_next_one():
    run = uuid.uuid4().hex[:10]
    name = f"alpha-{run}"
    with connect(name) as client:
        character = new_pilot(client, run)
        account = int(psql(f"SELECT id FROM {schema('EVE_IDENTITY_SCHEMA')}.accounts WHERE user_name = '{name}'"))
        psql(f"UPDATE {schema('EVE_IDENTITY_SCHEMA')}.accounts SET subscription_end_at = now() - interval '1 day' WHERE id = {account}")

        clock = lambda: m.plain(client.call("alphaInjectorMgr", "GetNextAvailableInjection"))  # noqa: E731
        assert clock() == 0, "an Alpha account that never injected has no next injection"

        first = dbseed.new_item(DAILY_ALPHA_INJECTOR, owner=character, location=STATION, singleton=True)
        injected = m.plain(client.call("skillHandler", "InjectSkillpoints", first, 1))
        assert injected == 50_000

        next_at = clock()
        seconds = (next_at - FILETIME_UNIX_EPOCH) // FILETIME_TICKS_PER_SECOND
        assert seconds % 86400 == DOWNTIME_SECONDS_OF_DAY, "the next injection is due at the 11:00 UTC downtime"
        assert next_at > (FILETIME_UNIX_EPOCH + 1) and seconds > 0

        second = dbseed.new_item(DAILY_ALPHA_INJECTOR, owner=character, location=STATION, singleton=True)
        refused = None
        try:
            client.call("skillHandler", "InjectSkillpoints", second, 1)
        except p.MachoError as error:  # the server answers a user error (CustomNotify)
            refused = repr(error.payload)
        assert refused is not None and "once per day" in refused, refused
        assert clock() == next_at, "a refused injection changes nothing"
        assert int(psql(f"SELECT count(*) FROM {schema('EVE_INVENTORY_SCHEMA')}.items WHERE id = {second}")) == 1

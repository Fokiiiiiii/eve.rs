"""War registry walk: a pilot reads its wars and the negotiations, as the client's war window does.

    EVE_LIVE_HOST=host:port [EVE_LIVE_SERVER=reference] pytest tests/test_walk_war_registry.py

The calls are the client's (eveMoniker.py:197 IsAllianceOrCorpLocal; war_cso.py reads GetWars and the negotiations).
Negotiations are a cut on eve.rs (DEVIATIONS.md), so only the read calls are walked.
"""

from __future__ import annotations

import uuid

import pytest

from tests import test_login_to_character as base
from tests.test_health import HEALTH
from tests.test_undock import arrive

SERVICE = "warRegistry"
pytestmark = pytest.mark.skipif(not base.LIVE, reason="set EVE_LIVE_HOST=host:port to test against a running server")


def test_a_pilot_reads_its_wars_and_the_negotiations():
    race = HEALTH["races"][0]
    run = uuid.uuid4().hex[:10]
    with base.connect(f"walk-war-{run}") as who:
        arrive(who, race, run)
        wars = who.call(SERVICE, "GetWars")
        assert isinstance(wars, list), wars
        assert who.call(SERVICE, "IsAllianceOrCorpLocal", 1) in (0, 1)
        assert who.call(SERVICE, "GetNegotiations") == []
        assert who.call(SERVICE, "GetWarNegotiation", 1) is None

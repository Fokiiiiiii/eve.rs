"""Corporation station door walk: a docked pilot binds the station's corporation door and asks it the station-service standings.

    EVE_LIVE_HOST=host:port [EVE_LIVE_SERVER=reference] pytest tests/test_walk_corp_station.py

The client binds the door with Moniker('corpStationMgr', stationID) (eveMoniker.py) and calls DoStandingCheckForStationService
from stationController.py:77. A pilot without a director role cannot move the corporation headquarters (MoveCorpHQHere).
"""

from __future__ import annotations

import uuid

import pytest

from tests import test_login_to_character as base
from tests.test_health import HEALTH
from tests.test_undock import STATION, arrive, outcome

SERVICE = "corpStationMgr"
CHECKED_SERVICE = 5  # a station service with a standing requirement in the SDE (stationStandingsRestrictions)
OPEN_SERVICE = 13  # a station service whose requirement is below zero: open to everyone
pytestmark = pytest.mark.skipif(not base.LIVE, reason="set EVE_LIVE_HOST=host:port to test against a running server")


def test_a_docked_pilot_asks_the_station_door_for_its_services():
    race = HEALTH["races"][0]
    run = uuid.uuid4().hex[:10]
    with base.connect(f"walk-corpstation-{run}") as who:
        arrive(who, race, run)
        bound = who.call(SERVICE, "MachoBindObject", STATION, None)
        door = bound[0]
        assert who.call_bound(door, "DoStandingCheckForStationService", OPEN_SERVICE) is None
        answer, error = outcome(lambda: who.call_bound(door, "DoStandingCheckForStationService", CHECKED_SERVICE))
        assert answer is None or error is not None, (answer, error)  # open for this station's owner, or refused by standing
        answer, error = outcome(lambda: who.call_bound(door, "MoveCorpHQHere"))
        assert error is not None, answer  # no director role: refused

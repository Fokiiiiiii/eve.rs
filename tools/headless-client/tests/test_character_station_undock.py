"""Character creation, station view, and the first undock on one live client session.

    EVE_LIVE_HOST=host:port pytest tests/test_character_station_undock.py

This joins the recorded login/selection and undock traces at the station UI boundary:
after SelectCharacterID, read the station cache record and station-item tuple, then
issue the V24.01 bound ship Undock call and compare its reply and notifications.
"""
from __future__ import annotations

import pytest

from evecli import marshal as m
from evecli.describe import describe
from tests.runid import run_token
from tests import test_login_to_character as login
from tests import test_undock as undock

pytestmark = pytest.mark.skipif(not login.LIVE, reason="set EVE_LIVE_HOST=host:port to test against a running server")


@pytest.mark.parametrize("race", undock.UNDOCK["races"], ids=lambda race: f"race-{race['raceID']}")
def test_character_entry_station_view_and_undock_follow_the_client_flow(race):
    run = run_token()
    problems: list[str] = []
    with login.connect(f"walk-entry-undock-{run}") as client:
        _, ship_id, fitted, mapping = undock.arrive(client, race, run)

        expected_station = next(station for station in login.STATIONS if station["id"] == undock.STATION)
        station = describe(client.call("stationSvc", "GetStation", undock.STATION))
        problems += login.same_way(
            login.without_cache_time(expected_station["call"]["ok"]),
            login.without_cache_time(station),
            "station view after character entry",
        )
        station_bits = describe(client.call("stationSvc", "GetStationItemBits"))
        assert len(station_bits["$tuple"]) == 4
        assert undock.number(station_bits["$tuple"][1]) == undock.STATION
        assert problems == [], "\\n".join(problems)

        ship = client.call("ship", "MachoBindObject", (undock.STATION, 15), None)
        step = next(step for step in race["steps"] if step["label"] == "undock")
        answer = describe(client.call_bound(ship[0], "Undock", ship_id, False, onlineModules=m.List(fitted)))
        problems += undock.compare_answer("undock", step["ok"], answer, "first undock")
        events = undock.wait_quietly(client, 2.0)
        problems += undock.compare_events(step["events"], events, "first undock", mapping)
    assert problems == [], "\\n".join(problems[:40])

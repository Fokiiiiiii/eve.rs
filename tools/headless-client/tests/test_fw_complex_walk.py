"""The factional warfare state on a live server (slice 3): the victory point state of every occupied system, as the client's
fwVictoryPointMgr reads it, from the same store the complexes move.

    EVE_LIVE_HOST=host:port pytest tests/test_fw_complex_walk.py

The walk logs in and reads `fwVictoryPointMgr.GetAllVictoryPointStates`. The answer maps each occupied system to a victory point state (the
attacker points against the threshold). The walk checks that the warzones' occupied systems are there, each with a threshold of 75,000 and
no points at the start (no complex has been captured on a fresh server). The capture of a complex needs a pilot in its site; that walk is not
here (DEVIATIONS "Factional warfare", complexes).
"""

import os

import pytest

from evecli import marshal as m
from evecli.client import Client

LIVE = os.environ.get("EVE_LIVE_HOST", "")
pytestmark = pytest.mark.skipif(not LIVE, reason="set EVE_LIVE_HOST=host:port to test against a running server")

VULNERABILITY_THRESHOLD = 75_000
# the two warzones hold 103 and 149 systems; the faction-held ones are seeded from the SDE, so a server has well over a hundred occupied
MIN_OCCUPIED = 100


def _client() -> Client:
    host, _, port = LIVE.partition(":")
    return Client(host or "127.0.0.1", int(port or 26000), timeout=20.0)


def _states(value):
    """The (system id, score, threshold) of each entry of the answer, whatever the containers are."""
    plain = m.plain(value)
    if isinstance(plain, dict):
        items = plain.items()
    else:
        items = []
    rows = []
    for system, state in items:
        numbers = [item for item in _flatten(state) if isinstance(item, (int, float)) and not isinstance(item, bool)]
        rows.append((int(system), numbers))
    return rows


def _flatten(value):
    if isinstance(value, (list, tuple)):
        for item in value:
            yield from _flatten(item)
    elif isinstance(value, dict):
        for item in value.values():
            yield from _flatten(item)
    elif hasattr(value, "__dict__"):
        yield from _flatten(vars(value))
    else:
        yield value


def test_every_occupied_system_has_a_victory_point_state_at_the_threshold():
    with _client() as client:
        client.login("evecli-live", "evecli-live")
        answer = client.call("fwVictoryPointMgr", "GetAllVictoryPointStates")
        rows = _states(answer)
        assert len(rows) >= MIN_OCCUPIED, f"only {len(rows)} occupied systems in {answer!r}"
        for system, numbers in rows:
            assert VULNERABILITY_THRESHOLD in numbers, f"system {system}: no threshold of 75,000 in {numbers!r}"
            assert 0 in numbers, f"system {system}: a fresh server has no victory points, got {numbers!r}"

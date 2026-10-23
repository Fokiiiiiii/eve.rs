"""The pirate FOB on a live server (slice 3): the warzones' campaigns start at boot, each with its FOB in the hub system, and the map's
campaign list names the FOB's structure id (the FOB ids are at least 1.1e12, see migration 0006_fob_ids).

    EVE_LIVE_HOST=host:port pytest tests/test_fob_walk.py

The list is read with the same client session as the incursion walk. The answer is a list of campaign objects; the walk looks for the
structure ids in it, so it does not depend on the object's field layout.
"""

import os

import pytest

from evecli import marshal as m
from evecli.client import Client

LIVE = os.environ.get("EVE_LIVE_HOST", "")
pytestmark = pytest.mark.skipif(not LIVE, reason="set EVE_LIVE_HOST=host:port to test against a running server")

FOB_ID_FLOOR = 1_100_000_000_000

def _client() -> Client:
    host, _, port = LIVE.partition(":")
    return Client(host or "127.0.0.1", int(port or 26000), timeout=20.0)

def _integers(value):
    """Every integer in a decoded answer, whatever the containers are."""
    if isinstance(value, bool):
        return
    if isinstance(value, int):
        yield value
    elif isinstance(value, dict):
        for item in value.values():
            yield from _integers(item)
    elif isinstance(value, (list, tuple, set)):
        for item in value:
            yield from _integers(item)
    elif hasattr(value, "__dict__"):
        yield from _integers(vars(value))

def test_the_campaigns_at_boot_name_their_fob_structures():
    with _client() as client:
        client.login("evecli-live", "evecli-live")
        campaigns = m.plain(client.call("insurgencySolarsystem", "GetAllVisibleCampaigns"))
        assert campaigns, "no campaign is visible: the warzones start one at boot"
        assert any(FOB_ID_FLOOR <= value for value in _integers(campaigns)), f"no FOB structure id in the answer: {campaigns!r}"

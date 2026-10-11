"""ST-1 e: a structure of the simulation, seen by a client in its system.

A pilot who is in a system with an imported Upwell structure (an Astrahus, in the
structure store) sees it in the ballpark, with its slim state and its timer. The
scenario needs a running server with that structure, so it is skipped unless the
environment names it:

  EVE_LIVE_HOST=host:port           the server (tools/dev/run-eve-live.sh)
  EVE_LIVE_STRUCTURE_ID=<item id>   the structure's id
  EVE_LIVE_STRUCTURE_SYSTEM=<id>    the solar system it is anchored in
  EVE_LIVE_CHARACTER=<id>           a character of an account that may enter the system
  EVE_LIVE_USER / EVE_LIVE_PASSWORD the account of that character

This file has not been run against a live server (see docs/project/handoff-ST-1.md).
"""

import os
import time

import pytest

from evecli.client import Client
from evecli.observe import events_of, timed_events
from tests.test_undock import keyval, number, slim_by_id

LIVE = os.environ.get("EVE_LIVE_HOST", "")
STRUCTURE = os.environ.get("EVE_LIVE_STRUCTURE_ID", "")
SYSTEM = os.environ.get("EVE_LIVE_STRUCTURE_SYSTEM", "")
CHARACTER = os.environ.get("EVE_LIVE_CHARACTER", "")
USER = os.environ.get("EVE_LIVE_USER", "")
PASSWORD = os.environ.get("EVE_LIVE_PASSWORD", "")

pytestmark = pytest.mark.skipif(
    not (LIVE and STRUCTURE and SYSTEM and CHARACTER and USER),
    reason="set EVE_LIVE_HOST, EVE_LIVE_STRUCTURE_ID, EVE_LIVE_STRUCTURE_SYSTEM, EVE_LIVE_CHARACTER and EVE_LIVE_USER",
)

# the structure's category in the client (categoryID 65, ST-1)
STRUCTURE_CATEGORY = 65


def _client() -> Client:
    host, _, port = LIVE.partition(":")
    return Client(host or "127.0.0.1", int(port or 26000), timeout=20.0)


def _structure_slim(events: list) -> dict | None:
    """The structure's slim item from the ballpark events seen so far, if the client has it."""
    for event in events:
        for item in _slims_of(event):
            if number(keyval(item)["itemID"]) == int(STRUCTURE):
                return item
    return None


def _slims_of(event: dict) -> list:
    """The slim items an AddBalls2 or SetState carries (the notification's arguments, as the client describes them)."""
    found = []
    for value in event.get("args", []) if isinstance(event, dict) else []:
        if isinstance(value, list):
            found.extend(item for item in value if isinstance(item, dict))
    return found


def test_a_pilot_in_the_system_sees_the_structure_with_its_state_and_timer():
    with _client() as client:
        client.login(USER, PASSWORD)
        client.call("charMgr", "SelectCharacterID", int(CHARACTER))
        seen = timed_events(client, 30.0, until=lambda events: _structure_slim(events) is not None)
        slim = _structure_slim(seen)
        assert slim is not None, f"the structure {STRUCTURE} was not in the ballpark of system {SYSTEM} within 30 s"
        described = keyval(slim)
        assert number(described["categoryID"]) == STRUCTURE_CATEGORY
        assert number(described["state"]) > 0, "the slim item has a state"
        assert "timer" in described or "upkeepState" in described

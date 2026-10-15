"""ST-1 e, ST-3 d: a structure of the simulation, seen and docked at by a pilot in its system.

A new character of the recorded race undocks at the station of system 30100032 (as tests/test_undock.py does) and sees an imported Upwell structure
(an Astrahus, in the structure store) in the ballpark with its state and timer; it docks at the structure (the simulation finishes the docking) and
undocks again. The environment seeds the structure before the pilot comes into the system: tools/dev/run-eve-live.sh runs this file with EVE_RUN,
which inserts the row into the structures schema first.

  EVE_LIVE_HOST=host:port           the server (tools/dev/run-eve-live.sh)
  EVE_LIVE_STRUCTURE_ID=<item id>   the seeded structure's id
  EVE_LIVE_STRUCTURE_SYSTEM=<id>    the solar system it is anchored in (30100032)
"""

import os
import uuid

import pytest

from evecli import marshal as m
from evecli import packet as p
from evecli.describe import describe
from evecli.observe import _plain_name, timed_events
from tests import test_login_to_character as base
from tests.test_undock import STATION, UNDOCK, arrive, number

LIVE = os.environ.get("EVE_LIVE_HOST", "")
STRUCTURE = os.environ.get("EVE_LIVE_STRUCTURE_ID", "")
SYSTEM = os.environ.get("EVE_LIVE_STRUCTURE_SYSTEM", "")

pytestmark = pytest.mark.skipif(
    not (LIVE and STRUCTURE and SYSTEM),
    reason="set EVE_LIVE_HOST, EVE_LIVE_STRUCTURE_ID and EVE_LIVE_STRUCTURE_SYSTEM (tools/dev/run-eve-live.sh seeds the structure in EVE_RUN)",
)

# the structure's category in the client (categoryID 65, ST-1)
STRUCTURE_CATEGORY = 65

RACE = UNDOCK["races"][0]  # the recorded character the undock scenario creates


def _structure_slim(events: list) -> dict | None:
    """The structure's slim item (its fields as `name -> value`) in the ballpark events seen so far, if the client has it: an item of an AddBalls2
    or of the ballpark's SetState, wherever the event nests it."""
    for node in _nodes(events):
        if isinstance(node, dict) and isinstance(node.get("$dict"), list) and all(isinstance(pair, list) and len(pair) == 2 for pair in node["$dict"]):
            fields = {key: value for key, value in node["$dict"] if isinstance(key, str)}
            if "itemID" in fields and number(fields["itemID"]) == int(STRUCTURE):
                return fields
    return None


def _nodes(value):
    """Every dict and list inside a described value, the value itself first."""
    yield value
    if isinstance(value, dict):
        for child in value.values():
            yield from _nodes(child)
    elif isinstance(value, list):
        for child in value:
            yield from _nodes(child)


def _undocked_pilot(client) -> int:
    """A new character of the recorded race, undocked at its station and bound to the system's ballpark: its active ship's id."""
    _, ship_id, fitted, _ = arrive(client, RACE, uuid.uuid4().hex[:10])
    ship = client.call("ship", "MachoBindObject", (STATION, 15), None)
    client.call_bound(ship[0], "Undock", ship_id, False, onlineModules=m.List(fitted))
    client.call("beyonce", "MachoBindObject", int(SYSTEM), None)
    return ship_id


def test_a_pilot_in_the_system_sees_the_structure_with_its_state_and_timer():
    with base.connect(f"walk-structure-{uuid.uuid4().hex[:10]}") as client:
        _undocked_pilot(client)
        seen = timed_events(client, 30.0, until=lambda events: _structure_slim(events) is not None)
        slim = _structure_slim(seen)
        assert slim is not None, f"the structure {STRUCTURE} was not in the ballpark of system {SYSTEM} within 30 s"
        described = slim
        assert number(described["categoryID"]) == STRUCTURE_CATEGORY
        assert number(described["state"]) > 0, "the slim item has a state"
        assert "timer" in described or "upkeepState" in described


def test_a_pilot_docks_at_the_structure_and_undocks_from_it():
    """ST-3 d: the docking is finished by the simulation (OnDockingFinished) and the ship leaves the structure again.

    The ship is undocked a few kilometres from the structure: a dock asked for from outside the docking range makes the ship fly to it on its own
    (scene/docking.rs), so the answer is the docking, not an approach refusal.
    """
    with base.connect(f"walk-structure-{uuid.uuid4().hex[:10]}") as client:
        ship_id = _undocked_pilot(client)
        seen = timed_events(client, 30.0, until=lambda events: _structure_slim(events) is not None)
        assert _structure_slim(seen) is not None, f"the structure {STRUCTURE} is not in the ballpark"

        try:
            client.call("structureDocking", "Dock", int(STRUCTURE), int(ship_id))
        except p.MachoError as failure:
            pytest.fail(f"the docking was refused: the server answered {_user_error_label(failure)}: {describe(failure.payload)}")
        finished = timed_events(
            client,
            60.0,
            until=lambda events: any(_plain_name(event["name"]) == "OnDockingFinished" for event in events if "name" in event),
        )
        docked = [event for event in finished if "name" in event and _plain_name(event["name"]) == "OnDockingFinished"]
        assert docked, "the docking was not finished within 60 s (no OnDockingFinished)"

        client.call("structureDocking", "Undock", int(STRUCTURE), int(ship_id), ignoreContraband=False)
        left = timed_events(
            client,
            30.0,
            until=lambda events: any(_plain_name(event["name"]) == "OnItemsChanged" for event in events if "name" in event),
        )
        assert any(_plain_name(event["name"]) == "OnItemsChanged" for event in left if "name" in event), "no item change after the undock"


def _user_error_label(failure: p.MachoError) -> str:
    """The label of the client's UserError the server raised (the first item of its tuple)."""
    payload = describe(failure.payload)["$objectex"]["$tuple"]
    return str(payload[1]["$tuple"][0])

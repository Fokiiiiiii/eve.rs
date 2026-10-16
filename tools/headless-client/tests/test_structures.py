"""ST-1 e, ST-3 d, ST-5, ST-6a, ST-8 refusals: a structure of the simulation, seen, docked at and refused by name by pilots in its system.

A new character of the recorded race undocks at the station of system 30100032 (as tests/test_undock.py does) and sees an imported Upwell structure
(an Astrahus, in the structure store) in the ballpark with its state and timer; it docks at the structure (the simulation finishes the docking) and
undocks again. The structure is inserted into the structures schema by the first test of this module (`seeded_structure`), before any pilot comes
into the system; tools/dev/run-eve-live.sh exports the schema (EVE_STRUCTURES_SCHEMA) and EVE_PG_URL for the run.

The refusals are the ones the reference sends (EVE.js, cited per test); a test that asserts one of them fails when the server answers otherwise.

  EVE_LIVE_HOST=host:port           the server (tools/dev/run-eve-live.sh)
  EVE_LIVE_STRUCTURE_ID=<item id>   optional: another structure id than the seeded one
  EVE_LIVE_STRUCTURE_SYSTEM=<id>    optional: the solar system it is anchored in (30100032)
"""

import os
import uuid

import pytest

from evecli import marshal as m
from evecli import packet as p
from evecli.describe import describe
from evecli.observe import _plain_name, timed_events
from tests import dbseed
from tests import test_login_to_character as base
from tests.test_undock import STATION, UNDOCK, arrive, number

LIVE = os.environ.get("EVE_LIVE_HOST", "")
SEEDED_STRUCTURE = 1040000000777  # the Astrahus the module seeds (its item id; the structure store's key)
SEEDED_SYSTEM = 30100032  # the solar system of the station the pilots undock at
STRUCTURE = int(os.environ.get("EVE_LIVE_STRUCTURE_ID") or SEEDED_STRUCTURE)
SYSTEM = int(os.environ.get("EVE_LIVE_STRUCTURE_SYSTEM") or SEEDED_SYSTEM)
UNKNOWN_STRUCTURE = 1040000000999  # no structure has this id

pytestmark = pytest.mark.skipif(not LIVE, reason="set EVE_LIVE_HOST (tools/dev/run-eve-live.sh runs this file)")

# the structure's category in the client (categoryID 65, ST-1)
STRUCTURE_CATEGORY = 65

NPC_CORPORATIONS = range(1_000_000, 2_000_000)  # the NPC corporations' ids (StructureAnchorAuthority: DropNeedsPlayerCorp)
DIRECTOR = 1  # the corporation role bit a Director holds (the anchor needs Station Manager, Director or CEO)

RACE = UNDOCK["races"][0]  # the recorded character the undock scenario creates


@pytest.fixture(scope="module", autouse=True)
def seeded_structure():
    """The Astrahus the scenarios use: a row of the structures schema (the owner is an NPC corporation, so the owner-corporation access
    rule admits the pilots of the other tests), inserted once per run. The run drops its schema at the end."""
    if not (os.environ.get("EVE_PG_URL") and os.environ.get("EVE_STRUCTURES_SCHEMA")):
        pytest.skip("EVE_PG_URL and EVE_STRUCTURES_SCHEMA come from tools/dev/run-eve-live.sh, which seeds the structure")
    schema = dbseed.schema("STRUCTURES")
    dbseed.sql(
        f"insert into {schema}.structures (structure_id, type_id, solar_system_id, x, y, z, owner_corporation_id, name, state, state_started_ms, "
        f"upkeep, core_installed, doc) select {STRUCTURE}, 35832, {SYSTEM}, 311989875775, -13069156275, 156646281447, 1000006, 'Live Astrahus', "
        f"110, 0, 1, true, '{{}}' where not exists (select 1 from {schema}.structures where structure_id = {STRUCTURE})"
    )
    dbseed.sql(
        f"insert into {schema}.structure_services (structure_id, service_id, state) select {STRUCTURE}, 1, 1 "
        f"where not exists (select 1 from {schema}.structure_services where structure_id = {STRUCTURE} and service_id = 1)"
    )


def _structure_slim(events: list) -> dict | None:
    """The structure's slim item (its fields as `name -> value`) in the ballpark events seen so far, if the client has it: an item of an AddBalls2
    or of the ballpark's SetState, wherever the event nests it."""
    for node in _nodes(events):
        if isinstance(node, dict) and isinstance(node.get("$dict"), list) and all(isinstance(pair, list) and len(pair) == 2 for pair in node["$dict"]):
            fields = {key: value for key, value in node["$dict"] if isinstance(key, str)}
            if "itemID" in fields and number(fields["itemID"]) == STRUCTURE:
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


def _reenter(client, character: int) -> None:
    """The character logs off and selects again, so its session takes the corporation and roles the database now has."""
    client.call("userSvc", "UserLogOffCharacter")
    assert base.attempt(client, "charUnboundMgr", "SelectCharacterID", character, None, False) == ("ok", None)


def _undock(client, ship_id: int, fitted: list) -> None:
    """The pilot leaves its station with its ship and is bound to the system's ballpark."""
    ship = client.call("ship", "MachoBindObject", (STATION, 15), None)
    client.call_bound(ship[0], "Undock", ship_id, False, onlineModules=m.List(fitted))
    client.call("beyonce", "MachoBindObject", SYSTEM, None)


def _undocked_pilot(client) -> tuple[int, int]:
    """A new character of the recorded race, undocked at its station and bound to the system's ballpark: (character id, its active ship's id)."""
    character, ship_id, fitted, _ = arrive(client, RACE, uuid.uuid4().hex[:10])
    _undock(client, ship_id, fitted)
    return character, ship_id


def _pilot_without_role(ceo, pilot) -> tuple[int, int, int]:
    """A member of a player corporation with no role, undocked: (its corporation, character id, its ship id). The corporation's chief executive is
    the character of `ceo`; the pilot's session takes the membership by a log off and select."""
    run = uuid.uuid4().hex[:8]
    director, _, _, _ = arrive(ceo, RACE, run + "d")
    character, ship_id, fitted, _ = arrive(pilot, RACE, run + "p")
    corporation = dbseed.found_corporation(STATION, SYSTEM, director, run)
    dbseed.join_corporation(director, corporation, DIRECTOR)
    dbseed.join_corporation(character, corporation, 0)
    _reenter(pilot, character)
    _undock(pilot, ship_id, fitted)
    return corporation, character, ship_id


def _refusal(failure: p.MachoError) -> tuple[str, str]:
    """The label of the client's UserError the server raised (the first item of its tuple) and the whole of its payload as text."""
    payload = describe(failure.payload)["$objectex"]["$tuple"]
    return str(payload[1]["$tuple"][0]), str(describe(failure.payload))


def _refused(call) -> tuple[str, str]:
    """(label, payload text) of the refusal the call gets; a call that is answered fails the test."""
    try:
        answer = call()
    except p.MachoError as failure:
        return _refusal(failure)
    pytest.fail(f"the server answered the call instead of refusing it: {describe(answer)}")


def _anchor(client, item_id: int):
    """structureDeployment.Anchor as the client sends it (Deploy/ConfirmDeployment: item, x, z, yaw, profile, name, bio, reinforce weekday and hour)."""
    return client.call("structureDeployment", "Anchor", item_id, 1000.0, 1000.0, 0.0, None, "Refused anchor", "", None, None, None)


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
        _, ship_id = _undocked_pilot(client)
        seen = timed_events(client, 30.0, until=lambda events: _structure_slim(events) is not None)
        assert _structure_slim(seen) is not None, f"the structure {STRUCTURE} is not in the ballpark"

        try:
            client.call("structureDocking", "Dock", STRUCTURE, int(ship_id))
        except p.MachoError as failure:
            pytest.fail(f"the docking was refused: the server answered {_refusal(failure)[0]}: {describe(failure.payload)}")
        finished = timed_events(
            client,
            60.0,
            until=lambda events: any(_plain_name(event["name"]) == "OnDockingFinished" for event in events if "name" in event),
        )
        docked = [event for event in finished if "name" in event and _plain_name(event["name"]) == "OnDockingFinished"]
        assert docked, "the docking was not finished within 60 s (no OnDockingFinished)"

        client.call("structureDocking", "Undock", STRUCTURE, int(ship_id), ignoreContraband=False)
        left = timed_events(
            client,
            30.0,
            until=lambda events: any(_plain_name(event["name"]) == "OnItemsChanged" for event in events if "name" in event),
        )
        assert any(_plain_name(event["name"]) == "OnItemsChanged" for event in left if "name" in event), "no item change after the undock"


def test_an_npc_corporation_pilot_is_refused_the_anchor_by_name():
    """structureDeployment.Anchor from an NPC corporation: `DropNeedsPlayerCorp` (EVE.js sovPlayerDeployment.js; the server's
    StructureAnchorAuthority gate, which runs before the role check). The item is the ship, in space, so only the corporation gate can refuse."""
    with base.connect(f"walk-anchor-npc-{uuid.uuid4().hex[:10]}") as client:
        character, ship_id, fitted, _ = arrive(client, RACE, uuid.uuid4().hex[:10])
        corporation = dbseed.corporation_of(character)
        assert corporation in NPC_CORPORATIONS, f"the new pilot is in corporation {corporation}, not an NPC corporation: the premise of the refusal"
        _undock(client, ship_id, fitted)
        label, _ = _refused(lambda: _anchor(client, ship_id))
        assert label == "DropNeedsPlayerCorp"


def test_a_pilot_without_the_station_manager_or_director_role_is_refused_the_anchor_by_name():
    """structureDeployment.Anchor by a member of a player corporation with no role: `CrpAccessDenied` with the reason `Insufficient roles`
    (EVE.js sovPlayerDeployment.js, the wiki's \"Anchoring structures\": a Station Manager, a Director or the CEO)."""
    run = uuid.uuid4().hex[:8]
    with base.connect(f"walk-anchor-ceo-{run}") as ceo, base.connect(f"walk-anchor-plain-{run}") as pilot:
        corporation, character, ship_id = _pilot_without_role(ceo, pilot)
        assert dbseed.corporation_of(character) == corporation, "the pilot's session has its corporation"
        label, text = _refused(lambda: _anchor(pilot, ship_id))
        assert label == "CrpAccessDenied"
        assert "Insufficient roles" in text


def test_a_structure_with_no_pilot_answers_none_for_its_pilot():
    """structureControl.GetStructurePilot of a structure no pilot controls: None (EVE.js structureControlService.js GetStructurePilot)."""
    with base.connect(f"walk-control-{uuid.uuid4().hex[:10]}") as client:
        _undocked_pilot(client)
        assert client.call("structureControl", "GetStructurePilot", STRUCTURE) is None


def test_taking_control_from_space_is_refused_until_the_pilot_is_docked_in_the_structure():
    """structureControl.TakeControl by a pilot in space: CustomNotify `You must be docked in this structure to take control.` (EVE.js
    structureControlService.js TakeControl, the docking check before the structure's setting)."""
    with base.connect(f"walk-control-{uuid.uuid4().hex[:10]}") as client:
        _undocked_pilot(client)
        label, text = _refused(lambda: client.call("structureControl", "TakeControl", STRUCTURE))
        assert label == "CustomNotify"
        assert "You must be docked in this structure to take control." in text


def test_boarding_a_structure_that_does_not_exist_is_refused_as_a_cancelled_targeting():
    """structureControl.BoardStructure of an unknown id: `TargetingAttemptCancelled` (EVE.js structureControlService.js, STRUCTURE_NOT_FOUND)."""
    with base.connect(f"walk-control-{uuid.uuid4().hex[:10]}") as client:
        _undocked_pilot(client)
        label, _ = _refused(lambda: client.call("structureControl", "BoardStructure", UNKNOWN_STRUCTURE))
        assert label == "TargetingAttemptCancelled"


def test_hacking_calls_with_no_open_attempt_answer_none_and_an_ack():
    """hackingMgr.QuitHackingAttempt and ClickedOnTile with no attempt open: None (EVE.js hackingMgrService.js returns null); a tile click
    answers the turn with an ACK event (OnHackingTurnComplete), the quit sends nothing."""
    with base.connect(f"walk-hacking-{uuid.uuid4().hex[:10]}") as client:
        _undocked_pilot(client)
        assert client.call("hackingMgr", "QuitHackingAttempt") is None
        assert client.call("hackingMgr", "ClickedOnTile", (0, 0)) is None
        seen = timed_events(client, 5.0, until=lambda events: any(_plain_name(e["name"]) == "OnHackingTurnComplete" for e in events if "name" in e))
        assert any(_plain_name(e["name"]) == "OnHackingTurnComplete" for e in seen if "name" in e), "the tile click with no attempt sent no ACK"


def test_delivering_from_space_is_refused_until_the_pilot_is_inside_the_structure():
    """structureDeliveries.Deliver from outside the structure: CustomNotify `You must be inside the structure to deliver items.` (EVE.js
    structureDeliveriesService.js Handle_Deliver, checked before the recipient)."""
    with base.connect(f"walk-deliver-{uuid.uuid4().hex[:10]}") as client:
        character, ship_id = _undocked_pilot(client)
        label, text = _refused(lambda: client.call("structureDeliveries", "Deliver", STRUCTURE, character, [ship_id]))
        assert label == "CustomNotify"
        assert "You must be inside the structure to deliver items." in text


def test_a_moon_extraction_for_a_duration_that_is_not_positive_is_refused_by_name():
    """moonExtractions.StartNewExtraction with a duration of 0: `MoonminingEventTooShort` (the server's refusal, DEVIATIONS: the reference
    answers None, the client reads that as success)."""
    with base.connect(f"walk-moon-{uuid.uuid4().hex[:10]}") as client:
        _undocked_pilot(client)
        label, _ = _refused(lambda: client.call("moonExtractions", "StartNewExtraction", STRUCTURE, 0, False))
        assert label == "MoonminingEventTooShort"


def test_a_moon_extraction_started_from_space_is_refused_as_a_structure_defense_denial():
    """moonExtractions.StartNewExtraction by a pilot who is not docked in the structure: `StructureDefenseDenied` (EVE.js
    moonExtractionsService.js validateMoonExtractionMutationAccess)."""
    with base.connect(f"walk-moon-{uuid.uuid4().hex[:10]}") as client:
        _undocked_pilot(client)
        label, _ = _refused(lambda: client.call("moonExtractions", "StartNewExtraction", STRUCTURE, 518460, False))
        assert label == "StructureDefenseDenied"

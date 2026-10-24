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

import json
import os
import time
import uuid

import pytest

from evecli import marshal as m
from evecli import packet as p
from evecli.describe import describe
from evecli.observe import _plain_name, timed_events
from tests import dbseed
from tests.runid import run_token
from tests import test_login_to_character as base
from tests.test_undock import STATION, UNDOCK, arrive, number

LIVE = os.environ.get("EVE_LIVE_HOST", "")
SEEDED_STRUCTURE = 1040000000777  # the Astrahus the module seeds (its item id; the structure store's key)
SEEDED_SYSTEM = 30100032  # the solar system of the station the pilots undock at
STRUCTURE = int(os.environ.get("EVE_LIVE_STRUCTURE_ID") or SEEDED_STRUCTURE)
# the refinery is seeded 200 000 km from Manifest's station (SDE 60015249 at x 311989870775): 5 km away, the station's sphere would push a ship out of

RETURN_STRUCTURE = STRUCTURE + 1
STRUCTURE_X, STRUCTURE_Y, STRUCTURE_Z = 311989870775.0 + 200_000_000.0, -13069156275.0, 156646281447.0
SYSTEM = int(os.environ.get("EVE_LIVE_STRUCTURE_SYSTEM") or SEEDED_SYSTEM)
UNKNOWN_STRUCTURE = 1040000000999  # no structure has this id
ATHANOR = 35835
MOON = 40491234  # a moon of Manifest (SDE mapMoons, system 30100032): the refinery mines it
MOON_RESOURCES = '{"45490": 0.75, "45494": 0.25}'  # the moon's ore shares: Zeolites and Cobaltite, published moon ores (SDE groups 1884, 1920)
TRITANIUM = 34
CARGO_FLAG = 5  # the ship's cargo hold (CharacterShips ship-hold flags)
DELIVERIES_FLAG = 173
ASTRAHUS = 35832
OBELISK = 20187  # the freighter has room for the control tower hull
# a system where Upwell structures can be anchored (its pinned SDE record does not forbid category 65 or the Astrahus group): low security, not a
# trade hub, one station and no asteroid belt (mapSolarSystems, npcStations, mapAsteroidBelts in the pinned SDE)
LEGAL_SYSTEM, LEGAL_STATION = 30000903, 60014062
LEGAL_CONSTELLATION, LEGAL_REGION = 20000132, 10000011
LEGAL_STATION_POSITION = (-1121428807680.0, 197508833280.0, 1232634716160.0)
WARP_OFF = 2_000_000.0  # metres from the station: the anchor needs 1000 km from every station, belt and structure
ANCHOR_STATE = 115  # StructureState.AnchorVulnerable: the state a new anchor is left in (StructureModel.cs)
ONLINE_STATE = 110  # StructureState.ShieldVulnerable: an online structure, which may be unanchored
STATE_CHANGED = "OnStructureStateChanged"
MOON_DAYS = 518460  # six days and a minute: the client's extraction duration, clamped to 6 to 56 days by the server

pytestmark = pytest.mark.skipif(not LIVE, reason="set EVE_LIVE_HOST (tools/dev/run-eve-live.sh runs this file)")

STRUCTURE_CATEGORY = 65

NPC_CORPORATIONS = range(1_000_000, 2_000_000)  # the NPC corporations' ids (StructureAnchorAuthority: DropNeedsPlayerCorp)
DIRECTOR = 1  # the corporation role bit a Director holds (the anchor needs Station Manager, Director or CEO)

RACE = UNDOCK["races"][0]  # the recorded character the undock scenario creates

@pytest.fixture(scope="module", autouse=True)
def seeded_structure():
    """The refinery the scenarios use: an Athanor row of the structures schema (its owner is an NPC corporation, so the owner-corporation access
    rule admits the pilots of the other tests), with its docking and moon mining services online and a moon's resources, inserted once per run.
    The run drops its schema at the end."""
    if os.environ.get("EVE_LIVE_SERVER") == "evejs":
        return
    if not (os.environ.get("EVE_PG_URL") and os.environ.get("EVE_STRUCTURES_SCHEMA")):
        pytest.skip("EVE_PG_URL and EVE_STRUCTURES_SCHEMA come from tools/dev/run-eve-live.sh, which seeds the structure")
    schema = dbseed.schema("STRUCTURES")
    dbseed.sql(
        f"insert into {schema}.structures (structure_id, type_id, solar_system_id, x, y, z, owner_corporation_id, name, state, state_started_ms, "
        f"upkeep, core_installed, doc) select {STRUCTURE}, {ATHANOR}, {SYSTEM}, 311989875775, -13069156275, 156646281447, 1000006, 'Live Refinery', "
        f"110, 0, 1, true, '{{}}' where not exists (select 1 from {schema}.structures where structure_id = {STRUCTURE})"
    )
    dbseed.sql(
        f"insert into {schema}.structures (structure_id, type_id, solar_system_id, x, y, z, owner_corporation_id, name, state, state_started_ms, "
        f"upkeep, core_installed, doc) select {RETURN_STRUCTURE}, {ATHANOR}, {SYSTEM}, {STRUCTURE_X}, {STRUCTURE_Y}, {STRUCTURE_Z}, 1000006, 'Live Return Refinery', "
        f"110, 0, 1, true, '{{}}' where not exists (select 1 from {schema}.structures where structure_id = {RETURN_STRUCTURE})"
    )
    for service in (1, 14):  # the docking service and the moon mining service (the Standup Moon Drill) online
        dbseed.sql(
            f"insert into {schema}.structure_services (structure_id, service_id, state) select {STRUCTURE}, {service}, 1 "
            f"where not exists (select 1 from {schema}.structure_services where structure_id = {STRUCTURE} and service_id = {service})"
        )
    dbseed.sql(
        f"insert into {schema}.structure_moon_resources (structure_id, moon_id, solar_system_id, owner_corporation_id, yield_multiplier, resources) "
        f"select {STRUCTURE}, {MOON}, {SYSTEM}, 1000006, 1.0, '{MOON_RESOURCES}' "
        f"where not exists (select 1 from {schema}.structure_moon_resources where structure_id = {STRUCTURE})"
    )

def _structure_slim(events: list, structure: int = STRUCTURE) -> dict | None:
    """The structure's slim item (its fields as `name -> value`) in the ballpark events seen so far, if the client has it: an item of an AddBalls2
    or of the ballpark's SetState, wherever the event nests it."""
    for node in _nodes(events):
        if isinstance(node, dict) and isinstance(node.get("$dict"), list) and all(isinstance(pair, list) and len(pair) == 2 for pair in node["$dict"]):
            fields = {key: value for key, value in node["$dict"] if isinstance(key, str)}
            if "itemID" in fields and number(fields["itemID"]) == structure:
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

def _undock(client, ship_id: int, fitted: list, station: int = STATION, system: int = SYSTEM) -> None:
    """The pilot leaves its station with its ship and is bound to the system's ballpark."""
    ship = client.call("ship", "MachoBindObject", (station, 15), None)
    assert client.call_bound(ship[0], "Undock", ship_id, False, onlineModules=m.List(fitted)) is not None, "the ship did not undock"
    client.call("beyonce", "MachoBindObject", system, None)

def _undocked_pilot(client) -> tuple[int, int]:
    """A new character of the recorded race, undocked at its station and bound to the system's ballpark: (character id, its active ship's id)."""
    character, ship_id, fitted, _ = arrive(client, RACE, run_token())
    _undock(client, ship_id, fitted)
    return character, ship_id

def _pilot_without_role(ceo, pilot) -> tuple[int, int, int]:
    """A member of a player corporation with no role, undocked: (its corporation, character id, its ship id). The corporation's chief executive is
    the character of `ceo`; the pilot's session takes the membership by a log off and select."""
    run = run_token()
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
    with base.connect(f"walk-structure-{run_token()}") as client:
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
    with base.connect(f"walk-structure-{run_token()}") as client:
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
    with base.connect(f"walk-anchor-npc-{run_token()}") as client:
        character, ship_id, fitted, _ = arrive(client, RACE, run_token())
        corporation = dbseed.corporation_of(character)
        assert corporation in NPC_CORPORATIONS, f"the new pilot is in corporation {corporation}, not an NPC corporation: the premise of the refusal"
        _undock(client, ship_id, fitted)
        label, _ = _refused(lambda: _anchor(client, ship_id))
        assert label == "DropNeedsPlayerCorp"

def test_a_pilot_without_the_station_manager_or_director_role_is_refused_the_anchor_by_name():
    """structureDeployment.Anchor by a member of a player corporation with no role: `CrpAccessDenied` with the reason `Insufficient roles`
    (EVE.js sovPlayerDeployment.js, the wiki's \"Anchoring structures\": a Station Manager, a Director or the CEO)."""
    run = run_token()
    with base.connect(f"walk-anchor-ceo-{run}") as ceo, base.connect(f"walk-anchor-plain-{run}") as pilot:
        corporation, character, ship_id = _pilot_without_role(ceo, pilot)
        assert dbseed.corporation_of(character) == corporation, "the pilot's session has its corporation"
        label, text = _refused(lambda: _anchor(pilot, ship_id))
        assert label == "CrpAccessDenied"
        assert "Insufficient roles" in text

def test_a_structure_with_no_pilot_answers_none_for_its_pilot():
    """structureControl.GetStructurePilot of a structure no pilot controls: None (EVE.js structureControlService.js GetStructurePilot)."""
    with base.connect(f"walk-control-{run_token()}") as client:
        _undocked_pilot(client)
        assert client.call("structureControl", "GetStructurePilot", STRUCTURE) is None

def test_taking_control_from_space_is_refused_until_the_pilot_is_docked_in_the_structure():
    """structureControl.TakeControl by a pilot in space: CustomNotify `You must be docked in this structure to take control.` (EVE.js
    structureControlService.js TakeControl, the docking check before the structure's setting)."""
    with base.connect(f"walk-control-{run_token()}") as client:
        _undocked_pilot(client)
        label, text = _refused(lambda: client.call("structureControl", "TakeControl", STRUCTURE))
        assert label == "CustomNotify"
        assert "You must be docked in this structure to take control." in text

def test_boarding_a_structure_that_does_not_exist_is_refused_as_a_cancelled_targeting():
    """structureControl.BoardStructure of an unknown id: `TargetingAttemptCancelled` (EVE.js structureControlService.js, STRUCTURE_NOT_FOUND)."""
    with base.connect(f"walk-control-{run_token()}") as client:
        _undocked_pilot(client)
        label, _ = _refused(lambda: client.call("structureControl", "BoardStructure", UNKNOWN_STRUCTURE))
        assert label == "TargetingAttemptCancelled"

def test_hacking_calls_with_no_open_attempt_answer_none_and_an_ack():
    """hackingMgr.QuitHackingAttempt and ClickedOnTile with no attempt open: None (EVE.js hackingMgrService.js returns null); a tile click
    answers the turn with an ACK event (OnHackingTurnComplete), the quit sends nothing."""
    with base.connect(f"walk-hacking-{run_token()}") as client:
        _undocked_pilot(client)
        assert client.call("hackingMgr", "QuitHackingAttempt") is None
        assert client.call("hackingMgr", "ClickedOnTile", (0, 0)) is None
        seen = timed_events(client, 5.0, until=lambda events: any(_plain_name(e["name"]) == "OnHackingTurnComplete" for e in events if "name" in e))
        assert any(_plain_name(e["name"]) == "OnHackingTurnComplete" for e in seen if "name" in e), "the tile click with no attempt sent no ACK"

def test_delivering_from_space_is_refused_until_the_pilot_is_inside_the_structure():
    """structureDeliveries.Deliver from outside the structure: CustomNotify `You must be inside the structure to deliver items.` (EVE.js
    structureDeliveriesService.js Handle_Deliver, checked before the recipient)."""
    with base.connect(f"walk-deliver-{run_token()}") as client:
        character, ship_id = _undocked_pilot(client)
        label, text = _refused(lambda: client.call("structureDeliveries", "Deliver", STRUCTURE, character, [ship_id]))
        assert label == "CustomNotify"
        assert "You must be inside the structure to deliver items." in text

def test_a_moon_extraction_for_a_duration_that_is_not_positive_is_refused_by_name():
    """moonExtractions.StartNewExtraction with a duration of 0: `MoonminingEventTooShort` (the server's refusal, DEVIATIONS: the reference
    answers None, the client reads that as success)."""
    with base.connect(f"walk-moon-{run_token()}") as client:
        _undocked_pilot(client)
        label, _ = _refused(lambda: client.call("moonExtractions", "StartNewExtraction", STRUCTURE, 0, False))
        assert label == "MoonminingEventTooShort"

def test_a_moon_extraction_started_from_space_is_refused_as_not_controlling():
    """moonExtractions.StartNewExtraction by a pilot who is not docked in the structure: the named notice "You must be controlling this
    structure." (EVE.js moonExtractionsService.js validateMoonExtractionMutationAccess, NOT_CONTROLLING_STRUCTURE; ST-22 corrected the
    refusal the test used to expect, which EVE.js does not send)."""
    with base.connect(f"walk-moon-{run_token()}") as client:
        _undocked_pilot(client)
        label, payload = _refused(lambda: client.call("moonExtractions", "StartNewExtraction", STRUCTURE, 518460, False))
        assert label == "CustomNotify"
        assert "You must be controlling this structure." in payload

def _docked_pilot(client, structure: int = STRUCTURE) -> tuple[int, int]:
    """A new pilot of the recorded race, undocked and then docked in a structure (the ship goes into its hangar): (character id, ship id)."""
    character, ship_id = _undocked_pilot(client)
    seen = timed_events(client, 30.0, until=lambda events: _structure_slim(events, structure) is not None)
    assert _structure_slim(seen, structure) is not None, f"the structure {structure} is not in the ballpark of system {SYSTEM} within 30 s"
    deadline = time.time() + 120
    while True:
        try:
            client.call("structureDocking", "Dock", structure, int(ship_id))
            break
        except p.MachoError as failure:

            if _refusal(failure)[0] != "DockingApproach" or time.time() > deadline:
                pytest.fail(f"the docking was refused: the server answered {_refusal(failure)[0]}: {describe(failure.payload)}")
        timed_events(client, 5.0)
    finished = timed_events(client, 60.0, until=lambda events: _named(events, "OnDockingFinished") != [])
    assert _named(finished, "OnDockingFinished"), "the docking was not finished within 60 s"
    return character, ship_id

def called(client, service: str, method: str, *args):
    """The call's answer; a refusal fails the test with the label the server gave (the first name of its UserError)."""
    try:
        return client.call(service, method, *args)
    except p.MachoError as failure:
        pytest.fail(f"{service}.{method} was refused: {_refusal(failure)[0]}: {describe(failure.payload)}")

def _holds(value, wanted) -> bool:
    """Whether a described value holds the number `wanted` anywhere inside it."""
    if isinstance(value, dict):
        return any(_holds(child, wanted) for child in value.values()) or value.get("$long") == wanted
    if isinstance(value, list):
        return any(_holds(child, wanted) for child in value)
    return value == wanted

def keyval(described) -> dict:
    """The fields of a described util.KeyVal, by name."""
    return {k: v for k, v in described["args"]["$dict"]}

def _named(events: list, name: str) -> list:
    return [event for event in events if "name" in event and _plain_name(event["name"]) == name]

def test_a_docked_pilot_takes_control_of_the_structure_and_the_structure_names_it():
    """structureControl.TakeControl by a pilot docked in the structure (V24.01 structureControlService.js Handle_TakeControl, which returns
    None): the structure's pilot is the character from then on. The reference's assumeStructureControl changes the session's shipid to the
    structure and tells the client the cosmetics of the ship it left (OnShipCosmeticsChanged)."""
    with base.connect(f"walk-control-{run_token()}") as client:
        character, _ship_id = _docked_pilot(client)
        assert client.call("structureControl", "TakeControl", STRUCTURE) is None
        assert client.call("structureControl", "GetStructurePilot", STRUCTURE) == character
        told = timed_events(client, 5.0, until=lambda events: _named(events, "OnShipCosmeticsChanged") != [])
        assert _named(told, "OnShipCosmeticsChanged"), "no OnShipCosmeticsChanged after the take-over"
        assert client.call("structureControl", "ReleaseControl") is None
        assert client.call("structureControl", "GetStructurePilot", STRUCTURE) is None

def test_a_pilot_in_space_boards_the_structure_and_ejects_into_a_capsule():
    """structureControl.BoardStructure from space (V24.01 Handle_BoardStructure, which returns None) makes the pilot the structure's pilot, and
    EjectFromStructure with no ship to return to leaves the structure with a capsule (the pilot is no longer its pilot)."""
    with base.connect(f"walk-control-{run_token()}") as client:
        character, ship_id, fitted, _ = arrive(client, RACE, run_token())
        _undock(client, ship_id, fitted)
        seen = timed_events(client, 30.0, until=lambda events: _structure_slim(events) is not None)
        assert _structure_slim(seen) is not None, f"the structure {STRUCTURE} is not in the ballpark of system {SYSTEM} within 30 s"
        assert called(client, "structureControl", "BoardStructure", STRUCTURE) is None
        assert client.call("structureControl", "GetStructurePilot", STRUCTURE) == character
        assert client.call("structureControl", "EjectFromStructure", 0) is None
        assert client.call("structureControl", "GetStructurePilot", STRUCTURE) is None

def test_a_second_pilot_taking_control_displaces_the_first_as_the_reference_does():
    """Exclusivity: one pilot controls a structure at a time. The reference's assumeStructureControl relinquishes the previous controller
    (reason `override`) and binds the new one (scene sessions.js lets the previous controller through), so a second docked pilot's TakeControl
    succeeds and the structure names the second pilot; the first pilot is no longer its pilot."""
    run = run_token()
    with base.connect(f"walk-control-{run}a") as first, base.connect(f"walk-control-{run}b") as second:
        first_character, _ = _docked_pilot(first)
        assert first.call("structureControl", "TakeControl", STRUCTURE) is None
        assert first.call("structureControl", "GetStructurePilot", STRUCTURE) == first_character
        second_character, _ = _docked_pilot(second)
        assert second.call("structureControl", "TakeControl", STRUCTURE) is None
        assert second.call("structureControl", "GetStructurePilot", STRUCTURE) == second_character
        assert second_character != first_character

def test_a_docked_pilot_delivers_items_from_the_structure_hangar_and_the_recipient_is_told():
    """structureDeliveries.Deliver by a pilot docked in the structure (V24.01 structureDeliveriesService.js Handle_Deliver, which returns None):
    the items of the sender in the structure's hangar move to the recipient, into the deliveries bay (flag 173) of the structure, and the
    recipient is sent the StructureItemsDelivered notice (type 199, the quantity and type of each item)."""
    run = run_token()
    with base.connect(f"walk-deliver-{run}s") as sender, base.connect(f"walk-deliver-{run}r") as recipient_client:
        character, _ship_id = _docked_pilot(sender)
        recipient, _, _, _ = arrive(recipient_client, RACE, run + "r")
        item = dbseed.new_item(TRITANIUM, owner=character, location=STRUCTURE, flag=dbseed.HANGAR, quantity=100)
        assert called(sender, "structureDeliveries", "Deliver", STRUCTURE, recipient, [item]) is None
        assert dbseed.owner_of(item) == recipient
        assert dbseed.flag_of(item) == DELIVERIES_FLAG
        assert dbseed.location_of(item) == STRUCTURE
        told = timed_events(recipient_client, 30.0, until=lambda events: _named(events, "OnNotificationReceived") != [])
        notices = _named(told, "OnNotificationReceived")
        assert notices, "the recipient was not sent the delivery notice"
        assert _holds(notices[0]["args"], 199), f"the notice is not StructureItemsDelivered (199): {notices[0]['args']}"

def test_a_pilot_in_space_drops_cargo_at_the_structure_and_is_answered_the_moved_ids():
    """structureCargoDelivery.DropOffItems from a ship in space within Cargo Deposit range (V24.01 Handle_DropOffItems): the cargo of the ship
    moves into the structure's deliveries bay of the pilot, and the answer is the list of the item ids that moved."""
    with base.connect(f"walk-dropoff-{run_token()}") as client:
        character, ship_id, fitted, _ = arrive(client, RACE, run_token())
        _undock(client, ship_id, fitted)
        seen = timed_events(client, 30.0, until=lambda events: _structure_slim(events) is not None)
        assert _structure_slim(seen) is not None, f"the structure {STRUCTURE} is not in the ballpark of system {SYSTEM} within 30 s"
        cargo = [dbseed.new_item(TRITANIUM, owner=character, location=ship_id, flag=CARGO_FLAG, quantity=50),
                 dbseed.new_item(TRITANIUM, owner=character, location=ship_id, flag=CARGO_FLAG, quantity=20)]
        moved = client.call("structureCargoDelivery", "DropOffItems", cargo, ship_id, STRUCTURE)
        assert sorted(number(item) for item in moved) == sorted(cargo)
        for item in cargo:
            assert dbseed.owner_of(item) == character
            assert dbseed.flag_of(item) == DELIVERIES_FLAG
            assert dbseed.location_of(item) == STRUCTURE

def test_the_controlling_pilot_starts_a_moon_extraction_at_the_refinery_and_the_extraction_is_told():
    """moonExtractions.StartNewExtraction by the pilot in control of the refinery (V24.01 moonExtractionsService.js Handle_StartNewExtraction): the
    answer is the extraction (a KeyVal of its id, structure, moon, system, owner, times, duration and yield), the pilot is sent
    OnMoonExtractionUpdate with it, and a second start while it runs answers that extraction as it is."""
    with base.connect(f"walk-moon-{run_token()}") as client:
        _docked_pilot(client, STRUCTURE)
        assert client.call("structureControl", "TakeControl", STRUCTURE) is None
        answer = keyval(describe(called(client, "moonExtractions", "StartNewExtraction", STRUCTURE, MOON_DAYS, False)))
        assert set(answer) == {"extractionID", "structureID", "moonID", "solarSystemID", "ownerID", "startMoveTime", "chunkAvailableTime",
                               "naturalDecayTime", "durationSeconds", "yieldMultiplier"}
        assert number(answer["structureID"]) == STRUCTURE
        assert number(answer["moonID"]) == MOON
        assert number(answer["solarSystemID"]) == SYSTEM
        assert number(answer["ownerID"]) == 1000006
        assert number(answer["durationSeconds"]) == MOON_DAYS
        extraction = number(answer["extractionID"])
        told = timed_events(client, 10.0, until=lambda events: _named(events, "OnMoonExtractionUpdate") != [])
        updates = _named(told, "OnMoonExtractionUpdate")
        assert updates, "the pilot was not sent OnMoonExtractionUpdate"
        assert _holds(updates[0]["args"], extraction), f"the update does not carry extraction {extraction}: {updates[0]['args']}"
        again = keyval(describe(called(client, "moonExtractions", "StartNewExtraction", STRUCTURE, MOON_DAYS, False)))
        assert number(again["extractionID"]) == extraction
        assert client.call("structureControl", "ReleaseControl") is None

def test_a_director_is_refused_the_anchor_of_an_astrahus_in_this_system_by_name():
    """structureDeployment.Anchor by a Director of a player corporation at least seven days old, in the system the recorded pilots undock in: the
    refusal `CantDeployBlocked` with the item's type (EVE.js sovPlayerDeployment.js; the wiki's Anchoring structures). The system's pinned SDE
    record (mapSolarSystems disallowedAnchorCategories) forbids Upwell structures (category 65) here, so the refusal comes after the Director gate
    and before any placement. A successful anchor is not live: the recorded pilots only undock in this system; the Host tests cover it."""
    run = run_token()
    with base.connect(f"walk-anchor-{run}") as client:
        character, ship_id, fitted, _ = arrive(client, RACE, run)
        corporation = dbseed.found_corporation(STATION, SYSTEM, character, run)
        dbseed.join_corporation(character, corporation, DIRECTOR)
        dbseed.sql(f"update {dbseed.schema('CORPORATIONS')}.corporations set created_ms = {int(time.time() * 1000) - 8 * 86_400_000} where id = {corporation}")
        _reenter(client, character)
        _undock(client, ship_id, fitted)
        item = dbseed.new_item(ASTRAHUS, owner=character, location=ship_id, flag=CARGO_FLAG, singleton=True)
        label, text = _refused(lambda: client.call("structureDeployment", "Anchor", item, 1000.0, 1000.0, 0.0, None, "Live anchor", "", None, None, None))
        assert label == "CantDeployBlocked"
        assert f"'typeID', {ASTRAHUS}" in text or f"typeID', {{'$long': {ASTRAHUS}}}" in text or f"['typeID', {ASTRAHUS}]" in text, text
        assert dbseed.sql(f"select count(*) from {dbseed.schema('INVENTORY')}.items where id = {item}") == "1", "the refused item was consumed"

def _relocate(character: int, ship_id: int) -> None:
    """Puts the character and its ship in the legal system's station (the characters' location and the ship's hangar), as a pilot who docked
    there would be: the next selection of the character reads it."""
    dbseed.sql(f"update {dbseed.schema('CHARACTERS')}.characters set station_id = {LEGAL_STATION}, home_station_id = {LEGAL_STATION}, "
               f"solar_system_id = {LEGAL_SYSTEM}, constellation_id = {LEGAL_CONSTELLATION}, region_id = {LEGAL_REGION} where id = {character}")
    dbseed.sql(f"update {dbseed.schema('INVENTORY')}.items set location_id = {LEGAL_STATION}, flag = {dbseed.HANGAR} where id = {ship_id}")

def test_a_director_anchors_an_astrahus_from_its_ship_in_a_legal_system_and_the_structure_is_placed_and_told():
    """structureDeployment.Anchor by a Director of a player corporation at least seven days old, in a system the pinned SDE allows (V24.01
    sovPlayerDeployment.js; the wiki's Anchoring structures): the Astrahus leaves the ship (the item is consumed), the structure is created in the
    corporation in its anchoring state (AnchorVulnerable) with its default profile, the simulation places it in the system (the pilot sees it in
    the ballpark), and the corporation's sessions hear OnStructureStateChanged. The pilot warps 2000 km from the station first (the free-space rule),
    and the anchor is asked again until the warp is over, as in the warp test. Then the Director unanchors and cancels the unanchoring."""
    run = run_token()
    with base.connect(f"walk-anchor-ok-{run}") as client:
        character, ship_id, fitted, _ = arrive(client, RACE, run)
        corporation = dbseed.found_corporation(LEGAL_STATION, LEGAL_SYSTEM, character, run)
        dbseed.join_corporation(character, corporation, DIRECTOR)
        dbseed.sql(f"update {dbseed.schema('CORPORATIONS')}.corporations set created_ms = {int(time.time() * 1000) - 8 * 86_400_000} where id = {corporation}")
        _relocate(character, ship_id)
        _reenter(client, character)
        timed_events(client, 0.5)
        _undock(client, ship_id, fitted, LEGAL_STATION, LEGAL_SYSTEM)
        timed_events(client, 2.5)
        ballpark = client.call("beyonce", "MachoBindObject", LEGAL_SYSTEM, None)
        timed_events(client, 2.5)
        x, y, z = LEGAL_STATION_POSITION
        client.call_bound(ballpark[0], "CmdWarpToStuff", "position", (x + WARP_OFF, y, z), minRange=0)
        timed_events(client, 1.0)
        item = dbseed.new_item(ASTRAHUS, owner=character, location=ship_id, flag=CARGO_FLAG, singleton=True)
        deadline = time.time() + 300
        while True:
            try:
                assert client.call("structureDeployment", "Anchor", item, 1000.0, 1000.0, 0.0, None, "Live anchor", "", None, None, None) is None
                break
            except p.MachoError as failure:
                if _refusal(failure)[0] != "ShipInWarp" or time.time() > deadline:
                    pytest.fail(f"the anchor was refused: {_refusal(failure)[0]}: {describe(failure.payload)}")
            # a stop once the warp is over clears it on the server (refused while the ship is still in warp), as a pilot's stop does
            client.call_bound(ballpark[0], "CmdStop")
            client.call_bound(ballpark[0], "UpdateStateRequest")
            timed_events(client, 3.0)
        assert dbseed.sql(f"select count(*) from {dbseed.schema('INVENTORY')}.items where id = {item}") == "0", "the anchored item was not consumed"
        row = dbseed.sql(f"select owner_corporation_id, solar_system_id, state from {dbseed.schema('STRUCTURES')}.structures where structure_id = {item}")
        assert row == f"{corporation}|{LEGAL_SYSTEM}|{ANCHOR_STATE}", f"the structure row is {row!r}"
        told = timed_events(client, 30.0, until=lambda seen: _named(seen, STATE_CHANGED) != [] and _structure_slim(seen, item) is not None)
        assert _named(told, STATE_CHANGED), f"no {STATE_CHANGED} within 30 s"
        assert _structure_slim(told, item) is not None, f"the anchored structure {item} is not in the ballpark within 30 s"
        # the repair of an anchor runs in real time, so the structure is put in its online state (ShieldVulnerable): the state an anchored
        # structure is in once it is online. EVE.js refuses an unanchoring in the states of STRUCTURE_UNANCHOR_CANCEL_STATES (AnchorVulnerable among them)
        dbseed.sql(f"update {dbseed.schema('STRUCTURES')}.structures set state = {ONLINE_STATE} where structure_id = {item}")
        assert called(client, "structureDeployment", "Unanchor", item) is None
        assert called(client, "structureDeployment", "CancelUnanchor", item) is None

STRUCTURE_POSITION = (STRUCTURE_X, STRUCTURE_Y, STRUCTURE_Z)  # the return refinery's position (seeded above)
RETURN_DISTANCE_METERS = 48_000.0  # from the refinery's centre: its ball is 45 000 m (SDE radius), so about 3 km of surface, inside the 6 500 m a ship may return from (V24.01 maxBoardingDistance); nearer, the simulation pushes the ship out of the ball
FAR_FUTURE_MS = 4_000_000_000_000  # a saved place that does not expire

def _seed_ship_place(ship_id: int, system: int = SYSTEM) -> None:
    """The ship's saved place (space.space_items): RETURN_DISTANCE_METERS from the refinery's centre, along +x. The server restores the place when the pilot
    selects its character in space (SpaceSessions: a valid saved place is the ship's place), so the pilot's ship lies within reach of the structure."""
    x, y, z = STRUCTURE_POSITION
    dbseed.sql(
        f"insert into {dbseed.schema('SPACE')}.space_items (item_id, solar_system_id, x, y, z, dx, dy, dz, radius, group_id, launcher_id, character_id, "
        f"corporation_id, alliance_id, war_faction_id, expires_at_ms) "
        f"values ({int(ship_id)}, {int(system)}, {x + RETURN_DISTANCE_METERS}, {y}, {z}, 1, 0, 0, 40, 6, 0, 0, 0, 0, 0, {FAR_FUTURE_MS}) "
        f"on conflict (item_id) do update set solar_system_id = excluded.solar_system_id, x = excluded.x, y = excluded.y, z = excluded.z, "
        f"dx = excluded.dx, dy = excluded.dy, dz = excluded.dz, expires_at_ms = excluded.expires_at_ms"
    )

def _reselect_in_space(client, character: int, system: int = SYSTEM) -> None:
    """The character logs off (its place is saved), the place is seeded, and the character is selected again into the system's ballpark."""
    client.call("userSvc", "UserLogOffCharacter")
    time.sleep(3)  # the server saves the pilot's place as it logs off: the seed is written after that save
    _seed_ship_place(_ship_of(character))
    assert base.attempt(client, "charUnboundMgr", "SelectCharacterID", character, None, False) == ("ok", None)
    client.call("beyonce", "MachoBindObject", system, None)

def _ship_of(character: int) -> int:
    return int(dbseed.sql(f"select ship_id from {dbseed.schema('CHARACTERS')}.characters where id = {int(character)}") or 0)

def test_a_pilot_in_space_boards_the_structure_and_ejects_back_into_its_own_ship():
    """structureControl.BoardStructure from space (V24.01 Handle_BoardStructure), then EjectFromStructure with the pilot's own ship (V24.01 and EVE.js
    ejectFromStructureControl with restoreShipID): the ship is restored (OnShipCosmeticsChanged carries its id) and the structure has no pilot. The original ship remains 200,000 km from the refinery; eject restores that ship without treating it as a boarding action."""
    with base.connect(f"walk-return-{uuid.uuid4().hex[:10]}") as client:
        character, ship_id, fitted, _ = arrive(client, RACE, uuid.uuid4().hex[:10])
        _undock(client, ship_id, fitted)
        _reselect_in_space(client, character)
        seen = timed_events(client, 30.0, until=lambda events: _structure_slim(events, RETURN_STRUCTURE) is not None)
        assert _structure_slim(seen, RETURN_STRUCTURE) is not None, f"the structure {RETURN_STRUCTURE} is not in the ballpark of system {SYSTEM} within 30 s"
        assert called(client, "structureControl", "BoardStructure", RETURN_STRUCTURE) is None
        assert client.call("structureControl", "GetStructurePilot", RETURN_STRUCTURE) == character
        assert called(client, "structureControl", "EjectFromStructure", ship_id) is None
        assert client.call("structureControl", "GetStructurePilot", RETURN_STRUCTURE) is None
        told = timed_events(client, 15.0, until=lambda events: any(_holds(event.get("args"), ship_id) for event in _named(events, "OnShipCosmeticsChanged")))
        assert any(_holds(event.get("args"), ship_id) for event in _named(told, "OnShipCosmeticsChanged")), (
            f"the pilot was not told its ship {ship_id} is back: {_named(told, 'OnShipCosmeticsChanged')}")

def _moon_rows(structure: int, extraction: int) -> dict:
    row = dbseed.sql(
        f"select state, completed_reason, fracture_complete_ms, chunk_available_ms from {dbseed.schema('STRUCTURES')}.structure_moon_extractions "
        f"where extraction_id = {int(extraction)}")
    state, reason, fracture_complete, chunk = (row.split("|") + ["", "", "", ""])[:4]
    return {"state": state, "reason": reason, "fractureCompleteMs": int(fracture_complete or 0), "chunkMs": int(chunk or 0)}

def _owner_notifications(type_id: int, structure: int) -> list:
    """The owners' notification rows of a type for a structure: the notifications table of the game store, in which each box holds its rows."""
    found = []
    docs = dbseed.sql(f"select doc::text from {dbseed.schema('GAMESTORE')}.notifications")
    for doc in docs.splitlines():
        for node in _nodes(json.loads(doc)):
            if isinstance(node, dict) and node.get("typeID") == type_id and node.get("data", {}).get("structureID") == structure:
                found.append(node)
    return found

def test_a_moon_chunk_is_refused_until_ready_then_fractured_and_completed_and_its_owners_are_told():
    """FractureChunkWithStructure (V24.01 moonExtractionsService; EVE.js Handle_FractureChunkWithStructure): a chunk that is not ready is refused by name; a ready one
    fires: the extraction is fracturing, the owners are sent the laser-fired notice (type 205, the mailbox row), and 30 s later the fracture completes as
    fractured (the record and the ore field). No owner notice is written for a fractured completion (EVE.js writes none for it)."""
    with base.connect(f"walk-fracture-{uuid.uuid4().hex[:10]}") as client:
        character, _ = _docked_pilot(client, STRUCTURE)
        assert client.call("structureControl", "TakeControl", STRUCTURE) is None
        answer = keyval(describe(called(client, "moonExtractions", "StartNewExtraction", STRUCTURE, MOON_DAYS, False)))
        extraction = number(answer["extractionID"])
        label, text = _refused(lambda: client.call("moonExtractions", "FractureChunkWithStructure"))
        assert label == "CustomNotify" and "not ready" in text, (label, text)

        # the chunk is ready: its arrival time is in the past (the server reads the extraction row), and the fire is accepted
        dbseed.sql(f"update {dbseed.schema('STRUCTURES')}.structure_moon_extractions set chunk_available_ms = "
                   f"{int(time.time() * 1000) - 60_000} where extraction_id = {extraction}")
        assert called(client, "moonExtractions", "FractureChunkWithStructure") is None
        fired = _moon_rows(STRUCTURE, extraction)
        assert fired["state"] == "fracturing", fired

        deadline = time.time() + 60
        while not _owner_notifications(205, STRUCTURE) and time.time() < deadline:
            timed_events(client, 2.0)
        assert _owner_notifications(205, STRUCTURE), "the owners' laser-fired notice (205) was not written"

        # the fracture completes at its time (the server's job, 30 s after the fire)
        deadline = time.time() + 90
        while time.time() < deadline:
            rows = _moon_rows(STRUCTURE, extraction)
            if rows["state"] == "completed":
                break
            timed_events(client, 2.0)
        assert rows["state"] == "completed" and rows["reason"] == "fractured", rows
        # the ore field is placed by the space's event subscriber after the completion (a durable delivery): its rocks are in the system within seconds
        fields = 0
        deadline = time.time() + 60
        while fields == 0 and time.time() < deadline:
            fields = int(dbseed.sql(f"select count(*) from {dbseed.schema('SPACE')}.space_items where moon_extraction_id = {extraction}") or 0)
            if fields == 0:
                timed_events(client, 2.0)
        assert fields > 0, "the fractured chunk left no ore field in the system"
        assert _owner_notifications(205, STRUCTURE), "the laser-fired notice of the fire is not in the owners' rows any more"
        assert client.call("structureControl", "ReleaseControl") is None

def test_a_corporation_unanchors_and_scoops_its_control_tower():
    """V24 ship.Drop, posMgr.UnanchorStructure and ship.Scoop walk: reject an online tower, then unanchor, drop and recover its hull."""
    run = uuid.uuid4().hex[:10]
    with base.connect(f"walk-tower-{run}") as client:
        character, ship_id, fitted, _ = arrive(client, RACE, run)
        corporation = dbseed.found_corporation(LEGAL_STATION, LEGAL_SYSTEM, character, run)
        dbseed.join_corporation(character, corporation, DIRECTOR)
        dbseed.sql(
            f"update {dbseed.schema('CORPORATIONS')}.corporations set created_ms = {int(time.time() * 1000) - 8 * 86_400_000} "
            f"where id = {corporation}"
        )
        dbseed.sql(
            f"insert into {dbseed.schema('SKILLS')}.character_skills (character_id, type_id, level, skill_points) "
            f"values ({character}, 11584, 3, 0) on conflict (character_id, type_id) do update set level = 3, skill_points = 0"
        )
        _relocate(character, ship_id)
        dbseed.sql(f"update {dbseed.schema('INVENTORY')}.items set type_id = {OBELISK} where id = {ship_id}")
        _reenter(client, character)
        _undock(client, ship_id, fitted, LEGAL_STATION, LEGAL_SYSTEM)

        ship = client.call("ship", "MachoBindObject", (LEGAL_SYSTEM, 15), None)
        ballpark = client.call("beyonce", "MachoBindObject", LEGAL_SYSTEM, None)
        x, y, z = LEGAL_STATION_POSITION
        client.call_bound(ballpark[0], "CmdWarpToStuff", "position", (x + WARP_OFF, y, z), minRange=0)
        tower = dbseed.new_item(12235, owner=character, location=ship_id, flag=CARGO_FLAG, singleton=True)
        deadline = time.time() + 300
        while True:
            try:
                client.call_bound(ship[0], "Drop", [(tower, 1)], None, False)
                break
            except p.MachoError as failure:
                if _refusal(failure)[0] != "ShipInWarp" or time.time() > deadline:
                    pytest.fail(f"the tower drop was refused: {_refusal(failure)[0]}: {describe(failure.payload)}")
            client.call_bound(ballpark[0], "CmdStop")
            client.call_bound(ballpark[0], "UpdateStateRequest")
            timed_events(client, 3.0)

        assert dbseed.sql(
            f"select count(*) from {dbseed.schema('SPACE')}.deployed "
            f"where deployable_id = {tower} and kind = 'control_tower' and corporation_id = {corporation}"
        ) == "1", "ship.Drop did not place the corporation's control tower"
        dbseed.sql(
            f"update {dbseed.schema('SPACE')}.deployed set activate_at_ms = {int(time.time() * 1000) - 1}, tower_state = 'online' "
            f"where deployable_id = {tower}"
        )
        label, text = _refused(lambda: client.call("posMgr", "UnanchorStructure", tower))
        assert label == "CustomNotify" and "offline" in text, (label, text)

        dbseed.sql(f"update {dbseed.schema('SPACE')}.deployed set tower_state = 'offline' where deployable_id = {tower}")
        assert client.call("posMgr", "UnanchorStructure", tower) is None
        assert dbseed.sql(
            f"select tower_state from {dbseed.schema('SPACE')}.deployed where deployable_id = {tower}"
        ) == "unanchoring"

        dbseed.sql(
            f"update {dbseed.schema('SCHEDULER')}.jobs set run_at_ms = {int(time.time() * 1000)} "
            f"where kind = 'space.control_tower.unanchor' and key = 'tower:{tower}' and state = 'pending'"
        )
        deadline = time.time() + 90
        while time.time() < deadline and dbseed.sql(
            f"select count(*) from {dbseed.schema('SPACE')}.deployed where deployable_id = {tower}"
        ) != "0":
            timed_events(client, 2.0)
        assert dbseed.sql(f"select count(*) from {dbseed.schema('SPACE')}.deployed where deployable_id = {tower}") == "0"
        assert dbseed.sql(f"select count(*) from {dbseed.schema('SPACE')}.space_items where item_id = {tower}") == "1"
        item = dbseed.sql(
            f"select owner_id, location_id, flag, custom_info from {dbseed.schema('INVENTORY')}.items where id = {tower}"
        )
        assert item.startswith(f"{character}|{LEGAL_SYSTEM}|") and "unanchor" in item.lower(), item

        try:
            assert client.call_bound(ship[0], "Scoop", tower, None) is None
        except p.MachoError as failure:
            pytest.fail(f"the control tower scoop was refused: {_refusal(failure)[0]}: {describe(failure.payload)}")
        changes = timed_events(client, 15.0, until=lambda events: _named(events, "OnItemChange") != [])
        assert _named(changes, "OnItemChange"), "scooping the hull sent no item-change notification"
        scooped = dbseed.sql(
            f"select owner_id, location_id, flag from {dbseed.schema('INVENTORY')}.items where id = {tower}"
        )
        assert scooped == f"{character}|{ship_id}|{CARGO_FLAG}", scooped
        assert dbseed.sql(f"select count(*) from {dbseed.schema('SPACE')}.space_items where item_id = {tower}") == "0"

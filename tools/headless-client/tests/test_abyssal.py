"""Live reference coverage for the Abyssal solo-cruiser entry wire flow."""

from __future__ import annotations

import json
import math
import time
import uuid

import pytest

from evecli import marshal as m
from evecli import packet
from evecli.client import filetime_to_unix
from evecli.describe import describe
from evecli.observe import timed_events, wait_for_events, wait_quietly
from tests import test_login_to_character as base
from tests import test_undock as undock
from tests.abyssal_fixture import (
    MIN_DISTANCE_FROM_DISALLOWED_OBJECTS_METERS,
    PLACEMENT_TYPELIST_CATEGORY_IDS, PLACEMENT_TYPELIST_GROUP_IDS,
    ball_separation, prepare_rs_entry, surface_distance,
)

LIVE = base.LIVE
pytestmark = pytest.mark.skipif(not LIVE, reason="set EVE_LIVE_HOST=host:port to run the live Abyssal scenario")

THORAX_TYPE_ID = 627
TRANQUIL_DARK_FILAMENT = 56132
JITA_SYSTEM_ID = 30000142
JITA_PLANET_ID = 40009080


def _item_id(rows, type_id):
    return next(
        int(row.fields()["itemID"])
        for row in rows
        if int(row.fields()["typeID"]) == type_id
    )


def _integer(value):
    if isinstance(value, int) and not isinstance(value, bool):
        return value
    if isinstance(value, dict):
        for key in ("$long", "$bigunsigned", "$int"):
            number = value.get(key)
            if isinstance(number, int) and not isinstance(number, bool):
                return number
    return None


def _dicts(value):
    if isinstance(value, dict):
        row = value.get("$row")
        if isinstance(row, dict):
            yield row
            yield from _dicts(row)
        pairs = value.get("$dict")
        if isinstance(pairs, list):
            fields = {key: item for key, item in pairs if isinstance(key, str)}
            if fields:
                yield fields
            for pair in pairs:
                if isinstance(pair, (list, tuple)) and len(pair) == 2:
                    yield from _dicts(pair[1])
        for key, item in value.items():
            if key not in ("$dict", "$row"):
                yield from _dicts(item)
    elif isinstance(value, (list, tuple)):
        for item in value:
            yield from _dicts(item)


def _abyssal_system_id(events):
    for event in events:
        if event.get("name") != "$sessionchange":
            continue
        changes = event.get("session", {}).get("changes")
        for fields in _dicts(changes):
            values = fields.get("solarsystemid2")
            values = values.get("$tuple") if isinstance(values, dict) else None
            system_id = _integer(values[-1]) if values else None
            if system_id is not None and system_id != JITA_SYSTEM_ID:
                return system_id
    return None


def _event(events, name):
    return next((event for event in events if event.get("name") == name), None)


def _fields_with_keys(value, required):
    return next((fields for fields in _dicts(value) if required <= fields.keys()), None)


def _warp_stopped(events):
    return any(
        action.get("name") == "Stop"
        for event in events
        if event.get("name") == "DoDestinyUpdate"
        for action in event.get("destiny", {}).get("actions", [])
    )


def test_solo_filament_entry_reports_room_and_expiry():
    _run_abyssal_scenario()


def test_solo_npc_combat_advances_three_rooms_and_returns_to_origin():
    _run_abyssal_scenario(combat=True)



@pytest.mark.parametrize(
    ("game_mode_id", "ship_type_id", "filament_quantity"),
    [(2, 593, 3), (3, 16240, 2)],
    ids=["frigate-mode2-cost3", "destroyer-mode3-cost2"],
)
def test_fleet_manual_trace_activation_starts_entry_and_deadline(
    game_mode_id, ship_type_id, filament_quantity,
):
    _run_abyssal_scenario(
        game_mode_id=game_mode_id, ship_type_id=ship_type_id,
        filament_quantity=filament_quantity,
    )


def _trace_item_id(events):
    markers = {
        _integer(fields.get("itemID"))
        for event in events for fields in _dicts(event.get("args"))
        if _integer(fields.get("groupID")) == 1991
    } - {None}
    assert len(markers) <= 1, f"deployment exposed ambiguous trace markers: {markers}"
    return next(iter(markers), None)


def _run_abyssal_scenario(
    combat=False, game_mode_id=1, ship_type_id=THORAX_TYPE_ID, filament_quantity=1,
):
    run = uuid.uuid4().hex[:10]
    combat_modules = {}
    # SDE339 Jita III (baseline), IV (combat), I/II (manual modes 2/3).
    planet_id = 40009082 if combat else {1: JITA_PLANET_ID, 2: 40009077, 3: 40009078}[game_mode_id]

    def outfit(client, ship_id, station_id):
        combat_modules.update(_prepare_combat_modules(client, ship_id, station_id))

    with base.connect(f"abyssal-{run}") as client:
        if base.REFERENCE:
            character_id, starter_ship_id, _, _ = undock.arrive(
                client, undock.UNDOCK["races"][0], run
            )
            station_manager = client.call(
                "invbroker", "MachoBindObject", (undock.STATION, 15), None
            )[0]
            hangar = client.call_bound(station_manager, "GetInventory", 10004)

            ship_reply = m.plain(client.call("slash", "SlashCmd", f"/ship {ship_type_id}"))
            assert not str(ship_reply).startswith("Unknown command:"), ship_reply
            ship_id = _item_id(
                client.call_bound(hangar, "List"), ship_type_id
            )
            ship_service = client.call(
                "ship", "MachoBindObject", (undock.STATION, 15), None
            )[0]
            client.call_bound(ship_service, "Board", ship_id, starter_ship_id)
            wait_quietly(client, 1.0)
            if combat:
                outfit(client, ship_id, undock.STATION)

            client.call_bound(
                ship_service,
                "Undock",
                ship_id,
                False,
                onlineModules=m.List([]),
            )
            wait_quietly(client, 2.0)
            assert int(client.session.session_init["shipid"]) == ship_id
            solar_reply = m.plain(client.call("slash", "SlashCmd", "/solar Jita"))
            assert not str(solar_reply).startswith("Unknown command:"), solar_reply
            wait_quietly(client, 1.0)
            assert int(client.session.session_init["solarsystemid2"]) == JITA_SYSTEM_ID
            ballpark = client.call(
                "beyonce", "MachoBindObject", JITA_SYSTEM_ID, None
            )[0]
            client.call_bound(ballpark, "UpdateStateRequest")
            client.call_bound(
                ballpark, "CmdWarpToStuff", "item", planet_id, minRange=100_000_000
            )
            warp_events = []
            deadline = time.monotonic() + 150.0
            while time.monotonic() < deadline and not _warp_stopped(warp_events):
                client.call_bound(ballpark, "UpdateStateRequest")
                warp_events.extend(timed_events(client, 3.0))
            assert _warp_stopped(warp_events), "ship did not warp clear of nearby objects"

            filament_reply = m.plain(
                client.call("slash", "SlashCmd", f"/giveitem {TRANQUIL_DARK_FILAMENT} {filament_quantity}")
            )
            grant_events = wait_quietly(client, 1.0)
            filament = next(
                (
                    fields
                    for event in grant_events
                    for fields in _dicts(event.get("args"))
                    if _integer(fields.get("typeID")) == TRANQUIL_DARK_FILAMENT
                ),
                None,
            )
            assert filament is not None, f"filament setup did not create cargo: {filament_reply}"
            filament_id = _integer(filament.get("itemID"))
            assert filament_id is not None
            assert _integer(filament.get("locationID")) == ship_id
            assert _integer(filament.get("flagID")) == 5
            assert _integer(filament.get("ownerID")) == character_id
            assert _integer(filament.get("quantity")) == filament_quantity

        else:
            character_id, ship_id, filament_id = prepare_rs_entry(
                client, run, ship_type_id, TRANQUIL_DARK_FILAMENT,
                JITA_SYSTEM_ID, planet_id,
                prepare_combat=outfit if combat else None,
                filament_quantity=filament_quantity,
                warp_min_range=100_000_000,
            )

        if game_mode_id != 1:
            fleet = client.call("fleetObjectHandler", "CreateFleet")
            assert fleet is not None, "test pilot did not create its real fleet"
            assert m.plain(client.call_bound(
                fleet, "Init", ship_type_id, None, adInfoData=None,
            )) is None
            assert int(client.call_bound(fleet, "GetFleetID")) > 0
            wait_quietly(client, 1.0)

        _check_deployment_position(client, ship_id, planet_id, game_mode_id)

        with pytest.raises(packet.MachoError):
            client.call(
                "abyssalMgr", "AbyssalEntranceDeployment", ship_id, game_mode_id
            )
        try:
            deployment = client.call(
                "abyssalMgr", "AbyssalEntranceDeployment", filament_id, game_mode_id
            )
        except packet.MachoError as error:
            pytest.fail(f"valid solo deployment was rejected: {describe(error.payload)}")
        assert m.plain(deployment) is None

        deployment_events = []
        activation_window = None
        if game_mode_id != 1:
            deployment_events = wait_for_events(
                client, lambda seen: _trace_item_id(seen) is not None, timeout=15.0,
            )
            deployment_events.extend(timed_events(client, 5.0))
            assert _abyssal_system_id(deployment_events) is None, "fleet deployment entered before trace activation"
            assert not {
                "OnEnteringDungeonRoom", "OnAbyssalLocationEntered",
                "OnAddExpiryDurationForAbyssalContent",
            } & {event.get("name") for event in deployment_events}
            trace_id = _trace_item_id(deployment_events)
            assert trace_id is not None
            activation_started = time.time()
            assert m.plain(client.call(
                "abyssalMgr", "AbyssalEntranceGateActivation", trace_id,
            )) is None
            activation_window = (activation_started, time.time())

        session_events = deployment_events + wait_quietly(
            client, 30.0, until=lambda seen: _abyssal_system_id(seen) is not None
        )
        abyssal_system_id = _abyssal_system_id(session_events)
        assert abyssal_system_id is not None, "deployment did not change into an Abyssal system"
        abyssal_binding = client.call(
            "beyonce", "MachoBindObject", abyssal_system_id, None
        )
        assert isinstance(abyssal_binding, (tuple, list)) and abyssal_binding, (
            f"Abyssal ballpark bind did not return a bound object: {describe(abyssal_binding)}"
        )
        abyssal_ballpark = abyssal_binding[0]
        client.call_bound(abyssal_ballpark, "UpdateStateRequest")
        assert m.plain(client.call("abyssalMgr", "ClientIsReady")) is None

        names = {
            "OnEnteringDungeonRoom",
            "OnAbyssalLocationEntered",
            "OnAddExpiryDurationForAbyssalContent",
        }
        notifications = wait_for_events(
            client,
            lambda seen: names <= {event.get("name") for event in seen},
            timeout=30.0,
        )
        events = session_events + notifications
        consumed_filament = [
            item
            for event in events if event.get("name") == "OnItemChange"
            for item in _dicts(event.get("args"))
            if _integer(item.get("itemID")) == filament_id
            and _integer(item.get("locationID")) == 6
        ]
        ordered_names = [event.get("name") for event in events]
        assert ordered_names.index("$sessionchange") < ordered_names.index(
            "OnEnteringDungeonRoom"
        )
        assert ordered_names.index("OnEnteringDungeonRoom") < ordered_names.index(
            "OnAbyssalLocationEntered"
        ) < ordered_names.index("OnAddExpiryDurationForAbyssalContent")

        room_args = undock.args_of(_event(events, "OnEnteringDungeonRoom"))["$tuple"]
        assert len(room_args) == 5, room_args
        assert (_integer(room_args[0]) or 0) > 0
        assert (_integer(room_args[1]) or 0) > 0
        assert isinstance(room_args[2], dict) and len(room_args[2].get("$tuple", [])) == 3
        assert all(isinstance(value, (int, float)) and math.isfinite(value) for value in room_args[2]["$tuple"])
        assert room_args[3:] == [None, None]
        assert _event(events, "OnEnteringDungeonRoom")["idType"] == "shipid"

        entered = _event(events, "OnAbyssalLocationEntered")
        required = {
            "content_id",
            "nebula_graphic_id",
            "difficulty_tier",
            "location_id",
        }
        fields = _fields_with_keys(entered.get("args"), required)
        assert fields is not None, entered
        assert set(fields) == required, fields
        assert all(fields[key] is not None for key in required)
        assert entered["idType"] == "charid"

        expiry = _event(events, "OnAddExpiryDurationForAbyssalContent")
        assert expiry["idType"] == "charid"
        expiry_args = undock.args_of(expiry)["$tuple"]
        assert len(expiry_args) == 1, expiry
        expiry_time = _integer(expiry_args[0])
        assert expiry_time is not None and expiry_time > 116444736000000000, expiry
        filetimes = [expiry_time]
        assert filetime_to_unix(filetimes[0]) > time.time()
        if activation_window is not None:
            # Domain and reference start the 20-minute deadline on first entry.
            deadline_unix = filetime_to_unix(expiry_time)
            assert activation_window[0] + 1200 - 1 <= deadline_unix <= activation_window[1] + 1200 + 1, (
                "fleet deadline did not start at first manual activation",
                activation_window, deadline_unix,
            )
        active_content_id = fields["content_id"]
        assert isinstance(active_content_id, str), fields
        assert str(uuid.UUID(active_content_id)) == active_content_id
        assert _integer(fields["difficulty_tier"]) == 0
        assert _integer(fields["location_id"]) == 1

        client.close()
        time.sleep(1.0)
        with base.connect(f"abyssal-{run}") as reconnected:
            selected = reconnected.call(
                "charUnboundMgr",
                "SelectCharacterID",
                charID=character_id,
                secondChoiceID=None,
                skipTutorial=False,
            )
            assert m.plain(selected) is None
            reconnect_events = wait_quietly(
                reconnected,
                30.0,
                until=lambda seen: _abyssal_system_id(seen) is not None,
            )
            reconnect_system_id = _abyssal_system_id(reconnect_events)
            assert reconnect_system_id is not None, "relogin did not restore the Abyssal solar system"
            restored_binding = reconnected.call(
                "beyonce", "MachoBindObject", reconnect_system_id, None
            )
            assert isinstance(restored_binding, (tuple, list)) and restored_binding, (
                f"relogin ballpark bind did not return a bound object: {describe(restored_binding)}"
            )
            restored_ballpark = restored_binding[0]
            reconnected.call_bound(restored_ballpark, "UpdateStateRequest")
            current_dungeon = m.plain(
                reconnected.call("keeper", "GetCurrentDungeonForCharacter")
            )
            if base.REFERENCE:
                # The recorded EveJS relog query returns None. Keep that reference
                # limitation distinct from authoritative dungeon restoration on RS.
                assert current_dungeon is None or (
                    isinstance(current_dungeon, (list, tuple)) and len(current_dungeon) == 4
                ), current_dungeon
            assert m.plain(reconnected.call("abyssalMgr", "ClientIsReady")) is None
            restored_notifications = wait_for_events(
                reconnected,
                lambda seen: {
                    "OnEnteringDungeonRoom",
                    "OnAbyssalLocationEntered",
                    "OnAddExpiryDurationForAbyssalContent",
                } <= {event.get("name") for event in seen},
                timeout=30.0,
            )
            reconnect_events.extend(restored_notifications)
            reconnect_names = [event.get("name") for event in reconnect_events]
            assert "OnEnteringDungeonRoom" in reconnect_names
            assert "OnAbyssalLocationEntered" in reconnect_names
            assert "OnAddExpiryDurationForAbyssalContent" in reconnect_names
            assert reconnect_names.index("$sessionchange") < reconnect_names.index(
                "OnEnteringDungeonRoom"
            )
            assert reconnect_names.index("OnEnteringDungeonRoom") < reconnect_names.index(
                "OnAbyssalLocationEntered"
            ) < reconnect_names.index("OnAddExpiryDurationForAbyssalContent")
            if not base.REFERENCE:
                assert isinstance(current_dungeon, (list, tuple)) and len(current_dungeon) == 4, (
                    f"active Abyssal relog did not restore keeper dungeon: {current_dungeon}"
                )
                restored_room = undock.args_of(_event(reconnect_events, "OnEnteringDungeonRoom"))["$tuple"]
                assert tuple(current_dungeon[:2]) == tuple(_integer(value) for value in restored_room[:2])
            restored_entered = _event(reconnect_events, "OnAbyssalLocationEntered")
            restored_fields = _fields_with_keys(restored_entered.get("args"), required)
            assert restored_fields is not None, restored_entered
            assert restored_fields["content_id"] == active_content_id
            assert _integer(restored_fields["location_id"]) == 1
            restored_expiry = _event(
                reconnect_events, "OnAddExpiryDurationForAbyssalContent"
            )
            assert restored_expiry["idType"] == "charid"
            restored_expiry_args = undock.args_of(restored_expiry)["$tuple"]
            assert len(restored_expiry_args) == 1, restored_expiry
            restored_expiry_time = _integer(restored_expiry_args[0])
            assert restored_expiry_time is not None, restored_expiry
            restored_filetimes = [restored_expiry_time]
            assert filetime_to_unix(restored_filetimes[0]) > time.time()
            assert restored_filetimes[0] == filetimes[0], "relogin extended the Abyssal deadline"
            assert consumed_filament, "filament consumption did not notify the client inventory"
            if combat:
                _complete_three_rooms(
                    reconnected, ship_id, reconnect_system_id, restored_ballpark,
                    combat_modules, active_content_id, expiry_time,
                )



def _inventory_for(client, scope, item_id):
    if base.REFERENCE:
        # EveJS GetInventoryFromId retains a raw BigInt in its bound context;
        # List/Add/MultiAdd then JSON.stringify it and return None on failure.
        # Its existing GetInventory route normalizes the same actual item ID.
        manager = client.call("invbroker", "MachoBindObject", scope, None)[0]
        bound = client.call_bound(manager, "GetInventory", item_id)
    else:
        bound = client.call(
            "invbroker", "MachoBindObject", scope,
            ("GetInventoryFromId", (item_id, 0), None),
        )[1]
    assert bound is not None, f"inventory {item_id} did not bind"
    return bound


def _prepare_combat_modules(client, ship_id, station_id):
    """Equip this disposable pilot before deployment using existing setup commands."""
    reply = m.plain(client.call("slash", "SlashCmd", "/giveskill me all 5"))
    assert "not found" not in str(reply).lower() and "Unknown command" not in str(reply), reply
    # SDE339: four medium railguns, normal tank and capacitor support.
    fitted_types = [12344] * 4 + [3530, 2048, 11269, 11269, 10190, 12058, 2032, 2032, 2032]
    for type_id in fitted_types:
        reply = m.plain(client.call("slash", "SlashCmd", f"/fit me {type_id}"))
        assert "fitted" in str(reply).lower() and "offline" not in str(reply).lower(), reply
    client.call("slash", "SlashCmd", "/giveitem Antimatter Charge M 4000")
    wait_quietly(client, 1.0)
    dogma = client.call("dogmaIM", "MachoBindObject", (station_id, 15), None)[0]
    info = describe(client.call_bound(dogma, "GetAllInfo", True, True, None))
    modules = {}
    for fields in _dicts(info):
        type_id = _integer(fields.get("typeID"))
        item_id = _integer(fields.get("itemID"))
        if type_id in fitted_types and item_id is not None:
            modules.setdefault(type_id, set()).add(item_id)
    guns = sorted(modules.get(12344, ()))
    assert len(guns) == 4, f"combat setup did not expose four railguns: {modules}"
    manager = client.call("invbroker", "MachoBindObject", (station_id, 15), None)[0]
    hangar = client.call_bound(manager, "GetInventory", 10004)
    ammunition = _item_id(client.call_bound(hangar, "List"), 230)
    client.call_bound(dogma, "LoadAmmo", ship_id, m.List(guns), m.List([ammunition]), station_id)
    cargo = _inventory_for(client, (station_id, 15), ship_id)
    client.call_bound(cargo, "Add", ammunition, station_id, flag=5)
    spare = [row.fields() for row in client.call_bound(cargo, "List", 5)
             if int(row.fields()["typeID"]) == 230]
    assert len(spare) == 1 and int(spare[0]["quantity"]) >= 3000, spare
    assert len(modules.get(3530, ())) == len(modules.get(12058, ())) == 1, modules
    return {"guns": guns, "repair": next(iter(modules[3530])),
            "afterburner": next(iter(modules[12058])), "ship": ship_id,
            "ammunition": int(spare[0]["itemID"])}


def _room_snapshot(client, ballpark):
    client.call_bound(ballpark, "UpdateStateRequest")
    events = timed_events(client, 2.0)
    states = [
        event for event in events
        if any(action.get("name") == "SetState"
               for action in event.get("destiny", {}).get("actions", []))
    ]
    assert states, "UpdateStateRequest did not return a complete ballpark snapshot"
    entities = {}
    for event in states[-1:]:
        for fields in _dicts(event.get("args")):
            item_id = _integer(fields.get("itemID"))
            if item_id is not None and {"typeID", "groupID", "categoryID"} <= fields.keys():
                entities[item_id] = fields
    return entities, events




def _placement_observations(events, ship_id, entities):
    observations = []
    for item_id, fields in entities.items():
        if item_id == ship_id:
            continue
        if (
            _integer(fields["groupID"]) in PLACEMENT_TYPELIST_GROUP_IDS
            or _integer(fields["categoryID"]) in PLACEMENT_TYPELIST_CATEGORY_IDS
        ):
            measured = ball_separation(events, ship_id, item_id)
            record = {"itemID": item_id, "typeID": _integer(fields["typeID"]), **measured}
            observations.append(record)
            assert measured["center_m"] > MIN_DISTANCE_FROM_DISALLOWED_OBJECTS_METERS, (
                "setup remains inside a source typeList50 placement restriction", record
            )
    return observations


def _check_deployment_position(client, ship_id, planet_id, game_mode_id):
    bound = client.call("beyonce", "MachoBindObject", JITA_SYSTEM_ID, None)
    assert isinstance(bound, (tuple, list)) and bound
    entities, events = _room_snapshot(client, bound[0])
    observations = _placement_observations(events, ship_id, entities)
    # The SDE planet is a measured position anchor, not classified as a blocker.
    anchor = ball_separation(events, ship_id, planet_id)
    print("ABYSSAL_DEPLOY_CLEARANCE " + json.dumps({
        "mode": game_mode_id, "shipID": ship_id, "planetID": planet_id,
        "source_min_center_m": MIN_DISTANCE_FROM_DISALLOWED_OBJECTS_METERS,
        "observed_blocker_count": len(observations),
        "closest_observed_blocker": min(observations, key=lambda item: item["center_m"], default=None),
        "planet_anchor": anchor,
    }, sort_keys=True), flush=True)


def _has_item_id(value, item_id):
    if _integer(value) == item_id:
        return True
    if isinstance(value, dict):
        return any(_has_item_id(child, item_id) for child in value.values())
    if isinstance(value, (list, tuple)):
        return any(_has_item_id(child, item_id) for child in value)
    return False


def _destroyed_on_wire(events, target):
    actions = [
        action for event in events
        for action in event.get("destiny", {}).get("actions", [])
        if _has_item_id(action.get("args"), target)
    ]
    names = {action.get("name") for action in actions}
    return {"TerminalPlayDestructionEffect", "RemoveBalls"} <= names


def _shoot_entity(client, ballpark, dogma, modules, target, expiry_time):
    """Use ordinary movement, target locks and loaded guns; never alter NPC health."""
    client.call_bound(ballpark, "CmdSetSpeedFraction", 1.0)
    client.call_bound(ballpark, "CmdFollowBall", target, 2500)
    client.call_bound(
        dogma, "LoadAmmo", modules["ship"], m.List(modules["guns"]),
        m.List([modules["ammunition"]]), modules["ship"],
    )
    # SDE339 type12344 reloadTime=5000ms; drain the real reload notifications.
    timed_events(client, 6.0)
    client.call_bound(dogma, "AddTarget", target)
    deadline = min(time.monotonic() + 90.0, time.monotonic() + filetime_to_unix(expiry_time) - time.time())
    while time.monotonic() < deadline:
        if target in [int(value) for value in client.call_bound(dogma, "GetTargets")]:
            break
        timed_events(client, 1.0)
    else:
        pytest.fail(f"could not lock real Abyssal entity {target}")
    shot_events = []
    arming_events = []
    for gun in modules["guns"]:
        client.call_bound(dogma, "Activate", gun, "targetAttack", target, 1000)
        armed = timed_events(client, 0.5)
        arming_events.extend(armed)
        shot_events.extend(event for event in armed if event.get("name") == "OnDamageMessage")
        if _destroyed_on_wire(arming_events, target):
            break
    deadline = min(time.monotonic() + 240.0, time.monotonic() + filetime_to_unix(expiry_time) - time.time())
    while time.monotonic() < deadline:
        entities, events = _room_snapshot(client, ballpark)
        shot_events.extend(event for event in events if event.get("name") == "OnDamageMessage")
        if target not in entities:
            assert any(
                fields.get("attackType") == "me" and _integer(fields.get("target")) == target
                for event in shot_events for fields in _dicts(event.get("args"))
            ), f"entity {target} disappeared without this pilot firing a recorded shot"
            return
    pytest.fail(f"normal weapons did not destroy Abyssal entity {target} before the combat deadline")



def _approach_static_entity(client, ballpark, ship_id, item_id, expiry_time):
    client.call_bound(ballpark, "CmdSetSpeedFraction", 1.0)
    client.call_bound(ballpark, "CmdFollowBall", item_id, 0)
    deadline = min(time.monotonic() + 180.0, time.monotonic() + filetime_to_unix(expiry_time) - time.time())
    while time.monotonic() < deadline:
        _, events = _room_snapshot(client, ballpark)
        if surface_distance(events, ship_id, item_id) <= 1000:
            return
    pytest.fail(f"ordinary approach did not reach Abyssal object {item_id}")


def _loot_destroyed_cache(client, ballpark, ship_id, system_id, wreck_id, expiry_time):
    _approach_static_entity(client, ballpark, ship_id, wreck_id, expiry_time)
    opened = _inventory_for(client, (system_id, 5), wreck_id)
    inside = [row.fields() for row in client.call_bound(opened, "List")]
    assert inside, "destroyed main cache exposed no loot in its actual inventory"
    cargo = _inventory_for(client, (system_id, 5), ship_id)

    def totals(rows):
        result = {}
        for row in rows:
            type_id = int(row["typeID"])
            result[type_id] = result.get(type_id, 0) + int(row["quantity"])
        return result

    before = totals([row.fields() for row in client.call_bound(cargo, "List", 5)])
    expected = totals(inside)
    client.call_bound(cargo, "MultiAdd", m.List([int(row["itemID"]) for row in inside]), wreck_id, flag=5)
    after = totals([row.fields() for row in client.call_bound(cargo, "List", 5)])
    assert all(after.get(type_id, 0) == before.get(type_id, 0) + quantity
               for type_id, quantity in expected.items()), (before, expected, after)
    assert not list(client.call_bound(opened, "List")), "looted cache still held the transferred items"



def _is_inactive_support_error(error, module_name):
    envelope = _fields_with_keys(error, {"msg", "dict"})
    notification = _fields_with_keys(error, {"notify"})
    return (
        envelope is not None and envelope["msg"] == "CustomNotify"
        and notification is not None
        and notification["notify"] == f"{module_name} is not active."
    )


def _stop_support_module(client, dogma, module_id, effect, module_name):
    result, error = undock.outcome(
        lambda: client.call_bound(dogma, "Deactivate", module_id, effect)
    )
    if error is None:
        assert m.plain(result) == 1
    else:
        # Automatic capacitor/cycle termination may have already stopped it.
        # Only the actual module's exact inactive response is an acceptable stop.
        assert _is_inactive_support_error(error, module_name), error


def _complete_three_rooms(client, ship_id, system_id, ballpark, modules, content_id, expiry_time):
    for room in (1, 2, 3):
        dogma = client.call("dogmaIM", "MachoBindObject", (system_id, 5), None)[0]
        client.call_bound(dogma, "GetAllInfo", True, True, None)
        client.call_bound(dogma, "Activate", modules["repair"], "armorRepair", ship_id, 1000)
        client.call_bound(dogma, "Activate", modules["afterburner"], "moduleBonusAfterburner", ship_id, 1000)
        entities, _ = _room_snapshot(client, ballpark)
        rats = [
            item_id for item_id, fields in entities.items()
            if _integer(fields["categoryID"]) == 11 and _integer(fields["groupID"]) != 2009
        ]
        assert rats, f"room {room} exposed no actual combat NPCs"
        for rat in rats:
            _shoot_entity(client, ballpark, dogma, modules, rat, expiry_time)
        entities, _ = _room_snapshot(client, ballpark)
        assert not [
            item_id for item_id, fields in entities.items()
            if _integer(fields["categoryID"]) == 11 and _integer(fields["groupID"]) != 2009
        ], f"room {room} still has living combat NPCs"
        caches = [item_id for item_id, fields in entities.items() if _integer(fields["groupID"]) == 2009]
        assert len(caches) == 1, f"room {room} did not expose its damageable main cache"
        _shoot_entity(client, ballpark, dogma, modules, caches[0], expiry_time)
        entities, _ = _room_snapshot(client, ballpark)
        wrecks = [
            item_id for item_id, fields in entities.items()
            if _integer(fields["typeID"]) in (48120, 49692)
        ]
        assert len(wrecks) == 1, f"room {room} cache destruction did not create its real wreck"
        _loot_destroyed_cache(
            client, ballpark, ship_id, system_id, wrecks[0], expiry_time
        )
        gates = [
            item_id for item_id, fields in entities.items()
            if _integer(fields["groupID"]) == 366 and fields.get("isAbyssGateOpen") is True
        ]
        assert len(gates) == 1, f"room {room} gate did not open after real NPC combat"
        gate = gates[0]
        _approach_static_entity(
            client, ballpark, ship_id, gate, expiry_time
        )
        _stop_support_module(client, dogma, modules["repair"], "armorRepair", "Medium Armor Repairer II")
        _stop_support_module(client, dogma, modules["afterburner"], "moduleBonusAfterburner", "10MN Afterburner II")
        method = "AbyssalGateActivation" if room < 3 else "AbyssalEndGateActivation"
        assert m.plain(client.call("abyssalMgr", method, gate)) is None
        transition = wait_quietly(
            client, 30.0,
            until=lambda events: _event(events, "OnAbyssalLocationExited") is not None
            and _event(events, "OnAbyssalLocationEntered" if room < 3 else "$sessionchange") is not None,
        )
        if room == 3:
            # The return receipt follows the ordinary public ballpark bootstrap.
            # EveJS otherwise expires its pending return with no Finished notification.
            returned = client.call("beyonce", "MachoBindObject", JITA_SYSTEM_ID, None)
            assert isinstance(returned, (tuple, list)) and returned
            client.call_bound(returned[0], "UpdateStateRequest")
            transition += wait_quietly(
                client, 15.0,
                until=lambda events: _event(events, "OnAbyssalContentFinished") is not None,
            )
        names = [event.get("name") for event in transition]
        assert "OnAddExpiryDurationForAbyssalContent" not in names, "room transfer extended the deadline"
        if room < 3:
            assert names.index("OnEnteringDungeonRoom") < names.index("OnExitingDungeon")
            assert names.index("OnExitingDungeon") < names.index("OnAbyssalLocationEntered") < names.index("OnAbyssalLocationExited")
            fields = _fields_with_keys(_event(transition, "OnAbyssalLocationEntered")["args"], {"content_id", "location_id"})
            assert fields["content_id"] == content_id and _integer(fields["location_id"]) == room + 1
            assert m.plain(client.call("abyssalMgr", "ClientIsReady")) is None
            retry_events = timed_events(client, 1.0)
            assert not _event(retry_events, "OnAddExpiryDurationForAbyssalContent"), (
                "ClientIsReady retry resent the transfer deadline"
            )
        else:
            assert names.index("OnExitingDungeon") < names.index("OnAbyssalLocationExited") < names.index("$sessionchange") < names.index("OnAbyssalContentFinished")
            finished = _event(transition, "OnAbyssalContentFinished")
            assert finished["idType"] == "charid"
            args = undock.args_of(finished)["$tuple"]
            assert len(args) == 2 and args[0] == content_id
            assert _integer(args[1]) == 11, finished
            assert int(client.session.session_init["solarsystemid2"]) == JITA_SYSTEM_ID

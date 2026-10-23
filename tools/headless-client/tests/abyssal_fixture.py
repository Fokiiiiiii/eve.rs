"""Entry-only setup using existing ship and inventory authorities on eve.rs."""

from __future__ import annotations

import math
import re
import time

from evecli import marshal as m
from evecli.observe import (
    _ego_ball_from_state, _set_state_fields, ego_state, timed_events, wait_quietly,
)
from tests import test_login_to_character as base
from tests import test_undock as undock



# Pinned EveJS MIN_DISTANCE_FROM_DISALLOWED_OBJECTS_METERS and SDE339 typeLists[50].
MIN_DISTANCE_FROM_DISALLOWED_OBJECTS_METERS = 1_000_000
PLACEMENT_TYPELIST_GROUP_IDS = frozenset((10, 925, 6, 995, 366, 988, 1991))
PLACEMENT_TYPELIST_CATEGORY_IDS = frozenset((65, 40, 23, 3, 22))


def prepare_rs_entry(client, run, ship_type_id, filament_type_id, system_id, planet_id, prepare_combat=None, filament_quantity=1, warp_min_range=100_000_000):
    race = undock.UNDOCK["races"][0]
    recorded_run = re.search(r"(\d{6,})$", race["create"]["call"][2][0]).group(1)
    service, method, arguments = race["create"]["call"]
    character_id = int(
        client.call(service, method, *[base.revive(value, run, {}, recorded_run) for value in arguments])
    )
    assert client.call(
        "charUnboundMgr", "SelectCharacterID", character_id, None, False
    ) is None
    station_id = int(client.session.session_init["stationid"])
    starter_ship_id = int(client.session.session_init["shipid"])
    assert int(client.session.session_init["solarsystemid2"]) == system_id, (
        "the isolated RS host must start test characters in Jita"
    )
    manager = client.call("invbroker", "MachoBindObject", (station_id, 15), None)[0]
    hangar = client.call_bound(manager, "GetInventory", 10004)

    def find_item(type_id):
        rows = client.call_bound(hangar, "List")
        found = [row.fields() for row in rows if int(row.fields()["typeID"]) == type_id]
        assert len(found) == 1, f"setup expected one type {type_id}, got {len(found)}"
        assert int(found[0]["ownerID"]) == character_id
        return int(found[0]["itemID"])

    ship_reply = m.plain(client.call("slash", "SlashCmd", f"/giveitem {ship_type_id}"))
    assert "was added to your station hangar" in ship_reply, ship_reply
    ship_id = find_item(ship_type_id)
    ship_service = client.call("ship", "MachoBindObject", (station_id, 15), None)[0]
    client.call_bound(ship_service, "Board", ship_id, starter_ship_id)
    wait_quietly(client, 1.0)

    if prepare_combat is not None:
        prepare_combat(client, ship_id, station_id)

    filament_reply = m.plain(client.call("slash", "SlashCmd", f"/giveitem {filament_type_id} {filament_quantity}"))
    assert "was added to your station hangar" in filament_reply, filament_reply
    filament_id = find_item(filament_type_id)
    cargo = client.call(
        "invbroker", "MachoBindObject", (station_id, 15),
        ("GetInventoryFromId", (ship_id, 0), None),
    )[1]
    client.call_bound(cargo, "Add", filament_id, station_id, qty=filament_quantity, flag=5)
    loaded = [
        row.fields() for row in client.call_bound(cargo, "List", 5)
        if int(row.fields()["typeID"]) == filament_type_id
    ]
    assert len(loaded) == 1
    filament_id = int(loaded[0]["itemID"])
    assert int(loaded[0]["locationID"]) == ship_id
    assert int(loaded[0]["flagID"]) == 5
    assert int(loaded[0]["ownerID"]) == character_id
    assert int(loaded[0]["quantity"]) == filament_quantity

    client.call_bound(ship_service, "Undock", ship_id, False, onlineModules=m.List([]))
    wait_quietly(client, 2.0)
    assert int(client.session.session_init["shipid"]) == ship_id
    assert int(client.session.session_init["solarsystemid2"]) == system_id
    ballpark = client.call("beyonce", "MachoBindObject", system_id, None)[0]
    client.call_bound(ballpark, "UpdateStateRequest")
    client.call_bound(ballpark, "CmdWarpToStuff", "item", planet_id, minRange=warp_min_range)
    deadline = time.monotonic() + 150.0
    stopped = False
    while time.monotonic() < deadline and not stopped:
        client.call_bound(ballpark, "UpdateStateRequest")
        events = timed_events(client, 3.0)
        stopped = any(
            action.get("name") == "Stop"
            for event in events if event.get("name") == "DoDestinyUpdate"
            for action in event.get("destiny", {}).get("actions", [])
        )
    assert stopped, "setup ship did not finish its normal warp"
    wait_quietly(client, 0.5)
    return character_id, ship_id, filament_id


def ball_separation(events, ship_id, target_id):
    """Read actual centers and radii from the latest complete Destiny state."""
    pilot = ego_state(events, ship_id)
    assert pilot is not None, "snapshot did not expose the pilot's actual ball"
    target = None
    for event in events:
        for action in event.get("destiny", {}).get("actions", []):
            if action.get("name") == "SetState":
                state = _set_state_fields(action.get("args")).get("state")
                target = _ego_ball_from_state(state, target_id)
    assert target is not None, f"snapshot did not expose target ball {target_id}"
    center = math.dist(pilot[0], target[0])
    return {
        "center_m": center,
        "surface_m": max(0.0, center - pilot[1] - target[1]),
        "pilot_radius_m": pilot[1], "target_radius_m": target[1],
        "pilot_position": list(pilot[0]), "target_position": list(target[0]),
    }


def surface_distance(events, ship_id, target_id):
    return ball_separation(events, ship_id, target_id)["surface_m"]

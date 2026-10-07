"""What a client sees happen around a call: the events the server sends, and where things are afterwards."""

from __future__ import annotations

import time

from . import marshal as m
from . import packet as p
from .client import Client
from .describe import buffer_bytes, decode_marshaled, describe, destiny_state_header


def _sequence(value):
    if isinstance(value, (list, tuple)):
        return list(value)
    if isinstance(value, dict) and isinstance(value.get("$tuple"), list):
        return value["$tuple"]
    return None


def _plain_name(value):
    if isinstance(value, str):
        return value
    if isinstance(value, dict):
        return value.get("$token") or value.get("$unicode")
    return None


def _notification_body(packet):
    try:
        body = packet.payload[0][1]
    except (IndexError, TypeError):
        return {
            "$decodeError": "missing-notification-payload",
            "payload": describe(getattr(packet, "payload", None)),
        }
    data = getattr(body, "value", body)
    try:
        return open_buffers(describe(m.decode(bytes(data))))
    except Exception as error:
        return {"$decodeError": type(error).__name__, "payload": describe(packet.payload)}


def _notification_event(packet):
    raw = packet.raw if isinstance(packet.raw, (tuple, list)) else ()
    address = raw[2] if len(raw) > 2 else None
    fields = _sequence(getattr(address, "args", address))
    body = _notification_body(packet)
    address_kind = _integer(fields[0]) if fields else None
    if address_kind == p.ADDR_ANY:
        call = _sequence(body)
        if call and len(call) >= 3 and _integer(call[0]) == 1:
            service = describe(fields[1]) if len(fields) > 1 else None
            method = _plain_name(call[1]) or call[1]
            event = {
                "name": method,
                "idType": None,
                "args": call[2],
                "kind": "service-notification",
                "service": service,
                "method": method,
            }
            if len(call) > 3:
                event["kwargs"] = call[3]
        else:
            event = {
                "name": "$short",
                "idType": None,
                "args": {"address": describe(address), "body": body},
            }
    elif fields is None or len(fields) < 4:
        event = {
            "name": "$short",
            "idType": None,
            "args": {"address": describe(address), "body": body},
        }
    else:
        event = {"name": describe(fields[1]), "idType": describe(fields[3]), "args": body}
    if address is not None:
        event["address"] = describe(address)
    if len(raw) > 1:
        event["source"] = describe(raw[1])
    if len(raw) > 3:
        event["userID"] = describe(raw[3])
    if getattr(packet, "named_payload", None) is not None:
        event["namedPayload"] = describe(packet.named_payload)
    if event.get("kind") == "service-notification":
        return event
    if _plain_name(event["name"]) == "DoDestinyUpdate":
        event["kind"] = "destiny-update"
        event["destiny"] = _destiny_update_view(event["args"])
    else:
        event["kind"] = "notification"
    return event


def _session_event(packet, initial: bool = False):
    payload = _sequence(getattr(packet, "payload", None)) or []
    session = {"sid": describe(payload[0]) if payload else None}
    if initial:
        session.update({
            "sessionType": describe(payload[1]) if len(payload) > 1 else None,
            "initialState": describe(payload[2]) if len(payload) > 2 else None,
        })
        name = "$sessioninitialstate"
        kind = "session-initial-state"
    else:
        change = _sequence(payload[1]) if len(payload) > 1 else None
        session.update({
            "changeMarker": describe(change[0]) if change else None,
            "changes": describe(change[1]) if change and len(change) > 1 else None,
            "nodesOfInterest": describe(payload[2]) if len(payload) > 2 else None,
        })
        name = "$sessionchange"
        kind = "session-change"
    return {
        "name": name,
        "idType": None,
        "args": describe(getattr(packet, "raw", None)),
        "kind": kind,
        "session": session,
    }


def _notification_call_args(args):
    values = _sequence(args)
    if values is None:
        return None
    if len(values) >= 2 and values[0] == 0:
        values = _sequence(values[1]) or []
    if len(values) >= 2 and values[0] == 1:
        return _sequence(values[1]) or []
    return values


def _expand_destiny_actions(encoded):
    actions = []
    for update in _sequence(encoded) or []:
        pair = _sequence(update)
        if pair is None or len(pair) != 2:
            actions.append({"raw": update})
            continue
        stamp, payload = pair
        call = _sequence(payload)
        if call is None or len(call) < 2:
            actions.append({"stamp": stamp, "raw": payload})
            continue
        name = _plain_name(call[0]) or call[0]
        arguments = call[1]
        if name == "PackagedAction" and isinstance(arguments, dict):
            packed = arguments.get("$marshalled")
            if packed is not None:
                actions.extend(_expand_destiny_actions(packed))
                continue
        actions.append({"stamp": stamp, "name": name, "args": arguments})
    return actions


def _set_state_fields(arguments):
    values = _sequence(arguments)
    bag = values[0] if values else arguments
    if isinstance(bag, dict) and "$object" in bag:
        bag = bag.get("args")
    if isinstance(bag, dict) and isinstance(bag.get("$dict"), list):
        return {
            (_plain_name(pair[0]) or str(pair[0])): pair[1]
            for pair in bag["$dict"]
            if isinstance(pair, (list, tuple)) and len(pair) == 2
        }
    return {}


def _destiny_update_view(args):
    payload = _notification_call_args(args)
    if not payload:
        return {"actions": [], "raw": args}
    actions = _expand_destiny_actions(payload[0])
    view = {"actions": actions}
    if len(payload) > 1:
        view["waitForBubble"] = payload[1]
    if len(payload) > 2:
        view["dogmaMessages"] = payload[2]
    resets = []
    for action in actions:
        if action.get("name") != "SetState":
            continue
        fields = _set_state_fields(action.get("args"))
        resets.append({
            "stamp": action.get("stamp"),
            "ego": fields.get("ego"),
            "stateHeader": destiny_state_header(fields.get("state")),
            "slimCount": len(_sequence(fields.get("slims")) or []),
        })
    if resets:
        view["ballparkResets"] = resets
    return view


def notifications_of(client: Client) -> list[dict]:
    """The named notifications emitted since the client last looked."""
    client.ping()
    out = [
        _notification_event(packet)
        for packet in client.notifications
        if packet.command == p.NOTIFICATION
    ]
    client.notifications.clear()
    return out


def events_of(client: Client) -> list[dict]:
    """Notifications and session state received since the previous read, in wire order."""
    client.ping()
    out = []
    for packet in client.notifications:
        if packet.command == p.NOTIFICATION:
            out.append(_notification_event(packet))
        elif packet.command == p.SESSIONCHANGENOTIFICATION:
            out.append(_session_event(packet))
        elif packet.command == p.SESSIONINITIALSTATENOTIFICATION:
            out.append(_session_event(packet, initial=True))
    client.notifications.clear()
    return out


def open_buffers(described):
    """A description with nested marshal buffers decoded for packet comparison."""
    if isinstance(described, list):
        return [open_buffers(value) for value in described]
    if isinstance(described, dict):
        if set(described) == {"$buffer"}:
            data = buffer_bytes(described)
            if data is not None and data[:1] == b"~":
                try:
                    return {"$marshalled": open_buffers(decode_marshaled(data))}
                except Exception:
                    return described
            return described
        return {key: open_buffers(value) for key, value in described.items()}
    return described


def hangar_and_ship(client: Client, mgr, ship_id: int) -> dict:
    """What the station hangar and the dogma manager say is where, after a change: the hangar's rows and the ship's info."""
    hangar = client.call_bound(mgr, "GetInventory", 10004)
    rows = [{k: v for k, v in describe(r)["$row"].items() if k in ("itemID", "typeID", "locationID", "flagID", "quantity")} for r in client.call_bound(hangar, "List")]
    bound = client.call("dogmaIM", "MachoBindObject", (60015249, 15), ("GetAllInfo", (True, True, None), None))
    info = {k: v for k, v in describe(bound[1])["args"]["$dict"]}
    fitted = []
    for key, item in info["shipInfo"]["$dict"]:
        row = {k: v for k, v in item["args"]["$dict"]}["invItem"]["$row"]
        fitted.append({k: row[k] for k in ("itemID", "typeID", "locationID", "flagID")})
    return {"hangar": rows, "shipInfo": fitted}


def ship_of(client: Client) -> int:
    """The item id of the ship a character docked in its station has: the one the hangar lists."""
    manager = client.call("invbroker", "MachoBindObject", (60015249, 15), None)[0]
    hangar = client.call_bound(manager, "GetInventory", 10004)
    return int(client.call_bound(hangar, "List")[0].fields()["itemID"])


def queue_of(described) -> list:
    """A queue as the client reads it: each entry as its fields (`{"trainingTypeID": ..}`), in order."""
    return [{k: v for k, v in entry["args"]["$dict"]} for entry in described]


def training_view(who: Client) -> dict:
    """What the skill window shows after a step: the queue, the totals, and the skills that are in training or were changed."""
    info = describe(who.call("skillHandler", "GetMySkillInfo"))
    fields = {k: v for k, v in info["args"]["$dict"]}
    skills = {}
    for type_id, entry in fields["skills"]["$dict"]:
        header = entry["$objectex"]["$tuple"]
        state = {k: v for k, v in header[2]["$dict"]} if len(header) > 2 else {}
        skills[type_id] = {"level": header[1]["$tuple"][1], "points": header[1]["$tuple"][2], "inTraining": state.get("inTraining")}
    return {"queue": queue_of(fields["queue"]), "skillPoints": fields["skillPoints"], "freeSkillPoints": fields["freeSkillPoints"], "skills": skills}


def wait_for_events(who: Client, done, timeout: float = 15.0) -> list:
    """The events the server sends until `done(all so far)` is true (the reference finishes a level in the background)."""
    seen: list = []
    deadline = time.time() + timeout
    while time.time() < deadline:
        seen += notifications_of(who)
        if done(seen):
            break
        time.sleep(0.2)
    return seen


def wait_quietly(client: Client, seconds: float, until=None) -> list:
    """The events that arrive in `seconds` (or until `until(all so far)` is true)."""
    seen: list = []
    deadline = time.time() + seconds
    while time.time() < deadline:
        seen += events_of(client)
        if until is not None and until(seen):
            break
        time.sleep(0.25)
    return seen


def timed_events(who: Client, seconds: float, until=None) -> list:
    """`wait_quietly` with the seconds since the call each event arrived at (`at`): what a ship that flies somewhere takes to get there."""
    started = time.time()
    seen: list = []
    while time.time() - started < seconds:
        for event in events_of(who):
            event["at"] = round(time.time() - started, 2)
            seen.append(event)
        if until is not None and until(seen):
            break
        time.sleep(0.25)
    return seen


def _integer(value):
    if isinstance(value, int) and not isinstance(value, bool):
        return value
    if isinstance(value, dict):
        value = value.get("$long", value.get("$bigunsigned"))
        if isinstance(value, int) and not isinstance(value, bool):
            return value
    return None


def _ego_ball_from_state(state, ship_id: int):
    import math
    import struct

    data = buffer_bytes(state)
    header = destiny_state_header(data)
    if data is None or header is None or header["packetType"] != 0:
        return None
    identity = struct.pack("<q", ship_id)
    offset = 5  # Destiny stream header: packet type byte and little-endian stamp.
    while True:
        at = data.find(identity, offset)
        if at < 0:
            return None
        offset = at + 1
        if at + 37 > len(data):
            continue
        mode = data[at + 8]
        radius = struct.unpack_from("<f", data, at + 9)[0]
        position = struct.unpack_from("<3d", data, at + 13)
        if (
            mode <= 15
            and math.isfinite(radius)
            and radius >= 0
            and all(math.isfinite(v) for v in position)
        ):
            return position, radius


def ego_state(events: list, ship_id: int):
    """The ego ball from the latest official SetState Destiny action, or None."""
    state = None
    for event in events:
        if event.get("name") != "DoDestinyUpdate":
            continue
        destiny = event.get("destiny")
        if not isinstance(destiny, dict):
            destiny = _destiny_update_view(event.get("args"))
        for action in destiny.get("actions", []):
            if action.get("name") != "SetState":
                continue
            state = None
            fields = _set_state_fields(action.get("args"))
            ego = _integer(fields.get("ego"))
            if ego is not None and ego != ship_id:
                continue
            state = _ego_ball_from_state(fields.get("state"), ship_id)
    return state

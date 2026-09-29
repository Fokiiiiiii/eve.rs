"""What a client sees happen around a call: the events the server sends, and where things are afterwards."""

from __future__ import annotations

import time

from . import marshal as m
from . import packet as p
from .client import Client
from .describe import describe


def notifications_of(client: Client) -> list[dict]:
    """The named events the server has sent the client since it last looked, decoded: the packet's broadcast name and id
    type, and the arguments the event carries (they travel as a marshalled stream inside the packet)."""
    client.ping()
    out = []
    for n in client.notifications:
        if n.command != p.NOTIFICATION:
            continue
        address = n.raw[2]
        fields = address.args if hasattr(address, "args") else address
        body = n.payload[0][1]
        inner = m.decode(bytes(body.value if hasattr(body, "value") else body))
        out.append({"name": describe(fields[1]), "idType": describe(fields[3]), "args": open_buffers(describe(inner))})
    client.notifications.clear()
    return out


def open_buffers(described):
    """A description with every buffer that holds a marshalled value replaced by that value, so the events that carry
    one (the brain) can be compared by what is in them: `{"$marshalled": description}`."""
    if isinstance(described, list):
        return [open_buffers(v) for v in described]
    if isinstance(described, dict):
        if set(described) == {"$buffer"}:
            data = bytes.fromhex(described["$buffer"])
            if data[:1] == b"\x7e":
                try:
                    return {"$marshalled": open_buffers(describe(m.decode(data)))}
                except Exception:
                    return described
            return described
        return {k: open_buffers(v) for k, v in described.items()}
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

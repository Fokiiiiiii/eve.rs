"""MachoNet packets and addresses.

A packet is a marshal `PyObject(<guid>, state)` whose state is the tuple

    (command, source, destination, userID, payload, namedPayload, contextKey, ...)

padded with nulls to 14 elements. An address is a tuple whose shape depends on
its kind (the field order of the client's `machoNetAddress.__getstate__`):

    node       (1, nodeID, service, callID)
    client     (2, clientID, callID, service)
    broadcast  (4, broadcastID, narrowcast, idtype)
    any        (8, service, callID)
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import Any, Optional

from . import marshal as m

# Packet types (MACHONETMSG_TYPE)
AUTHENTICATION_REQ = 0
AUTHENTICATION_RSP = 1
IDENTIFICATION_REQ = 2
IDENTIFICATION_RSP = 3
CALL_REQ = 6
CALL_RSP = 7
TRANSPORTCLOSED = 8
RESOLVE_REQ = 10
RESOLVE_RSP = 11
NOTIFICATION = 12
ERRORRESPONSE = 15
SESSIONCHANGENOTIFICATION = 16
SESSIONINITIALSTATENOTIFICATION = 18
PING_REQ = 20
PING_RSP = 21

ADDR_NODE = 1
ADDR_CLIENT = 2
ADDR_BROADCAST = 4
ADDR_ANY = 8

_GUID = {
    CALL_REQ: "carbon.common.script.net.machoNetPacket.CallReq",
    PING_REQ: "carbon.common.script.net.machoNetPacket.PingReq",
    NOTIFICATION: "carbon.common.script.net.machoNetPacket.Notification",
}

_STATE_LENGTH = 14  # (command, source, destination, userID, payload, oob, contextKey, ... 14 in all)


def any_address(service: Optional[str] = None, call_id: Optional[int] = None) -> tuple:
    return (ADDR_ANY, service, call_id)


def client_address(client_id, call_id: Optional[int] = None, service: Optional[str] = None) -> tuple:
    return (ADDR_CLIENT, client_id, call_id, service)


@dataclass
class Address:
    kind: int
    service: Optional[str] = None
    call_id: Optional[int] = None
    node_id: Any = None
    client_id: Any = None

    @staticmethod
    def parse(value) -> "Address":
        if isinstance(value, m.Object):
            value = value.args
        if not isinstance(value, tuple) or not value or not isinstance(value[0], int):
            return Address(0)

        def at(index):
            return value[index] if len(value) > index else None

        def text(item):
            if isinstance(item, (bytes, bytearray)):
                return bytes(item).decode("utf-8", "replace")
            return str(item) if item is not None else None

        kind = int(value[0])
        if kind == ADDR_NODE:
            return Address(kind, text(at(2)), at(3), node_id=at(1))
        if kind == ADDR_CLIENT:
            return Address(kind, text(at(3)), at(2), client_id=at(1))
        if kind == ADDR_ANY:
            return Address(kind, text(at(1)), at(2))
        return Address(kind)


@dataclass
class Packet:
    command: int
    source: Address
    destination: Address
    user_id: Any
    payload: Any
    named_payload: Any = None
    context_key: Any = None
    raw: Any = None

    @staticmethod
    def parse(value) -> "Packet":
        state = value.args if isinstance(value, m.Object) else value
        if not isinstance(state, tuple) or len(state) < 6:
            raise m.MarshalError(f"not a MachoNet packet: {type(state).__name__}")
        return Packet(
            command=int(state[0]),
            source=Address.parse(state[1]),
            destination=Address.parse(state[2]),
            user_id=state[3],
            payload=state[4],
            named_payload=state[5],
            context_key=state[6] if len(state) > 6 else None,
            raw=state,
        )


def build_packet(command: int, source: tuple, destination: tuple, user_id, payload, named_payload=None,
                 context_key=None) -> m.Object:
    state = [command, source, destination, user_id, payload, named_payload, context_key]
    state += [None] * (_STATE_LENGTH - len(state))
    return m.Object(_GUID.get(command, "carbon.common.script.net.machoNetPacket.Packet"), tuple(state))


def call_payload(method: str, args: tuple = (), kwargs: Optional[dict] = None) -> tuple:
    """The payload of a CallReq: (isBound, SubStream((1, method, args, kwargs)))."""
    keywords = m.Dict(list((kwargs or {}).items()))
    return ((0, m.SubStream(value=(1, method, tuple(args), keywords if keywords else None))),)


def result_of(packet: Packet):
    """The value a CALL_RSP carries: its payload is (SubStream(result),) or (result,)."""
    payload = packet.payload
    if isinstance(payload, tuple) and payload:
        first = payload[0]
        return first.value if isinstance(first, m.SubStream) else first
    return payload


class MachoError(Exception):
    """An ERRORRESPONSE from the server."""

    def __init__(self, code, payload=None, original_command=None):
        super().__init__(f"macho error {code!r}")
        self.code = code
        self.payload = payload
        self.original_command = original_command

    @staticmethod
    def from_packet(packet: Packet) -> "MachoError":
        body = packet.payload
        if isinstance(body, tuple) and len(body) >= 3:
            detail = body[2]
            if isinstance(detail, tuple) and detail and isinstance(detail[0], m.SubStream):
                detail = detail[0].value
            return MachoError(body[1], detail, body[0])
        return MachoError(None, body)

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

_ADDRESS_GUID = "macho.MachoAddress"
_GENERIC_PACKET_GUID = "macho.MachoPacket"

# These are the concrete GUIDs used by the official client's MachoPacket classes.
_GUID = {
    AUTHENTICATION_REQ: "macho.AuthenticationReq",
    IDENTIFICATION_REQ: "macho.IdentificationReq",
    IDENTIFICATION_RSP: "macho.IdentificationRsp",
    CALL_REQ: "macho.CallReq",
    CALL_RSP: "macho.CallRsp",
    TRANSPORTCLOSED: "macho.TransportClosed",
    NOTIFICATION: "macho.Notification",
    ERRORRESPONSE: "macho.ErrorResponse",
    SESSIONCHANGENOTIFICATION: "macho.SessionChangeNotification",
    SESSIONINITIALSTATENOTIFICATION: "macho.SessionInitialStateNotification",
    PING_REQ: "macho.PingReq",
    PING_RSP: "macho.PingRsp",
}

_STATE_LENGTH = 14  # (command, source, destination, userID, payload, oob, contextKey, ... 14 in all)


def any_address(service: Optional[str] = None, call_id: Optional[int] = None) -> tuple:
    return (ADDR_ANY, service, call_id)


def client_address(client_id, call_id: Optional[int] = None, service: Optional[str] = None) -> tuple:
    return (ADDR_CLIENT, client_id, call_id, service)


def node_address(node_id, service: Optional[str] = None, call_id: Optional[int] = None) -> tuple:
    return (ADDR_NODE, node_id, service, call_id)


def broadcast_address(broadcast_id=None, narrowcast=(), id_type: Optional[str] = None) -> tuple:
    return (ADDR_BROADCAST, broadcast_id, list(narrowcast), id_type)


@dataclass
class Address:
    kind: int
    service: Optional[str] = None
    call_id: Optional[int] = None
    node_id: Any = None
    client_id: Any = None
    broadcast_id: Any = None
    narrowcast: Any = None
    id_type: Optional[str] = None

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
        if kind == ADDR_BROADCAST:
            narrowcast = at(2)
            return Address(
                kind,
                broadcast_id=at(1),
                narrowcast=list(narrowcast or []),
                id_type=text(at(3)),
            )
        if kind == ADDR_ANY:
            return Address(kind, text(at(1)), at(2))
        return Address(kind)

    def to_wire(self) -> tuple:
        if self.kind == ADDR_NODE:
            return (ADDR_NODE, self.node_id, self.service, self.call_id)
        if self.kind == ADDR_CLIENT:
            return (ADDR_CLIENT, self.client_id, self.call_id, self.service)
        if self.kind == ADDR_BROADCAST:
            return (ADDR_BROADCAST, self.broadcast_id, list(self.narrowcast or []), self.id_type)
        if self.kind == ADDR_ANY:
            return (ADDR_ANY, self.service, self.call_id)
        raise ValueError(f"unsupported MachoNet address type {self.kind}")


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
    journey_id: Any = None
    trace_id: Any = None
    parent_span_id: Any = None
    sampled: Any = None
    ingress_id: Any = None
    sample_rate: Any = None
    cut_parent: Any = None
    compressed_part: int = 0

    @staticmethod
    def parse(value) -> "Packet":
        state = value.args if isinstance(value, m.Object) else value
        if not isinstance(state, tuple) or len(state) < 6:
            raise m.MarshalError(f"not a MachoNet packet: {type(state).__name__}")
        named_payload = state[5]
        compressed_part = named_payload.get("compressedPart", 0) if hasattr(named_payload, "get") else 0
        return Packet(
            command=int(state[0]),
            source=Address.parse(state[1]),
            destination=Address.parse(state[2]),
            user_id=state[3],
            payload=state[4],
            named_payload=state[5],
            context_key=state[6] if len(state) > 6 else None,
            raw=state,
            journey_id=state[7] if len(state) > 7 else None,
            trace_id=state[8] if len(state) > 8 else None,
            parent_span_id=state[9] if len(state) > 9 else None,
            sampled=state[10] if len(state) > 10 else None,
            ingress_id=state[11] if len(state) > 11 else None,
            sample_rate=state[12] if len(state) > 12 else None,
            cut_parent=state[13] if len(state) > 13 else None,
            compressed_part=compressed_part,
        )


def _wire_address(value) -> m.Object:
    if isinstance(value, Address):
        value = value.to_wire()
    if isinstance(value, m.Object):
        return value
    return m.Object(_ADDRESS_GUID, tuple(value))


def build_packet(
    command: int,
    source,
    destination,
    user_id,
    payload,
    named_payload=None,
    context_key=None,
    journey_id=None,
    trace_id=None,
    parent_span_id=None,
    sampled=None,
    ingress_id=None,
    sample_rate=None,
    cut_parent=None,
    compressed_part: int = 0,
) -> m.Object:
    if command == PING_REQ and payload == ():
        payload = ([],)
    if compressed_part:
        if isinstance(named_payload, dict):
            oob = list(named_payload.items())
        else:
            oob = list(named_payload or [])
        replaced = False
        for index, (key, _) in enumerate(oob):
            key_text = key.decode("utf-8", "replace") if isinstance(key, (bytes, bytearray)) else key
            if key_text == "compressedPart":
                oob[index] = (key, compressed_part)
                replaced = True
        if not replaced:
            oob.append(("compressedPart", compressed_part))
        named_payload = m.Dict(oob)
    elif not named_payload:
        named_payload = None

    state = [
        command,
        _wire_address(source),
        _wire_address(destination),
        user_id,
        payload,
        named_payload,
        context_key,
        journey_id,
        trace_id,
        parent_span_id,
        sampled,
        ingress_id,
        sample_rate,
        cut_parent,
    ]
    state += [None] * (_STATE_LENGTH - len(state))
    return m.Object(_GUID.get(command, _GENERIC_PACKET_GUID), tuple(state))


def call_payload(method: str, args: tuple = (), kwargs: Optional[dict] = None, *, is_bound: bool = True) -> tuple:
    """The CallReq body; a direct service/object call has its official isBound flag set."""
    keywords = m.Dict(list((kwargs or {}).items()))
    return ((1 if is_bound else 0, m.SubStream(value=(1, method, tuple(args), keywords if keywords else None))),)


def _substream_value(value):
    if isinstance(value, m.SubStream):
        return value.value
    if isinstance(value, tuple) and len(value) == 1 and isinstance(value[0], m.SubStream):
        return value[0].value
    return value


def result_of(packet: Packet):
    """Unwrap the official CallDown flag and marshal stream, or the legacy result shape."""
    payload = packet.payload
    if isinstance(payload, tuple) and len(payload) == 1:
        payload = payload[0]
    if isinstance(payload, tuple) and len(payload) == 2 and type(payload[0]) is int and payload[0] in (0, 1):
        payload = payload[1]
    return _substream_value(payload)


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
        if isinstance(body, (tuple, list)) and len(body) >= 3:
            return MachoError(_substream_value(body[1]), _substream_value(body[2]), body[0])
        return MachoError(None, _substream_value(body))

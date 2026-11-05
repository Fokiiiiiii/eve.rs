"""The public gateway's wire messages as the headless walks encode and read them (the client's messages by field number).

The gRPC prefix is added by grpcio itself, so the messages go in unframed. The helpers are written from the field numbers of the V24.01
descriptors (eve-logic skills/skillplan/grpc/message_bus/*, cosmetic/character/title/api): not from the server's codec.
"""

import uuid

import grpc

from evecli import marshal as m

REQUEST_SEND = "/eve_public.gateway.Requests/Send"
REQUEST_PING = "/eve_public.gateway.Requests/Ping"

def varint(value: int) -> bytes:
    out = bytearray()
    while True:
        low = value & 0x7F
        value >>= 7
        if value:
            out.append(low | 0x80)
        else:
            out.append(low)
            return bytes(out)

def field(number: int, data: bytes) -> bytes:
    return varint(number << 3 | 2) + varint(len(data)) + data

def number_field(number: int, value: int) -> bytes:
    return varint(number << 3) + varint(value)

def identifier(uuid_value: uuid.UUID) -> bytes:
    return field(1, uuid_value.bytes)

def type_identifier(type_id: int) -> bytes:
    return number_field(1, type_id)

def requirement(type_id: int, level: int) -> bytes:
    return field(1, type_identifier(type_id)) + number_field(2, level)

def plan_attributes(name: str, description: str, requirements) -> bytes:
    body = field(1, name.encode()) + b"".join(field(2, requirement(t, lv)) for t, lv in requirements) + field(3, description.encode())
    return body

def request(name: str, character: int, payload: bytes, request_id: uuid.UUID | None = None) -> bytes:
    any_message = field(1, ("type.evetech.net/" + name).encode()) + field(2, payload)
    identity = field(10, field(1, number_field(1, character)))
    return field(2, (request_id or uuid.uuid4()).bytes) + field(3, any_message) + field(6, identity)

def read_fields(data: bytes) -> dict:
    """The fields of a message as {number: [values]} (varints as ints, the others as bytes)."""
    fields: dict = {}
    offset = 0
    while offset < len(data):
        key, offset = read_varint(data, offset)
        number, wire = key >> 3, key & 7
        if wire == 0:
            value, offset = read_varint(data, offset)
        elif wire == 2:
            length, offset = read_varint(data, offset)
            value, offset = data[offset:offset + length], offset + length
        else:
            raise AssertionError(f"wire type {wire} is not expected in the gateway messages")
        fields.setdefault(number, []).append(value)
    return fields

def read_varint(data: bytes, offset: int):
    value = shift = 0
    while True:
        byte = data[offset]
        offset += 1
        value |= (byte & 0x7F) << shift
        if not byte & 0x80:
            return value, offset
        shift += 7

def answer(response: bytes):
    """A gateway response: (status, the type name of its payload, the payload's message)."""
    fields = read_fields(response)
    status = fields.get(3, [0])[0]
    if 5 not in fields:
        return status, "", b""
    any_fields = read_fields(fields[5][0])
    type_url = any_fields[1][0].decode()
    return status, type_url[type_url.rfind("/") + 1:], any_fields.get(2, [b""])[0]


class Authorized:
    """A gRPC channel whose calls carry the account's bearer token (the client's `authorization` metadata)."""

    def __init__(self, channel, token: str):
        self._channel = channel
        self._metadata = (("authorization", token),)

    def unary_unary(self, method):
        return lambda request: self._channel.unary_unary(method)(request, metadata=self._metadata)

    def stream_stream(self, method):
        return lambda requests, **kwargs: self._channel.stream_stream(method)(requests, metadata=self._metadata, **kwargs)

def as_user(channel, client) -> Authorized:
    """Use the signed token issued to the authenticated account and active character."""
    token = m.plain(client.call("userSvc", "GetUserToken"))
    assert isinstance(token, str), type(token)
    return Authorized(channel, token)

def send(channel, requests):
    """Sends the requests on one Requests/Send stream; returns the answers in order and checks the gRPC status."""
    # grpcio frames each message itself (the 5-byte gRPC prefix), so the messages go in unframed
    call = channel.stream_stream(REQUEST_SEND)(iter(requests))
    frames = []
    for chunk in call:
        frames.append(chunk)
    assert call.code() == grpc.StatusCode.OK, call.details()
    return [answer(chunk) for chunk in frames]


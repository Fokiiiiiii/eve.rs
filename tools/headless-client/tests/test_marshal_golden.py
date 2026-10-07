"""Replay the golden fixtures recorded from the JS reference against the Python codec.

Encoding must reproduce the recorded bytes exactly, decoding the recorded value
(compared in EJSON, down to IEEE-754 bits), and failures must be the same class
of failure. The Rust crate replays the very same file.
"""

import json
import pathlib

import pytest

from evecli import marshal as m
from evecli import packet
from evecli.string_table import OFFICIAL_STRING_TABLE, STRING_TABLE

from .oracle import js_to_value, parse_ejson, to_ejson

FIXTURES = pathlib.Path(__file__).resolve().parents[3] / "oracle/fixtures/marshal/cases.json"
CASES = json.loads(FIXTURES.read_text())


def test_fixture_set_is_present():
    assert len(CASES) > 500


@pytest.mark.parametrize("case", [c for c in CASES if "input" in c], ids=lambda c: c["id"])
def test_encode_matches_recorded_bytes(case):
    js = parse_ejson(case["input"])
    if "encodeError" in case:
        with pytest.raises(m.MarshalError) as info:
            m.encode(js_to_value(js))
        assert info.value.kind == case["encodeError"]
    else:
        assert m.encode(js_to_value(js)).hex() == case["encoded"]


def _check_decode(decode, data, case, ok_key, err_key):
    if err_key in case:
        with pytest.raises(m.MarshalError) as info:
            decode(data)
        assert info.value.kind == case[err_key]
    else:
        assert to_ejson(decode(data)) == case[ok_key]


@pytest.mark.parametrize("case", [c for c in CASES if "encoded" in c], ids=lambda c: c["id"])
def test_decode_matches_recorded_value(case):
    data = bytes.fromhex(case["encoded"])
    _check_decode(m.decode, data, case, "decoded", "decodeError")
    _check_decode(m.decode_exact, data, case, "decodedExact", "decodeExactError")


def test_hostile_nesting_fails_cleanly():
    with pytest.raises(m.MarshalError):
        m.decode(b"\x7e\x00\x00\x00\x00" + b"\x25" * 100_000 + b"\x01")
    nested = None
    for _ in range(m.MAX_DEPTH + 10):
        nested = (nested,)
    with pytest.raises(m.MarshalError):
        m.encode(nested)


def test_huge_declared_counts_do_not_allocate():
    for opcode in (0x14, 0x15, 0x16):
        with pytest.raises(m.MarshalError):
            m.decode(b"\x7e\x00\x00\x00\x00" + bytes((opcode, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF)))


def test_packet_framing():
    packet = m.encode_packet((1, "two"))
    payload, rest = m.split_packet(packet + b"tail")
    assert payload == packet[4:] and rest == b"tail"
    assert m.split_packet(packet[:-1]) is None


def test_official_client_string_table_is_selected_explicitly():
    assert len(STRING_TABLE) == 195
    assert len(OFFICIAL_STRING_TABLE) == 210
    assert OFFICIAL_STRING_TABLE[0] == "*fleetid"
    assert OFFICIAL_STRING_TABLE[-1] == "wingid"

    official = bytes.fromhex("7e000000001102")
    legacy = bytes.fromhex("7e000000001152")
    assert m.encode("*multicastID", string_table=OFFICIAL_STRING_TABLE) == official
    assert m.encode("*multicastID") == legacy
    assert m.encode(m.PyString(b"*multicastID"), string_table=OFFICIAL_STRING_TABLE) == official
    assert m.decode(official, string_table=OFFICIAL_STRING_TABLE) == m.PyString(b"*multicastID")
    assert m.decode(legacy) == "*multicastID"

    framed = m.encode_packet("*fleetid", string_table=OFFICIAL_STRING_TABLE)
    payload, rest = m.split_packet(framed + b"tail")
    assert payload == m.encode("*fleetid", string_table=OFFICIAL_STRING_TABLE)
    assert rest == b"tail"


def test_native_python_containers_use_their_official_marshal_opcodes():
    assert m.encode([1, 2]) == m.encode(m.List([1, 2]))
    assert m.encode({"first": 1, "second": 2}) == m.encode(m.Dict([("first", 1), ("second", 2)]))


def test_packet_build_and_parse_match_official_machonet_state():
    source = packet.client_address(m.Long(7_000_001), 4, "machoNet")
    destination = packet.broadcast_address(17, [m.Long(7_000_001)], "+clientID")
    addresses = (
        packet.node_address(65_450, "invbroker", 5),
        source,
        destination,
        packet.any_address("machoNet", 6),
    )
    for address in addresses:
        assert packet.Address.parse(m.Object("macho.MachoAddress", address)).to_wire() == address

    packet_guids = {
        packet.AUTHENTICATION_REQ: "macho.AuthenticationReq",
        packet.IDENTIFICATION_REQ: "macho.IdentificationReq",
        packet.IDENTIFICATION_RSP: "macho.IdentificationRsp",
        packet.CALL_REQ: "macho.CallReq",
        packet.CALL_RSP: "macho.CallRsp",
        packet.TRANSPORTCLOSED: "macho.TransportClosed",
        packet.NOTIFICATION: "macho.Notification",
        packet.ERRORRESPONSE: "macho.ErrorResponse",
        packet.SESSIONCHANGENOTIFICATION: "macho.SessionChangeNotification",
        packet.SESSIONINITIALSTATENOTIFICATION: "macho.SessionInitialStateNotification",
        packet.PING_REQ: "macho.PingReq",
        packet.PING_RSP: "macho.PingRsp",
    }
    for command, guid in packet_guids.items():
        assert packet.build_packet(command, source, destination, 7, ()).name == guid
    assert packet.build_packet(packet.RESOLVE_REQ, source, destination, 7, ()).name == "macho.MachoPacket"
    assert packet.build_packet(packet.PING_REQ, source, destination, 7, ()).args[4] == ([],)
    value = packet.build_packet(
        packet.CALL_REQ,
        source,
        destination,
        m.Long(7),
        packet.call_payload("GetTime"),
        named_payload=m.Dict([("traceMe", True)]),
        journey_id=m.Long(31),
        trace_id="trace-1",
        parent_span_id="parent-1",
        sampled=True,
        ingress_id=m.Long(8),
        sample_rate=0.25,
        cut_parent=False,
        compressed_part=3,
    )

    assert value.name == "macho.CallReq"
    assert len(value.args) == 14
    assert value.args[1] == m.Object("macho.MachoAddress", source)
    assert value.args[2] == m.Object("macho.MachoAddress", destination)

    wire = m.encode_packet(value, string_table=OFFICIAL_STRING_TABLE)
    payload, rest = m.split_packet(wire)
    assert rest == b""
    parsed = packet.Packet.parse(m.decode(payload, string_table=OFFICIAL_STRING_TABLE))
    assert parsed.source.kind == packet.ADDR_CLIENT
    assert parsed.source.client_id == m.Long(7_000_001)
    assert parsed.source.call_id == 4
    assert parsed.destination.broadcast_id == 17
    assert parsed.destination.narrowcast == [m.Long(7_000_001)]
    assert parsed.destination.id_type == "+clientID"
    assert parsed.destination.to_wire() == destination
    assert parsed.journey_id == m.Long(31)
    assert bytes(parsed.trace_id) == b"trace-1"
    assert bytes(parsed.parent_span_id) == b"parent-1"
    assert parsed.sampled is True
    assert parsed.ingress_id == m.Long(8)
    assert parsed.sample_rate == 0.25
    assert parsed.cut_parent is False
    assert parsed.named_payload.get("traceMe") is True
    assert parsed.named_payload.get("compressedPart") == 3
    assert parsed.compressed_part == 3
    is_bound, stream = parsed.payload[0]
    assert is_bound == 1 and isinstance(stream, m.SubStream)
    assert stream.value[0] == 1
    assert bytes(stream.value[1]) == b"GetTime"
    assert stream.value[2:] == ((), None)


def test_call_result_and_error_response_unwrap_official_substreams():
    response = packet.Packet(
        packet.CALL_RSP,
        packet.Address(packet.ADDR_ANY),
        packet.Address(packet.ADDR_CLIENT),
        7,
        ((0, m.SubStream(value=m.Long(42))),),
    )
    assert packet.result_of(response) == m.Long(42)

    list_result = m.List([0, "kept as the method result"])
    list_response = packet.Packet(
        packet.CALL_RSP,
        packet.Address(packet.ADDR_ANY),
        packet.Address(packet.ADDR_CLIENT),
        7,
        (list_result,),
    )
    assert packet.result_of(list_response) == list_result

    error = packet.Packet(
        packet.ERRORRESPONSE,
        packet.Address(packet.ADDR_ANY),
        packet.Address(packet.ADDR_CLIENT),
        7,
        (packet.CALL_REQ, 2, m.SubStream(value="no such method")),
    )
    raised = packet.MachoError.from_packet(error)
    assert raised.original_command == packet.CALL_REQ
    assert raised.code == 2
    assert raised.payload == "no such method"

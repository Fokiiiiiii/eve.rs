"""Replay the golden fixtures recorded from the JS reference against the Python codec.

Encoding must reproduce the recorded bytes exactly, decoding the recorded value
(compared in EJSON, down to IEEE-754 bits), and failures must be the same class
of failure. The Rust crate replays the very same file.
"""

import json
import pathlib

import pytest

from evecli import marshal as m

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

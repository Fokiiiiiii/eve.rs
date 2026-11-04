#!/usr/bin/env python3
"""Writes oracle/fixtures/hostlink/messages.json: the host-link frames of docs/design/adr/0002, byte for byte.

This is the third writer of the wire format (the Rust front and the C# host are the other two); both
replay the file, so a layout change has to be made here first. u64 values are decimal strings in the JSON.
"""
import json
import os
import struct

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "..", "..", "oracle", "fixtures", "hostlink", "messages.json")

KINDS = {"Hello": 1, "HelloAck": 2, "AuthRequest": 3, "AuthReply": 4, "SessionOpen": 5,
         "SessionClose": 6, "Packet": 7, "Send": 8, "Disconnect": 9, "ClusterConfig": 10}

def s(text):
    data = text.encode("utf-8")
    return struct.pack("<H", len(data)) + data

def b(data):  # u32 length + bytes
    return struct.pack("<I", len(data)) + data

def frame(kind, body):
    payload = bytes([KINDS[kind]]) + body
    return struct.pack("<I", len(payload)) + payload

def build(m):
    k = m["kind"]
    u64 = lambda name: struct.pack("<Q", int(m[name]))  # noqa: E731
    if k == "Hello":
        return frame(k, struct.pack("<HI", m["version"], m["nodeId"]))
    if k == "HelloAck":
        return frame(k, struct.pack("<H", m["version"]))
    if k == "AuthRequest":
        credential = {"Password": 0, "Sso": 1}[m["credentialKind"]]
        return frame(k, struct.pack("<IB", m["corr"], credential) + s(m["userName"])
                     + b(bytes.fromhex(m["passwordHash"])) + s(m["ssoToken"])
                     + s(m["language"]) + s(m["address"]))
    if k == "AuthReply":
        if m["ok"]:
            return frame(k, struct.pack("<IB", m["corr"], 0) + struct.pack("<I", m["userId"]) + u64("role")
                         + s(m["countryCode"]))
        return frame(k, struct.pack("<IB", m["corr"], 1) + s(m["reason"]))
    if k == "SessionOpen":
        return frame(k, u64("sessionId") + u64("clientId") + struct.pack("<I", m["userId"]) + u64("role")
                     + s(m["address"]) + s(m["language"]) + s(m["countryCode"]))
    if k == "SessionClose":
        return frame(k, u64("sessionId"))
    if k in ("Packet", "Send"):
        return frame(k, u64("sessionId") + bytes.fromhex(m["payload"]))
    if k == "Disconnect":
        return frame(k, u64("sessionId") + s(m["reason"]))
    if k == "ClusterConfig":
        return frame(k, bytes.fromhex(m["payload"]))
    raise ValueError(k)

valid = [
    {"name": "hello", "kind": "Hello", "version": 2, "nodeId": 65450},
    {"name": "hello ack", "kind": "HelloAck", "version": 2},
    {"name": "auth request", "kind": "AuthRequest", "corr": 7, "credentialKind": "Password", "userName": "pilot",
     "passwordHash": "00112233445566778899aabbccddeeff00112233", "ssoToken": "", "language": "EN", "address": "127.0.0.1"},
    {"name": "auth request, SSO", "kind": "AuthRequest", "corr": 8, "credentialKind": "Sso",
     "userName": "EVE-SSO-CONNECTION", "passwordHash": "00112233445566778899aabbccddeeff00112233",
     "ssoToken": "test-only-token-placeholder", "language": "EN", "address": "127.0.0.1"},
    {"name": "auth request, non-ASCII name, empty hash", "kind": "AuthRequest", "corr": 4294967295,
     "credentialKind": "Password", "userName": "パイロット", "passwordHash": "", "ssoToken": "", "language": "JA", "address": "::1"},
    {"name": "auth accepted", "kind": "AuthReply", "corr": 7, "ok": True, "userId": 42,
     "role": "18446744073709551615", "countryCode": "GB"},
    {"name": "auth rejected", "kind": "AuthReply", "corr": 7, "ok": False, "reason": "LoginAuthFailed"},
    {"name": "session open", "kind": "SessionOpen", "sessionId": "1790646377000015", "clientId": "42065450",
     "userId": 42, "role": "0", "address": "127.0.0.1", "language": "EN", "countryCode": "GB"},
    {"name": "session close", "kind": "SessionClose", "sessionId": "1790646377000015"},
    {"name": "packet", "kind": "Packet", "sessionId": "1790646377000015", "payload": "7e0000000003"},
    {"name": "empty packet", "kind": "Packet", "sessionId": "1", "payload": ""},
    {"name": "send", "kind": "Send", "sessionId": "1790646377000015", "payload": "0123456789abcdef" * 4},
    {"name": "disconnect", "kind": "Disconnect", "sessionId": "1790646377000015", "reason": "LoginAuthFailed"},
    {"name": "cluster config", "kind": "ClusterConfig", "payload": "7e000000000e0000"},
]
for case in valid:
    case["hex"] = build(case).hex()

le32 = lambda n: struct.pack("<I", n)  # noqa: E731
invalid = [
    {"name": "unknown kind", "reason": "unknown-kind", "hex": (le32(1) + bytes([0xFF])).hex()},
    {"name": "kind zero", "reason": "unknown-kind", "hex": (le32(1) + bytes([0])).hex()},
    {"name": "empty frame", "reason": "empty", "hex": le32(0).hex()},
    {"name": "hello truncated", "reason": "truncated", "hex": (le32(3) + bytes([1, 1, 0])).hex()},
    {"name": "hello trailing bytes", "reason": "trailing", "hex": (le32(8) + bytes([1, 1, 0]) + le32(1) + bytes([0])).hex()},
    {"name": "string longer than the frame", "reason": "truncated",
     "hex": (le32(1 + 4 + 1 + 2) + bytes([3]) + le32(0) + bytes([0]) + struct.pack("<H", 500)).hex()},
    {"name": "invalid UTF-8", "reason": "utf8",
     "hex": (le32(1 + 4 + 1 + 2 + 2 + 4 + 2 + 2 + 2) + bytes([3]) + le32(1) + bytes([0])
             + struct.pack("<H", 2) + b"\xff\xfe" + le32(0) + struct.pack("<H", 0)
             + struct.pack("<H", 0) + struct.pack("<H", 0)).hex()},
    {"name": "auth request with an unknown credential kind", "reason": "bad-credential-kind",
     "hex": (le32(1 + 4 + 1 + 2 + 4 + 2 + 2 + 2) + bytes([3]) + le32(7) + bytes([9])
             + s("") + b(b"") + s("") + s("") + s("")).hex()},
    {"name": "auth reply with an unknown status", "reason": "bad-status",
     "hex": (le32(1 + 4 + 1) + bytes([4]) + le32(1) + bytes([9])).hex()},
    {"name": "packet without a session id", "reason": "truncated", "hex": (le32(5) + bytes([7]) + le32(0)).hex()},
]

for case in valid + invalid:  # every declared length must match the bytes: the invalid ones fail on content only
    raw = bytes.fromhex(case["hex"])
    assert struct.unpack("<I", raw[:4])[0] == len(raw) - 4, case["name"]

os.makedirs(os.path.dirname(OUT), exist_ok=True)
with open(OUT, "w") as f:
    json.dump({"note": "Generated by tools/dev/gen-hostlink-fixtures.py. Frames include their u32 length prefix.",
               "valid": valid, "invalid": invalid}, f, indent=1, ensure_ascii=False)
    f.write("\n")
print(f"{len(valid)} valid, {len(invalid)} invalid frames -> {os.path.normpath(OUT)}")

"""tools/mitm: the proxy forwards bytes unchanged, decodes what the fixtures hold, and diffs captures by call order.

The MITM is in tools/mitm/mitm.py; it reuses evecli's decoders. The packets are the recorded ones of
oracle/fixtures/services/dispatch.json, the golden fixture the C# dispatcher replays.
"""

import json
import socket
import struct
import sys
import threading
import zlib
from pathlib import Path

from evecli import marshal as m
from evecli import packet as p
from evecli.client import Client
from evecli.describe import describe

from .mock_server import MockServer

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "tools" / "mitm"))
import mitm  # noqa: E402

DISPATCH = json.loads((ROOT / "oracle" / "fixtures" / "services" / "dispatch.json").read_text(encoding="utf-8"))

def frame(payload: bytes) -> bytes:
    return struct.pack("<I", len(payload)) + payload

def fixture_steps():
    for scenario in DISPATCH:
        for step in scenario["steps"]:
            yield scenario["id"], step

def records_of(payloads, direction="c2s"):
    """Feed framed payloads through a Stream into a capture file and read the records back."""
    import tempfile

    with tempfile.TemporaryDirectory() as folder:
        path = Path(folder) / "capture.jsonl"
        journal = mitm.Journal(str(path))
        stream = mitm.Stream(direction, 1, journal)
        for payload in payloads:
            stream.feed(frame(payload))
        journal.close()
        return mitm.load(path)

def test_fixture_packets_decode_and_survive_an_encode_round_trip():
    count = 0
    for _, step in fixture_steps():
        for hex_packet in [step["packet"], *step["responses"]]:
            raw = bytes.fromhex(hex_packet)
            value = m.decode(raw)
            # the codec writes an object header with another opcode than the reference, so compare the meaning
            assert describe(m.decode(m.encode(value))) == describe(value)
            count += 1
    assert count >= 144

def test_request_fixtures_come_out_as_call_records():
    payloads = [bytes.fromhex(step["packet"]) for _, step in fixture_steps()]
    records = records_of(payloads)
    # the fixtures hold one deliberately short MachoNet packet: it is logged as undecodable, with its hex
    undecoded = [r for r in records if r["stage"] == "undecoded"]
    assert len(undecoded) == 1 and undecoded[0]["error"].startswith("macho packet: not a MachoNet packet")
    assert len(records) == len(payloads)
    assert {r["type"] for r in records if r["stage"] == "macho"} == {"CallReq", "Notification", "PingReq"}
    calls = [r for r in records if r.get("type") == "CallReq"]
    assert len(calls) >= 60
    assert all(r["dir"] == "c2s" and r["encoding"] == "marshal" for r in records if r["stage"] == "macho")
    first = calls[0]
    expected = p.Packet.parse(m.decode(bytes.fromhex(fixture_steps().__next__()[1]["packet"])))
    assert first["service"] == expected.destination.service
    assert first["call_id"] == expected.source.call_id

def test_zlib_wrapped_payloads_are_inflated_and_recorded_as_zlib():
    payload = bytes.fromhex(next(fixture_steps())[1]["packet"])
    wrapped = zlib.compress(payload)
    assert wrapped[:1] == b"\x78"
    (record,) = records_of([wrapped])
    assert record["encoding"] == "zlib" and record["type"] == "CallReq"

def test_client_notification_capture_keeps_the_method_name_and_wire_parts():
    released = [("N=65450:6", m.BigUnsigned(134391092820280000))]
    payload = ((1, m.SubStream(value=(released, "ClientHasReleasedTheseObjects", (), m.Dict([("machoVersion", 1)])))),)
    named = m.Dict([("OID-", m.Dict(released))])
    raw = m.encode(p.build_packet(
        p.NOTIFICATION,
        p.client_address(42),
        p.broadcast_address(65450),
        7,
        payload,
        named,
    ))

    (record,) = records_of([raw])

    assert record["method"] == "ClientHasReleasedTheseObjects"
    assert record["args"] == {"$tuple": []}
    assert record["kwargs"] == {"$dict": [["machoVersion", 1]]}
    assert record["prefix"] == {"$tuple": [[{"$tuple": ["N=65450:6", {"$bigunsigned": 134391092820280000}]}]]}
    assert record["named_payload"] == {"$dict": [["OID-", {"$dict": [["N=65450:6", {"$bigunsigned": 134391092820280000}]]}]]}

def test_bound_call_keeps_the_existing_capture_record_shape():
    handle = ("N=65450:6", m.Long(134391092820280000))
    payload = ((1, m.SubStream(value=(handle, "List", (), None))),)
    raw = m.encode(p.build_packet(
        p.CALL_REQ,
        p.client_address(42, 7),
        p.any_address(None),
        7,
        payload,
    ))

    (record,) = records_of([raw])

    assert record["service"] is None
    assert record["method"] == "List"
    assert record["bound_object"] == {"$tuple": ["N=65450:6", {"$long": 134391092820280000}]}
    assert set(record) == {
        "seq", "t", "dir", "conn", "stage", "encoding", "type", "service", "call_id", "method", "args", "bound_object"
    }

def test_a_frame_split_across_reads_is_reassembled():
    payload = bytes.fromhex(next(fixture_steps())[1]["packet"])
    import tempfile

    with tempfile.TemporaryDirectory() as folder:
        path = Path(folder) / "capture.jsonl"
        journal = mitm.Journal(str(path))
        stream = mitm.Stream("c2s", 1, journal)
        for byte in frame(payload) + frame(payload):
            stream.feed(bytes([byte]))
        journal.close()
        records = mitm.load(path)
    assert [r["type"] for r in records] == ["CallReq", "CallReq"]

def test_an_undecodable_payload_is_logged_as_hex_and_the_stream_goes_on():
    good = bytes.fromhex(next(fixture_steps())[1]["packet"])
    garbage = b"\x7e\xff\xff\xff\xff not marshal"
    records = records_of([garbage, good])
    assert records[0]["stage"] == "undecoded" and records[0]["hex"] == garbage[:256].hex()
    assert records[1]["type"] == "CallReq"

def test_an_oversized_length_stops_parsing_that_direction_only():
    journal = mitm.Journal(None)
    stream = mitm.Stream("s2c", 1, journal)
    written = []
    journal.write = lambda rec: written.append(rec)
    stream.feed(struct.pack("<I", mitm.MAX_FRAME + 1))
    stream.feed(frame(b"\x7e"))
    assert len(written) == 1 and "exceeds" in written[0]["error"] and stream.dead

def test_the_proxy_forwards_bytes_unchanged_and_logs_both_directions(tmp_path):
    requests = [frame(bytes.fromhex(step["packet"])) for _, step in list(fixture_steps())[:5]]
    replies = [frame(bytes.fromhex(hex_packet)) for _, step in list(fixture_steps())[:5] for hex_packet in step["responses"][:1]]
    upstream_seen = bytearray()

    upstream = socket.create_server(("127.0.0.1", 0))

    def upstream_side():
        conn, _ = upstream.accept()
        with conn:
            while len(upstream_seen) < sum(map(len, requests)):
                data = conn.recv(65536)
                if not data:
                    break
                upstream_seen.extend(data)
            conn.sendall(b"".join(replies))

    proxy = socket.create_server(("127.0.0.1", 0))
    out = tmp_path / "capture.jsonl"
    journal = mitm.Journal(str(out))

    def proxy_side():
        client, _ = proxy.accept()
        mitm._bridge(client, upstream.getsockname(), journal, 1)

    threads = [threading.Thread(target=upstream_side, daemon=True), threading.Thread(target=proxy_side, daemon=True)]
    for thread in threads:
        thread.start()

    received = bytearray()
    with socket.create_connection(proxy.getsockname(), timeout=10) as client:
        for chunk in requests:
            client.sendall(chunk)
        client.shutdown(socket.SHUT_WR)
        while True:
            data = client.recv(65536)
            if not data:
                break
            received.extend(data)
    for thread in threads:
        thread.join(10)
    journal.close()
    upstream.close()
    proxy.close()

    assert bytes(upstream_seen) == b"".join(requests)
    assert bytes(received) == b"".join(replies)
    records = mitm.load(out)
    assert [r["dir"] for r in records if r.get("type") == "CallReq"] == ["c2s"] * 5
    assert [r["dir"] for r in records if r.get("type") == "CallRsp"] == ["s2c"] * 5

def call(seq, method, service="machoNet", call_id=1, args=None, kwargs=None, conn=1, bound_object=None):
    record = {"seq": seq, "dir": "c2s", "conn": conn, "stage": "macho", "encoding": "marshal", "type": "CallReq",
              "service": service, "method": method, "call_id": call_id, "args": args or {"$tuple": []}}
    if kwargs is not None:
        record["kwargs"] = kwargs
    if bound_object is not None:
        record["bound_object"] = bound_object
    return record

def result(seq, value, service="machoNet", call_id=1, conn=1):
    return {"seq": seq, "dir": "s2c", "conn": conn, "stage": "macho", "encoding": "marshal", "type": "CallRsp",
            "service": service, "call_id": call_id, "result": value}

def error(seq, code, payload, service="machoNet", call_id=1, conn=1):
    return {"seq": seq, "dir": "s2c", "conn": conn, "stage": "macho", "encoding": "marshal", "type": "ErrorResponse",
            "service": service, "call_id": call_id, "error": {"code": code, "payload": payload}}

def notice(seq, method, args=None, conn=1):
    return {"seq": seq, "dir": "s2c", "conn": conn, "stage": "macho", "encoding": "marshal", "type": "Notification",
            "service": "notificationMgr", "call_id": None, "method": method, "args": args or {"$tuple": []}}

def write(path, records):
    path.write_text("".join(json.dumps(r) + "\n" for r in records), encoding="utf-8")
    return path

def diff(tmp_path, reference, candidate, *, ignore, extra=(), all_calls=False):
    a = write(tmp_path / "a.jsonl", reference)
    b = write(tmp_path / "b.jsonl", candidate)
    args = ["diff", str(a), str(b)] + (["--ignore-volatile"] if ignore else []) + list(extra)
    if not all_calls:
        args.append("--first")
    return mitm.main(args)

def test_identical_calls_are_identical(tmp_path, capsys):
    run = [call(1, "GetInitVals"), result(2, {"$dict": []})]
    assert diff(tmp_path, run, run, ignore=False) == 0
    assert "identical" in capsys.readouterr().out

def test_a_different_call_order_is_the_first_divergence(tmp_path, capsys):
    a = [call(1, "GetInitVals"), result(2, {"$dict": []}), call(3, "GetTime", call_id=2), result(4, {"$long": 1}, call_id=2)]
    b = [call(1, "GetTime"), result(2, {"$long": 1}), call(3, "GetInitVals", call_id=2), result(4, {"$dict": []}, call_id=2)]
    assert diff(tmp_path, a, b, ignore=True) == 1
    out = capsys.readouterr().out
    assert "first divergence at event 0" in out and "call order" in out

def test_full_diff_reports_each_call_reply_error_and_notification_order(tmp_path, capsys):
    ref = [
        call(1, "GetBalance", call_id=1),
        result(2, {"$long": 15}, call_id=1),
        call(3, "PlaceOrder", call_id=2),
        error(4, {"$unicode": "UserError"}, {"$tuple": ["MktInvalidPrice", {"$dict": [["info", {"$unicode": "Bad price"}]]}]}, call_id=2),
        notice(5, "OnWalletChanged"),
        notice(6, "OnOrderChanged"),
    ]
    got = [
        call(1, "GetBalance", call_id=11),
        call(2, "PlaceOrder", call_id=12),
        error(3, {"$unicode": "MachoFailed"}, {"$tuple": ["MktInvalidPrice", {"$dict": [["info", {"$unicode": "Price rejected"}]]}]}, call_id=12),
        notice(4, "OnOrderChanged"),
        notice(5, "OnWalletChanged"),
        error(6, {"$unicode": "MachoFailed"}, {"$tuple": ["Unmatched"]}, call_id=99),
    ]

    assert diff(tmp_path, ref, got, ignore=True, all_calls=True) == 1
    out = capsys.readouterr().out
    assert "GetBalance" in out and "reply presence differs" in out
    assert "PlaceOrder" in out and "error code differs" in out and "error text differs" in out
    assert "notification name/order differs" in out
    assert "OnWalletChanged" in out and "OnOrderChanged" in out
    assert "candidate orphan reply" in out and "call_id=99" in out

def test_full_diff_checks_reply_shapes_but_ignores_changed_leaf_values(tmp_path, capsys):
    ref = [call(1, "GetState"), result(2, {"$tuple": [{"$long": 15}, {"$dict": [["balance", {"$long": 90}]]}]})]
    same_shape = [call(1, "GetState"), result(2, {"$tuple": [{"$long": 99}, {"$dict": [["balance", {"$long": 500}]]}]})]
    assert diff(tmp_path, ref, same_shape, ignore=True, all_calls=True) == 0
    assert "identical" in capsys.readouterr().out

    changed_shape = [call(1, "GetState"), result(2, {"$tuple": [{"$long": 99}, {"$dict": [["balance", {"$long": 500}]]}, {"$none": None}]})]
    assert diff(tmp_path, ref, changed_shape, ignore=True, all_calls=True) == 1
    assert "reply marshal shape differs" in capsys.readouterr().out

def test_full_diff_reports_one_sided_calls_without_replies(tmp_path, capsys):
    ref = [call(1, "GetBalance"), call(2, "GetOrders", call_id=2)]
    got = [call(1, "GetBalance")]
    assert diff(tmp_path, ref, got, ignore=True, all_calls=True) == 1
    out = capsys.readouterr().out
    assert "GetOrders" in out and "request missing from candidate; reply=missing" in out

def test_full_diff_aligns_later_sessions_by_handshake_account_after_extra_retry(tmp_path, capsys):
    def login(seq, conn, user):
        value = {"$tuple": [{"$buffer": "challenge"}, {"$dict": [["user_name", user]]}]}
        return {"seq": seq, "dir": "c2s", "conn": conn, "stage": "handshake", "encoding": "marshal", "value": value}

    ref = [login(1, 1, "evecli-live"), call(2, "GetTime", call_id=1, conn=1),
           login(3, 2, "evecli-main-aaaaaaaaaa"), call(4, "GetInitVals", call_id=1, conn=2)]
    got = [login(1, 1, "evecli-live"), call(2, "GetTime", call_id=1, conn=1),
           login(3, 2, "evecli-retry-bbbbbbbbbb"), call(4, "GetBalance", call_id=1, conn=2),
           login(5, 3, "evecli-main-cccccccccc"), call(6, "GetInitVals", call_id=1, conn=3)]
    assert diff(tmp_path, ref, got, ignore=True, all_calls=True) == 1
    out = capsys.readouterr().out
    assert "SAME user=evecli-live" in out and "SAME user=evecli-main-*" in out
    assert "candidate-only #1 machoNet.GetBalance" in out

def test_full_diff_pairs_the_scenario_socket_after_an_identity_setup_login(tmp_path, capsys):
    def login(seq, conn):
        value = {"$tuple": [{"$buffer": "challenge"}, {"$dict": [["user_name", "evecli-agent"]]}]}
        return {"seq": seq, "dir": "c2s", "conn": conn, "stage": "handshake", "encoding": "marshal", "value": value}

    ref = [login(1, 1), call(2, "GetServiceInfo", call_id=1, conn=1),
           call(3, "CreateCharacter", call_id=2, conn=1)]
    got = [login(1, 1), call(2, "GetServiceInfo", call_id=1, conn=1),
           login(3, 2), call(4, "GetServiceInfo", call_id=1, conn=2),
           call(5, "CreateCharacter", call_id=2, conn=2)]
    assert diff(tmp_path, ref, got, ignore=True, all_calls=True) == 1
    out = capsys.readouterr().out
    assert "SAME user=evecli-agent connection#1 #2 machoNet.CreateCharacter" in out
    assert "candidate-only #1 machoNet.GetServiceInfo" in out
    assert "reference-only" not in out

def test_a_result_shape_change_is_reported_even_when_volatile_fields_are_ignored(tmp_path, capsys):
    a = [call(1, "GetInitVals"), result(2, {"$tuple": [{"$long": 1}]})]
    b = [call(1, "GetInitVals"), result(2, {"$tuple": [{"$long": 1}, {"$long": 2}]})]
    assert diff(tmp_path, a, b, ignore=True) == 1
    assert "result shape" in capsys.readouterr().out

def test_session_ids_and_times_only_count_without_ignore_volatile(tmp_path):
    session = lambda v: {"$dict": [["sessionID", {"$long": v}], ["name", "x"]]}  # noqa: E731
    a = [call(1, "Logon"), result(2, session(1))]
    b = [call(1, "Logon"), result(2, session(2))]
    assert diff(tmp_path, a, b, ignore=True) == 0
    assert diff(tmp_path, a, b, ignore=False) == 1

def test_the_session_id_that_leads_a_session_notification_is_volatile(tmp_path):
    def initial(session_id):
        return {"seq": 1, "dir": "s2c", "conn": 1, "stage": "macho", "type": "SessionInitialStateNotification",
                "service": None, "call_id": None, "payload": {"$tuple": [{"$long": session_id}, 5, {"$dict": [["userid", 1]]}]}}

    assert diff(tmp_path, [initial(1)], [initial(2)], ignore=True) == 0
    assert diff(tmp_path, [initial(1)], [initial(2)], ignore=False) == 1

def test_get_time_values_and_bound_object_ids_are_volatile(tmp_path):
    a = [call(1, "GetTime"), result(2, {"$long": 100}), call(3, "Do", service="OID+1", call_id=2), result(4, {"$none": None}, service="OID+1", call_id=2)]
    b = [call(1, "GetTime"), result(2, {"$long": 999}), call(3, "Do", service="OID+7", call_id=2), result(4, {"$none": None}, service="OID+7", call_id=2)]
    assert diff(tmp_path, a, b, ignore=True) == 0
    assert diff(tmp_path, a, b, ignore=False) == 1

def test_show_filters_by_service_method_and_notifications(tmp_path, capsys):
    records = [call(1, "GetInitVals"), call(2, "GetTime", service="other", call_id=2),
               {"seq": 3, "dir": "s2c", "conn": 1, "stage": "macho", "type": "Notification", "service": None, "method": "OnThing", "args": {"$tuple": []}}]
    path = write(tmp_path / "c.jsonl", records)
    assert mitm.main(["show", str(path), "--service", "other"]) == 0
    assert "other.GetTime" in capsys.readouterr().out and "GetInitVals" not in capsys.readouterr().out
    mitm.main(["show", str(path), "--notifications-only"])
    out = capsys.readouterr().out
    assert "OnThing" in out and "GetInitVals" not in out

def test_a_login_and_a_call_through_the_proxy_are_logged_and_answered(tmp_path):
    """The real handshake frames and a call go through the proxy to a scripted server and back, all decoded."""
    out = tmp_path / "login.jsonl"
    with MockServer() as server:
        server.add_account("pilot", "hunter2")
        proxy = socket.create_server(("127.0.0.1", 0))
        journal = mitm.Journal(str(out))
        serving = threading.Thread(
            target=lambda: mitm._bridge(proxy.accept()[0], ("127.0.0.1", server.port), journal, 1), daemon=True)
        serving.start()
        with Client("127.0.0.1", proxy.getsockname()[1]) as client:
            client.login("pilot", "hunter2")
            assert client.call("machoNet", "GetTime") is not None
        serving.join(10)
        journal.close()
        proxy.close()
    records = mitm.load(out)
    assert any(r["stage"] == "handshake" and r["dir"] == "s2c" for r in records)
    assert any(r.get("type") == "CallReq" and r.get("method") == "GetTime" and r["dir"] == "c2s" for r in records)
    assert any(r.get("type") == "CallRsp" and r["dir"] == "s2c" for r in records)
    assert [r for r in records if r["stage"] == "undecoded"] == []

def test_key_order_of_a_dictionary_is_a_divergence_unless_ignored(tmp_path, capsys):
    a = [call(1, "Logon"), result(2, {"$dict": [["userid", 1], ["role", {"$long": 4}]]})]
    b = [call(1, "Logon"), result(2, {"$dict": [["role", {"$long": 4}], ["userid", 1]]})]
    assert diff(tmp_path, a, b, ignore=False) == 1
    assert diff(tmp_path, a, b, ignore=False, extra=["--ignore-key-order"]) == 0
    assert "identical" in capsys.readouterr().out

def test_key_order_does_not_hide_a_changed_value(tmp_path):
    a = [call(1, "Logon"), result(2, {"$dict": [["userid", 1], ["role", {"$long": 4}]]})]
    b = [call(1, "Logon"), result(2, {"$dict": [["role", {"$long": 8}], ["userid", 1]]})]
    assert diff(tmp_path, a, b, ignore=False, extra=["--ignore-key-order"]) == 1

def test_notices_in_another_order_are_a_divergence_unless_compared_as_a_set(tmp_path, capsys):
    def notice(seq, method, value):
        return {"seq": seq, "dir": "s2c", "conn": 1, "stage": "macho", "type": "Notification", "service": "notificationMgr",
                "method": method, "args": {"$tuple": [value]}}

    a = [call(1, "GetInitVals"), result(2, {"$dict": []}), notice(3, "OnA", {"$long": 1}), notice(4, "OnB", {"$long": 2})]
    b = [call(1, "GetInitVals"), result(2, {"$dict": []}), notice(3, "OnB", {"$long": 2}), notice(4, "OnA", {"$long": 1})]
    assert diff(tmp_path, a, b, ignore=False) == 1
    assert diff(tmp_path, a, b, ignore=False, extra=["--ignore-notification-order"]) == 0
    assert "identical" in capsys.readouterr().out

def test_a_missing_notice_is_still_a_divergence_when_notices_are_a_set(tmp_path):
    def notice(seq, method):
        return {"seq": seq, "dir": "s2c", "conn": 1, "stage": "macho", "type": "Notification", "service": "notificationMgr",
                "method": method, "args": {"$tuple": []}}

    a = [call(1, "GetInitVals"), result(2, {"$dict": []}), notice(3, "OnA"), notice(4, "OnB")]
    b = [call(1, "GetInitVals"), result(2, {"$dict": []}), notice(3, "OnA")]
    assert diff(tmp_path, a, b, ignore=False, extra=["--ignore-notification-order"]) == 1


def core_reply(seq, call_id, *, kind="CallRsp", result_value=None, error=None, conn=1):
    record = {"seq": seq, "dir": "s2c", "conn": conn, "stage": "macho", "type": kind,
              "service": "machoNet", "call_id": call_id}
    if kind == "CallRsp":
        record["result"] = result_value
    else:
        record["error"] = error
    return record


def core_notice(seq, method, args):
    return {"seq": seq, "dir": "s2c", "conn": 1, "stage": "macho", "type": "Notification",
            "service": "notificationMgr", "method": method, "args": args}


def core_session_change(seq, session_id, attributes):
    return {"seq": seq, "dir": "s2c", "conn": 1, "stage": "macho", "type": "SessionChangeNotification",
            "service": None, "call_id": None,
            "payload": {"$tuple": [{"$long": session_id}, {"$tuple": [0, attributes]}, []]}}


def test_diff_core_reports_each_call_reply_presence_and_reply_shape(tmp_path):
    requests = [call(1, "GetInitVals", call_id=1), call(2, "GetTime", call_id=2)]
    reference = requests + [
        core_reply(3, 1, result_value={"$dict": []}),
        core_reply(4, 2, kind="ErrorResponse", error={"code": "Unavailable", "payload": "clock unavailable"}),
    ]
    candidate = requests + [core_reply(3, 1, result_value={"$dict": [["service", "machoNet"]]})]

    report = mitm.diff_core(reference, candidate)

    assert [row["method"] for row in [call["reference"] for call in report["calls"]]] == ["GetInitVals", "GetTime"]
    assert report["calls"][0]["reply"]["reference"]["presence"] == "present"
    assert report["calls"][0]["reply"]["candidate"]["presence"] == "present"
    assert any(issue["kind"] == "reply_shape" for issue in report["call_differences"][0]["issues"])
    assert report["calls"][1]["reply"]["reference"]["presence"] == "present"
    assert report["calls"][1]["reply"]["candidate"]["presence"] == "missing"
    assert report["calls"][1]["issues"][0]["kind"] == "reply_presence"
    assert report["summary"]["unanswered_calls"] == 1


def test_diff_core_reports_error_codes_text_notification_order_and_session_attributes():
    request = call(1, "SelectCharacterID", service="charUnboundMgr", call_id=1)
    error = lambda seq, code, text: core_reply(
        seq, 1, kind="ErrorResponse", error={"code": code, "payload": {"$dict": [["info", text]]}},
    )
    attributes_a = {"$dict": [["charid", {"$long": 100}], ["stationid", {"$long": 60000001}]]}
    attributes_b = {"$dict": [["charid", {"$long": 200}], ["stationid", {"$long": 60000002}]]}
    reference = [request, error(2, "CustomInfo", "Character is unavailable."),
                 core_notice(3, "OnA", {"$tuple": []}), core_session_change(4, 11, attributes_a),
                 core_notice(5, "OnB", {"$tuple": []})]
    candidate = [request, error(2, "CharacterUnavailable", "Try again later."),
                 core_notice(3, "OnB", {"$tuple": []}), core_session_change(4, 99, attributes_b),
                 core_notice(5, "OnA", {"$tuple": []})]

    report = mitm.diff_core(reference, candidate)

    error_issue = report["call_differences"][0]["issues"][0]
    assert error_issue["kind"] == "error_value"
    assert any("CustomInfo" in difference and "CharacterUnavailable" in difference
               for difference in error_issue["differences"])
    assert [row["kind"] for row in report["calls"][0]["reply"]["reference"]["replies"]] == ["ErrorResponse"]
    assert any(issue["kind"] == "notification_order_or_name"
               for row in report["notification_differences"] for issue in row["issues"])
    session = report["session_changes"][0]
    assert session["reference_attribute_keys"] == session["candidate_attribute_keys"] == ["charid", "stationid"]
    assert session["attribute_difference"]["kind"] == "value"
    assert any("stationid" in difference for difference in session["attribute_difference"]["differences"])
    assert not any("charid" in difference for difference in session["attribute_difference"]["differences"])


def test_diff_core_masks_random_names_and_bound_handles_but_keeps_reply_shapes():
    reference = [
        call(1, "GetValidRandomName", service="charUnboundMgr", args={"$tuple": [1]}),
        call(2, "MachoBindObject", service="ship", call_id=2, args={"$tuple": [60015249, 15]}),
        core_reply(3, 1, result_value="A random pilot name"),
        core_reply(
            4,
            2,
            result_value={"$tuple": [{"$substream": {"$tuple": ["N=65450:6", {"$long": 134393100758480000}]}}]},
        ),
    ]
    candidate = [
        call(1, "GetValidRandomName", service="charUnboundMgr", args={"$tuple": [1]}),
        call(2, "MachoBindObject", service="ship", call_id=2, args={"$tuple": [60015249, 15]}),
        core_reply(3, 1, result_value="Another random pilot name"),
        core_reply(
            4,
            2,
            result_value={"$tuple": [{"$substream": {"$tuple": ["N=65450:7", {"$long": 134393096149190000}]}}]},
        ),
    ]

    report = mitm.diff_core(reference, candidate)

    assert report["call_differences"] == []
    assert report["summary"]["compared_calls"] == 2

    candidate[-1] = core_reply(4, 2, result_value={"$none": None})
    shape_report = mitm.diff_core(reference, candidate)

    assert any(
        issue["kind"] == "reply_shape"
        for row in shape_report["call_differences"]
        for issue in row["issues"]
    )


def test_diff_core_aligns_calls_by_service_and_method_around_an_inserted_call():
    reference = [
        call(1, "GetTime", call_id=1),
        call(2, "SelectCharacterID", service="charUnboundMgr", call_id=2),
        core_reply(3, 1, result_value={"$long": 100}),
        core_reply(4, 2, result_value=None),
    ]
    candidate = [
        call(1, "GetTime", call_id=11),
        call(2, "GetInitVals", call_id=12),
        call(3, "SelectCharacterID", service="charUnboundMgr", call_id=13),
        core_reply(4, 11, result_value={"$long": 200}),
        core_reply(5, 12, result_value={"$dict": []}),
        core_reply(6, 13, result_value=None),
    ]

    report = mitm.diff_core(reference, candidate)

    assert len(report["calls"]) == 3
    assert report["calls"][0]["reference"]["method"] == "GetTime"
    assert report["calls"][1]["reference"] is None
    assert report["calls"][1]["candidate"]["method"] == "GetInitVals"
    assert report["calls"][2]["reference"]["method"] == "SelectCharacterID"
    assert report["calls"][2]["reply"]["reference"]["presence"] == "present"
    assert report["calls"][2]["reply"]["candidate"]["presence"] == "present"
    assert [issue["kind"] for issue in report["call_differences"][0]["issues"]] == [
        "call_presence", "reply_presence",
    ]


def test_diff_core_pairs_connection_streams_when_one_side_has_an_extra_login():
    reference = [
        call(1, "GetServiceInfo", call_id=1, conn=4),
        call(2, "CreateCharacterWithDoll", service="charUnboundMgr", call_id=2, conn=4),
        core_reply(3, 1, result_value={"$dict": []}, conn=4),
        core_reply(4, 2, result_value=140000001, conn=4),
    ]
    candidate = [
        call(1, "GetServiceInfo", call_id=10, conn=2),  # role-seeding login opened an extra client connection
        core_reply(2, 10, result_value={"$dict": []}, conn=2),
        call(3, "GetServiceInfo", call_id=11, conn=9),
        call(4, "CreateCharacterWithDoll", service="charUnboundMgr", call_id=12, conn=9),
        core_reply(5, 11, result_value={"$dict": []}, conn=9),
        core_reply(6, 12, result_value=140000010, conn=9),
    ]

    report = mitm.diff_core(reference, candidate)

    matched_creation = next(row for row in report["calls"] if row["reference"] and row["reference"]["method"] == "CreateCharacterWithDoll")
    assert (matched_creation["reference_conn"], matched_creation["candidate_conn"]) == (4, 9)
    assert matched_creation["reply"]["reference"]["presence"] == "present"
    assert matched_creation["reply"]["candidate"]["presence"] == "present"
    extra_login = next(row for row in report["calls"] if row["reference"] is None)
    assert extra_login["candidate_conn"] == 2
    assert [issue["kind"] for issue in extra_login["issues"]] == ["call_presence", "reply_presence"]
    assert report["summary"]["unanswered_calls"] == 0
    assert report["summary"]["reply_presence_differences"] == 1


def test_diff_core_pairs_single_call_connections_to_report_a_method_change():
    reference = [call(1, "GetTime", call_id=1, conn=3), result(2, {"$long": 100}, call_id=1, conn=3)]
    candidate = [call(1, "GetServerTime", call_id=5, conn=8), result(2, {"$long": 100}, call_id=5, conn=8)]

    report = mitm.diff_core(reference, candidate)

    assert len(report["calls"]) == 1
    assert [issue["kind"] for issue in report["calls"][0]["issues"]] == ["call_order"]


def test_diff_core_treats_an_unobserved_bound_service_as_unknown():
    reference_call = call(1, "List", call_id=1, service=None, args={"$tuple": [4]})
    reference_call["bound_service"] = "invbroker"
    candidate_call = call(1, "List", call_id=11, service=None, args={"$tuple": [4]})
    candidate_call["bound_service"] = None
    reference = [
        reference_call,
        core_reply(2, 1, result_value={"$list": []}),
    ]
    candidate = [
        candidate_call,
        core_reply(2, 11, result_value={"$list": []}),
    ]

    report = mitm.diff_core(reference, candidate)

    assert report["identical"] is True
    assert report["calls"][0]["reference"]["service"] == "invbroker"
    assert report["calls"][0]["candidate"]["service"] is None
    assert report["calls"][0]["issues"] == []


def test_diff_core_masks_a_created_character_id_result_only_by_default():
    request = call(1, "CreateCharacterWithDoll", service="charUnboundMgr", call_id=1)
    reference = [request, core_reply(2, 1, result_value=140000005)]
    candidate = [request, core_reply(2, 1, result_value=140000001)]

    assert mitm.diff_core(reference, candidate)["identical"] is True
    assert mitm.diff_core(reference, candidate, strict_volatile=True)["identical"] is False


def test_diff_core_masks_only_the_dynamic_positional_character_and_ship_ids():
    methods = [
        ("CreateCharacterWithDoll", "charUnboundMgr", ["Ref Pilot", 4, 5]),
        ("SelectCharacterID", "charUnboundMgr", [140000001, None, False]),
        ("Undock", None, [1990000010, False]),
        ("CmdDock", None, [60015249, 1990000010]),
        ("CmdStargateJump", None, [50016630, 50016839, 1990000010]),
    ]
    reference = []
    candidate = []
    for index, (method, service, args) in enumerate(methods, start=1):
        reference.append(call(index, method, service=service, call_id=index, args={"$tuple": args}))
        candidate_args = list(args)
        if method == "CreateCharacterWithDoll":
            candidate_args[0] = "Candidate Pilot"
        elif method == "SelectCharacterID":
            candidate_args[0] = 140000099
        elif method == "Undock":
            candidate_args[0] = 1990000099
        elif method == "CmdDock":
            candidate_args[1] = 1990000099
        else:
            candidate_args[2] = 1990000099
        candidate.append(call(index, method, service=service, call_id=index, args={"$tuple": candidate_args}))
        reference.append(result(index + len(methods), None, call_id=index))
        candidate.append(result(index + len(methods), None, call_id=index))

    report = mitm.diff_core(reference, candidate)

    assert report["identical"] is True
    assert mitm.diff_core(reference, candidate, strict_volatile=True)["identical"] is False


def test_diff_core_masks_online_module_ids_but_keeps_the_map_shape_and_flags():
    request = call(
        1,
        "Undock",
        args={"$tuple": [{"$long": 9988400000171}, False]},
        kwargs={"$dict": [["onlineModules", {"$dict": [[27, {"$long": 9988400000173}], [28, {"$long": 9988400000174}]]}]]},
    )
    reply = result(2, None, call_id=1)
    candidate_request = call(
        1,
        "Undock",
        args={"$tuple": [{"$long": 1990000032}, False]},
        kwargs={"$dict": [["onlineModules", {"$dict": [[27, {"$long": 1990000034}], [28, {"$long": 1990000035}]]}]]},
    )
    candidate_reply = result(2, None, call_id=1)

    assert mitm.diff_core([request, reply], [candidate_request, candidate_reply])["identical"] is True

    changed_flag = call(
        1,
        "Undock",
        args={"$tuple": [{"$long": 1990000032}, False]},
        kwargs={"$dict": [["onlineModules", {"$dict": [[29, {"$long": 1990000034}], [28, {"$long": 1990000035}]]}]]},
    )
    diff = mitm.diff_core([request, reply], [changed_flag, candidate_reply])
    assert diff["identical"] is False
    assert any("onlineModules" in str(issue) or "29" in str(issue) for issue in diff["call_differences"][0]["issues"])


def test_diff_core_keeps_a_marshal_type_difference_in_a_volatile_id_visible():
    reference = [call(1, "Undock", args={"$tuple": [{"$long": 9988400000171}, False]}), result(2, None, call_id=1)]
    candidate = [call(1, "Undock", args={"$tuple": [1990000032, False]}), result(2, None, call_id=1)]

    report = mitm.diff_core(reference, candidate)

    assert report["identical"] is False
    assert report["call_differences"][0]["issues"][0]["kind"] == "request_args_shape"


def test_capture_associates_remote_object_calls_with_the_service_that_bound_them(tmp_path):
    path = tmp_path / "bound-service.jsonl"
    journal = mitm.Journal(str(path))
    journal.write({"stage": "macho", "conn": 2, "type": "CallReq", "call_id": 5,
                   "service": "ship", "method": "MachoBindObject", "args": {"$tuple": [[60015249, 15], None]}})
    journal.write({"stage": "macho", "conn": 2, "type": "CallRsp", "call_id": 5, "service": "ship",
                   "result": {"$tuple": [{"$substruct": {"$substream": {"$tuple": ["N=65450:15", {"$long": 123}]}}}, None]}})
    journal.write({"stage": "macho", "conn": 2, "type": "CallReq", "call_id": 6, "service": None,
                   "method": "Undock", "bound_object": {"$substruct": {"$substream": {"$tuple": ["N=65450:15", {"$long": 123}]}}},
                   "args": {"$tuple": [{"$long": 9988400000171}, False]}})
    journal.close()

    records = mitm.load(path)
    assert records[2]["bound_service"] == "ship"
    signature = mitm._core_request_signature(records[2], ignore_volatile=True)
    assert signature["service"] == "ship" and signature["method"] == "Undock"


def test_diff_core_keeps_later_notices_aligned_after_an_inserted_notice():
    reference = [core_notice(1, "OnA", {"$tuple": []}), core_notice(2, "OnB", {"$tuple": []})]
    candidate = [
        core_notice(1, "OnA", {"$tuple": []}),
        core_notice(2, "OnExtra", {"$tuple": []}),
        core_notice(3, "OnB", {"$tuple": []}),
    ]

    report = mitm.diff_core(reference, candidate)

    assert len(report["notification_differences"]) == 1
    issue = report["notification_differences"][0]["issues"][0]
    assert issue["kind"] == "notification_presence"
    assert report["notification_differences"][0]["candidate"][2] == "OnExtra"


def test_diff_core_command_emits_all_call_rows_and_can_write_json(tmp_path, capsys):
    records = [call(1, "GetInitVals"), result(2, {"$dict": []})]
    left = write(tmp_path / "reference.jsonl", records)
    right = write(tmp_path / "candidate.jsonl", records)

    assert mitm.main(["diff-core", str(left), str(right), "--all-calls"]) == 0
    assert '"method":"GetInitVals"' in capsys.readouterr().out
    assert mitm.main(["diff-core", str(left), str(right), "--json"]) == 0
    report = json.loads(capsys.readouterr().out)
    assert report["identical"] is True
    assert len(report["calls"]) == 1


def run_flow(*, name="Pilot A", session_id=100, call_id=7, extra=(), reply_value=None):
    """One recorded flow: a request, its reply and a notice. Ids and names are what a second run changes."""
    reply = name if reply_value is None else reply_value
    return [
        call(1, "GetValidRandomName", service="charUnboundMgr", args={"$tuple": [1]}, call_id=call_id),
        result(2, reply, call_id=call_id),
        notice(3, "OnServerSkillsChanged", args={"$tuple": [{"$long": session_id}]}),
        *extra,
    ]


def test_diff_runs_of_identical_flows_have_no_differences_and_no_wildcards():
    report = mitm.diff_runs(run_flow(), run_flow(), run_flow(), run_flow())

    assert report["identical"] is True
    assert report["differences"] == []
    assert report["wildcards"] == []
    assert report["summary"]["compared_records"] == 3


def test_values_that_change_between_runs_of_one_server_are_wildcards():
    report = mitm.diff_runs(
        run_flow(name="Pilot A", session_id=100, call_id=7),
        run_flow(name="Pilot B", session_id=200, call_id=9),
        run_flow(name="Pilot C", session_id=300, call_id=4),
        run_flow(name="Pilot D", session_id=400, call_id=5),
    )

    assert report["identical"] is True
    assert {item["path"] for item in report["wildcards"]} == {"/result", "/args/$tuple/0/$long"}
    assert report["summary"]["wildcards"] == 2


def test_a_value_that_is_stable_within_each_server_is_compared_exactly():
    report = mitm.diff_runs(
        run_flow(session_id=100),
        run_flow(session_id=100),
        run_flow(session_id=300),
        run_flow(session_id=300),
    )

    assert report["identical"] is False
    assert report["wildcards"] == []
    assert report["differences"] == [{
        "record": "conn1 Notification notificationMgr.OnServerSkillsChanged#1",
        "path": "/args/$tuple/0/$long",
        "kind": "value",
        "reference": ["int", 100],
        "candidate": ["int", 300],
    }]


def test_a_wildcard_still_has_to_keep_its_marshal_type():
    report = mitm.diff_runs(
        run_flow(reply_value="Pilot A"),
        run_flow(reply_value="Pilot B"),
        run_flow(reply_value=5),
        run_flow(reply_value=6),
    )

    assert report["identical"] is False
    assert [(item["kind"], item["path"]) for item in report["differences"]] == [("wildcard_type", "/result")]
    assert report["differences"][0]["reference"] == ["str", "Pilot A"]
    assert report["differences"][0]["candidate"] == ["int", 5]


def test_a_record_seen_in_only_one_run_of_a_server_is_unstable_not_a_difference():
    extra = [notice(4, "OnOnlyOnce")]

    report = mitm.diff_runs(run_flow(extra=extra), run_flow(), run_flow(), run_flow())

    assert report["identical"] is False
    assert report["differences"] == []
    assert report["unstable_records"] == [
        {"side": "reference", "record": "conn1 Notification notificationMgr.OnOnlyOnce#1"},
    ]


def test_a_record_missing_from_the_candidate_in_both_runs_is_a_difference():
    report = mitm.diff_runs(run_flow(extra=[notice(4, "OnTutorial")]), run_flow(extra=[notice(4, "OnTutorial")]),
                            run_flow(), run_flow())

    assert report["identical"] is False
    assert report["unstable_records"] == []
    assert report["differences"] == [{
        "record": "conn1 Notification notificationMgr.OnTutorial#1",
        "kind": "missing_record",
    }]


def test_diff_runs_command_exit_codes_and_json(tmp_path, capsys):
    same = [write(tmp_path / f"{name}.jsonl", run_flow()) for name in ("ref1", "ref2", "cand1", "cand2")]
    args = ["diff-runs", *map(str, same)]

    assert mitm.main(args) == 0
    assert "identical under run-to-run wildcard rules" in capsys.readouterr().out
    assert mitm.main([*args, "--json"]) == 0
    assert json.loads(capsys.readouterr().out)["identical"] is True

    changed = write(tmp_path / "cand3.jsonl", run_flow(session_id=300))
    args = ["diff-runs", str(same[0]), str(same[1]), str(changed), str(changed)]

    assert mitm.main(args) == 1
    assert "differences: " in capsys.readouterr().out
    assert mitm.main([*args, "--json"]) == 1
    report = json.loads(capsys.readouterr().out)
    assert report["summary"]["differences"] == 1

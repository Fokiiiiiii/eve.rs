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


def call(seq, method, service="machoNet", call_id=1, args=None, conn=1):
    return {"seq": seq, "dir": "c2s", "conn": conn, "stage": "macho", "encoding": "marshal", "type": "CallReq",
            "service": service, "method": method, "call_id": call_id, "args": args or {"$tuple": []}}


def result(seq, value, service="machoNet", call_id=1, conn=1):
    return {"seq": seq, "dir": "s2c", "conn": conn, "stage": "macho", "encoding": "marshal", "type": "CallRsp",
            "service": service, "call_id": call_id, "result": value}


def write(path, records):
    path.write_text("".join(json.dumps(r) + "\n" for r in records), encoding="utf-8")
    return path


def diff(tmp_path, reference, candidate, *, ignore):
    a = write(tmp_path / "a.jsonl", reference)
    b = write(tmp_path / "b.jsonl", candidate)
    args = ["diff", str(a), str(b)] + (["--ignore-volatile"] if ignore else [])
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

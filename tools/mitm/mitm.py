#!/usr/bin/env python3
"""A man-in-the-middle proxy for the EVE client: forwards bytes unchanged and logs every MachoNet packet.

    tools/mitm/mitm capture --upstream 127.0.0.1:26000 --out captures/evejs-login.jsonl
    tools/mitm/mitm show captures/evejs-login.jsonl --service machoNet --direction s2c
    tools/mitm/mitm diff captures/evejs-login.jsonl captures/eve-rs-login.jsonl --ignore-volatile

Standard library plus the decoders of tools/headless-client (evecli). The wire is the client's own: a
u32 little-endian length, then a marshal payload, in both directions. A payload that starts with 0x78 is
zlib-wrapped (the client does this for its inbound packets). Nothing is encrypted (the Placebo handshake).
A payload that does not decode is logged as raw hex and forwarded all the same.
"""

from __future__ import annotations

import argparse
import json
import re
import socket
import struct
import sys
import threading
import time
import zlib
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "headless-client"))
from evecli import marshal as m  # noqa: E402
from evecli import packet as p  # noqa: E402
from evecli.describe import describe, differences, shape  # noqa: E402

MAX_FRAME = 16 * 1024 * 1024
MACHO_NAMES = {code: guid.split(".", 1)[1] for code, guid in p._GUID.items()}
NOTIFICATION_TYPES = {"Notification", "SessionChangeNotification", "SessionInitialStateNotification"}
# What --ignore-volatile does not compare: values under these dictionary keys (ids, times, seeds) are compared
# by type only, the bound-object ids of an OID+/OID- address are masked, and GetTime results by type.
VOLATILE_KEYS = frozenset({
    "sessionID", "sessionid", "session_id", "sessionKey", "clientID", "clientid", "user_clientid", "callID",
    "callId", "time", "timestamp", "fileTime", "now", "seed", "randomSeed", "nonce", "serverTime",
})
VOLATILE_METHODS = frozenset({"GetTime", "GetCurrentTime"})
SESSION_TYPES = frozenset({"SessionChangeNotification", "SessionInitialStateNotification"})


def _text(value):
    if isinstance(value, (bytes, bytearray)):
        return bytes(value).decode("utf-8", "replace")
    return value if isinstance(value, str) else None


def _call_body(payload):
    """(method, args, kwargs) of a CallReq or Notification payload: ((flag, SubStream((1, method, args, kwargs))),)."""
    item = payload[0] if isinstance(payload, (tuple, list)) and len(payload) == 1 else payload
    if isinstance(item, (tuple, list)) and len(item) == 2 and isinstance(item[0], int):
        item = item[1]
    value = item.value if isinstance(item, m.SubStream) else item
    if isinstance(value, tuple) and len(value) >= 3 and _text(value[1]) is not None:
        return _text(value[1]), value[2], (value[3] if len(value) > 3 else None)
    return None, None, None


def _is_macho_packet(name):
    """The client's packet classes (macho.CallReq) and the reference server's (carbon...machoNetPacket.CallReq)."""
    text = _text(name) or ""
    return text.startswith("macho.") or "machoNetPacket." in text


def macho_record(pk, encoding):
    """One decoded MachoNet packet as a JSON-serialisable record (no timestamps: see Journal.write)."""
    kind = MACHO_NAMES.get(pk.command, f"type{pk.command}")
    rec = {"stage": "macho", "encoding": encoding, "type": kind}
    rec["service"] = pk.destination.service if pk.destination.service is not None else pk.source.service
    rec["call_id"] = next((c for c in (pk.source.call_id, pk.destination.call_id) if c is not None), None)
    if kind in ("CallReq", "Notification"):
        rec["method"], args, kwargs = _call_body(pk.payload)
        rec["args"] = describe(args)
        if kwargs is not None:
            rec["kwargs"] = describe(kwargs)
    elif kind == "CallRsp":
        rec["result"] = describe(p.result_of(pk))
    elif kind == "ErrorResponse":
        error = p.MachoError.from_packet(pk)
        rec["error"] = {"code": describe(error.code), "payload": describe(error.payload)}
    elif kind.startswith("Ping"):
        pass  # the timings in a ping are per-run
    else:
        rec["payload"] = describe(pk.payload)
    return rec


class Journal:
    """The capture file (one JSON object per line) and, optionally, a readable line per record on stdout."""

    def __init__(self, path, *, echo=None):
        self.file = None
        if path:
            Path(path).parent.mkdir(parents=True, exist_ok=True)
            self.file = open(path, "w", encoding="utf-8")
        self.echo = echo
        self.lock = threading.Lock()
        self.seq = 0
        self.conns = 0
        self.started = time.monotonic()

    def next_conn(self):
        with self.lock:
            self.conns += 1
            return self.conns

    def write(self, rec):
        with self.lock:
            self.seq += 1
            rec = {"seq": self.seq, "t": int((time.monotonic() - self.started) * 1000), **rec}
            if self.file:
                self.file.write(json.dumps(rec, ensure_ascii=False) + "\n")
                self.file.flush()
            if self.echo and self.echo(rec):
                print(render(rec), flush=True)

    def close(self):
        if self.file:
            self.file.close()


class Stream:
    """One direction of one connection: frames the bytes it is shown and records what each frame holds."""

    def __init__(self, direction, conn, journal):
        self.direction, self.conn, self.journal = direction, conn, journal
        self.buffer = bytearray()
        self.dead = False

    def _emit(self, rec):
        self.journal.write({"dir": self.direction, "conn": self.conn, **rec})

    def feed(self, data):
        if self.dead:
            return
        try:
            self.buffer += data
            while not self.dead and len(self.buffer) >= 4:
                (length,) = struct.unpack_from("<I", self.buffer, 0)
                if length > MAX_FRAME:
                    self._emit({"stage": "undecoded", "error": f"frame length {length} exceeds {MAX_FRAME}; this direction is no longer parsed"})
                    self.dead = True
                    return
                if len(self.buffer) < 4 + length:
                    return
                payload = bytes(self.buffer[4 : 4 + length])
                del self.buffer[: 4 + length]
                self._frame(payload)
        except Exception as error:  # the bridge must keep forwarding whatever the analysis does
            self._emit({"stage": "undecoded", "error": f"{type(error).__name__}: {error}"})
            self.dead = True

    def _frame(self, payload):
        encoding = "marshal"
        try:
            if payload[:1] == b"\x78":
                payload = zlib.decompress(payload)
                encoding = "zlib"
            value = m.decode(payload)
        except Exception as error:
            self._emit({"stage": "undecoded", "encoding": encoding, "bytes": len(payload),
                        "error": f"{type(error).__name__}: {error}", "hex": payload[:256].hex()})
            return
        if isinstance(value, m.Object) and _is_macho_packet(value.name):
            try:
                self._emit(macho_record(p.Packet.parse(value), encoding))
                return
            except Exception as error:
                self._emit({"stage": "undecoded", "encoding": encoding, "error": f"macho packet: {error}", "hex": payload[:256].hex()})
                return
        self._emit({"stage": "handshake", "encoding": encoding, "value": describe(value)})


def _pump(src, dst, stream):
    try:
        while True:
            data = src.recv(65536)
            if not data:
                break
            dst.sendall(data)  # forward first: the analysis must not delay or change the bytes
            stream.feed(data)
    except OSError:
        pass
    finally:
        try:
            dst.shutdown(socket.SHUT_WR)
        except OSError:
            pass


def _bridge(client, upstream, journal, conn):
    journal.write({"stage": "event", "conn": conn, "note": f"client connected from {client.getpeername()[0]}"})
    try:
        server = socket.create_connection(upstream, timeout=10)
    except OSError as error:
        journal.write({"stage": "event", "conn": conn, "note": f"upstream {upstream[0]}:{upstream[1]} refused: {error}"})
        client.close()
        return
    server.settimeout(None)
    threads = [
        threading.Thread(target=_pump, args=(client, server, Stream("c2s", conn, journal)), daemon=True),
        threading.Thread(target=_pump, args=(server, client, Stream("s2c", conn, journal)), daemon=True),
    ]
    for thread in threads:
        thread.start()
    for thread in threads:
        thread.join()
    client.close()
    server.close()
    journal.write({"stage": "event", "conn": conn, "note": "closed"})


def _hostport(text):
    host, _, port = text.rpartition(":")
    return (host or "127.0.0.1", int(port))


def _filter(args):
    def keep(rec):
        if args.direction not in (None, "both") and rec.get("dir") != args.direction:
            return False
        if args.service is not None and rec.get("service") != args.service:
            return False
        if args.method is not None and rec.get("method") != args.method:
            return False
        if args.notifications_only and rec.get("type") not in NOTIFICATION_TYPES:
            return False
        return True
    return keep


def _brief(value, limit=220):
    text = json.dumps(value, ensure_ascii=False, separators=(",", ":"))
    return text if len(text) <= limit else text[: limit - 3] + "..."


def render(rec):
    """One readable line: C>S / S>C, the packet type, the service and method, the call id, the value."""
    if "dir" not in rec:
        return f"#{rec['seq']} -- {rec.get('note', '')}"
    arrow = "C>S" if rec["dir"] == "c2s" else "S>C"
    stage = rec["stage"]
    if stage == "handshake":
        return f"#{rec['seq']} {arrow} handshake {_brief(rec['value'])}"
    if stage == "undecoded":
        return f"#{rec['seq']} {arrow} UNDECODED {rec.get('bytes', '?')} bytes ({rec['error']}) hex={rec.get('hex', '')}"
    kind, service, call_id = rec["type"], rec.get("service"), rec.get("call_id")
    if kind == "CallReq":
        body = f"{service}.{rec.get('method')} args={_brief(rec.get('args'))}"
    elif kind == "CallRsp":
        body = f"{service} -> {_brief(rec.get('result'))}"
    elif kind == "ErrorResponse":
        body = f"{service} code={_brief(rec['error']['code'])} payload={_brief(rec['error']['payload'])}"
    elif kind == "Notification":
        body = f"{rec.get('method')} args={_brief(rec.get('args'))}"
    elif kind.startswith("Ping"):
        body = ""
    else:
        body = f"{service or ''} {_brief(rec.get('payload'))}"
    return f"#{rec['seq']} {arrow} {kind} call={call_id} {body}".rstrip()


def cmd_capture(args):
    listen, upstream = _hostport(args.listen), _hostport(args.upstream)
    keep = _filter(args)
    journal = Journal(args.out, echo=None if args.quiet else (lambda rec: "dir" not in rec or keep(rec)))
    server = socket.create_server(listen)
    print(f"mitm: {listen[0]}:{listen[1]} -> {upstream[0]}:{upstream[1]}, writing {args.out or 'nothing'}", file=sys.stderr)
    try:
        while True:
            client, _ = server.accept()
            threading.Thread(target=_bridge, args=(client, upstream, journal, journal.next_conn()), daemon=True).start()
    except KeyboardInterrupt:
        pass
    finally:
        server.close()
        journal.close()
    return 0


def cmd_show(args):
    keep = _filter(args)
    with open(args.capture, encoding="utf-8") as source:
        for line in source:
            rec = json.loads(line)
            if "dir" not in rec or keep(rec):
                print(render(rec))
    return 0


def load(path):
    with open(path, encoding="utf-8") as source:
        return [json.loads(line) for line in source if line.strip()]


def _normal_service(name, ignore_volatile):
    return re.sub(r"OID[+-]\d+", "OID", name) if (ignore_volatile and name) else name


def events(records, ignore_volatile):
    """Two streams, each in capture order: the calls the client made, and what the server sent.

    The two directions are not interleaved: a server notification can race the client's first call on a new
    connection, and that order depends on timing. Results and errors take the service and method of their call."""
    calls = {}
    for rec in records:
        if rec.get("stage") == "macho" and rec["type"] == "CallReq":
            calls[(rec.get("conn"), rec.get("call_id"))] = (rec.get("service"), rec.get("method"))
    client, server = [], []
    for rec in records:
        if rec.get("stage") != "macho" or rec["type"].startswith("Ping"):
            continue
        kind, key = rec["type"], (rec.get("conn"), rec.get("call_id"))
        if kind == "CallReq":
            client.append(("call", _normal_service(rec.get("service"), ignore_volatile), rec.get("method"), rec.get("args")))
        elif kind in ("CallRsp", "ErrorResponse"):
            service, method = calls.get(key, (rec.get("service"), None))
            value = rec.get("result") if kind == "CallRsp" else rec.get("error")
            if ignore_volatile and method in VOLATILE_METHODS and kind == "CallRsp":
                value = shape(value)
            category = "result" if kind == "CallRsp" else "error"
            server.append((category, _normal_service(service, ignore_volatile), method, value))
        elif kind == "Notification":
            server.append(("notification", _normal_service(rec.get("service"), ignore_volatile), rec.get("method"), rec.get("args")))
        else:
            value = rec.get("payload")
            if ignore_volatile and kind in SESSION_TYPES and isinstance(value, dict) and value.get("$tuple"):
                # the payload leads with the session id: (sessionID, changeKind, changes)
                value = {"$tuple": [shape(value["$tuple"][0]), *value["$tuple"][1:]]}
            server.append((kind, None, None, value))
    return client, server


def _first_divergence(reference, candidate, ignore_volatile):
    """The first divergence of two event lists, or None: call order, then shape, then values."""
    volatile = VOLATILE_KEYS if ignore_volatile else frozenset()
    for index, (a, b) in enumerate(zip(reference, candidate)):
        if a[:3] != b[:3]:
            return index, "call order", [f"reference {a[:3]}", f"candidate {b[:3]}"]
        if shape(a[3]) != shape(b[3]):
            return index, f"{a[0]} shape", differences(shape(a[3]), shape(b[3]))[:10]
        found = differences(a[3], b[3], volatile_keys=volatile)
        if found:
            return index, f"{a[0]} value", found[:10]
    if len(reference) != len(candidate):
        longer = "reference" if len(reference) > len(candidate) else "candidate"
        extra = abs(len(reference) - len(candidate))
        return min(len(reference), len(candidate)), "length", [f"{longer} has {extra} more events"]
    return None


def compare(reference, candidate, ignore_volatile):
    """The first divergence in the client's calls, else in the server's events: (stream, index, kind, details, a, b)."""
    for stream, a, b in (("client calls", reference[0], candidate[0]), ("server events", reference[1], candidate[1])):
        found = _first_divergence(a, b, ignore_volatile)
        if found:
            index, kind, details = found
            at_a = a[index] if index < len(a) else None
            at_b = b[index] if index < len(b) else None
            return stream, index, kind, details, at_a, at_b, max(len(a), len(b))
    return None


def cmd_diff(args):
    reference, candidate = events(load(args.reference), args.ignore_volatile), events(load(args.candidate), args.ignore_volatile)
    mode = "volatile fields ignored" if args.ignore_volatile else "exact values"
    found = compare(reference, candidate, args.ignore_volatile)
    if found is None:
        print(f"identical: {len(reference[0])} client calls and {len(reference[1])} server events, {mode}")
        return 0
    stream, index, kind, details, at_a, at_b, total = found
    print(f"first divergence at event {index} of {total} in {stream} ({kind}; {mode})")
    print(f"  reference: {_brief(at_a, 400) if at_a is not None else '(none)'}")
    print(f"  candidate: {_brief(at_b, 400) if at_b is not None else '(none)'}")
    for line in details:
        print(f"  - {line}")
    return 1


def main(argv=None):
    parser = argparse.ArgumentParser(prog="mitm", description=__doc__.split("\n")[0])
    sub = parser.add_subparsers(dest="command", required=True)

    def filters(command):
        command.add_argument("--service", help="only packets to or from this service (or bound object id)")
        command.add_argument("--method", help="only calls and notifications of this method")
        command.add_argument("--direction", choices=["c2s", "s2c", "both"], default="both")
        command.add_argument("--notifications-only", action="store_true", help="only notifications and session changes")

    capture = sub.add_parser("capture", help="listen, forward to the upstream server and log every packet")
    capture.add_argument("--upstream", required=True, help="host:port of the real server (eve.rs or EVE.js)")
    capture.add_argument("--listen", default="127.0.0.1:26200", help="host:port the client connects to (default 127.0.0.1:26200; the servers default to 26000 and 26100)")
    capture.add_argument("--out", help="capture file (JSON lines); the file always holds every packet, filters only shape the console")
    capture.add_argument("--quiet", action="store_true", help="do not print the readable lines")
    filters(capture)
    capture.set_defaults(run=cmd_capture)

    show = sub.add_parser("show", help="print a capture file as readable lines")
    show.add_argument("capture")
    filters(show)
    show.set_defaults(run=cmd_show)

    diff = sub.add_parser("diff", help="compare two captures by call order, argument and result shape, then values")
    diff.add_argument("reference", help="the reference capture (EVE.js)")
    diff.add_argument("candidate", help="the capture to check (eve.rs)")
    diff.add_argument("--ignore-volatile", action="store_true",
                      help="ignore ids, session ids, times, random seeds and bound-object ids (see README)")
    diff.set_defaults(run=cmd_diff)

    args = parser.parse_args(argv)
    return args.run(args)


if __name__ == "__main__":
    sys.exit(main())

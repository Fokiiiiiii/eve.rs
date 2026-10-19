"""A scripted MachoNet server: the server half of the login handshake and a few
services, built from the message shapes of the JS reference (handshake.js,
packetDispatcher.js). It lets the client be tested without game data."""

import binascii
import socket
import struct
import threading
import time

from evecli import marshal as m
from evecli import packet as p
from evecli.handshake import password_hash

FILETIME_UNIX_EPOCH = 116444736000000000

def _pickle_str(text: str) -> bytes:
    data = text.encode("utf-8")
    return b"X" + struct.pack("<I", len(data)) + data

def transport_closed(reason: str) -> m.CPickled:
    """The cPickle of `exceptions.GPSTransportClosed(reason, code, {})` the server sends on a refusal."""
    return m.CPickled(
        b"\x80\x02cexceptions\nGPSTransportClosed\n"
        + _pickle_str(reason) + _pickle_str(reason) + b"}" + b"\x87" + b"R."
    )

class MockServer:
    def __init__(self, accounts=None, auto_create=False, macho_version=496):
        self.accounts = dict(accounts or {})  # user name -> password hash bytes
        self.auto_create = auto_create
        self.macho_version = macho_version
        self.received = []  # (kind, value) records for assertions
        self.errors = []
        self._listener = socket.socket()
        self._listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        self._listener.bind(("127.0.0.1", 0))
        self._listener.listen(4)
        self.port = self._listener.getsockname()[1]
        self._thread = threading.Thread(target=self._serve, daemon=True)
        self._stop = threading.Event()

    def add_account(self, user: str, password: str) -> None:
        self.accounts[user] = password_hash(user, password)

    def __enter__(self):
        self._thread.start()
        return self

    def __exit__(self, *exc):
        self._stop.set()
        try:
            socket.create_connection(("127.0.0.1", self.port), timeout=1).close()
        except OSError:
            pass
        self._listener.close()
        self._thread.join(timeout=5)

    # ── framing ──

    @staticmethod
    def _send(sock, value):
        sock.sendall(m.encode_packet(value))

    @staticmethod
    def _recv(sock, buffer):
        while True:
            split = m.split_packet(buffer[0])
            if split:
                payload, buffer[0] = split
                return m.decode(payload)
            chunk = sock.recv(65536)
            if not chunk:
                raise ConnectionError("client closed")
            buffer[0] += chunk

    # ── the server ──

    def _serve(self):
        while not self._stop.is_set():
            try:
                sock, _ = self._listener.accept()
            except OSError:
                return
            if self._stop.is_set():
                sock.close()
                return
            sock.settimeout(10)
            try:
                self._session(sock)
            except (ConnectionError, OSError, socket.timeout):
                pass
            except Exception as error:  # surfaced to the test through .errors
                self.errors.append(error)
            finally:
                sock.close()

    def _session(self, sock):
        buffer = [b""]
        send = lambda value: self._send(sock, value)  # noqa: E731
        recv = lambda: self._recv(sock, buffer)  # noqa: E731

        send((170472, self.macho_version, 0, 24.01, 3396210, "mock@local", None))
        version = recv()
        assert isinstance(version, tuple) and len(version) >= 6, version
        self.received.append(("version", version))

        command = recv()
        assert isinstance(command, tuple) and len(command) == 3 and bytes(command[1]) == b"VK", command
        self.received.append(("vk", command))

        crypto = recv()
        self.received.append(("crypto", crypto))
        send("OK CC")

        challenge = recv()
        login = challenge[1].as_dict()
        self.received.append(("login", login))
        send(2)
        user = bytes(login["user_name"]).decode() if isinstance(login["user_name"], bytes) else login["user_name"]
        supplied = bytes(login["user_password_hash"])
        known = self.accounts.get(user)
        if known is None and self.auto_create:
            self.accounts[user] = known = supplied
        if known is None or known != supplied:
            send(transport_closed("LoginAuthFailed"))
            return

        client_id = 1_000_000 * 7 + 1
        signed_func = m.Buffer(b"t\x04\x00\x00\x00None")
        send((
            "",
            (signed_func, False),
            m.Dict(),
            m.Dict([
                ("challenge_responsehash", "55087"),
                ("macho_version", self.macho_version),
                ("boot_version", 24.01),
                ("boot_build", 3396210),
                ("cluster_usercount", 0),
                ("proxy_nodeid", 0xFFAA),
                ("user_logonqueueposition", 1),
                ("config_vals", m.Dict([("imageserverurl", "http://127.0.0.1:26001/")])),
            ]),
        ))
        result = recv()
        assert isinstance(result, tuple) and len(result) == 3, result
        self.received.append(("handshake-result", result))

        send(m.Dict([
            ("live_updates", m.List()),
            ("session_init", m.Dict([
                ("languageID", "EN"), ("countryCode", "GB"), ("userid", 7),
                ("userType", 30), ("role", m.Long(0x6000_0000_8000_0000)), ("address", "127.0.0.1"), ("inDetention", None),
            ])),
            ("sessionID", m.Long(int(time.time() * 1000) * 15)),
            ("client_hash", None),
            ("user_clientid", m.Long(client_id)),
        ]))

        while True:
            self._dispatch(recv(), send, client_id)

    def _dispatch(self, raw, send, client_id):
        pkt = p.Packet.parse(raw)
        self.received.append(("packet", pkt))
        call_id = pkt.source.call_id or pkt.destination.call_id or 0
        reply_to = p.client_address(m.Long(client_id), call_id)

        if pkt.command == p.PING_REQ:
            now = m.Long(int(time.time() * 1e7) + FILETIME_UNIX_EPOCH)
            times = m.List([(now - 20, now, "proxy::handle_message")])
            send(p.build_packet(p.PING_RSP, pkt.destination and p.any_address(), reply_to, 7, (times,), m.Dict()))
            return

        assert pkt.command == p.CALL_REQ, pkt.command
        body = pkt.payload[0][1].value
        _, method, args, kwargs = body
        method = bytes(method).decode() if isinstance(method, bytes) else method
        service = pkt.destination.service

        if (service, method) == ("machoNet", "GetServiceInfo"):
            result = m.Dict([
                ("machoNet", None),
                ("echo", None),
                ("stationSvc", "station"),
                ("invbroker", "station"),
                ("beyonce", "solarsystem2"),
            ])
        elif (service, method) == ("machoNet", "GetTime"):
            result = m.Long(int(time.time() * 1e7) + FILETIME_UNIX_EPOCH)
        elif (service, method) == ("echo", "Echo"):
            result = (args, kwargs if isinstance(kwargs, m.Dict) else None)
        else:
            error = (p.CALL_REQ, 2, (m.SubStream(value=f"no such service method {service}.{method}"),))
            send(p.build_packet(p.ERRORRESPONSE, p.any_address(service), reply_to, 7, error, m.Dict()))
            return

        response = p.build_packet(p.CALL_RSP, p.any_address(service), reply_to, 7, (m.SubStream(value=result),), m.Dict())
        response.name = "carbon.common.script.net.machoNetPacket.CallRsp"
        send(response)

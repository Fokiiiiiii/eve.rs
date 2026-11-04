"""The login handshake (the client half of EVE's "Placebo" GPS handshake).

    1. S -> C  VersionExchangeServer  (birthday, machoVersion, userCount, bootVersion, bootBuild, project, None)
    2. C -> S  VersionExchangeClient  the same shape
    3. C -> S  (None, "VK", key)
    4. C -> S  CryptoRequest          ("placebo", {})          S -> C  "OK CC"
    5. C -> S  CryptoChallenge        (challenge, {user_name, user_password_hash, ...})
       S -> C  2                      (the server wants hashed passwords)
       S -> C  CryptoServerHandshake  (challenge, (signedFunc, verify), {}, {macho_version, ...})
    6. C -> S  CryptoHandshakeResult  (hash(challenge), funcOutput, funcResult)
    7. S -> C  ack dict               {session_init, sessionID, user_clientid, ...}

Only the Placebo crypto pack is spoken: nothing is encrypted. The server's
`signedFunc` is Python 2 code meant to run inside the real client; a headless
client does not run it and answers with an empty result.
"""

from __future__ import annotations

import binascii
import hashlib
import os
import re
import socket
import struct
from dataclasses import dataclass, field
from typing import Any, Optional

from . import marshal as m

class HandshakeError(Exception):
    pass

class TransportClosed(HandshakeError):
    """The server closed an established transport with a reason."""

    def __init__(self, reason: str):
        super().__init__(f"transport closed: {reason}")
        self.reason = reason


class LoginRejected(HandshakeError):
    """The server refused the login and closed the transport."""

    def __init__(self, reason: str):
        super().__init__(f"login rejected: {reason}")
        self.reason = reason

def password_hash(user: str, password: str) -> bytes:
    """The client's `machobase.PasswordHash`: SHA-1 over the UTF-16LE password and a
    salt (the lower-cased, stripped user name in UTF-16LE), then 1000 more rounds
    of SHA-1 over digest + salt."""
    salt = user.strip().encode("utf-16-le").lower()
    digest = hashlib.sha1(password.encode("utf-16-le") + salt).digest()
    for _ in range(1000):
        digest = hashlib.sha1(digest + salt).digest()
    return digest

class Connection:
    """A framed marshal connection: [u32 length][payload] in both directions."""

    def __init__(self, sock: socket.socket, timeout: float = 10.0):
        self.sock = sock
        self.sock.settimeout(timeout)
        self.timeout = timeout
        self._buffer = b""

    def send(self, value) -> None:
        self.sock.sendall(m.encode_packet(value))

    def recv_payload(self) -> bytes:
        while True:
            split = m.split_packet(self._buffer)
            if split is not None:
                payload, self._buffer = split
                return payload
            chunk = self.sock.recv(65536)
            if not chunk:
                raise HandshakeError("connection closed by the server")
            self._buffer += chunk

    def recv(self):
        return m.decode(self.recv_payload())

    def close(self) -> None:
        try:
            self.sock.close()
        except OSError:
            pass

@dataclass
class ServerVersion:
    birthday: Any
    macho_version: Any
    user_count: Any
    boot_version: Any
    boot_build: Any
    project: Any

@dataclass
class SessionInfo:
    """What the login gave us."""

    server: ServerVersion
    user_name: str
    user_id: int
    client_id: Any
    session_id: Any
    role: Any
    session_init: dict = field(default_factory=dict)
    handshake: dict = field(default_factory=dict)
    signed_func: Any = None

    def apply_session_change(self, session_id, changes) -> None:
        if session_id is not None:
            self.session_id = session_id
        values = m.plain(changes)
        if not isinstance(values, dict):
            values = _as_dict(changes)
        for key, old_new in values.items():
            name = _text(key)
            new_value = old_new[1] if isinstance(old_new, (tuple, list)) and len(old_new) >= 2 else old_new
            self.session_init[name] = new_value
        self._sync_identity()

    def apply_initial_state(self, session_id, initial_state) -> None:
        if session_id is not None:
            self.session_id = session_id
        values = m.plain(initial_state)
        if not isinstance(values, dict):
            values = _as_dict(initial_state)
        self.session_init = {_text(key): value for key, value in values.items()}
        self._sync_identity()

    def _sync_identity(self) -> None:
        self.user_id = self.session_init.get("userid", self.user_id)
        self.role = self.session_init.get("role", self.role)

def _text(value) -> str:
    if isinstance(value, (bytes, bytearray)):
        return bytes(value).decode("utf-8", "replace")
    return str(value)

def _as_dict(value) -> dict:
    return value.as_dict() if isinstance(value, m.Dict) else {}

_PICKLED_UNICODE = re.compile(rb"X(.{4})", re.DOTALL)

def _rejection_reason(value) -> Optional[str]:
    """The reason of a GPSTransportClosed pickle sent instead of a reply."""
    if isinstance(value, m.CPickled):
        payload = bytes(value).split(b"GPSTransportClosed", 1)[-1]
        found = _PICKLED_UNICODE.search(payload)
        if found is None:
            return "transport closed"
        length = int.from_bytes(found.group(1), "little")
        start = found.end()
        text = payload[start:start + length]
        return text.decode("utf-8", "replace") if len(text) == length else "transport closed"
    return None

def login(conn: Connection, user: str, password: str, *, language: str = "EN", sso_token: str | None = None) -> SessionInfo:
    """Run the handshake on `conn` and return the session it opened."""
    first = conn.recv()
    if not isinstance(first, tuple) or len(first) < 6:
        raise HandshakeError(f"unexpected VersionExchangeServer: {first!r}")
    server = ServerVersion(*first[:6])

    # 2. VersionExchangeClient: echo the server's release (the check is one-sided)
    conn.send((server.birthday, server.macho_version, 0, server.boot_version, server.boot_build,
               server.project, None))
    # 3. VK
    conn.send((None, "VK", None))
    # 4. CryptoRequest with no keys: the Placebo pack encrypts nothing
    conn.send(("placebo", m.Dict()))
    ok = conn.recv()
    if _text(ok) != "OK CC":
        raise HandshakeError(f"expected 'OK CC', got {ok!r}")

    # 5. CryptoChallenge (login)
    login_dict = m.Dict([
        ("macho_version", server.macho_version),
        ("boot_version", server.boot_version),
        ("boot_build", server.boot_build),
        ("boot_codename", "evecli"),
        ("boot_region", "local"),
        ("user_name", user),
        ("user_password", None),
        ("user_password_hash", m.Buffer(password_hash(user, password))),
        ("user_languageid", language),
        ("user_affiliateid", ""),
        ("user_sso_token", sso_token),
    ])
    conn.send((m.Buffer(os.urandom(64)), login_dict))

    reply = conn.recv()
    reason = _rejection_reason(reply)
    if reason:
        raise LoginRejected(reason)
    if reply != 2:
        raise HandshakeError(f"expected password version 2, got {reply!r}")

    server_handshake = conn.recv()
    reason = _rejection_reason(server_handshake)
    if reason:
        raise LoginRejected(reason)
    if not isinstance(server_handshake, tuple) or len(server_handshake) < 4:
        raise HandshakeError(f"unexpected CryptoServerHandshake: {server_handshake!r}")
    challenge, signed_func, _context, handshake = server_handshake[:4]

    # 6. CryptoHandshakeResult. The Placebo crypto hash is the CRC-HQX of the marshalled value.
    challenge_hash = str(binascii.crc_hqx(m.encode(challenge), 0))
    conn.send((challenge_hash, m.PyString(b""), None))

    # 7. The ack
    ack = conn.recv()
    reason = _rejection_reason(ack)
    if reason:
        raise LoginRejected(reason)
    if not isinstance(ack, m.Dict):
        raise HandshakeError(f"unexpected handshake ack: {ack!r}")
    fields = ack.as_dict()
    session_init = _as_dict(fields.get("session_init"))
    return SessionInfo(
        server=server,
        user_name=user,
        user_id=session_init.get("userid", 0),
        client_id=fields.get("user_clientid"),
        session_id=fields.get("sessionID"),
        role=session_init.get("role"),
        session_init=session_init,
        handshake=_as_dict(handshake),
        signed_func=signed_func,
    )

def login_sso(conn: Connection, token: str, *, language: str = "EN") -> SessionInfo:
    """Run the V24 EVE-SSO-CONNECTION handshake with its inbound SSO access token."""
    return login(conn, "EVE-SSO-CONNECTION", "fakePW", language=language, sso_token=token)


def connect(host: str, port: int, timeout: float = 10.0) -> Connection:
    return Connection(socket.create_connection((host, port), timeout=timeout), timeout)

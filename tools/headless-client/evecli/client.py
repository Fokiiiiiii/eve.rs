"""A headless EVE client: log in, call services, ping."""

from __future__ import annotations

import time
from typing import Any, Optional

from . import handshake, packet
from . import marshal as m

# Windows FILETIME (100 ns ticks since 1601-01-01) to Unix seconds.
_FILETIME_EPOCH_OFFSET = 116444736000000000


def filetime_to_unix(filetime: int) -> float:
    return (int(filetime) - _FILETIME_EPOCH_OFFSET) / 1e7


class Client:
    def __init__(self, host: str = "127.0.0.1", port: int = 26000, timeout: float = 10.0):
        self.host, self.port, self.timeout = host, port, timeout
        self.conn: Optional[handshake.Connection] = None
        self.session: Optional[handshake.SessionInfo] = None
        self.notifications: list = []
        self._call_id = 0

    # ── connection ──

    def login(self, user: str, password: str) -> handshake.SessionInfo:
        self.conn = handshake.connect(self.host, self.port, self.timeout)
        try:
            self.session = handshake.login(self.conn, user, password)
        except Exception:
            self.close()
            raise
        return self.session

    def close(self) -> None:
        if self.conn is not None:
            self.conn.close()
            self.conn = None

    def __enter__(self) -> "Client":
        return self

    def __exit__(self, *exc) -> None:
        self.close()

    # ── packets ──

    def _next_call_id(self) -> int:
        self._call_id += 1
        return self._call_id

    def _source(self, call_id: int) -> tuple:
        assert self.session is not None
        return packet.client_address(self.session.client_id, call_id)

    def _await(self, command: int, call_id: int) -> packet.Packet:
        """Read packets until the response to `call_id` arrives; keep the rest."""
        assert self.conn is not None
        deadline = time.monotonic() + self.timeout
        while True:
            if time.monotonic() > deadline:
                raise TimeoutError(f"no response to call {call_id}")
            received = packet.Packet.parse(self.conn.recv())
            if received.command == packet.ERRORRESPONSE:
                raise packet.MachoError.from_packet(received)
            if received.command == command and received.destination.call_id in (call_id, None):
                return received
            self.notifications.append(received)

    def call(self, service: str, method: str, *args: Any, **kwargs: Any) -> Any:
        """Call `service.method(*args, **kwargs)` and return its result."""
        assert self.conn is not None and self.session is not None, "not logged in"
        call_id = self._next_call_id()
        request = packet.build_packet(
            packet.CALL_REQ,
            self._source(call_id),
            packet.any_address(service),
            self.session.user_id,
            packet.call_payload(method, args, kwargs),
        )
        self.conn.send(request)
        return packet.result_of(self._await(packet.CALL_RSP, call_id))

    def ping(self) -> float:
        """Round-trip time of a PingReq, in seconds."""
        assert self.conn is not None and self.session is not None, "not logged in"
        call_id = self._next_call_id()
        started = time.monotonic()
        self.conn.send(packet.build_packet(
            packet.PING_REQ, self._source(call_id), packet.any_address(), self.session.user_id, (),
        ))
        self._await(packet.PING_RSP, call_id)
        return time.monotonic() - started

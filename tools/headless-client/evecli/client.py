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
        self.service_info: dict[str, Any] = {}
        self.global_config: dict[str, Any] = {}
        self._service_nodes: dict[str, Any] = {}

    # ── connection ──

    def login(self, user: str, password: str) -> handshake.SessionInfo:
        self.close()
        self._call_id = 0
        self.conn = handshake.connect(self.host, self.port, self.timeout)
        try:
            self.session = handshake.login(self.conn, user, password)
            self.call("machoNet", "GetServiceInfo")
        except Exception:
            self.close()
            raise
        return self.session

    def close(self) -> None:
        if self.conn is not None:
            self.conn.close()
        self.conn = None
        self.session = None
        self.service_info.clear()
        self.global_config.clear()
        self._service_nodes.clear()
        self.notifications.clear()

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

    @staticmethod
    def _address_text(value: Any) -> str:
        if isinstance(value, (bytes, bytearray)):
            return bytes(value).decode("utf-8", "replace")
        return str(value)

    def _store_service_info(self, value: Any, *, init_vals: bool = False) -> None:
        value = m.plain(value)
        if init_vals and isinstance(value, (tuple, list)):
            self.global_config = value[1] if len(value) > 1 and isinstance(value[1], dict) else {}
            value = value[0] if value else {}
        self.service_info = (
            {
                self._address_text(name): None if route is None else self._address_text(route)
                for name, route in value.items()
            }
            if isinstance(value, dict)
            else {}
        )

    def _address_for_service(self, service: str) -> tuple:
        node_id = self._service_nodes.get(service)
        where = self.service_info.get(service)
        if node_id is None and where:
            where = self._address_text(where)
            for cached_service, cached_node_id in self._service_nodes.items():
                cached_where = self.service_info.get(cached_service)
                if cached_where:
                    cached_where = self._address_text(cached_where)
                    if cached_where.startswith(where) or where.startswith(cached_where):
                        node_id = cached_node_id
                        break
        return (packet.ADDR_NODE, node_id, service, None) if node_id is not None else packet.any_address(service)

    def _remember_service_node(self, service: str, response: packet.Packet) -> None:
        if service and response.source.kind == packet.ADDR_NODE and response.source.node_id is not None:
            self._service_nodes[service] = response.source.node_id

    def _apply_server_notification(self, received: packet.Packet) -> bool:
        if self.session is None:
            return False
        payload = m.plain(received.payload)
        if not isinstance(payload, (tuple, list)):
            return False
        if received.command == packet.SESSIONCHANGENOTIFICATION and len(payload) >= 2:
            change = payload[1]
            if isinstance(change, (tuple, list)) and len(change) >= 2:
                change = change[1]
            self.session.apply_session_change(payload[0], change)
            return True
        if received.command == packet.SESSIONINITIALSTATENOTIFICATION and len(payload) >= 3:
            self.session.apply_initial_state(payload[0], payload[-1])
            return True
        return False

    def _await(self, command: int, call_id: int) -> packet.Packet:
        """Read packets until the response to `call_id` arrives; apply session changes en route."""
        assert self.conn is not None
        deadline = time.monotonic() + self.timeout
        while True:
            if time.monotonic() > deadline:
                raise TimeoutError(f"no response to call {call_id}")
            received = packet.Packet.parse(self.conn.recv())
            if received.command in (packet.SESSIONCHANGENOTIFICATION, packet.SESSIONINITIALSTATENOTIFICATION):
                self._apply_server_notification(received)
                self.notifications.append(received)
                continue
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
            self._address_for_service(service),
            self.session.user_id,
            packet.call_payload(method, args, kwargs),
        )
        self.conn.send(request)
        response = self._await(packet.CALL_RSP, call_id)
        self._remember_service_node(service, response)
        result = packet.result_of(response)
        if service == "machoNet" and method == "GetServiceInfo":
            self._store_service_info(result)
        elif service == "machoNet" and method == "GetInitVals":
            self._store_service_info(result, init_vals=True)
        return result

    @staticmethod
    def bound_object(handle: Any) -> Any:
        """The `(oid, time)` a bind answer carries (`substruct(substream((oid, time)))`)."""
        while hasattr(handle, "value"):
            handle = handle.value
        return handle

    def call_bound(self, handle: Any, method: str, *args: Any, **kwargs: Any) -> Any:
        """Call a method of a bound object: `handle` is what a service returned to hand it out. The object is named in the
        first element of the call body instead of in the address."""
        assert self.conn is not None and self.session is not None, "not logged in"
        call_id = self._next_call_id()
        keywords = m.Dict(list(kwargs.items())) if kwargs else None
        body = (self.bound_object(handle), method, tuple(args), keywords)
        request = packet.build_packet(
            packet.CALL_REQ,
            self._source(call_id),
            packet.any_address(None),
            self.session.user_id,
            ((1, m.SubStream(value=body)),),
        )
        self.conn.send(request)
        return packet.result_of(self._await(packet.CALL_RSP, call_id))

    def ping(self) -> float:
        """Round-trip time of a PingReq, in seconds."""
        assert self.conn is not None and self.session is not None, "not logged in"
        call_id = self._next_call_id()
        started = time.perf_counter()
        self.conn.send(packet.build_packet(
            packet.PING_REQ, self._source(call_id), packet.any_address(), self.session.user_id, (),
        ))
        self._await(packet.PING_RSP, call_id)
        return time.perf_counter() - started

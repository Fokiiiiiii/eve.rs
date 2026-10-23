"""Small synchronous XMPP client for the headless integration walks."""

from __future__ import annotations

import base64
from collections import deque
from dataclasses import dataclass, field
from socket import create_connection
import ssl
import time
from typing import Callable
from xml.parsers import expat


@dataclass
class Element:
    name: str
    attributes: dict[str, str] = field(default_factory=dict)
    text: str = ""
    children: list[Element] = field(default_factory=list)

    def child(self, name: str) -> Element | None:
        return next((child for child in self.children if child.name == name), None)


class _StreamReader:
    def __init__(self) -> None:
        self.restart()

    def restart(self) -> None:
        """Reset the XML parser when SASL restarts the open XMPP stream."""
        self.parser = expat.ParserCreate()
        self.stack: list[Element] = []
        self.ready: deque[Element] = deque()
        self.parser.StartElementHandler = self._start
        self.parser.EndElementHandler = self._end
        self.parser.CharacterDataHandler = self._text

    @staticmethod
    def _local(name: str) -> str:
        return name.rsplit("}", 1)[-1].rsplit(":", 1)[-1]

    def _start(self, name: str, attributes: dict[str, str]) -> None:
        element = Element(self._local(name), {self._local(key): value for key, value in attributes.items()})
        if self.stack:
            self.stack[-1].children.append(element)
        self.stack.append(element)

    def _end(self, name: str) -> None:
        element = self.stack.pop()
        if self.stack and self.stack[-1].name == "stream":
            self.ready.append(element)

    def _text(self, value: str) -> None:
        if self.stack:
            self.stack[-1].text += value

    def feed(self, data: bytes) -> None:
        self.parser.Parse(data, False)


class XmppClient:
    """Enough of XMPP 1.0 for authenticated MUC and private-conversation walks."""

    def __init__(self, host: str, port: int, domain: str, *, tls: bool = True, timeout: float = 10.0):
        self.host = host
        self.port = port
        self.domain = domain
        self.tls = tls
        self.timeout = timeout
        self.socket = None
        self.reader = _StreamReader()
        self.pending: deque[Element] = deque()
        self.jid = ""

    def connect(self, character_id: int, token: str, resource: str) -> None:
        sock = create_connection((self.host, self.port), timeout=self.timeout)
        if self.tls:
            sock = ssl.create_default_context().wrap_socket(sock, server_hostname=self.domain)
        self.socket = sock
        self._open_stream()
        self._next(lambda item: item.name == "features")
        payload = base64.b64encode(f"\0{character_id}\0{token}".encode()).decode()
        self._send(f"<auth xmlns='urn:ietf:params:xml:ns:xmpp-sasl' mechanism='PLAIN'>{payload}</auth>")
        if self._next(lambda item: item.name in {"success", "failure"}).name != "success":
            raise RuntimeError("XMPP SASL authentication was rejected")
        self.reader.restart()
        self.pending.clear()
        self._open_stream()
        self._next(lambda item: item.name == "features")
        self._send(f"<iq type='set' id='bind'><bind xmlns='urn:ietf:params:xml:ns:xmpp-bind'><resource>{_escape(resource)}</resource></bind></iq>")
        bound = self._next(lambda item: item.name == "iq" and item.attributes.get("id") == "bind")
        if bound.attributes.get("type") != "result":
            raise RuntimeError("XMPP resource binding failed")
        jid = bound.child("bind")
        self.jid = jid.child("jid").text if jid and jid.child("jid") else ""
        self._send("<iq type='set' id='session'><session xmlns='urn:ietf:params:xml:ns:xmpp-session'/></iq>")
        established = self._next(lambda item: item.name == "iq" and item.attributes.get("id") == "session")
        if established.attributes.get("type") != "result":
            raise RuntimeError("XMPP session establishment failed")

    def join(self, room: str, nickname: str) -> None:
        address = f"{room}@conference.{self.domain}/{nickname}"
        self._send(f"<presence to='{_escape(address)}'><x xmlns='http://jabber.org/protocol/muc'/></presence>")
        self._next(lambda item: item.name == "presence" and item.attributes.get("from") == address
                   and item.child("x") is not None)

    def send_groupchat(self, room: str, body: str) -> None:
        self._send(f"<message to='{_escape(room)}@conference.{self.domain}' type='groupchat'><body>{_escape(body)}</body></message>")

    def invite(self, room: str, character_id: int, reason: str = "") -> None:
        reason_xml = f"<reason>{_escape(reason)}</reason>" if reason else ""
        self._send(f"<message to='{_escape(room)}@conference.{self.domain}'><x xmlns='http://jabber.org/protocol/muc#user'><invite to='{character_id}@{_escape(self.domain)}'>{reason_xml}</invite></x></message>")

    def wait_for_message(self, room: str, body: str) -> Element:
        return self._next(lambda item: item.name == "message"
                          and item.attributes.get("type") == "groupchat"
                          and item.attributes.get("from", "").startswith(f"{room}@conference.{self.domain}/")
                          and (item.child("body") is not None and item.child("body").text == body))

    def wait_for_invite(self, room: str, inviter_id: int) -> Element:
        return self._next(lambda item: item.name == "message"
                          and item.attributes.get("from", "").startswith(f"{room}@conference.{self.domain}")
                          and item.child("x") is not None
                          and item.child("x").child("invite") is not None
                          and item.child("x").child("invite").attributes.get("from") == f"{inviter_id}@{self.domain}")

    def _open_stream(self) -> None:
        self._send(f"<stream:stream to='{_escape(self.domain)}' version='1.0' xmlns='jabber:client' xmlns:stream='http://etherx.jabber.org/streams'>")

    def _send(self, text: str) -> None:
        if self.socket is None:
            raise RuntimeError("XMPP client is not connected")
        self.socket.sendall(text.encode("utf-8"))

    def _next(self, predicate: Callable[[Element], bool]) -> Element:
        deadline = time.monotonic() + self.timeout
        for _ in range(len(self.pending)):
            item = self.pending.popleft()
            if predicate(item):
                return item
            self.pending.append(item)
        while time.monotonic() < deadline:
            while self.reader.ready:
                item = self.reader.ready.popleft()
                if predicate(item):
                    return item
                self.pending.append(item)
            if self.socket is None:
                break
            self.socket.settimeout(max(0.1, deadline - time.monotonic()))
            data = self.socket.recv(4096)
            if not data:
                break
            self.reader.feed(data)
        raise TimeoutError("timed out waiting for the expected XMPP stanza")

    def close(self) -> None:
        if self.socket is None:
            return
        try:
            self._send("</stream:stream>")
        except OSError:
            pass
        self.socket.close()
        self.socket = None

    def __enter__(self) -> XmppClient:
        return self

    def __exit__(self, *exc) -> None:
        self.close()


def _escape(value: str) -> str:
    return value.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;").replace("'", "&apos;").replace('"', "&quot;")

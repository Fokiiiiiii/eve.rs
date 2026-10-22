"""Two pilots exchange Local/private chat and persisted mail through live services.

    EVE_LIVE_HOST=host:port EVE_LIVE_XMPP_PORT=5222 pytest tests/test_walk_xmpp_live.py

The walk is enabled when the Host XMPP listener is available. It uses the
client's chat authentication RPC and endpoint settings, then speaks the same
SASL PLAIN and MUC flow as the V24.01 client.
"""

from __future__ import annotations

import os
import time
import uuid
import zlib

import pytest

from evecli import marshal as m
from evecli.xmpp import XmppClient
from tests import test_login_to_character as base
from tests.test_health import HEALTH
from tests.test_undock import SYSTEM, arrive

LIVE_XMPP_PORT = os.environ.get("EVE_LIVE_XMPP_PORT", os.environ.get("EVE_XMPP_PORT", ""))
pytestmark = [
    pytest.mark.skipif(not base.LIVE, reason="set EVE_LIVE_HOST=host:port to test against a running server"),
    pytest.mark.skipif(not LIVE_XMPP_PORT, reason="set EVE_LIVE_XMPP_PORT or EVE_XMPP_PORT for the XMPP listener"),
]


def _text(value) -> str:
    value = m.plain(value)
    return value.decode("utf-8") if isinstance(value, bytes) else str(value)


def _connect_chat(client, character_id: int, nickname: str, domain: str) -> XmppClient:
    token = _text(client.call("chatAuthenticationService", "GetAuthenticationToken"))
    host = os.environ.get("EVE_LIVE_XMPP_HOST") or _text(client.call("XmppChatMgr", "GetDeprecatedPrefsFallback"))
    tls_setting = os.environ.get("EVE_LIVE_XMPP_TLS")
    tls = tls_setting.lower() not in {"0", "false", "off"} if tls_setting else bool(os.environ.get("EVE_XMPP_CERTIFICATE_PATH"))
    chat = XmppClient(host, int(LIVE_XMPP_PORT), domain, tls=tls, timeout=20.0)
    chat.connect(character_id, token, nickname)
    return chat


def test_two_pilots_exchange_local_and_private_messages_over_xmpp():
    run = uuid.uuid4().hex[:10]
    race = HEALTH["races"][0]
    with base.connect(f"walk-xmpp-first-{run}") as first, base.connect(f"walk-xmpp-second-{run}") as second:
        first_character, *_ = arrive(first, race, f"{run}a")
        second_character, *_ = arrive(second, race, f"{run}b")
        domain = _text(first.call("XmppChatMgr", "Hostname"))
        room = f"local_{SYSTEM}"

        with _connect_chat(first, first_character, "FirstPilot", domain) as first_chat, \
                _connect_chat(second, second_character, "SecondPilot", domain) as second_chat:
            first_chat.join(room, "FirstPilot")
            second_chat.join(room, "SecondPilot")
            first_chat.send_groupchat(room, "headless Local check")
            second_chat.wait_for_message(room, "headless Local check")

            first_channels = m.plain(first.call("XmppChatMgr", "ResyncSystemChannelAccess"))
            second_channels = m.plain(second.call("XmppChatMgr", "ResyncSystemChannelAccess"))
            decode_channel = lambda value: value.decode("utf-8") if isinstance(value, bytes) else str(value)
            first_corporations = {decode_channel(value) for value in first_channels if decode_channel(value).startswith("corp_")}
            second_corporations = {decode_channel(value) for value in second_channels if decode_channel(value).startswith("corp_")}
            shared_corporations = sorted(first_corporations & second_corporations)
            assert shared_corporations, "the two live pilots must share a corporation room for this walk"
            corporation_room = shared_corporations[0]
            first_chat.join(corporation_room, "FirstPilot")
            second_chat.join(corporation_room, "SecondPilot")
            time.sleep(0.8)
            first_chat.send_groupchat(corporation_room, "headless corporation check")
            second_chat.wait_for_message(corporation_room, "headless corporation check")

            private_room = _text(first.call("XmppChatMgr", "CreatePrivateChannel", second_character))
            first_chat.join(private_room, "FirstPilot")
            first_chat.invite(private_room, second_character, "headless private check")
            second_chat.wait_for_invite(private_room, first_character)
            second_chat.join(private_room, "SecondPilot")

            time.sleep(0.8)
            first_chat.send_groupchat(private_room, "headless private check")
            second_chat.wait_for_message(private_room, "headless private check")

            subject = f"headless mail {run}"
            message_id = int(m.plain(first.call(
                "mailMgr", "SendMail", [second_character], None, None,
                subject, "Two pilots crossed chat and mail.", None, None)))
            mailbox = m.plain(second.call("mailMgr", "SyncMail", None, None))[1]
            assert any(int(header[1]["messageID"]) == message_id and header[1]["title"] == subject
                       for header in mailbox["newMail"]), (message_id, subject, [(header[1]["messageID"], header[1]["title"]) for header in mailbox["newMail"]])

            body = second.call("mailMgr", "GetBody", message_id, True)
            assert zlib.decompress(body).decode("utf-8") == "Two pilots crossed chat and mail."
            read_mail = m.plain(second.call("mailMgr", "SyncMail", None, None))[1]
            read_status = next(status[1] for status in read_mail["mailStatus"]
                               if int(status[1]["messageID"]) == message_id)
            assert int(read_status["statusMask"]) & 1

            reply_subject = f"Re: {subject}"
            reply_body = "I received the message."
            reply_id = int(m.plain(second.call(
                "mailMgr", "SendMail", [first_character], None, None,
                reply_subject, reply_body, message_id, None)))
            first_mailbox = m.plain(first.call("mailMgr", "SyncMail", None, None))[1]
            assert any(int(header[1]["messageID"]) == reply_id and header[1]["title"] == reply_subject
                       for header in first_mailbox["newMail"])
            first_reply = zlib.decompress(first.call("mailMgr", "GetBody", reply_id, True)).decode("utf-8")
            assert first_reply == reply_body

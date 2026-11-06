"""End-to-end SSO login against the local eve.rs host.

The graphical V24 client is not used. This drives its Placebo handshake, then asks the
character-selection service for data and selects a character through the normal RPC path.
"""

from __future__ import annotations

import os
import uuid

import pytest

from evecli import marshal as m
from evecli.client import Client
from tests import dbseed
from tests.sso_issuer import SsoTestIssuer
from tests.test_login_to_character import WALK, revive

LIVE = os.environ.get("EVE_LIVE_HOST", "")
IDENTITY_SCHEMA = os.environ.get("EVE_IDENTITY_SCHEMA", "")
pytestmark = pytest.mark.skipif(
    not LIVE or not IDENTITY_SCHEMA,
    reason="requires tools/dev/run-eve-live.sh with PostgreSQL scratch schemas",
)


def test_v24_sso_handshake_reaches_and_selects_a_character():
    host, _, port_text = LIVE.partition(":")
    address = (host or "127.0.0.1", int(port_text or 26000))
    issuer_port = int(os.environ["EVE_SSO_TEST_PORT"])
    run = uuid.uuid4().hex[:10]
    user_name = f"sso-walk-{run}"
    subject = f"CHARACTER:EVE:{1_000_000_000 + int(run, 16)}"

    with SsoTestIssuer(issuer_port) as issuer:
        with Client(*address, timeout=30.0) as bootstrap:
            account_id = bootstrap.login(user_name, "sso-bootstrap-password").user_id

        dbseed.link_sso_identity(account_id, issuer.issuer, subject)
        token = issuer.issue(subject)
        with Client(*address, timeout=30.0) as client:
            session = client.login_sso(token)
            assert session.user_id == account_id

            selection = m.plain(client.call("charUnboundMgr", "GetCharacterSelectionData"))
            assert len(selection[2]) == 0

            creation = next(
                step for step in WALK["steps"]
                if step["call"][1] == "CreateCharacterWithDoll" and "ok" in step
            )
            service, method, described = creation["call"]
            character_id = m.plain(client.call(service, method, *[revive(value, run) for value in described]))

            selection = m.plain(client.call("charUnboundMgr", "GetCharacterSelectionData"))
            assert len(selection[2]) == 1
            character = selection[2][0]
            assert character[0] == "util.KeyVal"
            assert int(character[1]["characterID"]) == character_id

            client.notifications.clear()
            result = client.call(
                "charUnboundMgr",
                "SelectCharacterID",
                charID=character_id,
                secondChoiceID=None,
                skipTutorial=False,
            )
            assert m.plain(result) is None
            assert client.session.session_init.get("userid") == account_id
            assert client.session.session_init.get("charid") == character_id

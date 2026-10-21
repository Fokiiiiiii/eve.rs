"""The fitting browser reads a character's and corporation's shared fitting lists.

    EVE_LIVE_HOST=host:port pytest tests/test_fitting_lists_live.py
"""

from __future__ import annotations

import uuid

import pytest

from evecli import marshal as m
from tests import test_login_to_character as base
from tests.test_health import HEALTH
from tests.test_undock import arrive

pytestmark = pytest.mark.skipif(not base.LIVE, reason="set EVE_LIVE_HOST=host:port to test against a running server")


def test_fitting_browser_reads_owned_character_and_corporation_lists():
    run = uuid.uuid4().hex[:10]
    with base.connect(f"walk-fittings-{run}") as client:
        character_id, *_ = arrive(client, HEALTH["races"][0], run)
        corporation_id = client.session.session_init.get("corpid")
        assert isinstance(corporation_id, int) and corporation_id > 0

        character_fittings = m.plain(client.call("charFittingMgr", "GetFittings", character_id))
        corporation_fittings = m.plain(client.call("corpFittingMgr", "GetFittings", corporation_id))

        assert isinstance(character_fittings, dict)
        assert isinstance(corporation_fittings, dict)

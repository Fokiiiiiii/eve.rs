"""The re-sculpt certificate against a running server (CH-8): `userSvc.ActivateCharacterReSculpt` (SDE 34132 "Pilot's Body Resculpt Certificate").

    EVE_LIVE_HOST=host:port EVE_PG_URL=postgres://... EVE_RUN=... tools/dev/run-eve-live.sh (or pytest tests/test_resculpt_live.py)

A new pilot has no recustomization state. The test gives it a certificate in its station hangar (dbseed). Activating it consumes the item and sets the
state the certificate with gender gives (3, full recustomizing), which `charMgr.GetPaperdollState` reports; the consumed certificate is refused the
second time and the state does not change.
"""

from __future__ import annotations

import os
import uuid

import pytest

from evecli import marshal as m
from tests import dbseed
from tests.test_agents_live import new_pilot, psql, schema
from tests.test_login_to_character import connect
from tests.test_undock import STATION

LIVE = os.environ.get("EVE_LIVE_HOST", "")
PG_URL = os.environ.get("EVE_PG_URL", "")
RESCULPT_CERTIFICATE = 34132
FULL_RECUSTOMIZING = 3
pytestmark = pytest.mark.skipif(not (LIVE and PG_URL), reason="set EVE_LIVE_HOST=host:port and EVE_PG_URL to test against a running server")

def test_a_resculpt_certificate_is_used_once_and_the_pilot_is_full_recustomizing():
    run = uuid.uuid4().hex[:10]
    with connect(f"resculpt-{run}") as client:
        character = new_pilot(client, run)
        state = lambda: m.plain(client.call("charMgr", "GetPaperdollState"))  # noqa: E731
        certificate = dbseed.new_item(RESCULPT_CERTIFICATE, owner=character, location=STATION, singleton=True)

        assert m.plain(client.call("userSvc", "ActivateCharacterReSculpt", certificate)) is True
        assert int(psql(f"SELECT count(*) FROM {schema('EVE_INVENTORY_SCHEMA')}.items WHERE id = {certificate}")) == 0, "the certificate is consumed"
        assert state() == FULL_RECUSTOMIZING

        assert m.plain(client.call("userSvc", "ActivateCharacterReSculpt", certificate)) is False, "a consumed certificate is refused"
        assert state() == FULL_RECUSTOMIZING

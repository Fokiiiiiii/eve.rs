"""Personal contact labels through the bound charMgr RPCs.

    EVE_LIVE_HOST=host:port EVE_PG_URL=postgres://... pytest tests/test_character_contact_labels_live.py
"""

from __future__ import annotations

import os
import uuid

import pytest

from evecli import marshal as m
from tests.test_agents_live import new_pilot
from tests.test_login_to_character import connect

LIVE = os.environ.get("EVE_LIVE_HOST", "")
PG_URL = os.environ.get("EVE_PG_URL", "")
pytestmark = pytest.mark.skipif(not (LIVE and PG_URL), reason="set EVE_LIVE_HOST=host:port and EVE_PG_URL to test against a running server")


def test_personal_contact_labels_can_be_created_edited_and_deleted_live():
    run = uuid.uuid4().hex[:10]
    with connect(f"contact-label-{run}") as client:
        new_pilot(client, run)
        assert m.plain(client.call("charMgr", "GetLabels")) == {}

        label_id = int(m.plain(client.call("charMgr", "CreateLabel", f"Fleet {run}", 7)))
        labels = m.plain(client.call("charMgr", "GetLabels"))
        label_object, label_fields = labels[label_id]
        assert label_object == "util.KeyVal"
        assert label_fields["name"] == f"Fleet {run}"
        assert label_fields["color"] == 7

        assert m.plain(client.call("charMgr", "EditLabel", label_id, f"Wing {run}", 9)) is None
        labels = m.plain(client.call("charMgr", "GetLabels"))
        label_object, label_fields = labels[label_id]
        assert label_object == "util.KeyVal"
        assert label_fields["name"] == f"Wing {run}"
        assert label_fields["color"] == 9

        assert m.plain(client.call("charMgr", "DeleteLabel", label_id)) is None
        assert label_id not in m.plain(client.call("charMgr", "GetLabels"))

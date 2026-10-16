"""Multiple Character Training against a running server: the activation is dispatched and refused as the reference does.

    EVE_LIVE_HOST=host:port pytest tests/test_multi_training_live.py

A new account enters a new character. Activating an item that does not exist is refused with the client's CustomInfo text, and
the queue list still reads (no queue is granted). A certificate that is actually consumed needs an item the account holds; the
live server has none to give, so that path is checked in the Host tests. Skipped unless EVE_LIVE_HOST is set.
"""

import os
import uuid

import pytest

from evecli import marshal as m
from evecli import packet as p

from tests.test_login_to_character import WALK, connect, revive
from tests.test_market import _label_and_arguments

LIVE = os.environ.get("EVE_LIVE_HOST", "")
pytestmark = pytest.mark.skipif(not LIVE, reason="set EVE_LIVE_HOST=host:port to test against a running server")


def enter_new_character(client, run: str) -> int:
    creation = next(step for step in WALK["steps"] if step["call"][1] == "CreateCharacterWithDoll" and "ok" in step)
    service, method, described = creation["call"]
    character_id = m.plain(client.call(service, method, *[revive(value, run) for value in described]))
    client.notifications.clear()
    client.call("charUnboundMgr", "SelectCharacterID", charID=character_id, secondChoiceID=None, skipTutorial=False)
    return character_id


def test_activating_a_missing_item_is_refused_and_no_queue_is_granted():
    run = uuid.uuid4().hex[:10]
    with connect(f"mct-{run}") as client:
        enter_new_character(client, run)
        assert m.plain(client.call("userSvc", "GetUpdatedMultiCharacterTraining", False)) == []

        with pytest.raises(p.MachoError) as refused:
            client.call("userSvc", "ActivateMultiTraining", 1, trainingID=None)
        label, arguments = _label_and_arguments(refused.value)
        assert label == "CustomInfo"
        assert arguments["info"] == "That item does not exist."

        assert m.plain(client.call("userSvc", "GetUpdatedMultiCharacterTraining", False)) == []

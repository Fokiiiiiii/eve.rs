"""Multiple Character Training against a running server: the activation is dispatched and refused as the reference does.

    EVE_LIVE_HOST=host:port pytest tests/test_multi_training_live.py

A new account enters a new character. Activating an item that does not exist is refused with the client's CustomInfo text, and
the queue list still reads (no queue is granted). A stack of two certificates (dbseed, in the pilot's station hangar) gives up one
certificate per activation: the first leaves one in the stack and opens one queue, the second removes the stack and opens the second
queue, and a third is refused as a missing item. Skipped unless EVE_LIVE_HOST is set.
"""

import os
import uuid

import pytest

from evecli import marshal as m
from evecli import packet as p

from tests import dbseed
from tests.test_agents_live import new_pilot, psql, schema
from tests.test_login_to_character import WALK, connect, revive
from tests.test_market import _label_and_arguments
from tests.test_undock import STATION

MULTIPLE_CHARACTER_TRAINING = 63188

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

def test_a_stack_of_certificates_gives_up_one_certificate_per_activation():
    run = uuid.uuid4().hex[:10]
    with connect(f"mct-stack-{run}") as client:
        character = new_pilot(client, run)
        stack = dbseed.new_item(MULTIPLE_CHARACTER_TRAINING, owner=character, location=STATION, quantity=2)

        first = m.plain(client.call("userSvc", "ActivateMultiTraining", stack, trainingID=None))
        assert len(first) == 1
        assert psql(f"SELECT quantity FROM {schema('EVE_INVENTORY_SCHEMA')}.items WHERE id = {stack}") == "1"
        assert len(m.plain(client.call("userSvc", "GetUpdatedMultiCharacterTraining", False))) == 1

        second = m.plain(client.call("userSvc", "ActivateMultiTraining", stack, trainingID=None))
        assert len(second) == 2
        assert psql(f"SELECT count(*) FROM {schema('EVE_INVENTORY_SCHEMA')}.items WHERE id = {stack}") == "0"

        with pytest.raises(p.MachoError) as refused:
            client.call("userSvc", "ActivateMultiTraining", stack, trainingID=None)
        label, arguments = _label_and_arguments(refused.value)
        assert label == "CustomInfo"
        assert arguments["info"] == "That item does not exist."

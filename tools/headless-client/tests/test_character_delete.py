"""Character deletion against a running server: queue, refusal, cancellation and deletion.

    EVE_LIVE_HOST=host:port pytest tests/test_character_delete.py

A new account enters a new character. The test checks queue idempotence, refusal during the ten-hour wait, cancellation and
refusal without a queue. It then ages the persisted deadline in the test fixture and calls DeleteCharacter through the live
RPC, checking that the character row is marked deleted and its queue is cleared. Skipped unless EVE_LIVE_HOST is set.
"""

import os
import time
import uuid

import pytest

from evecli import marshal as m
from evecli import packet as p

from tests.test_agents_live import psql, schema
from tests.test_login_to_character import WALK, connect, revive
from tests.test_market import _label_and_arguments

LIVE = os.environ.get("EVE_LIVE_HOST", "")
pytestmark = pytest.mark.skipif(not LIVE, reason="set EVE_LIVE_HOST=host:port to test against a running server")

FILETIME_EPOCH = 116_444_736_000_000_000  # 1601-01-01 in 100 ns ticks


def enter_new_character(client, run: str) -> int:
    creation = next(step for step in WALK["steps"] if step["call"][1] == "CreateCharacterWithDoll" and "ok" in step)
    service, method, described = creation["call"]
    character_id = m.plain(client.call(service, method, *[revive(value, run) for value in described]))
    client.notifications.clear()
    client.call("charUnboundMgr", "SelectCharacterID", charID=character_id, secondChoiceID=None, skipTutorial=False)
    return character_id


def test_a_character_is_queued_refused_while_waiting_cancelled_and_refused_without_a_queue():
    run = uuid.uuid4().hex[:10]
    with connect(f"chardelete-{run}") as client:
        character = enter_new_character(client, run)
        # deletion is offered on the selection screen: the character must be offline (the reference refuses an online one)
        client.call("userSvc", "UserLogOffCharacter")

        # the queue: the answer is the FILETIME the deletion becomes possible, in the future (the reference's ten hours)
        ready = m.plain(client.call("charUnboundMgr", "PrepareCharacterForDelete", character))
        ready_unix = (ready - FILETIME_EPOCH) / 10_000_000
        assert time.time() + 9 * 3600 < ready_unix <= time.time() + 11 * 3600
        assert m.plain(client.call("charUnboundMgr", "PrepareCharacterForDelete", character)) == ready

        # a delete while the time runs is refused with the client's CustomInfo text
        with pytest.raises(p.MachoError) as refused:
            client.call("charUnboundMgr", "DeleteCharacter", character)
        label, arguments = _label_and_arguments(refused.value)
        assert label == "CustomInfo"
        assert arguments["info"] == "That character is still waiting for the biomass timer to expire."

        # cancel clears the queue; a delete then is refused as not queued
        assert m.plain(client.call("charUnboundMgr", "CancelCharacterDeletePrepare", character)) is None
        with pytest.raises(p.MachoError) as not_queued:
            client.call("charUnboundMgr", "DeleteCharacter", character)
        label, arguments = _label_and_arguments(not_queued.value)
        assert label == "CustomInfo"
        assert arguments["info"] == "That character is not in the biomass queue."

        # Age the persisted queue deadline as a test fixture; the real RPC still validates it and performs the deletion.
        assert m.plain(client.call("charUnboundMgr", "PrepareCharacterForDelete", character)) is not None
        characters = schema("EVE_CHARACTERS_SCHEMA")
        psql(f"UPDATE {characters}.characters SET delete_prepare_at = TIMESTAMPTZ '2000-01-01T00:00:00Z' WHERE id = {character}")
        assert m.plain(client.call("charUnboundMgr", "DeleteCharacter", character)) is None
        deleted = psql(
            f"SELECT CASE WHEN deleted_at IS NOT NULL AND delete_prepare_at IS NULL THEN 1 ELSE 0 END "
            f"FROM {characters}.characters WHERE id = {character}"
        )
        assert deleted == "1", "the real deletion call marks the character deleted and clears its queue"

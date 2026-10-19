"""The votes and reports against a running server (GATE-C: each answers true and keeps nothing).

    EVE_LIVE_HOST=host:port pytest tests/test_user_votes_live.py

A new account enters a new character, then casts an upvote and a downvote and files a bot report and an ISK spam report. Each
answers true, as the reference does; the client reads no answer. The playtime read answers 0 (no stored source; see DEVIATIONS).
Skipped unless EVE_LIVE_HOST is set.
"""

import os
import uuid

import pytest

from evecli import marshal as m

from tests.test_login_to_character import WALK, connect, revive

LIVE = os.environ.get("EVE_LIVE_HOST", "")
pytestmark = pytest.mark.skipif(not LIVE, reason="set EVE_LIVE_HOST=host:port to test against a running server")

def enter_new_character(client, run: str) -> int:
    creation = next(step for step in WALK["steps"] if step["call"][1] == "CreateCharacterWithDoll" and "ok" in step)
    service, method, described = creation["call"]
    character_id = m.plain(client.call(service, method, *[revive(value, run) for value in described]))
    client.notifications.clear()
    client.call("charUnboundMgr", "SelectCharacterID", charID=character_id, secondChoiceID=None, skipTutorial=False)
    return character_id

def test_votes_and_reports_answer_true_and_playtime_answers_zero():
    run = uuid.uuid4().hex[:10]
    with connect(f"votes-{run}") as client:
        enter_new_character(client, run)
        assert m.plain(client.call("userSvc", "CastUpvote")) is True
        assert m.plain(client.call("userSvc", "CastDownvote", "feedback")) is True
        assert m.plain(client.call("userSvc", "ReportBot", 90000004)) is True
        assert m.plain(client.call("userSvc", "ReportISKSpammer", 90000004)) is True
        assert m.plain(client.call("userSvc", "GetSessionPlaytimeMinutes")) == 0

"""The login milestone against a running server (eve.rs: the EveJS rule, a two-hour milestone per account, 500,000 ISK).

    EVE_LIVE_HOST=host:port EVE_PG_URL=postgres://... tools/dev/run-eve-live.sh (or pytest tests/test_login_milestone_live.py)

The two hours cannot be waited for, so the run's scratch characters schema holds the account's progress one second short of the milestone: the
logon starts the timer, the timer reaches the milestone, the client claims it, and the second logon of the account shows it claimed (no timer, no
second reward).
"""

from __future__ import annotations

import os
import uuid

import pytest

from evecli import marshal as m
from evecli.observe import notifications_of
from tests.test_agents_live import new_pilot, psql, schema
from tests.test_login_to_character import connect
from tests.test_others import watch

LIVE = os.environ.get("EVE_LIVE_HOST", "")
PG_URL = os.environ.get("EVE_PG_URL", "")
pytestmark = pytest.mark.skipif(not (LIVE and PG_URL), reason="set EVE_LIVE_HOST=host:port and EVE_PG_URL to test against a running server")

MILESTONE = 1
REWARD_ISK = 500_000
DURATION_MS = 2 * 60 * 60 * 1000

def isk_of(character: int) -> int:
    return int(float(psql(f"SELECT isk FROM {schema('EVE_WALLET_SCHEMA')}.balances WHERE owner_id = {character} AND account_key = 1000")))

def named(events: list[dict], name: str) -> list[dict]:
    return [event for event in events if event["name"] == name]

def integers_in(value) -> list[int]:
    """Every integer of a plain marshal value, in order (the notification's arguments are the last two of its tuples)."""
    if isinstance(value, bool):
        return []
    if isinstance(value, int):
        return [value]
    if isinstance(value, dict):
        return [number for item in value.values() for number in integers_in(item)]
    if isinstance(value, (list, tuple)):
        return [number for item in value for number in integers_in(item)]
    return []

def test_the_login_milestone_is_reached_claimed_once_and_stays_claimed_after_a_relogin():
    run = uuid.uuid4().hex[:10]
    name = f"milestone-{run}"
    with connect(name) as client:
        character = new_pilot(client, run)
        account = int(psql(f"SELECT id FROM {schema('EVE_IDENTITY_SCHEMA')}.accounts WHERE user_name = '{name}'"))
        psql(f"INSERT INTO {schema('EVE_CHARACTERS_SCHEMA')}.login_milestones (account_id, online_ms, achieved, claimed, updated_ms) "
             f"VALUES ({account}, {DURATION_MS - 1000}, false, false, 0)")
        before = isk_of(character)

        client.notifications.clear()
        client.call("milestoneMgr", "ProcessCharacterLogon")
        started = named(notifications_of(client), "OnMilestoneTimerStarted")
        assert len(started) == 1, "the logon starts the timer the client is told of"
        reached = named(watch({"events": client}, 2.5)["events"], "OnLoginMilestoneAchieved")
        assert len(reached) == 1, "the timer reached the milestone and the client is told"
        assert integers_in(m.plain(reached[0]["args"]))[-2:] == [MILESTONE, REWARD_ISK]

        client.call("milestoneMgr", "ClaimRewards", MILESTONE)
        assert isk_of(character) == before + REWARD_ISK
        assert psql(f"SELECT claimed FROM {schema('EVE_CHARACTERS_SCHEMA')}.login_milestones WHERE account_id = {account}") == "t"

        client.call("milestoneMgr", "ClaimRewards", MILESTONE)
        assert isk_of(character) == before + REWARD_ISK, "a second claim pays nothing"

    with connect(name) as again:
        again.call("charUnboundMgr", "SelectCharacterID", charID=character, secondChoiceID=None, skipTutorial=False)
        again.notifications.clear()
        again.call("milestoneMgr", "ProcessCharacterLogon")
        events = notifications_of(again)
        assert not named(events, "OnMilestoneTimerStarted") and not named(events, "OnLoginMilestoneAchieved"), "a claimed milestone is not offered again"
        again.call("milestoneMgr", "ClaimRewards", MILESTONE)
        assert isk_of(character) == before + REWARD_ISK

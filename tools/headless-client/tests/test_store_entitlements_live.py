"""The New Eden Store against a running server: Omega and Multiple Character Training are bought at no charge and granted.

    EVE_LIVE_HOST=host:port pytest tests/test_store_entitlements_live.py

A new account enters a new character (the creation recorded in login_to_character.json), then buys an Omega offer, a
Multiple Character Training offer and a PLEX pack. It checks what the client reads back: the vault balance (unchanged by the
Omega and MCT purchases, raised by the PLEX pack), the subscription end time, the training queues, and the notifications
the account's session receives. Skipped unless EVE_LIVE_HOST is set.
"""

import os
import time
import uuid

import pytest

from evecli import marshal as m
from evecli.observe import notifications_of

from tests.test_login_to_character import WALK, connect, revive

LIVE = os.environ.get("EVE_LIVE_HOST", "")
pytestmark = pytest.mark.skipif(not LIVE, reason="set EVE_LIVE_HOST=host:port to test against a running server")

FILETIME_EPOCH = 116_444_736_000_000_000  # 1601-01-01 in 100 ns ticks
DAY = 86400
NEW_ACCOUNT_END = 4_102_444_800  # 2100-01-01 00:00 UTC, the Omega end of a new account (unix seconds)

def unix_of(filetime: int) -> float:
    return (filetime - FILETIME_EPOCH) / 10_000_000

def enter_new_character(client, run: str) -> int:
    creation = next(step for step in WALK["steps"] if step["call"][1] == "CreateCharacterWithDoll" and "ok" in step)
    service, method, described = creation["call"]
    character_id = m.plain(client.call(service, method, *[revive(value, run) for value in described]))
    client.notifications.clear()
    client.call("charUnboundMgr", "SelectCharacterID", charID=character_id, secondChoiceID=None, skipTutorial=False)
    return character_id

def plex_balance(client) -> int:
    return m.plain(client.call("vaultManager", "get_account_balance", "PLX"))

def names(client) -> set:
    return {event["name"] for event in notifications_of(client)}

def test_omega_and_multiple_character_training_are_bought_at_no_charge_and_granted():
    run = uuid.uuid4().hex[:10]
    with connect(f"store-{run}") as client:
        enter_new_character(client, run)
        balance = plex_balance(client)

        # the Omega offer: a new account is Omega until 2100, and a purchase never moves that end time (SubscriptionPolicy.Extend: the purchase
        # horizon is kept; DEVIATIONS #62)
        client.notifications.clear()
        assert m.plain(client.call("storeManager", "buy_offer", "omega_30_days", "PLX", 1)) is True
        assert plex_balance(client) == balance
        end = unix_of(m.plain(client.call("subscriptionMgr", "GetSubscriptionTime")))
        assert end == NEW_ACCOUNT_END
        assert m.plain(client.call("subscriptionMgr", "GetCloneGrade")) == 1
        assert "OnSubscriptionTimeUpdateServer" in names(client)

        # the Multiple Character Training offer: one queue opens for 30 days
        client.notifications.clear()
        assert m.plain(client.call("storeManager", "buy_offer", "mct_slot_30_days", "PLX", 1)) is True
        assert plex_balance(client) == balance
        queues = m.plain(client.call("userSvc", "GetUpdatedMultiCharacterTraining", False))
        assert [slot for slot, _ in queues] == [2]
        assert time.time() < unix_of(queues[0][1]) <= time.time() + 31 * DAY
        assert "OnMultipleCharactersTrainingUpdated" in names(client)

        # a PLEX pack from the store is credited to the vault (100 PLEX for 100 USD)
        assert m.plain(client.call("storeManager", "buy_offer", "cash_plex_100_usd", "USD", 1, store_id=9)) is True
        assert plex_balance(client) == balance + 100

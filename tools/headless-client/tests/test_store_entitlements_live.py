"""The New Eden Store against a running server: purchases fail closed until settlement is wired.

    EVE_LIVE_HOST=host:port pytest tests/test_store_entitlements_live.py

A new account enters a new character, checks that the client sees checkout disabled for Omega, MCT and PLEX, and confirms
that failed purchases change no wallet, subscription, training queue or session notification. Skipped unless EVE_LIVE_HOST is set.
"""

import os
import uuid

import pytest

from evecli import marshal as m
from evecli.observe import notifications_of

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

def plex_balance(client) -> int:
    return m.plain(client.call("vaultManager", "get_account_balance", "PLX"))

def names(client) -> set:
    return {event["name"] for event in notifications_of(client)}

def test_store_purchases_fail_closed_until_settlement_is_wired():
    run = uuid.uuid4().hex[:10]
    with connect(f"store-{run}") as client:
        enter_new_character(client, run)
        balance = plex_balance(client)
        subscription = m.plain(client.call("subscriptionMgr", "GetSubscriptionTime"))
        clone_grade = m.plain(client.call("subscriptionMgr", "GetCloneGrade"))
        training = m.plain(client.call("userSvc", "GetUpdatedMultiCharacterTraining", False))
        client.notifications.clear()

        offers = [
            ("omega_30_days", "PLX", 1, {}),
            ("mct_slot_30_days", "PLX", 1, {}),
            ("cash_plex_100_usd", "USD", 1, {"store_id": 9}),
        ]
        for offer_id, currency, quantity, kwargs in offers:
            availability = m.plain(client.call("storeManager", "can_purchase_offer", offer_id, **kwargs))
            assert availability["canPurchase"] is False
            assert availability["purchaseLimitedReason"] == "payment_unavailable"
            assert availability["offerID"] is None
            assert availability["storeOfferID"] is None

            purchase = client.call("storeManager", "buy_offer", offer_id, currency, quantity, **kwargs)
            assert m.plain(purchase) is None, f"{offer_id} returned success without a settlement provider"

        assert plex_balance(client) == balance
        assert m.plain(client.call("subscriptionMgr", "GetSubscriptionTime")) == subscription
        assert m.plain(client.call("subscriptionMgr", "GetCloneGrade")) == clone_grade
        assert m.plain(client.call("userSvc", "GetUpdatedMultiCharacterTraining", False)) == training
        assert names(client) == set()

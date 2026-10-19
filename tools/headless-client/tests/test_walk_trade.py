"""Direct trade walk: two pilots docked in one station; the seller offers a Cockroach it holds for the buyer's ISK.

    EVE_LIVE_HOST=host:port tests/... (through tools/dev/run-eve-live.sh so EVE_PG_URL and the schemas are exported)

Both pilots are made and chosen as the health recording's pilot is (arrive), so both are docked at the same station. The Cockroach (typeID
11019) is seeded into the seller's station hangar and the buyer gets ISK (dbseed). The trade follows the client's calls: the seller
initiates (trademgr.InitiateTrade), both bind the moniker (MachoBindObject), the seller adds the ship (Add), the buyer offers ISK
(OfferMoney), both accept with a manifest (MakeOffer). The database then holds the ship with the buyer and the ISK moved.
Only eve.rs has the seeded walk: EveJS has no database to seed, so its side is compared by reading its code.
"""

from __future__ import annotations

import os
import uuid

import pytest

from evecli.describe import describe
from tests import dbseed
from tests import gm
from tests import test_login_to_character as base
from tests.test_health import HEALTH
from tests.test_undock import STATION, arrive

COCKROACH = 11019
PRICE = 500000
pytestmark = [
    pytest.mark.skipif(not base.LIVE, reason="set EVE_LIVE_HOST=host:port to test against a running server"),
    pytest.mark.skipif(os.environ.get("EVE_LIVE_SERVER", "eve.rs") == "reference", reason="seeding needs the database: eve.rs only"),
]

def _tuple_of_one(node):
    """The integer in the first one-element tuple of a described value (the (tradeID,) of the moniker), or None."""
    if isinstance(node, dict):
        items = node.get("$tuple")
        if isinstance(items, list) and len(items) == 1 and isinstance(items[0], (int, dict)):
            value = items[0]
            return int(value["$long"]) if isinstance(value, dict) else int(value)
        children = list(node.values())
    elif isinstance(node, list):
        children = node
    else:
        return None
    for child in children:
        found = _tuple_of_one(child)
        if found is not None:
            return found
    return None

def trade_id_of(moniker) -> int:
    found = _tuple_of_one(describe(moniker))
    assert found is not None, "the moniker names a trade"
    return found

def manifest(trade: int, money: list, items: list) -> dict:
    """What a trader was shown: the container, the ISK of each trader in trader order (the seller first), and the items in the trade."""
    return {"tradeContainerID": trade, "money": money, "tradeItems": [{"itemID": item} for item in items]}

def test_two_pilots_trade_a_cockroach_for_isk_and_the_database_holds_the_result():
    race = HEALTH["races"][0]
    run = uuid.uuid4().hex[:10]
    with base.connect(f"walk-trade-seller-{run}") as seller, base.connect(f"walk-trade-buyer-{run}") as buyer:
        seller_char, _, _, _ = arrive(seller, race, run + "s")
        buyer_char, _, _, _ = arrive(buyer, race, run + "b")
        ship = dbseed.new_item(COCKROACH, owner=seller_char, location=STATION, singleton=True)
        gm.set_isk(buyer, PRICE * 2)
        seller_isk, buyer_isk = dbseed.isk_of(seller_char), dbseed.isk_of(buyer_char)

        moniker = seller.call("trademgr", "InitiateTrade", buyer_char)
        trade = trade_id_of(moniker)
        seller_bound = seller.call("trademgr", "MachoBindObject", (trade,), None)[0]
        buyer_bound = buyer.call("trademgr", "MachoBindObject", (trade,), None)[0]

        assert seller.call_bound(seller_bound, "Add", ship) is True
        assert buyer.call_bound(buyer_bound, "OfferMoney", PRICE) is True
        assert seller.call_bound(seller_bound, "MakeOffer", manifest(trade, [0, PRICE], [ship])) is True
        assert buyer.call_bound(buyer_bound, "MakeOffer", manifest(trade, [0, PRICE], [ship])) is True

        assert dbseed.owner_of(ship) == buyer_char
        assert dbseed.isk_of(buyer_char) == buyer_isk - PRICE
        assert dbseed.isk_of(seller_char) == seller_isk + PRICE

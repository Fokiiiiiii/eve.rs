"""Focused V24.01 market and trade RPC checks through the live Host registration.

Run through tools/dev/run-eve-live.sh or the CI live job. These checks exercise the real
marketProxy/trademgr services and their client-visible replies and notifications.
"""

from __future__ import annotations

import os
import uuid

import pytest

from evecli.describe import describe
from tests import dbseed
from tests import test_login_to_character as base
from tests import test_market as market
from tests import test_walk_trade as trade_walk
from tests.test_health import HEALTH
from tests.test_undock import STATION, arrive

pytestmark = [
    pytest.mark.skipif(not base.LIVE, reason="set EVE_LIVE_HOST=host:port to test against a running server"),
    pytest.mark.skipif(os.environ.get("EVE_LIVE_SERVER", "eve.rs") == "reference", reason="these RPC regressions target eve.rs"),
]


def test_plex_sell_modify_and_startup_match_the_v24_wire_contract():
    run = uuid.uuid4().hex[:10]
    with base.connect(f"rpc-plex-market-{run}") as client:
        character_id = market._new_character(client, run)
        station_id = client.session.session_init.get("stationid")
        assert isinstance(station_id, int)
        assert client.call("marketProxy", "StartupCheck") is None

        item_id = dbseed.new_item(44992, owner=character_id, location=station_id, quantity=2)
        entry = market.m.Dict([
            ("typeID", 44992), ("price", 500.0), ("quantity", 2),
            ("stationID", station_id), ("itemID", item_id),
        ])
        market.events_of(client)
        assert market.m.plain(client.call("marketProxy", "PlacePlexSellOrder", entry, False, 90, None)) is True
        order = next(row for row in market._own_orders(client) if market._field(row, "typeID") == 44992)
        order_id = market._field(order, "orderID")
        assert market._field(order, "volRemaining") == 2
        assert market._field(order, "price") == 500.0
        assert any(market._plain_name(event.get("name")) == "OnOwnOrdersChanged" for event in market.events_of(client))

        with pytest.raises(market.p.MachoError) as refused:
            client.call(
                "marketProxy", "ModifyPlexCharOrder", order_id, 550.0,
                market._field(order, "bid"), market._field(order, "stationID"),
                market._field(order, "solarSystemID"), market._field(order, "price"),
                market._field(order, "range"), market._field(order, "volRemaining"),
                market._field(order, "issueDate"),
            )
        label, arguments = market._label_and_arguments(refused.value)
        assert label == "CustomInfo" and "seconds" in arguments["info"], arguments
        current = next(row for row in market._own_orders(client) if market._field(row, "orderID") == order_id)
        assert market._field(current, "price") == 500.0


def test_initiate_trade_moniker_lists_the_client_consumed_row_and_notifies_peer():
    run = uuid.uuid4().hex[:10]
    race = HEALTH["races"][0]
    with base.connect(f"rpc-trade-seller-{run}") as seller, base.connect(f"rpc-trade-buyer-{run}") as buyer:
        seller_char, _, _, _ = arrive(seller, race, run + "s")
        buyer_char, _, _, _ = arrive(buyer, race, run + "b")
        assert seller.session.session_init.get("stationid") == buyer.session.session_init.get("stationid") == STATION

        moniker = seller.call("trademgr", "InitiateTrade", buyer_char)
        trade = trade_walk.trade_id_of(moniker)
        seller_bound = seller.call("trademgr", "MachoBindObject", (trade,), None)[0]
        buyer.call("trademgr", "MachoBindObject", (trade,), None)
        row = str(describe(seller.call_bound(seller_bound, "List")))
        assert all(name in row for name in ("tradeContainerID", "traders", "money", "items"))
        assert str(trade) in row and str(seller_char) in row and str(buyer_char) in row
        notifications = market.timed_events(buyer, 2.0)
        assert any(
            market._plain_name(event.get("name")) == "OnTradeInitiate"
            for event in notifications
        )

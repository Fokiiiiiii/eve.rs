"""The market walk (M1): a character docked at its station calls the market service the V24.01 client calls (marketProxy) and reads the
answers the client reads. Each answer is checked on the shape of its client call site (marketsvc.py, marketSvc.py, sellMulti.py); a
refusal is the client's own label with its arguments. The live host runs with EVE_MARKET_SEED=skip, so the book starts empty: the walk
checks the empty answers, the own-order and transaction lists, and the refusals.

    EVE_LIVE_HOST=host:port pytest tests/test_market.py

Skipped unless EVE_LIVE_HOST is set. tools/dev/run-eve-live.sh runs it.
"""

from __future__ import annotations

import os
import time
import uuid

import pytest

from evecli import marshal as m
from evecli import packet as p
from evecli.describe import describe
from evecli.observe import _plain_name, events_of, timed_events
from tests import dbseed
from tests import test_login_to_character as base
from tests import test_structures as structures
from tests.test_undock import arrive

pytestmark = pytest.mark.skipif(not base.LIVE, reason="set EVE_LIVE_HOST=host:port to test against a running server")

TRITANIUM = 34

def _new_character(client, run: str) -> int:
    creation = next(step for step in base.WALK["steps"] if step["call"][1] == "CreateCharacterWithDoll" and "ok" in step)
    service, method, described = creation["call"]
    character_id = m.plain(client.call(service, method, *[base.revive(value, run) for value in described]))
    assert client.call("charUnboundMgr", "SelectCharacterID", charID=character_id, secondChoiceID=None, skipTutorial=False) is None
    return character_id

CACHED_RESULT = "carbon.common.script.net.objectCaching.CachedMethodCallResult"

def _payload(value):
    """A cached answer (objectCaching.CachedMethodCallResult: the cache info, the payload, the version) as its payload."""
    if isinstance(value, tuple) and value and value[0] == CACHED_RESULT:
        return value[1][1]
    return value

def _number(described):
    """A number as the describe form gives it (a long is wrapped)."""
    return described["$long"] if isinstance(described, dict) else described

def _label_and_arguments(error: p.MachoError):
    """The client's UserError label and its dict of arguments, from the wrapped exception object (eveexceptions.UserError)."""
    payload = describe(error.payload)["$objectex"]["$tuple"]
    label, arguments = payload[1]["$tuple"][0], payload[1]["$tuple"][1]
    return label, dict(arguments.get("$dict", []))

def test_a_docked_character_reads_the_market_as_the_client_does():
    run = uuid.uuid4().hex[:10]
    with base.connect(f"walk-market-{run}") as client:
        _new_character(client, run)
        station_id = client.session.session_init.get("stationid")
        assert isinstance(station_id, int), "a character that selected its station is docked: the session says where"

        # Station/system range asks are (price, volRemaining, typeID, stationID) tuples for MarketBase conversion.
        asks = _payload(m.plain(client.call("marketProxy", "GetStationAsks")))
        assert isinstance(asks, dict)
        for type_id, row in asks.items():
            assert isinstance(row, tuple) and len(row) == 4, f"ask row for {type_id} is {row!r}"
            assert row[2] == type_id and row[3] == station_id

        # the own-order lists and the escrow of a character that trades nothing yet are empty, not errors (proxy 6469, 6652)
        assert m.plain(client.call("marketProxy", "GetCharOrders")) is not None
        escrow = {k: _number(v) for k, v in describe(client.call("marketProxy", "GetCharEscrow"))["args"]["$dict"]}
        assert escrow == {"iskEscrow": 0, "itemsEscrow": 0}

        # CharGetTransactions (proxy 6582): the personal fills, none for a new character (marketSvc.py:20-26)
        assert m.plain(client.call("marketProxy", "CharGetTransactions", None)) == []

        # BuyMultipleItems (proxy 6915) with no line: the rowset of the orders created, empty, which BuyMulti iterates (marketsvc.py:270-273)
        created = m.plain(client.call("marketProxy", "BuyMultipleItems", station_id, [], False))
        assert created[0] == "eve.common.script.sys.rowset.Rowset" and created[1]["lines"] == []

        # PlaceMultiSellOrder with no line: no order traded, so False (proxy 6967; sellMulti.py:462-469)
        assert m.plain(client.call("marketProxy", "PlaceMultiSellOrder", [], False, 90, None)) is False

        # the corporation transactions of a character outside a player corporation (NPC corporation) are refused with the
        # client's CrpAccessDenied and its reason, as the reference refuses them (proxy 6616-6631)
        with pytest.raises(p.MachoError) as refused:
            client.call("marketProxy", "CorpGetTransactions", None, None)
        label, _ = _label_and_arguments(refused.value)
        assert label == "CrpAccessDenied"

        # a buy whose minimum volume exceeds its quantity is the client's own label, with no arguments (marketsvc.py:264-265)
        with pytest.raises(p.MachoError) as refused:
            client.call("marketProxy", "PlaceBuyOrder", station_id, TRITANIUM, 10.0, 5, -1, 6, 0, False, None)
        label, arguments = _label_and_arguments(refused.value)
        assert label == "MktInvalidMinVolumeCannotExceedQuantity" and arguments == {}

        # server writes, and nothing is escrowed (the check runs before any funds move)
        with pytest.raises(p.MachoError) as refused:
            client.call("marketProxy", "PlaceBuyOrder", station_id, TRITANIUM, 12345.0, 1, -1, 1, 90, False, None)
        label, arguments = _label_and_arguments(refused.value)
        assert label == "CustomInfo"
        assert arguments == {"info": "Order prices have at most four significant figures."}

# ---- Upwell market: dock, post both sides, match and read the committed result -----------------------------------------------

def _dock_at_market_structure(client, character: int, ship_id: int, fitted: list, structure_id: int) -> None:
    structures._reenter(client, character)
    structures._undock(client, ship_id, fitted, station=structures.STATION, system=structures.SYSTEM)
    seen = timed_events(client, 30.0, until=lambda events: structures._structure_slim(events, structure_id) is not None)
    assert structures._structure_slim(seen, structure_id) is not None, f"Fortizar {structure_id} did not enter the ballpark"
    deadline = time.time() + 120
    while True:
        try:
            client.call("structureDocking", "Dock", structure_id, int(ship_id))
            break
        except p.MachoError as failure:
            if structures._refusal(failure)[0] != "DockingApproach" or time.time() > deadline:
                raise
            timed_events(client, 5.0)
    finished = timed_events(client, 60.0, until=lambda events: any(
        _plain_name(event.get("name")) == "OnDockingFinished" for event in events
    ))
    assert any(_plain_name(event.get("name")) == "OnDockingFinished" for event in finished), (
        f"ship {ship_id} did not dock at market structure {structure_id}"
    )

def _assert_wallet_notification_order(events, owner: str) -> None:
    names = [_plain_name(event.get("name")) for event in events if "name" in event]
    account_changes = [index for index, name in enumerate(names) if name == "OnAccountChange"]
    order_changes = [index for index, name in enumerate(names) if name == "OnOwnOrdersChanged"]
    assert account_changes and order_changes, f"{owner} did not receive both wallet and market order notifications: {names}"
    assert max(account_changes) < min(order_changes), f"{owner}'s order notification preceded its committed wallet change: {names}"

def test_docked_corporation_members_trade_at_an_eligible_upwell_structure():
    """A V24 headless walk at a Fortizar with docking and market services online.

    The two pilots dock through structureDocking, open GetStationAsks at the structure, post a resting bid and ask, fill both sides,
    then check the structure's order book, wallet journal, item delivery, and notification order.
    """
    if not (os.environ.get("EVE_PG_URL") and os.environ.get("EVE_STRUCTURES_SCHEMA")):
        pytest.skip("the live structure market walk needs the scratch database schemas from tools/dev/run-eve-live.sh")

    seller_run = uuid.uuid4().hex[:10]
    buyer_run = uuid.uuid4().hex[:10]
    with base.connect(f"market-structure-seller-{seller_run}") as seller, base.connect(f"market-structure-buyer-{buyer_run}") as buyer:
        seller_character, seller_ship, seller_fitted, _ = arrive(seller, structures.RACE, seller_run)
        buyer_character, buyer_ship, buyer_fitted, _ = arrive(buyer, structures.RACE, buyer_run)
        corporation = dbseed.found_corporation(structures.STATION, structures.SYSTEM, seller_character, seller_run)
        dbseed.join_corporation(seller_character, corporation, structures.DIRECTOR)
        dbseed.join_corporation(buyer_character, corporation, 0)

        fortizar_type = dbseed.sql(
            "select key from sde.types where doc #>> '{name,en}' = 'Fortizar'"
        )
        assert fortizar_type, "the pinned SDE has no Fortizar type"
        structure_id = 1_040_000_000_000 + int(uuid.uuid4().hex[:12], 16)
        structure_schema = dbseed.schema("STRUCTURES")
        dbseed.sql(
            f"insert into {structure_schema}.structures "
            "(structure_id, type_id, solar_system_id, x, y, z, owner_corporation_id, name, state, state_started_ms, upkeep, core_installed, doc) "
            f"values ({structure_id}, {int(fortizar_type)}, {structures.SYSTEM}, {structures.STRUCTURE_X}, {structures.STRUCTURE_Y}, "
            f"{structures.STRUCTURE_Z}, {corporation}, 'Market Walk Fortizar', {structures.ONLINE_STATE}, 0, 1, true, '{{}}'::jsonb)"
        )
        for service_id in (1, 5):  # StructureServiceIds.Docking and Market, persisted online
            dbseed.sql(
                f"insert into {structure_schema}.structure_services (structure_id, service_id, state) "
                f"values ({structure_id}, {service_id}, 1)"
            )

        _dock_at_market_structure(seller, seller_character, seller_ship, seller_fitted, structure_id)
        _dock_at_market_structure(buyer, buyer_character, buyer_ship, buyer_fitted, structure_id)
        assert _payload(m.plain(buyer.call("marketProxy", "GetStationAsks"))) == {}
        events_of(seller)
        events_of(buyer)

        seller_isk = dbseed.isk_of(seller_character)
        buyer_isk = dbseed.isk_of(buyer_character)
        seller_journal_at = _journal_checkpoint(seller_character)
        buyer_journal_at = _journal_checkpoint(buyer_character)
        item_id = dbseed.new_item(TRITANIUM, owner=seller_character, location=structure_id, quantity=4)

        # Post a standing bid from the buyer. The expected wallet update arrives before the order-change notice.
        buyer.call("marketProxy", "PlaceBuyOrder", structure_id, TRITANIUM, 50.0, 2, -1, 1, 90, False, None)
        _assert_wallet_notification_order(events_of(buyer), "buyer")

        bid = next(row for row in _own_orders(buyer) if _field(row, "typeID") == TRITANIUM)
        bid_id = _field(bid, "orderID")
        assert _number(_field(bid, "stationID")) == structure_id
        bids = _plain_rows(_payload(m.plain(buyer.call("marketProxy", "GetOrders", TRITANIUM)))[1])
        assert len(bids) == 1 and _number(_field(bids[0], "stationID")) == structure_id, bids

        # The seller posts four items at the bid price; two fill the bid and two remain for cancellation.
        entry = m.Dict([
            ("typeID", TRITANIUM), ("price", 50.0), ("quantity", 4),
            ("stationID", structure_id), ("itemID", item_id),
        ])
        assert m.plain(seller.call("marketProxy", "PlaceMultiSellOrder", [entry], False, 90, None)) is True
        _assert_wallet_notification_order(events_of(seller), "seller")
        _assert_wallet_notification_order(events_of(buyer), "buyer after fill")

        ask = next(row for row in _own_orders(seller) if _field(row, "typeID") == TRITANIUM)
        ask_id = _field(ask, "orderID")
        assert _field(ask, "volRemaining") == 2 and _number(_field(ask, "stationID")) == structure_id, ask
        summary = _payload(m.plain(buyer.call("marketProxy", "GetStationAsks")))[TRITANIUM]
        assert isinstance(summary, tuple) and len(summary) == 4, summary
        assert summary == (50, 2, TRITANIUM, structure_id)
        assert all(_field(row, "orderID") != bid_id for row in _own_orders(buyer))

        order_books = _payload(m.plain(buyer.call("marketProxy", "GetOrders", TRITANIUM)))
        visible_asks = _plain_rows(order_books[0])
        visible_bids = _plain_rows(order_books[1])
        assert len(visible_asks) == 1 and _number(_field(visible_asks[0], "stationID")) == structure_id
        assert visible_asks[0]["volRemaining"] == 2 and visible_bids == []

        bought = [row for row in _plain_rows(buyer.call("marketProxy", "CharGetTransactions", None))
                  if _field(row, "typeID") == TRITANIUM]
        sold = [row for row in _plain_rows(seller.call("marketProxy", "CharGetTransactions", None))
                if _field(row, "typeID") == TRITANIUM]
        assert sum(_field(row, "quantity") for row in bought) == 2, bought
        assert sum(_field(row, "quantity") for row in sold) == 2, sold
        assert all(_number(_field(row, "locationID")) == structure_id for row in bought + sold)

        buyer_hangar = [row for row in _station_hangar_items(buyer, structure_id) if _field(row, "typeID") == TRITANIUM]
        seller_hangar = [row for row in _station_hangar_items(seller, structure_id) if _field(row, "typeID") == TRITANIUM]
        assert sum(_field(row, "quantity") for row in buyer_hangar) == 2, buyer_hangar
        assert sum(_field(row, "quantity") for row in seller_hangar) == 2, seller_hangar

        seller_journal = _journal_since(seller_character, seller_journal_at)
        buyer_journal = _journal_since(buyer_character, buyer_journal_at)
        assert any(ref == 2 and amount == 100 for ref, amount in seller_journal), seller_journal
        assert any(ref == 54 and amount < 0 for ref, amount in seller_journal), seller_journal
        assert any(ref == 46 and amount < 0 for ref, amount in seller_journal), seller_journal
        assert any(ref == 2 and amount == -100 for ref, amount in buyer_journal), buyer_journal
        assert any(ref == 46 and amount < 0 for ref, amount in buyer_journal), buyer_journal
        assert dbseed.isk_of(seller_character) - seller_isk == pytest.approx(sum(amount for _, amount in seller_journal))
        assert dbseed.isk_of(buyer_character) - buyer_isk == pytest.approx(sum(amount for _, amount in buyer_journal))

        seller.call("marketProxy", "CancelCharOrder", ask_id)
        assert all(_field(row, "orderID") != ask_id for row in _own_orders(seller))
        returned = [row for row in _station_hangar_items(seller, structure_id) if _field(row, "typeID") == TRITANIUM]
        assert sum(_field(row, "quantity") for row in returned) == 2, returned
        assert _payload(m.plain(buyer.call("marketProxy", "GetStationAsks"))) == {}

# ---- M9: a trade through the market, as the client makes it ------------------------------------------------------------------------
#
# A seller takes stock from the station hangar (the game master's /giveitem), offers it (PlaceMultiSellOrder, quote.py), a buyer buys
# part of it at once (PlaceBuyOrder with no duration), both read their transactions (CharGetTransactions, marketsvc.py:20-26), the seller
# cancels the rest (CancelCharOrder) and sees its escrow come back. A change inside the five-minute modification delay is refused with
# the client sentence. Expiry and the modification deadline use controlled-clock tests (ExpiryTests, MarketCoherenceTests,
# ModificationRulesTests in dotnet/tests/Eve.Market.Tests), keeping the live walk below the five-minute timeout.

def _plain_rows(value):
    """The rows of a rowset or a list, as dicts keyed by the column names (a cached answer is unwrapped first)."""
    plain = _payload(m.plain(value))
    if isinstance(plain, list):
        return [_named(row) for row in plain]
    if isinstance(plain, tuple) and len(plain) == 2 and isinstance(plain[1], dict):
        args = plain[1]
        lines, columns = args.get("lines"), args.get("columns")
        if isinstance(lines, list) and all(isinstance(line, dict) for line in lines):
            return lines
        if isinstance(lines, list) and isinstance(columns, list):
            names = [str(name) for name in columns]
            return [dict(zip(names, line)) for line in lines]
    raise AssertionError(f"not a rowset or a list of rows: {type(plain).__name__} {str(plain)[:600]}")

def _named(row):
    """A KeyVal (util.KeyVal of the wire, plain: (name, arguments)) as its dict of names."""
    if isinstance(row, dict):
        return row
    if isinstance(row, tuple) and len(row) == 2 and isinstance(row[1], dict):
        return row[1]
    raise AssertionError(f"not a named row: {type(row).__name__} {str(row)[:400]}")

def _field(row, *names):
    """The first of the names a row carries (a row of a rowset is read by its column names)."""
    for name in names:
        if isinstance(row, dict) and name in row:
            return row[name]
    raise AssertionError(f"no column {names} in row {row!r}")

def _station_hangar_items(client, station_id):
    return _plain_rows(client.call("invbroker", "ListStationItems", station_id))

def _own_orders(client):
    return _plain_rows(client.call("marketProxy", "GetCharOrders"))

def _journal_checkpoint(owner_id: int) -> int:
    wallet = dbseed.schema("WALLET")
    return int(dbseed.sql(f"SELECT COALESCE(MAX(id), 0) FROM {wallet}.journal WHERE owner_id = {owner_id}") or 0)

def _journal_since(owner_id: int, checkpoint: int) -> list[tuple[int, float]]:
    wallet = dbseed.schema("WALLET")
    rows = dbseed.sql(
        f"SELECT ref_type_id || ' ' || amount::text FROM {wallet}.journal "
        f"WHERE owner_id = {owner_id} AND id > {checkpoint} ORDER BY id"
    )
    return [(int(ref_type), float(amount)) for ref_type, amount in (line.split() for line in rows.splitlines())]

def test_a_seller_and_a_buyer_trade_through_the_market():
    seller_run = uuid.uuid4().hex[:10]
    buyer_run = uuid.uuid4().hex[:10]
    with base.connect(f"trade-seller-{seller_run}") as seller, base.connect(f"trade-buyer-{buyer_run}") as buyer:
        _new_character(seller, seller_run)
        _new_character(buyer, buyer_run)
        station_id = seller.session.session_init.get("stationid")
        assert isinstance(station_id, int) and buyer.session.session_init.get("stationid") == station_id
        seller_isk = dbseed.isk_of(seller.session.session_init.get("charid"))
        buyer_isk = dbseed.isk_of(buyer.session.session_init.get("charid"))
        seller_journal_at = _journal_checkpoint(seller.session.session_init.get("charid"))
        buyer_journal_at = _journal_checkpoint(buyer.session.session_init.get("charid"))

        # stock: the game master's command puts the items in the station hangar and says so
        message = seller.call("slash", "SlashCmd", "/giveitem Tritanium 100")
        assert "Tritanium" in str(message) and "station hangar" in str(message), message
        stock = [row for row in _station_hangar_items(seller, station_id) if _field(row, "typeID") == TRITANIUM]
        assert stock, "the hangar lists the stock"
        item_id = _field(stock[0], "itemID")

        # place: a sell order of 40 at 50 ISK (a stack of the seller's hangar, quote.py's entry fields)
        entry = m.Dict([("typeID", TRITANIUM), ("price", 50.0), ("quantity", 40), ("stationID", station_id), ("itemID", item_id)])
        assert m.plain(seller.call("marketProxy", "PlaceMultiSellOrder", [entry], False, 90, None)) is True
        ask = next(row for row in _own_orders(seller) if _field(row, "typeID") == TRITANIUM)
        ask_id = _field(ask, "orderID")
        assert _field(ask, "volRemaining") == 40 and _field(ask, "price") == 50.0, ask

        # the book shows the ask to a buyer at the station (GetStationAsks, the KeyVal rows of M1)
        asks = _payload(m.plain(buyer.call("marketProxy", "GetStationAsks")))
        assert isinstance(asks, dict), f"{type(asks).__name__}: {str(asks)[:700]}"
        best = _named(asks[TRITANIUM])
        assert best["volRemaining"] == 40 and best["price"] == 50.0, best

        # match: an immediate buy of 15 takes 15 of the ask at its price; no order rests and no row is faked
        assert buyer.call("marketProxy", "PlaceBuyOrder", station_id, TRITANIUM, 50.0, 15, -1, 1, 0, False, None) is None
        bought = [row for row in _plain_rows(buyer.call("marketProxy", "CharGetTransactions", None)) if _field(row, "typeID") == TRITANIUM]
        assert bought and sum(_field(row, "quantity") for row in bought) == 15, bought
        sold = [row for row in _plain_rows(seller.call("marketProxy", "CharGetTransactions", None)) if _field(row, "typeID") == TRITANIUM]
        assert sold and sum(_field(row, "quantity") for row in sold) == 15, sold
        assert _field(next(row for row in _own_orders(seller) if _field(row, "orderID") == ask_id), "volRemaining") == 25

        # Each account's balance delta is represented by its persisted trade and fee journal entries.
        seller_journal = _journal_since(seller.session.session_init.get("charid"), seller_journal_at)
        buyer_journal = _journal_since(buyer.session.session_init.get("charid"), buyer_journal_at)
        assert any(ref == 2 and amount == 750 for ref, amount in seller_journal), seller_journal
        assert any(ref == 54 and amount < 0 for ref, amount in seller_journal), seller_journal
        assert any(ref == 46 and amount == -100 for ref, amount in seller_journal), seller_journal
        assert not any(ref == 149 for ref, _ in seller_journal), seller_journal
        assert buyer_journal == [(2, -750.0)], buyer_journal
        assert dbseed.isk_of(seller.session.session_init.get("charid")) - seller_isk == sum(amount for _, amount in seller_journal)
        assert dbseed.isk_of(buyer.session.session_init.get("charid")) - buyer_isk == sum(amount for _, amount in buyer_journal)

        # modify inside the five-minute delay: refused with the client's sentence, the price unchanged
        with pytest.raises(p.MachoError) as refused:
            seller.call("marketProxy", "ModifyCharOrder", ask_id, 55.0, 50.0, 25, None, None)
        label, arguments = _label_and_arguments(refused.value)
        assert label == "CustomInfo" and "seconds" in arguments["info"], arguments
        assert _field(next(row for row in _own_orders(seller) if _field(row, "orderID") == ask_id), "price") == 50.0

        # cancel: the rest of the ask leaves the book, its item returns to the hangar, and the escrow reads zero
        assert seller.call("marketProxy", "CancelCharOrder", ask_id) is None
        assert all(_field(row, "orderID") != ask_id for row in _own_orders(seller))
        escrow = {k: _number(v) for k, v in describe(seller.call("marketProxy", "GetCharEscrow"))["args"]["$dict"]}
        assert escrow == {"iskEscrow": 0, "itemsEscrow": 0}, escrow
        # the hangar: 100 taken, 40 offered (60 stay in the hangar), then 25 of the offer returned on the cancel: 85
        hangar = [row for row in _station_hangar_items(seller, station_id) if _field(row, "typeID") == TRITANIUM]
        assert sum(_field(row, "quantity") for row in hangar) == 85, hangar

        # the corporation's order book of a character in an NPC corporation is empty, and a corporation order is refused
        assert _plain_rows(seller.call("marketProxy", "GetCorporationOrders")) == []
        with pytest.raises(p.MachoError) as refused:
            buyer.call("marketProxy", "PlaceBuyOrder", station_id, TRITANIUM, 40.0, 5, -1, 1, 90, True, None)
        label, arguments = _label_and_arguments(refused.value)
        assert label == "CustomInfo" and arguments == {"info": "Your character does not hold the market rights of that corporation."}, arguments



def test_market_browse_and_history_refresh_fit_one_session():
    run = uuid.uuid4().hex[:10]
    with base.connect(f"walk-market-budget-{run}") as client:
        _new_character(client, run)

        # Market groups are loaded before the selected Minerals group is browsed.
        groups = _plain_rows(client.call("marketProxy", "GetMarketGroups"))
        assert any(_field(row, "marketGroupID") == 4 for row in groups)

        for _ in range(3):
            # MarketBase._GetAsksForRange uses GetRegionBest in region view. The result is a typeID map
            # whose best-order values provide the fields consumed by the market panel.
            best = _payload(m.plain(client.call("marketProxy", "GetRegionBest")))
            assert isinstance(best, dict)
            for type_id, row in best.items():
                fields = _named(row)
                assert {"price", "volRemaining", "typeID", "stationID"} <= set(fields)
                assert fields["typeID"] == type_id

            # Refresh the selected type's asks/bids, own orders, and price history.
            orders = _payload(m.plain(client.call("marketProxy", "GetOrders", TRITANIUM)))
            assert isinstance(orders, (list, tuple)) and len(orders) == 2
            _plain_rows(client.call("marketProxy", "GetCharOrders"))
            try:
                _plain_rows(client.call("marketProxy", "GetMarketOrderHistory"))
            except p.MachoError as error:
                pytest.fail(
                    f"GetMarketOrderHistory failed: code={error.code!r}, payload={error.payload!r}",
                    pytrace=True,
                )
            _plain_rows(client.call("marketProxy", "GetOldPriceHistory", TRITANIUM))
            _plain_rows(client.call("marketProxy", "GetNewPriceHistory", TRITANIUM))

            # Refresh PLEX orders, best price and history within this same client session.
            plex_orders = _payload(m.plain(client.call("marketProxy", "GetPlexOrders")))
            assert isinstance(plex_orders, (list, tuple)) and len(plex_orders) == 2
            plex_best = _payload(m.plain(client.call("marketProxy", "GetPlexBest")))
            assert isinstance(plex_best, dict)
            for type_id, row in plex_best.items():
                fields = _named(row)
                assert {"price", "volRemaining", "typeID", "stationID"} <= set(fields)
                assert fields["typeID"] == type_id

            plex_history = m.plain(client.call("marketProxy", "GetPlexHistory"))
            assert isinstance(plex_history, dict) and 44992 in plex_history
            assert len(plex_history[44992]) == 2
            _plain_rows(client.call("marketProxy", "GetPlexOldPriceHistory"))
            _plain_rows(client.call("marketProxy", "GetPlexNewPriceHistory"))

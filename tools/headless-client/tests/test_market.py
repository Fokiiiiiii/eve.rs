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
from tests import dbseed
from tests import test_login_to_character as base

pytestmark = pytest.mark.skipif(not base.LIVE, reason="set EVE_LIVE_HOST=host:port to test against a running server")

TRITANIUM = 34
ASK_ROW_NAMES = {"typeID", "stationID", "price", "volRemaining"}  # marketsvc.py:297-300; quote.py:97-100 reads .typeID

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

        # GetStationAsks (proxy 6211): a dict of typeID -> KeyVal rows read by name; empty while nobody sells here
        asks = _payload(m.plain(client.call("marketProxy", "GetStationAsks")))
        assert isinstance(asks, dict)
        for type_id, row in asks.items():
            assert isinstance(row, dict) and ASK_ROW_NAMES <= set(row), f"ask row for {type_id} has {sorted(row)}"
            assert row["typeID"] == type_id

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

# ---- M9: a trade through the market, as the client makes it ------------------------------------------------------------------------
#
# A seller takes stock from the station hangar (the game master's /giveitem), offers it (PlaceMultiSellOrder, quote.py), a buyer buys
# part of it at once (PlaceBuyOrder with no duration), both read their transactions (CharGetTransactions, marketsvc.py:20-26), the seller
# cancels the rest (CancelCharOrder) and sees its escrow come back. A change inside the five-minute modification delay is refused with
# the labelled sentence; the change after the delay is checked when EVE_LIVE_SLOW=1 (it waits five minutes). Expiry is not live: it is
# covered by the fake-clock tests (ExpiryTests, MarketCoherenceTests in dotnet/tests/Eve.Market.Tests).

SLOW = os.environ.get("EVE_LIVE_SLOW") == "1"

def _plain_rows(value):
    """The rows of a rowset or a list, as dicts keyed by the column names (a cached answer is unwrapped first)."""
    plain = _payload(m.plain(value))
    if isinstance(plain, list):
        return [_named(row) for row in plain]
    if isinstance(plain, tuple) and len(plain) == 2 and isinstance(plain[1], dict):
        args = plain[1]
        header, lines = args.get("header"), args.get("lines")
        if isinstance(lines, list) and all(isinstance(line, dict) for line in lines):
            return lines
        if isinstance(lines, list) and isinstance(header, list):
            names = [str(name) for name in header]
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

        if SLOW:
            time.sleep(300)
            seller.call("marketProxy", "ModifyCharOrder", ask_id, 55.0, 50.0, 25, None, None)
            assert _field(next(row for row in _own_orders(seller) if _field(row, "orderID") == ask_id), "price") == 55.0

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

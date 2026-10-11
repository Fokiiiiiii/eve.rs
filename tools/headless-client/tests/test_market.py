"""The market walk (M1): a character docked at its station calls the market service the V24.01 client calls (marketProxy) and reads the
answers the client reads. Each answer is checked on the shape of its client call site (marketsvc.py, marketSvc.py, sellMulti.py); a
refusal is the client's own label with its arguments. The live host runs with EVE_MARKET_SEED=skip, so the book starts empty: the walk
checks the empty answers, the own-order and transaction lists, and the refusals.

    EVE_LIVE_HOST=host:port pytest tests/test_market.py

Skipped unless EVE_LIVE_HOST is set. tools/dev/run-eve-live.sh runs it.
"""

from __future__ import annotations

import uuid

import pytest

from evecli import marshal as m
from evecli import packet as p
from evecli.describe import describe
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

        # BuyMultipleItems (proxy 6915) with no line: the empty list, which BuyMulti iterates (marketsvc.py:270-273)
        assert m.plain(client.call("marketProxy", "BuyMultipleItems", station_id, [], False)) == []

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

        # a price of five significant figures is off the ticks (M2, decision Q12): the client's CustomInfo carries the sentence the
        # server writes, and nothing is escrowed (the check runs before any funds move)
        with pytest.raises(p.MachoError) as refused:
            client.call("marketProxy", "PlaceBuyOrder", station_id, TRITANIUM, 12345.0, 1, -1, 1, 90, False, None)
        label, arguments = _label_and_arguments(refused.value)
        assert label == "CustomInfo"
        assert arguments == {"info": "Order prices have at most four significant figures."}

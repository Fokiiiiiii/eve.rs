"""Two-character public item-exchange walk through contractProxy RPCs.

Run against eve.rs through tools/dev/run-eve-live.sh so the contract, inventory,
wallet, scheduler, event and identity schemas are available to the seed helpers.
"""

from __future__ import annotations

import os
import time
import uuid

import pytest

from evecli.describe import describe
from evecli.marshal import Object as MarshalObject
from evecli.observe import notifications_of
from tests import dbseed
from tests import test_login_to_character as base
from tests.test_health import HEALTH
from tests.test_undock import STATION, arrive, number

ITEM_TYPE = 34
PRICE = 100_000
pytestmark = [
    pytest.mark.skipif(not base.LIVE, reason="set EVE_LIVE_HOST=host:port to test against a running server"),
    pytest.mark.skipif(os.environ.get("EVE_LIVE_SERVER", "eve.rs") == "reference", reason="the walk seeds PostgreSQL state"),
]


def _wait_for_notification(client, name: str, seconds: float = 5.0) -> bool:
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        if any(event.get("name") == name for event in notifications_of(client)):
            return True
        time.sleep(0.2)
    return False


def test_two_characters_create_accept_and_settle_a_public_item_exchange():
    race = HEALTH["races"][0]
    run = uuid.uuid4().hex[:10]
    with base.connect(f"walk-contract-issuer-{run}") as issuer, base.connect(f"walk-contract-acceptor-{run}") as acceptor:
        issuer_id, _, _, _ = arrive(issuer, race, run + "i")
        acceptor_id, _, _, _ = arrive(acceptor, race, run + "a")
        item_id = dbseed.new_item(ITEM_TYPE, owner=issuer_id, location=STATION, quantity=2)
        dbseed.set_isk(issuer_id, 2_000_000)
        dbseed.set_isk(acceptor_id, 2_000_000)
        issuer_before = dbseed.isk_of(issuer_id)
        acceptor_before = dbseed.isk_of(acceptor_id)

        creation = {
            "contractType": 1,
            "isPrivate": False,
            "assignedToID": 0,
            "minutesExpire": 1440,
            "numDays": 0,
            "startStationID": STATION,
            "destinationID": 0,
            "price": PRICE,
            "reward": 0,
            "collateral": 0,
            "title": f"Headless contract {run}",
            "description": "Two-character public item exchange",
            "itemList": [(item_id, 1)],
            "startStationDivision": dbseed.HANGAR,
            "requestItemTypeList": [],
            "forCorp": False,
            "multiContract": False,
        }
        client_fields = (
            "contractType", "isPrivate", "assignedToID", "minutesExpire", "numDays", "startStationID",
            "destinationID", "price", "reward", "collateral", "title", "description", "itemList",
            "startStationDivision", "requestItemTypeList", "forCorp", "multiContract",
        )
        wire_creation = MarshalObject("contracts.contractStruct.ContractStruct", tuple(creation[name] for name in client_fields))
        kind, created = base.attempt(issuer, "contractProxy", "CreateContract", wire_creation, True)
        assert kind == "ok", base.brief(created)
        contract_id = number(created[0])
        assert contract_id > 0
        assert dbseed.owner_of(item_id) == issuer_id
        assert dbseed.isk_of(issuer_id) < issuer_before, "public listing fees and deposit are charged on creation"

        accepted = acceptor.call("contractProxy", "AcceptContract", contract_id, False)
        accepted_fields = dict(describe(accepted)["args"]["$dict"])
        assert number(accepted_fields["contractID"]) == contract_id
        assert number(accepted_fields["acceptorID"]) == acceptor_id
        assert dbseed.owner_of(item_id) == acceptor_id
        assert dbseed.isk_of(acceptor_id) == acceptor_before - PRICE
        assert dbseed.isk_of(issuer_id) > issuer_before - 200_000, "the issuer receives the agreed price after listing costs"
        assert _wait_for_notification(issuer, "OnContractAccepted"), "issuer did not receive OnContractAccepted"

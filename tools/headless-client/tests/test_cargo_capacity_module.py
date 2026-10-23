"""A live V24.01 inventory and Dogma walk for cargo-capacity module transitions.

    Run with EVE_LIVE_HOST=host:port: pytest tests/test_cargo_capacity_module.py
"""

from __future__ import annotations

import uuid

import pytest

from tests import test_login_to_character as base
from tests.test_cargo import CARGO, TRITANIUM
from tests.test_others import watch
from tests.test_target import plain_race
from tests.test_undock import STATION, arrive, outcome

pytestmark = pytest.mark.skipif(not base.LIVE, reason="set EVE_LIVE_HOST=host:port to test against a running server")

EXPANDED_CARGOHOLD_I = 1319
CARGO_FLAG = 5
LOW_SLOT_FIRST = 11


def rows(who, inventory, flag=None):
    values = who.call_bound(inventory, "List", flag) if flag is not None else who.call_bound(inventory, "List")
    return [row.fields() for row in values if hasattr(row, "fields")]


def events_named(events, name):
    return [event for event in events if event["name"] == name]


def test_a_cargo_module_cannot_be_offlined_or_unfitted_above_the_resulting_capacity():
    race = CARGO["races"][0]
    run = uuid.uuid4().hex[:10]
    with base.connect(f"walk-cargo-capacity-{run}") as who:
        _, ship, _, _ = arrive(who, plain_race({"pilot": race}, "pilot"), run)
        who.call("slash", "SlashCmd", "/giveskill me Hull Upgrades 1")
        who.call("slash", "SlashCmd", "/giveitem Expanded Cargohold I 1")

        manager = who.call("invbroker", "MachoBindObject", (STATION, 15), None)[0]
        hangar = who.call_bound(manager, "GetInventory", 10004)
        docked = who.call("invbroker", "MachoBindObject", (STATION, 15), ("GetInventoryFromId", (ship, 0), None))[1]
        module = next(int(item["itemID"]) for item in rows(who, hangar) if int(item["typeID"]) == EXPANDED_CARGOHOLD_I)
        dogma = who.call("dogmaIM", "MachoBindObject", (STATION, 5), None)[0]

        who.call_bound(docked, "Add", module, STATION, flag=LOW_SLOT_FIRST)
        who.call_bound(dogma, "SetModuleOnline", ship, module)

        who.call("slash", "SlashCmd", "/giveitem Tritanium 100000")
        tritanium = next(int(item["itemID"]) for item in rows(who, hangar) if int(item["typeID"]) == TRITANIUM)
        for _ in range(200):
            source = next((item for item in rows(who, hangar) if int(item["itemID"]) == tritanium), None)
            if source is None:
                break
            quantity = min(250, int(source["quantity"]))
            _, error = outcome(lambda: who.call_bound(docked, "Add", tritanium, STATION, qty=quantity, flag=CARGO_FLAG))
            if error is not None:
                break
        else:
            raise AssertionError("cargo did not reach the hold limit within the bounded fill walk")

        cargo_rows = rows(who, docked, CARGO_FLAG)
        assert any(int(item["typeID"]) == TRITANIUM for item in cargo_rows), "the client could not put cargo in the hold"
        watch({"events": who}, 0.5)
        _, refusal = outcome(lambda: who.call_bound(dogma, "TakeModuleOffline", ship, module))
        assert refusal is not None, "the server accepted an offline transition while cargo filled the hold"
        refused_events = watch({"events": who}, 0.5)["events"]
        assert not events_named(refused_events, "OnModuleAttributeChanges"), "a refused transition changed Dogma attributes"

        changed = False
        for _ in range(200):
            cargo = next((item for item in rows(who, docked, CARGO_FLAG) if int(item["typeID"]) == TRITANIUM), None)
            if cargo is None:
                break
            quantity = min(250, int(cargo["quantity"]))
            _, error = outcome(lambda: who.call_bound(hangar, "Add", int(cargo["itemID"]), ship, qty=quantity, flag=4))
            assert error is None
            _, retry_refusal = outcome(lambda: who.call_bound(dogma, "TakeModuleOffline", ship, module))
            if retry_refusal is None:
                changed = True
                break
        assert changed, "freeing cargo did not make the offline operation succeed"
        offline_events = watch(
            {"events": who}, 1.0,
            until=lambda seen: any(event["name"] == "OnModuleAttributeChanges" for event in seen["events"]),
        )["events"]
        assert events_named(offline_events, "OnModuleAttributeChanges")

        who.call_bound(dogma, "SetModuleOnline", ship, module)
        who.call_bound(hangar, "Add", module, ship, flag=4)
        assert any(int(item["itemID"]) == module for item in rows(who, hangar))

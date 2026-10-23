"""A live V24.01 headless client launches an assembled ship from a space container.

Run with EVE_LIVE_HOST=host:port and EVE_LIVE_SLOW=1 against EveJS or eve.rs.
"""

from __future__ import annotations

import uuid

import pytest

from evecli import marshal as m
from tests import test_login_to_character as base
from tests.test_loot import LOOT
from tests.test_target import plain_race
from tests.test_others import watch
from tests.test_undock import STATION, SYSTEM, arrive
from tests.test_weapon import slims_in

pytestmark = pytest.mark.skipif(not base.LIVE, reason="set EVE_LIVE_HOST=host:port to test against a running server")

TRITANIUM = 34
KESTREL = 602


def test_a_pilot_launches_an_assembled_ship_from_a_space_container():
    race = LOOT["races"][0]
    run = uuid.uuid4().hex[:10]
    with base.connect(f"walk-eject-{run}") as who:
        _, ship, _, _ = arrive(who, plain_race({"pilot": race}, "pilot"), run)
        station = who.call("invbroker", "MachoBindObject", (STATION, 15), None)[0]
        hangar = who.call_bound(station, "GetInventory", 10004)
        hold = who.call(
            "invbroker",
            "MachoBindObject",
            (STATION, 15),
            ("GetInventoryFromId", (ship, 0), None),
        )[1]

        ship_grant = m.plain(who.call("slash", "SlashCmd", f"/giveitem {KESTREL} 1"))
        ore_grant = m.plain(who.call("slash", "SlashCmd", f"/giveitem {TRITANIUM} 1"))
        assert "was added to your station hangar." in ship_grant, ship_grant
        assert "was added to your station hangar." in ore_grant, ore_grant
        hangar = who.call_bound(station, "GetInventory", 10004)
        rows = who.call_bound(hangar, "List")
        kestrel = next(int(row.fields()["itemID"]) for row in rows if int(row.fields()["typeID"]) == KESTREL)
        tritanium = next(int(row.fields()["itemID"]) for row in rows if int(row.fields()["typeID"]) == TRITANIUM)
        who.call_bound(hold, "Add", kestrel, STATION, qty=1, flag=5)
        who.call_bound(hold, "Add", tritanium, STATION, qty=1, flag=5)

        events = {"events": who}
        ship_service = who.call("ship", "MachoBindObject", (STATION, 15), None)
        who.call_bound(ship_service[0], "Undock", ship, False)
        who.call("beyonce", "MachoBindObject", SYSTEM, None)
        who.call_bound(who.call("dogmaIM", "MachoBindObject", (SYSTEM, 5), None)[0], "GetAllInfo", True, True, None)
        watch(events, 45.0, until=lambda seen: any(e["name"] == "OnInvulnCancelled" for e in seen["events"]))

        space_ship = who.call("ship", "MachoBindObject", SYSTEM, None)[0]
        who.call_bound(space_ship, "Jettison", m.List([tritanium]))
        created = watch(events, 4.0)
        can = next(
            (item for item, _ in slims_in(created["events"]) if item != ship),
            0,
        )
        assert can, "jettison did not create a visible container"

        opened = who.call(
            "invbroker",
            "MachoBindObject",
            (SYSTEM, 5),
            ("GetInventoryFromId", (can, 0), None),
        )[1]
        who.call_bound(opened, "Add", kestrel, ship, qty=1, flag=0)
        answer = who.call("eject", "LaunchShipOrContainerFromWreckOrContainer", can, kestrel)
        assert m.plain(answer) is None

        visible = watch(
            events,
            8.0,
            until=lambda seen: any(item == kestrel for item, _ in slims_in(seen["events"])),
        )
        assert any(item == kestrel for item, _ in slims_in(visible["events"])), (
            f"launched ship {kestrel} did not appear in the client's space updates"
        )

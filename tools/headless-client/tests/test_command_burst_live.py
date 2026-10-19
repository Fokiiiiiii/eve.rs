"""Two-pilot command-burst walk: fleet delivery, dogma updates and timed expiry.

    EVE_LIVE_HOST=host:port pytest tests/test_command_burst_live.py

The calls are the client's fleetObjectHandler CreateFleet/Init/Invite/AcceptInvite and dogmaIM Activate/GetAllInfo calls.
The fitted module and its one charge are seeded in the pilot's ship; fitting and ammunition loading use the client's LoadAmmo call.
"""

from __future__ import annotations

import uuid

import pytest

from evecli import marshal as m
from evecli.describe import describe
from evecli.observe import notifications_of, wait_for_events
from tests import dbseed, gm
from tests import test_login_to_character as base
from tests.test_health import HEALTH
from tests.test_undock import STATION, SYSTEM, arrive, number

pytestmark = pytest.mark.skipif(not base.LIVE, reason="set EVE_LIVE_HOST=host:port to test against a running server")

BURST_MODULE = 42529  # Shield Command Burst I
BURST_CHARGE = 42695  # Shield Harmonizing Charge
BURST_HULL = 37480  # Bifrost
SHIELD_EM_RESONANCE = 271


def ship_attribute(answer, ship_id: int, attribute_id: int) -> float:
    ship_info = dict(describe(answer)["args"]["$dict"])["shipInfo"]["$dict"]
    for item_id, item in ship_info:
        if int(number(item_id)) == ship_id:
            fields = dict(item["args"]["$dict"])
            for attribute, value in fields["attributes"]["$dict"]:
                if int(number(attribute)) == attribute_id:
                    while isinstance(value, dict) and len(value) == 1:
                        value = next(iter(value.values()))
                    return float(value)
            raise AssertionError(f"GetAllInfo omitted attribute {attribute_id} for ship {ship_id}")
    raise AssertionError(f"GetAllInfo omitted ship {ship_id}")


def named(events: list[dict], name: str) -> list[dict]:
    return [event for event in events if event["name"] == name]


def test_a_loaded_burst_changes_a_fleet_members_dogma_until_its_buff_expires():
    race = HEALTH["races"][0]
    run = uuid.uuid4().hex[:10]
    with base.connect(f"walk-burst-owner-{run}") as owner, base.connect(f"walk-burst-member-{run}") as member:
        owner_character, owner_ship, owner_modules, _ = arrive(owner, race, f"{run}a")
        member_character, member_ship, _, _ = arrive(member, race, f"{run}b")

        fleet = owner.call("fleetObjectHandler", "CreateFleet")
        assert fleet is not None, "pilot A did not create a fleet"
        owner.call_bound(fleet, "Init", BURST_HULL, None, adInfoData=None)
        fleet_id = int(owner.call_bound(fleet, "GetFleetID"))
        owner.call_bound(fleet, "Invite", member_character, None, None, None)
        invitation = wait_for_events(member, lambda events: named(events, "OnFleetInvite"), timeout=15)
        assert named(invitation, "OnFleetInvite"), "pilot B did not receive the fleet invitation"
        member_fleet = member.call("fleetObjectHandler", "MachoBindObject", fleet_id, None)
        assert member_fleet is not None, "pilot B could not bind the invited fleet"
        member.call_bound(member_fleet[0], "AcceptInvite", 587)
        assert int(member.call_bound(member_fleet[0], "GetFleetID")) == fleet_id

        gm.give_skill(owner, 3348, 1)  # Leadership, the module and charge requirement
        gm.give_skill(owner, 3350, 1)  # Shield Command, the module and charge requirement
        dbseed.set_hull(owner_ship, BURST_HULL)
        occupied = {dbseed.flag_of(item) for item in owner_modules}
        burst_flag = next(flag for flag in dbseed.HIGH_SLOTS if flag not in occupied)
        burst = dbseed.new_item(BURST_MODULE, owner=owner_character, location=owner_ship,
                                flag=burst_flag, singleton=True)
        charge = dbseed.new_item(BURST_CHARGE, owner=owner_character, location=owner_ship,
                                 flag=5, quantity=1)
        station_dogma = owner.call("dogmaIM", "MachoBindObject", (STATION, 15), None)
        owner.call_bound(station_dogma[0], "LoadAmmo", owner_ship, m.List([burst]), m.List([charge]), STATION)

        member_station_dogma = member.call("dogmaIM", "MachoBindObject", (STATION, 15), None)
        before_info = member.call_bound(member_station_dogma[0], "GetAllInfo", True, True, None)
        before = ship_attribute(before_info, member_ship, SHIELD_EM_RESONANCE)

        owner_ship_service = owner.call("ship", "MachoBindObject", (STATION, 15), None)
        member_ship_service = member.call("ship", "MachoBindObject", (STATION, 15), None)
        owner.call_bound(owner_ship_service[0], "Undock", owner_ship, False)
        member.call_bound(member_ship_service[0], "Undock", member_ship, False)
        owner_dogma = owner.call("dogmaIM", "MachoBindObject", (SYSTEM, 5), None)
        member_dogma = member.call("dogmaIM", "MachoBindObject", (SYSTEM, 5), None)
        owner.call_bound(owner_dogma[0], "GetAllInfo", True, True, None)
        member.call_bound(member_dogma[0], "GetAllInfo", True, True, None)
        notifications_of(member)

        owner.call_bound(owner_dogma[0], "SetModuleOnline", owner_ship, burst)
        owner.call_bound(owner_dogma[0], "Activate", burst, "moduleBonusWarfareLinkShield", None, 1000)
        active_events = wait_for_events(
            member,
            lambda events: named(events, "OnModuleAttributeChanges") and named(events, "OnDbuffUpdated"),
            timeout=85,
        )
        changes = named(active_events, "OnModuleAttributeChanges")
        dbuffs = named(active_events, "OnDbuffUpdated")
        assert changes and dbuffs, "the receiver did not hear both its dogma attributes and buff state"
        assert active_events.index(changes[0]) < active_events.index(dbuffs[0]), "dogma attributes must precede the buff-state update"

        active_info = member.call_bound(member_dogma[0], "GetAllInfo", True, True, None)
        active = ship_attribute(active_info, member_ship, SHIELD_EM_RESONANCE)
        assert active == pytest.approx(before * 0.92), f"shield resonance was {active}, expected an 8% resistance burst from {before}"

        notifications_of(member)
        expired_events = wait_for_events(
            member,
            lambda events: named(events, "OnModuleAttributeChanges") and named(events, "OnDbuffUpdated"),
            timeout=85,
        )
        assert named(expired_events, "OnModuleAttributeChanges"), "buff expiry did not restore receiver dogma attributes"
        assert named(expired_events, "OnDbuffUpdated"), "buff expiry did not update receiver buff state"
        after_info = member.call_bound(member_dogma[0], "GetAllInfo", True, True, None)
        after = ship_attribute(after_info, member_ship, SHIELD_EM_RESONANCE)
        assert after == pytest.approx(before), f"expired shield resonance stayed at {after}, baseline was {before}"

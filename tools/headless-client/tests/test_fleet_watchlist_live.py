"""Two-pilot watchlist RPC walk using the client's Python set wire shape."""

from __future__ import annotations

import uuid

import pytest

from evecli import marshal as m
from evecli.observe import wait_for_events
from tests import test_login_to_character as base
from tests.test_health import HEALTH
from tests.test_undock import arrive

pytestmark = pytest.mark.skipif(not base.LIVE, reason="set EVE_LIVE_HOST=host:port to test against a running server")


def test_fleet_watchlist_methods_accept_client_python_sets():
    race = HEALTH["races"][0]
    run = uuid.uuid4().hex[:10]
    with base.connect(f"walk-watch-owner-{run}") as owner, base.connect(f"walk-watch-member-{run}") as member:
        owner_character, _, _, _ = arrive(owner, race, f"{run}a")
        member_character, _, _, _ = arrive(member, race, f"{run}b")
        assert owner_character > 0

        fleet = owner.call("fleetObjectHandler", "CreateFleet")
        assert fleet is not None, "pilot A did not create a fleet"
        owner.call_bound(fleet, "Init", 37480, None, adInfoData=None)
        fleet_id = int(owner.call_bound(fleet, "GetFleetID"))
        owner.call_bound(fleet, "Invite", member_character, None, None, None)
        invitation = wait_for_events(member, lambda events: any(event["name"] == "OnFleetInvite" for event in events), timeout=15)
        assert any(event["name"] == "OnFleetInvite" for event in invitation), "pilot B did not receive the fleet invitation"
        member_fleet = member.call("fleetObjectHandler", "MachoBindObject", fleet_id, None)
        assert member_fleet is not None, "pilot B could not bind the invited fleet"
        member.call_bound(member_fleet[0], "AcceptInvite", 587)
        assert int(member.call_bound(member_fleet[0], "GetFleetID")) == fleet_id

        favorite = m.Object("set", ([member_character],))
        empty = m.Object("set", ([],))
        assert m.plain(owner.call("fleetMgr", "AddToWatchlist", [member_character], favorite)) is True
        assert m.plain(owner.call("fleetMgr", "RegisterForDamageUpdates", favorite)) is True
        assert m.plain(owner.call("fleetMgr", "RemoveFromWatchlist", member_character, empty)) is True

"""The Encounter Surveillance System (ESS-1) as the V24.01 client reads it, on a live host.

A character that has selected its station asks the essMgr and the dynamic resource cache for the ESS of the system it is in, the way the client does
(dynamicResourceSvc.py: GetESSDataForCurrentSystem, IsPlayerLinkedToReserveBank, GetESSAccessHistoryForCurrentSystem, GetESSReserveBankKeysForCurrentSystem;
essSystemInfoContainer.py: GetESSSystemDetails). The host decides whether that system has an ESS: an operator places one with EVE_ESS_SYSTEMS, and the test
reads the placement from EVE_LIVE_ESS_SYSTEM=<solarSystemId>:<essId> (the same entry). Without that variable the test expects no ESS in the system. A link the
client asks for is refused by the server when the ESS object is not in space (no geometry), and the refusal changes nothing, as the client expects.

    EVE_LIVE_HOST=host:port EVE_LIVE_ESS_SYSTEM=30000142:900000001 pytest tests/test_ess.py

Skipped unless EVE_LIVE_HOST is set. tools/dev/run-eve-live.sh runs it.
"""

from __future__ import annotations

import os
import re
import uuid

import pytest

from evecli import marshal as m
from tests import test_login_to_character as base
from tests import test_market as market

pytestmark = pytest.mark.skipif(not base.LIVE, reason="set EVE_LIVE_HOST=host:port to test against a running server")

ESS_KEYS = {
    "essID",
    "beaconID",
    "typeID",
    "solarSystemID",
    "currentOutput",
    "mainValue",
    "reserveValue",
    "mainBankLink",
    "reserveBankLastPulseInitiated",
    "reserveBankPulsesRemaining",
    "reserveBankPulsesTotal",
    "reserveBankActiveLinks",
}
DETAIL_KEYS = {"autopaymentWindowState", "activeLinks", "totalPulses", "reserveTimeWindowState", "reserveTimeChosen"}
SDE_RESERVE_KEYS = {57714, 57715, 57716, 57717, 57718, 57719, 57720, 57721}
SERVER = os.environ.get("EVE_LIVE_SERVER", "eve.rs").strip().lower()

QUADRANT_KEY_PAIRS = [{57714, 57715}, {57716, 57719}, {57717, 57718}, {57720, 57721}]

def _placed_entry() -> tuple[int, int] | None:
    """The placement the host was started with (solarSystemId, essId), or None when the test expects no ESS."""
    raw = os.environ.get("EVE_LIVE_ESS_SYSTEM", "").strip()
    if not raw:
        return None
    system, ess = raw.split(":")
    return int(system), int(ess)

def test_a_docked_character_reads_its_systems_ess_as_the_client_does():
    run = uuid.uuid4().hex[:10]
    placed = _placed_entry()
    with base.connect(f"walk-ess-{run}") as client:
        market._new_character(client, run)
        system_id = client.session.session_init.get("solarsystemid2")
        assert isinstance(system_id, int), "a character that selected its station is in a system: the session says which"
        print(f"ess-test: docked in solar system {system_id}; placed {placed}")

        data = m.plain(client.call("essMgr", "GetDataForClientSolarSystem"))
        keys = m.plain(client.call("dynamicResourceCacheMgr", "GetESSReserveBankKeys", system_id))
        details = m.plain(client.call("dynamicResourceCacheMgr", "GetESSSystemDetails", system_id))
        thefts = m.plain(client.call("essMgr", "GetMainBankTheftsForClientSolarSystem"))
        reserve_thefts = m.plain(client.call("essMgr", "GetReserveBankTheftsForClientSolarSystem"))

        assert set(details) == DETAIL_KEYS
        assert thefts == [] and reserve_thefts == []
        if SERVER != "reference":
            grant = str(client.call("slash", "SlashCmd", "/ess grant"))
            assert "SDE type list 231" in grant and "station hangar" in grant, grant
            link = client.call("slash", "SlashCmd", "/ess link")
            if isinstance(link, bytes):
                link = link.decode("utf-8")
            link = str(link)
            assert link == "ESS main-bank link request sent. Check the ESS status notification for the result."
        if placed is None or placed[0] != system_id:
            # no ESS here: the client reads None as an offline system (dynamicResourceSvc.py:254) and no keys
            assert data is None
            assert keys == [] and details["totalPulses"] == 0
            return

        ess_system, ess_id = placed
        assert set(data) == ESS_KEYS
        assert (data["essID"], data["solarSystemID"]) == (ess_id, ess_system)
        assert data["mainBankLink"] is None and data["reserveBankActiveLinks"] == 0
        assert data["mainValue"] == 0 and data["reserveValue"] == 0
        # the keys of the system's quadrant: the 15 and the 45 variant of one quadrant, or none when the system is in no quadrant
        assert keys == [] or set(keys) in QUADRANT_KEY_PAIRS, keys
        assert details["autopaymentWindowState"] == 0, "an empty Main Bank has the long autopayment window"

        # a link the client asks for: refused here (no ESS in space), answered with None and no change to the state
        assert client.call("essMgr", "AttemptLinkToMainBank") is None
        after = m.plain(client.call("essMgr", "GetDataForClientSolarSystem"))
        assert after["mainBankLink"] is None
        assert client.call("essMgr", "RequestMainBankUnlink") is None

def test_a_linkable_hull_starts_and_cancels_an_in_space_main_bank_link():
    placed = _placed_entry()
    if placed is None:
        pytest.skip("set EVE_LIVE_ESS_SYSTEM to exercise an in-space ESS")

    run = uuid.uuid4().hex[:10]
    with base.connect(f"walk-ess-link-{run}") as client:
        pilot = market._new_character(client, run)
        station_id = int(client.session.session_init["stationid"])
        current_ship_id = int(client.session.session_init["shipid"])
        grant = str(client.call("slash", "SlashCmd", "/ess grant"))
        match = re.search(r"\((\d+)\), the first published hull", grant)
        assert match, grant
        hull_type_id = int(match.group(1))

        hangar = client.call("invbroker", "GetInventory", 10004)
        ships = client.call_bound(hangar, "List")
        hull_id = next(int(row.fields()["itemID"]) for row in ships if int(row.fields()["typeID"]) == hull_type_id)
        ship_service = client.call("ship", "MachoBindObject", (station_id, 15), None)[0]
        boarded = client.call_bound(ship_service, "Board", hull_id, current_ship_id)
        assert boarded is not None, "the selected ESS linkable hull was not boarded"
        wait_quietly(client, 1.0)

        client.call_bound(ship_service, "Undock", hull_id, False, onlineModules=m.List([]))
        wait_quietly(client, 2.0)
        solar_system_id, ess_id = placed
        moved = str(client.call("slash", "SlashCmd", f"/solar {solar_system_id}"))
        assert "Teleported to" in moved, moved
        warped = str(client.call("slash", "SlashCmd", f"/warpto {ess_id}"))
        assert "Warped to item" in warped, warped

        linked = client.call("essMgr", "AttemptLinkToMainBank")
        assert linked is None
        data = m.plain(client.call("essMgr", "GetDataForClientSolarSystem"))
        assert data["mainBankLink"] is not None
        assert data["essID"] == ess_id

        assert client.call("essMgr", "RequestMainBankUnlink") is None
        after = m.plain(client.call("essMgr", "GetDataForClientSolarSystem"))
        assert after["mainBankLink"] is None

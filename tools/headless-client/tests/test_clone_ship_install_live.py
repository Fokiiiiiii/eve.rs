"""Two pilots install a jump clone through a fitted Clone Vat Bay in space (CH-15, live).

    EVE_LIVE_HOST=host:port EVE_PG_URL=postgres://... EVE_LIVE_SLOW=1 pytest tests/test_clone_ship_install_live.py

The offering pilot boards a seeded Rorqual with a fitted Clone Vat Bay I before character selection. The two pilots undock in the
same system; the recipient stops and the Rorqual follows until it is within its SDE 5 km operating range. Accepting charges the
recipient once, pays the offering pilot, writes the ship clone and settlement row, and tells both pilots. A second offer is canceled
by the recipient and tells each pilot once without another settlement.
"""

from __future__ import annotations

import os
import re
import time
import uuid

import pytest

from evecli import marshal as m
from evecli import packet as p
from evecli.observe import notifications_of
from tests import dbseed
from tests import test_login_to_character as base
from tests.test_death import DEATH
from tests.test_market import _label_and_arguments
from tests.test_others import watch
from tests.test_target import plain_race
from tests.test_undock import SYSTEM
from tests.runid import run_token

LIVE = os.environ.get("EVE_LIVE_HOST", "")
PG_URL = os.environ.get("EVE_PG_URL", "")
SLOW = os.environ.get("EVE_LIVE_SLOW") == "1"
pytestmark = [
    pytest.mark.skipif(not (LIVE and PG_URL), reason="set EVE_LIVE_HOST=host:port and EVE_PG_URL to test against a running server"),
    pytest.mark.skipif(not SLOW, reason="the Rorqual must close to Clone Vat Bay range: set EVE_LIVE_SLOW=1"),
]

RORQUAL = 28352
CLONE_VAT_BAY_I = 23735
INFOMORPH_PSYCHOLOGY = 24242
CLONE_FEE = 900_000
HIGH_SLOT_ZERO = 27
CLONE_SKILL_POINTS_LEVEL_TWO = 1_414
# This station's SDE undock locator is generic for both small and capital ships. The default station has
# separate locators 12,878 m apart, so a 60 m/s Rorqual cannot reach the 5 km Clone Vat Bay range quickly.
CLONE_TEST_STATION = 60015252


def integers(value) -> list[int]:
    if isinstance(value, bool):
        return []
    if isinstance(value, int):
        return [value]
    if isinstance(value, dict):
        return [number for child in value.values() for number in integers(child)]
    if isinstance(value, (list, tuple)):
        return [number for child in value for number in integers(child)]
    return []


def character_with_rorqual(client, run: str, *, fit_clone_vat_bay: bool) -> tuple[int, int, int | None]:
    race = plain_race(DEATH["races"][0], "victim")
    recorded_run = re.search(r"(\d{6,})$", race["create"]["call"][2][0]).group(1)
    service, method, *args = [
        race["create"]["call"][0],
        race["create"]["call"][1],
        *[base.revive(arg, run, {}, recorded_run) for arg in race["create"]["call"][2]],
    ]
    kind, made = base.attempt(client, service, method, *args)
    assert kind == "ok", base.brief(made)
    character = int(made)
    ship = dbseed.new_item(RORQUAL, owner=character, location=CLONE_TEST_STATION, flag=dbseed.HANGAR, singleton=True)
    module = (
        dbseed.new_item(CLONE_VAT_BAY_I, owner=character, location=ship, flag=HIGH_SLOT_ZERO, singleton=True)
        if fit_clone_vat_bay
        else None
    )
    dbseed.set_ship(character, ship)
    dbseed.sql(
        f"update {dbseed.schema('CHARACTERS')}.characters set station_id = {CLONE_TEST_STATION}, "
        f"home_station_id = {CLONE_TEST_STATION} where id = {character}"
    )
    dbseed.set_isk(character, 2_000_000)
    if module is not None:
        assert dbseed.location_of(module) == ship and dbseed.flag_of(module) == HIGH_SLOT_ZERO
    client.notifications.clear()
    client.call("charUnboundMgr", "SelectCharacterID", charID=character, secondChoiceID=None, skipTutorial=False)
    return character, ship, module


def grant_clone_skill(character: int) -> None:
    dbseed.sql(
        f"insert into {dbseed.schema('SKILLS')}.character_skills (character_id, type_id, level, skill_points) "
        f"values ({character}, {INFOMORPH_PSYCHOLOGY}, 2, {CLONE_SKILL_POINTS_LEVEL_TWO}) "
        f"on conflict (character_id, type_id) do update set level = 2, skill_points = {CLONE_SKILL_POINTS_LEVEL_TWO}"
    )


def undock(client, ship: int, *, stop: bool) -> object:
    service = client.call("ship", "MachoBindObject", (CLONE_TEST_STATION, 15), None)[0]
    client.call_bound(service, "Undock", ship, False)
    ballpark = client.call("beyonce", "MachoBindObject", SYSTEM, None)[0]
    watch({"pilot": client}, 2.0)
    if stop:
        client.call_bound(ballpark, "CmdStop")
        watch({"pilot": client}, 2.0)
    return ballpark


def bind_clone_service(client):
    return client.call("jumpCloneSvc", "MachoBindObject", (SYSTEM, 5), None)[0]


def event_payload(event: dict) -> list[int]:
    return integers(m.plain(event["args"]))[-4:]


def test_two_pilots_install_a_clone_from_a_ship_offer_and_can_cancel_the_next_offer():
    run = uuid.uuid4().hex[:10]
    with base.connect(f"clone-offering-{run}") as offering_client, base.connect(f"clone-recipient-{run}") as recipient_client:
        offering, ship, module = character_with_rorqual(offering_client, run_token(), fit_clone_vat_bay=True)
        recipient, recipient_ship, _ = character_with_rorqual(recipient_client, f"{run}target", fit_clone_vat_bay=False)
        grant_clone_skill(recipient)
        assert recipient_ship > 0 and module is not None

        undock(recipient_client, recipient_ship, stop=True)
        offering_ballpark = undock(offering_client, ship, stop=False)
        recipient_clones = bind_clone_service(recipient_client)
        offering_clones = bind_clone_service(offering_client)

        offering_client.call_bound(offering_ballpark, "CmdSetSpeedFraction", 1.0)
        offering_client.call_bound(offering_ballpark, "CmdFollowBall", recipient_ship, 1_000)
        deadline = time.monotonic() + 45
        while True:
            try:
                offer_answer = offering_client.call_bound(offering_clones, "OfferShipCloneInstallation", recipient)
                assert m.plain(offer_answer) is None
                break
            except p.MachoError as refusal:
                label, _arguments = _label_and_arguments(refusal)
                assert label == "InviteClone2", f"unexpected ship clone offer refusal: {base.brief(refusal.payload)}"
                assert time.monotonic() < deadline, "the Rorqual did not reach its Clone Vat Bay operating range at the shared undock locator"
                offering_client.call_bound(offering_ballpark, "CmdFollowBall", recipient_ship, 1_000)
                watch({"offering": offering_client, "recipient": recipient_client}, 2.0)

        offering_client.call_bound(offering_ballpark, "CmdStop")
        offered = [event for event in notifications_of(recipient_client) if event["name"] == "OnShipJumpCloneInstallationOffered"]
        assert len(offered) == 1
        assert event_payload(offered[0]) == [offering, recipient, ship, 0]
        assert not [event for event in notifications_of(offering_client) if event["name"] == "OnShipJumpCloneInstallationOffered"]

        offering_client.notifications.clear()
        recipient_client.notifications.clear()
        recipient_balance_before = dbseed.isk_of(recipient)
        offering_balance_before = dbseed.isk_of(offering)
        recipient_client.call_bound(recipient_clones, "AcceptShipCloneInstallation")

        recipient_events = notifications_of(recipient_client)
        offering_events = notifications_of(offering_client)
        recipient_done = [event for event in recipient_events if event["name"] == "OnShipJumpCloneInstallationDone"]
        offering_done = [event for event in offering_events if event["name"] == "OnShipJumpCloneInstallationDone"]
        assert len(recipient_done) == len(offering_done) == 1
        assert event_payload(recipient_done[0]) == event_payload(offering_done[0]) == [offering, recipient, ship, 0]
        recipient_names = [event["name"] for event in recipient_events]
        assert recipient_names.index("OnShipJumpCloneInstallationDone") < recipient_names.index("OnJumpCloneCacheInvalidated")

        settlements = dbseed.sql(
            f"select character_id, jump_clone_id, location_id, location_kind, amount, owner_corporation_id, offering_character_id "
            f"from {dbseed.schema('CHARACTERS')}.character_jump_clone_settlements where character_id = {recipient}"
        ).split("|")
        assert len(settlements) == 7, settlements
        settled_character, clone_id, location_id, location_kind, amount, owner_corporation, settled_offering = settlements
        assert int(settled_character) == recipient and int(clone_id) > 0
        assert int(location_id) == ship and location_kind == "ship"
        assert float(amount) == CLONE_FEE and int(owner_corporation) == 0 and int(settled_offering) == offering
        assert dbseed.isk_of(recipient) == recipient_balance_before - CLONE_FEE
        assert dbseed.isk_of(offering) == offering_balance_before
        journal = dbseed.sql(
            f"select amount, counterparty_id, ref_type_id, reason from {dbseed.schema('WALLET')}.journal "
            f"where owner_id = {recipient} and ref_type_id = 55 and reason = 'jump clone installation'"
        ).splitlines()
        assert len(journal) == 1, journal
        journal_amount, counterparty, ref_type, reason = journal[0].split("|")
        assert float(journal_amount) == -CLONE_FEE and int(counterparty) == offering
        assert int(ref_type) == 55 and reason == "jump clone installation"
        assert dbseed.sql(
            f"select location_id, location_kind from {dbseed.schema('CHARACTERS')}.character_jump_clones "
            f"where jump_clone_id = {int(clone_id)} and character_id = {recipient}"
        ) == f"{ship}|ship"
        assert dbseed.location_of(module) == ship

        offering_client.notifications.clear()
        recipient_client.notifications.clear()
        assert m.plain(offering_client.call_bound(offering_clones, "OfferShipCloneInstallation", recipient)) is None
        canceled_offer = [event for event in notifications_of(recipient_client) if event["name"] == "OnShipJumpCloneInstallationOffered"]
        assert len(canceled_offer) == 1 and event_payload(canceled_offer[0]) == [offering, recipient, ship, 0]
        assert m.plain(recipient_client.call_bound(recipient_clones, "CancelShipCloneInstallation")) is None
        recipient_canceled = [event for event in notifications_of(recipient_client) if event["name"] == "OnShipJumpCloneInstallationCanceled"]
        offering_canceled = [event for event in notifications_of(offering_client) if event["name"] == "OnShipJumpCloneInstallationCanceled"]
        assert len(recipient_canceled) == len(offering_canceled) == 1
        assert event_payload(recipient_canceled[0]) == event_payload(offering_canceled[0]) == [offering, recipient, ship, 0]
        assert dbseed.sql(
            f"select count(*) from {dbseed.schema('CHARACTERS')}.character_jump_clone_settlements where character_id = {recipient}"
        ) == "1"
        assert dbseed.isk_of(recipient) == recipient_balance_before - CLONE_FEE
        assert dbseed.isk_of(offering) == offering_balance_before

"""Clone installation against a running server: a character without the clone skill is refused with the client's label.

    EVE_LIVE_HOST=host:port pytest tests/test_clone_install_live.py

A new account enters a new character, docked at its start station. InstallCloneInStation is refused with the validation label
of the first failed check (the clone skill is missing), and no clone is installed. The paid install needs the skills and the
ISK, which the Host tests cover. Skipped unless EVE_LIVE_HOST is set.
"""

import os
import uuid

import pytest

from evecli import marshal as m
from evecli import packet as p
from evecli.observe import notifications_of

from tests.test_login_to_character import WALK, connect, revive
from tests.test_market import _label_and_arguments

LIVE = os.environ.get("EVE_LIVE_HOST", "")
pytestmark = pytest.mark.skipif(not LIVE, reason="set EVE_LIVE_HOST=host:port to test against a running server")

START_STATION = 60015249

def enter_new_character(client, run: str) -> int:
    creation = next(step for step in WALK["steps"] if step["call"][1] == "CreateCharacterWithDoll" and "ok" in step)
    service, method, described = creation["call"]
    character_id = m.plain(client.call(service, method, *[revive(value, run) for value in described]))
    client.notifications.clear()
    client.call("charUnboundMgr", "SelectCharacterID", charID=character_id, secondChoiceID=None, skipTutorial=False)
    return character_id

def test_a_character_without_the_clone_skill_is_refused_the_install_with_the_validation_label():
    run = uuid.uuid4().hex[:10]
    with connect(f"clone-install-{run}") as client:
        enter_new_character(client, run)
        clones = client.call("jumpCloneSvc", "MachoBindObject", (START_STATION, 15), None)[0]

        with pytest.raises(p.MachoError) as refused:
            client.call_bound(clones, "InstallCloneInStation")
        label, arguments = _label_and_arguments(refused.value)
        assert label == "CustomNotify"
        assert arguments["notify"] == "UI/Medical/JumpCloneSkillReqNotMet"

        assert m.plain(client.call_bound(clones, "GetStationCloneState"))[1]["lines"] == []

def test_a_ship_without_a_clone_vat_bay_is_refused_a_clone_offer_with_the_client_label():
    """OfferShipCloneInstallation: the pilot's ship has no Clone Vat Bay, so the offer is refused with InviteClone1 and nothing is sent."""
    run = uuid.uuid4().hex[:10]
    with connect(f"clone-offer-{run}") as client:
        character_id = enter_new_character(client, run)
        clones = client.call("jumpCloneSvc", "MachoBindObject", (START_STATION, 15), None)[0]

        with pytest.raises(p.MachoError) as refused:
            client.call_bound(clones, "OfferShipCloneInstallation", character_id)
        label, _arguments = _label_and_arguments(refused.value)
        assert label == "InviteClone1"
        assert "OnShipJumpCloneInstallationOffered" not in {event["name"] for event in notifications_of(client)}


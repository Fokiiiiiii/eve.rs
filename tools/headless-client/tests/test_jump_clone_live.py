"""The jump clone service against a running server: the clone state the client reads, the install validation, and the refusals.

    EVE_LIVE_HOST=host:port pytest tests/test_jump_clone_live.py

A new account enters a new character, docked at its start station. It binds the jump clone service the way the client does
(MachoBindObject on the station) and checks the state: no clones, no last jump, the station and ship reads empty, and the
install validation listing the missing clone skill. Renaming, destroying and resetting work on the state of that character:
a rename of a clone it does not have is refused with the client's CustomNotify label, and a reset sends the cache
invalidation the client listens for. Skipped unless EVE_LIVE_HOST is set.
"""

import os
import uuid

import pytest

from evecli import packet as p
from evecli import marshal as m
from evecli.observe import notifications_of

from tests.test_login_to_character import WALK, connect, revive
from tests.test_market import _label_and_arguments

LIVE = os.environ.get("EVE_LIVE_HOST", "")
pytestmark = pytest.mark.skipif(not LIVE, reason="set EVE_LIVE_HOST=host:port to test against a running server")

START_STATION = 60015249
MEDICAL_SKILL_LABEL = "UI/Medical/JumpCloneSkillReqNotMet"

def enter_new_character(client, run: str) -> int:
    creation = next(step for step in WALK["steps"] if step["call"][1] == "CreateCharacterWithDoll" and "ok" in step)
    service, method, described = creation["call"]
    character_id = m.plain(client.call(service, method, *[revive(value, run) for value in described]))
    client.notifications.clear()
    client.call("charUnboundMgr", "SelectCharacterID", charID=character_id, secondChoiceID=None, skipTutorial=False)
    return character_id

def bound_jump_clones(client):
    """The bound object of jumpCloneSvc at the start station, as the client's moniker makes it."""
    return client.call("jumpCloneSvc", "MachoBindObject", (START_STATION, 15), None)[0]

def fields(keyval) -> dict:
    """A util.KeyVal or a rowset as plain values: (name, {attribute: value})."""
    return m.plain(keyval)[1]

def test_the_jump_clone_state_of_a_new_character_is_empty_and_its_refusals_are_the_clients_labels():
    run = uuid.uuid4().hex[:10]
    with connect(f"jumpclone-{run}") as client:
        enter_new_character(client, run)
        clones = bound_jump_clones(client)

        state = fields(client.call_bound(clones, "GetCloneState"))
        assert state["timeLastJump"] == 0
        assert m.plain(state["clones"])[1]["lines"] == []
        assert m.plain(state["implants"])[1]["lines"] == []
        assert m.plain(client.call_bound(clones, "GetStationCloneState"))[1]["lines"] == []
        assert m.plain(client.call_bound(clones, "GetShipCloneState"))[1]["lines"] == []
        assert m.plain(client.call_bound(clones, "GetNumClonesInPilotsStructure")) == 0

        # the install validation: a new character has no clone skill, so the list names the missing skill
        errors = m.plain(client.call_bound(clones, "ValidateInstallJumpClone"))
        assert errors and errors[0] == MEDICAL_SKILL_LABEL

        # a clone the character does not have is refused, with the reference's text
        with pytest.raises(p.MachoError) as refused:
            client.call_bound(clones, "SetJumpCloneName", 140000999, "Ghost")
        label, arguments = _label_and_arguments(refused.value)
        assert label == "CustomNotify"
        assert arguments["notify"] == "Jump clone not found"

        # the reset sends the cache invalidations the client listens for
        client.notifications.clear()
        client.call_bound(clones, "ResetLastCloneJumpTime")
        names = {event["name"] for event in notifications_of(client)}
        assert "OnJumpCloneCacheInvalidated" in names
        assert fields(client.call_bound(clones, "GetCloneState"))["timeLastJump"] == 0

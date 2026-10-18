"""The clone jump against a running server (CH-16): the pilot leaves for the place of a clone, the body is the clone's, and the cooldown holds.

    EVE_RUN='<venv python> -m pytest -q tests/test_clone_jump_live.py' tools/dev/run-eve-live.sh

A new pilot enters at its start station. It is seeded (through dbseed, as the other live walks are) with a capsule in the station hangar as
its active ship, one implant plugged in, 2,000,000 ISK, and a jump clone at another station that wears an implant. It then makes the
client's call (CloneJump on the bound jumpCloneSvc). The clone at the start station is replaced by a clone of the body that left, with
the implant that body wore; the body is at the other station and its implant is the clone's; 900,000 ISK are charged once; the clone
invalidations are sent. A second jump within the cooldown is refused with the client's JumpCooldownError label. Skipped unless
EVE_LIVE_HOST is set.
"""

import os
import uuid

import pytest

from evecli import packet as p
from evecli.observe import notifications_of
from tests import dbseed
from tests.test_jump_clone_live import START_STATION, bound_jump_clones, enter_new_character, fields
from tests.test_login_to_character import connect
from tests.test_market import _label_and_arguments

LIVE = os.environ.get("EVE_LIVE_HOST", "")
pytestmark = pytest.mark.skipif(not LIVE, reason="set EVE_LIVE_HOST=host:port to test against a running server")

AWAY_STATION = 60004723  # an NPC station of Turnur (the zarzakh walk's start station), another system from the start station
CAPSULE = 670
MEMORY_AUGMENTATION_BASIC = 9941  # an implant (slot 6 of the memory augmentations)
IMPLANT_FLAG = 89
CLONE_JUMP_FEE = 900_000
COOLDOWN_LABEL = "UI/CharacterSheet/CharacterSheetWindow/JumpCloneScroll/JumpCooldownError"


def clone_rows(clones_state) -> list:
    return m_rows(fields(clones_state)["clones"])


def m_rows(rowset) -> list:
    from evecli import marshal as m

    return m.plain(rowset)[1]["lines"]


def test_a_clone_jump_moves_the_pilot_swaps_the_bodies_and_charges_once_then_the_cooldown_holds():
    run = uuid.uuid4().hex[:10]
    with connect(f"clonejump-{run}") as client:
        character = enter_new_character(client, run)
        capsule = dbseed.new_item(CAPSULE, owner=character, location=START_STATION, flag=dbseed.HANGAR, singleton=True)
        dbseed.set_ship(character, capsule)
        dbseed.new_item(MEMORY_AUGMENTATION_BASIC, owner=character, location=character, flag=IMPLANT_FLAG, singleton=True)
        dbseed.set_isk(character, 2_000_000)
        away = int(dbseed.sql(
            f"insert into {dbseed.schema('CHARACTERS')}.character_jump_clones (character_id, location_id, location_kind, clone_name) "
            f"values ({character}, {AWAY_STATION}, 'station', 'Away') returning jump_clone_id"))
        dbseed.sql(f"insert into {dbseed.schema('CHARACTERS')}.character_jump_clone_implants (jump_clone_id, slot, type_id) "
                   f"values ({away}, 0, {MEMORY_AUGMENTATION_BASIC})")
        isk_before = dbseed.isk_of(character)

        clones = bound_jump_clones(client)
        client.notifications.clear()
        client.call_bound(clones, "CloneJump", AWAY_STATION, away, float(CLONE_JUMP_FEE), False)

        # the body is at the away station with the clone's implant; the clone it left is at the start station with its implant
        state = fields(client.call_bound(clones, "GetCloneState"))
        assert state["timeLastJump"] != 0
        rows = m_rows(state["clones"])
        assert [row[1] for row in rows] == [START_STATION]
        replacement = rows[0][0]
        assert replacement != away
        assert [row[1] for row in m_rows(state["implants"]) if row[0] == replacement] == [MEMORY_AUGMENTATION_BASIC]
        assert dbseed.sql(
            f"select type_id from {dbseed.schema('INVENTORY')}.items where owner_id = {character} and location_id = {character} and flag = {IMPLANT_FLAG}"
        ).split() == [str(MEMORY_AUGMENTATION_BASIC)]
        assert dbseed.isk_of(character) == isk_before - CLONE_JUMP_FEE
        names = {event["name"] for event in notifications_of(client)}
        assert "OnJumpCloneCacheInvalidated" in names

        # the cooldown: a second jump, back to the start station, is refused with the client's label and nothing moves
        with pytest.raises(p.MachoError) as refused:
            client.call_bound(clones, "CloneJump", START_STATION, replacement, float(CLONE_JUMP_FEE), False)
        label, arguments = _label_and_arguments(refused.value)
        assert label == "CustomNotify"
        assert arguments["notify"] == COOLDOWN_LABEL
        assert dbseed.isk_of(character) == isk_before - CLONE_JUMP_FEE

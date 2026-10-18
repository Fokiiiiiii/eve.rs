"""Shared bookmark folders walk: two pilots and two access lists. The owner makes an admin list and a view list, puts the member on the view
list, makes a shared folder on them, and bookmarks in it; the member connects to the folder and hears the bookmark; the owner takes the member
off the view list and the member hears the folder go; the owner deletes the folder.

    EVE_LIVE_HOST=host:port pytest tests/test_walk_shared_bookmarks.py

The calls are the client's: ownerGroupManager (accessGroupsController.py: Create, AddMembers, DeleteMembers), accessGroupBookmarkMgr
(bookmarkSvc.py: AddFolder, AddToKnownFolders, BookmarkStaticLocation, DeleteFolder). The notices are read with evecli.observe.notifications_of.
"""

from __future__ import annotations

import uuid

import pytest

from evecli import marshal as m
from evecli.describe import describe
from evecli.observe import notifications_of, wait_for_events
from tests import dbseed
from tests import test_login_to_character as base
from tests.test_health import HEALTH
from tests.test_undock import STATION, arrive, number

BOOKMARKS = "accessGroupBookmarkMgr"
OWNER_GROUPS = "ownerGroupManager"
VIEW = 2
pytestmark = pytest.mark.skipif(not base.LIVE, reason="set EVE_LIVE_HOST=host:port to test against a running server")


def keyval_of(answer) -> dict:
    return dict(describe(answer)["args"]["$dict"])


def group_id_of(group) -> int:
    return number(keyval_of(group)["groupID"])


def folder_id_of(folder) -> int:
    return number(keyval_of(folder)["folderID"])


def named(events: list[dict], name: str) -> list[dict]:
    return [event for event in events if event["name"] == name]


def mentions(event: dict, value: int) -> bool:
    """Whether the decoded arguments of a notice carry the id (the shape nests lists and tuples)."""
    return str(value) in repr(describe(event["args"]))


def test_a_shared_folder_reaches_the_member_while_it_holds_a_view_and_goes_when_the_list_drops_it():
    race = HEALTH["races"][0]
    run = uuid.uuid4().hex[:10]
    with base.connect(f"walk-shared-owner-{run}") as owner, base.connect(f"walk-shared-member-{run}") as member:
        owner_character, *_ = arrive(owner, race, f"{run}o")
        member_character, *_ = arrive(member, race, f"{run}m")

        admin_list = group_id_of(owner.call(OWNER_GROUPS, "Create", "Walk admin list", ""))
        view_list = group_id_of(owner.call(OWNER_GROUPS, "Create", "Walk view list", ""))
        owner.call(OWNER_GROUPS, "AddMembers", view_list, m.Dict([(member_character, 0)]))

        folder = owner.call(BOOKMARKS, "AddFolder", False, "Walk shared", "a shared folder for the walk",
                            admin_list, admin_list, admin_list, view_list)
        folder_id = folder_id_of(folder)
        assert number(keyval_of(folder)["accessLevel"]) == 5

        joined = member.call(BOOKMARKS, "AddToKnownFolders", folder_id, True)
        assert number(keyval_of(joined[0])["accessLevel"]) == VIEW
        notifications_of(member)

        owner.call(BOOKMARKS, "BookmarkStaticLocation", STATION, folder_id, "Walk station", "a bookmark in the shared folder", 0, None)
        heard = named(notifications_of(member), "OnSharedBookmarksFolderUpdated")
        assert heard, "the member hears the bookmark in its active folder"
        assert mentions(heard[0], folder_id), describe(heard[0]["args"])

        owner.call(OWNER_GROUPS, "DeleteMembers", view_list, [member_character])
        # the notice follows the response (after it, not before), so wait for it rather than read the queue at once
        lost = named(wait_for_events(member, lambda seen: named(seen, "OnSharedBookmarksFoldersAccessLost")), "OnSharedBookmarksFoldersAccessLost")
        assert lost, "the member hears the folder go once it holds no list"
        assert mentions(lost[0], folder_id), describe(lost[0]["args"])

        owner.call(BOOKMARKS, "DeleteFolder", folder_id)
        owner.call(OWNER_GROUPS, "Delete", view_list)
        owner.call(OWNER_GROUPS, "Delete", admin_list)

def test_corporation_membership_changes_revoke_and_restore_a_shared_folder():
    race = HEALTH["races"][0]
    run = uuid.uuid4().hex[:10]
    with base.connect(f"walk-corp-bookmark-owner-{run}") as owner:
        owner_character, *_ = arrive(owner, race, f"{run}o")
        with base.connect(f"walk-corp-bookmark-member-{run}") as creator:
            member_character, *_ = arrive(creator, race, f"{run}m")

        corporation = dbseed.found_corporation(STATION, 30100032, owner_character, f"a{run}")
        other_corporation = dbseed.found_corporation(STATION, 30100032, owner_character, f"b{run}")
        dbseed.join_corporation(member_character, corporation, 0)

        with base.connect(f"walk-corp-bookmark-member-{run}") as member:
            assert member.call("charUnboundMgr", "SelectCharacterID", charID=member_character,
                               secondChoiceID=None, skipTutorial=False) is None

            admin_list = group_id_of(owner.call(OWNER_GROUPS, "Create", "Walk corp admin", ""))
            manage_list = group_id_of(owner.call(OWNER_GROUPS, "Create", "Walk corp manage", ""))
            use_list = group_id_of(owner.call(OWNER_GROUPS, "Create", "Walk corp use", ""))
            view_list = group_id_of(owner.call(OWNER_GROUPS, "Create", "Walk corp view", ""))
            owner.call(OWNER_GROUPS, "AddMembers", view_list,
                       m.Dict([(owner_character, 0), (corporation, 0)]))

            folder = owner.call(BOOKMARKS, "AddFolder", False, "Walk corp shared",
                                "shared with the corporation", admin_list, manage_list, use_list, view_list)
            folder_id = folder_id_of(folder)
            joined = member.call(BOOKMARKS, "AddToKnownFolders", folder_id, True)
            assert number(keyval_of(joined[0])["accessLevel"]) == VIEW
            notifications_of(member)

            # Let the one-minute upkeep record the connected pilot's initial corporation.
            wait_for_events(member, lambda _: False, timeout=65)

            dbseed.join_corporation(member_character, other_corporation, 0)
            lost = named(wait_for_events(member, lambda seen: named(seen, "OnSharedBookmarksFoldersAccessLost"),
                                         timeout=65), "OnSharedBookmarksFoldersAccessLost")
            assert lost, "leaving the corporation tells the pilot the shared folder is no longer available"
            assert mentions(lost[0], folder_id), describe(lost[0]["args"])

            dbseed.join_corporation(member_character, corporation, 0)
            changed = named(wait_for_events(member, lambda seen: named(seen, "OnSharedBookmarksFoldersAccessChanged"),
                                            timeout=65), "OnSharedBookmarksFoldersAccessChanged")
            assert changed, "joining the corporation tells the pilot the shared folder is available again"
            assert mentions(changed[0], folder_id), describe(changed[0]["args"])
            assert str(VIEW) in repr(describe(changed[0]["args"])), describe(changed[0]["args"])

            owner.call(BOOKMARKS, "DeleteFolder", folder_id)
            for group in (view_list, use_list, manage_list, admin_list):
                owner.call(OWNER_GROUPS, "Delete", group)

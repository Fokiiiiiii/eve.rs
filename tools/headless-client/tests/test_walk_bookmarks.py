"""Personal bookmark folders walk: a pilot makes a personal folder, a subfolder and a bookmark in the station, then deletes the folder.

    EVE_LIVE_HOST=host:port [EVE_LIVE_SERVER=reference] pytest tests/test_walk_bookmarks.py

The calls are the client's (bookmarkSvc.py: GetMyActiveBookmarks :163, AddFolder :426, CreateSubfolder :565,
BookmarkStaticLocation :612, DeleteFolder :453). Only personal folders are walked: shared folders are a cut (DEVIATIONS.md).
"""

from __future__ import annotations

import uuid

import pytest

from evecli.describe import describe
from tests import test_login_to_character as base
from tests.test_health import HEALTH
from tests.test_undock import STATION, arrive, number

SERVICE = "accessGroupBookmarkMgr"
pytestmark = pytest.mark.skipif(not base.LIVE, reason="set EVE_LIVE_HOST=host:port to test against a running server")

def folder_id_of(folder) -> int:
    """The folderID of a folder answer (a util.KeyVal, not a tuple)."""
    return number(dict(describe(folder)["args"]["$dict"])["folderID"])

def test_a_pilot_makes_and_deletes_a_personal_bookmark_folder_as_the_client_does():
    race = HEALTH["races"][0]
    run = uuid.uuid4().hex[:10]
    with base.connect(f"walk-bookmarks-{run}") as who:
        arrive(who, race, run)
        folders, bookmarks, subfolders = who.call(SERVICE, "GetMyActiveBookmarks")
        assert folders is not None and bookmarks is not None and subfolders is not None
        folder = who.call(SERVICE, "AddFolder", True, "Walk folder", "a personal folder for the walk")
        assert folder is not None
        folder_id = folder_id_of(folder)
        subfolder = who.call(SERVICE, "CreateSubfolder", folder_id, "Walk subfolder")
        assert subfolder is not None
        bookmark = who.call(SERVICE, "BookmarkStaticLocation", STATION, folder_id, "Walk station", "a bookmark for the walk", 0, None)
        assert bookmark is not None
        who.call(SERVICE, "DeleteFolder", folder_id)

"""Skill bundle info against a running server: the crate loot preview asks for the name and icon of a bundle and unpacks two values.

    EVE_LIVE_HOST=host:port pytest tests/test_skill_bundle_info_live.py

A new character answers GetSkillBundleInfo with the client's empty shape: an empty label and no icon (the client then shows its
default icon). No bundle data exists in the SDE or EveJS (DEVIATIONS, character phase S6). Skipped unless EVE_LIVE_HOST is set.
"""

import os
import uuid

import pytest

from evecli import marshal as m

from tests.test_clone_install_live import enter_new_character
from tests.test_login_to_character import connect

LIVE = os.environ.get("EVE_LIVE_HOST", "")
pytestmark = pytest.mark.skipif(not LIVE, reason="set EVE_LIVE_HOST=host:port to test against a running server")

def test_a_skill_bundle_answers_the_loot_preview_with_the_name_and_icon_pair():
    run = uuid.uuid4().hex[:10]
    with connect(f"skill-bundle-{run}") as client:
        enter_new_character(client, run)
        answer = m.plain(client.call("skillHandler", "GetSkillBundleInfo", 1))
        assert answer == ("", None)

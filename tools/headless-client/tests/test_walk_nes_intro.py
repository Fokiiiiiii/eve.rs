"""NES intro walk: the tutorial state a new character reads, then skips.

    EVE_LIVE_HOST=host:port [EVE_LIVE_SERVER=reference] pytest tests/test_walk_nes_intro.py

The character is made and chosen as the health recording's is, and stands in its station. The client's storyline controller
asks `nes_intro.get_nes_intro_state()` (sm.RemoteSvc('nes_intro'), storylines/client/nes_intro.py:70) and calls
`skip_nes_intro()` (:95). The walk asks, skips, and asks again; the MITM capture of this walk is compared with EveJS.
"""

from __future__ import annotations

import os
import uuid

import pytest

from tests import test_login_to_character as base
from tests.test_health import HEALTH
from tests.test_undock import arrive

SKIPPED = 3  # NesIntroState.SKIPPED in storylines/common/nes_intro_constants.py
pytestmark = pytest.mark.skipif(not base.LIVE, reason="set EVE_LIVE_HOST=host:port to test against a running server")

def test_a_new_character_reads_and_skips_the_nes_intro_as_the_client_does():
    race = HEALTH["races"][0]
    run = uuid.uuid4().hex[:10]
    with base.connect(f"walk-nes-{run}") as who:
        arrive(who, race, run)
        before = who.call("nes_intro", "get_nes_intro_state")
        assert isinstance(before, int) and 0 <= before <= SKIPPED, before
        who.call("nes_intro", "skip_nes_intro")
        after = who.call("nes_intro", "get_nes_intro_state")
        # the skip moves an active tutorial to SKIPPED and leaves any other state alone (nes_intro.py skip_nes_intro)
        assert after == (SKIPPED if before == 1 else before), (before, after)

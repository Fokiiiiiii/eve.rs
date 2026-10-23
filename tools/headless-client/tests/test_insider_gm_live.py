"""The Insider window's server calls, typed by pilots on a live server: the transcript of each answer, for the comparison with EVE.js.

The calls are the slash commands the Insider's menus and its helper service send (eve/devtools/script/insider.py, svc_slash.py, menusvc.py),
each sent as the client sends it (SlashCmd). EVE_GM_TRANSCRIPT keeps every answer, a refusal included, with the server that gave it. The walk
does not stop at a wording that differs: the outcomes it must reach are checked at the end, and the wording is compared by the transcript.
Run against either server (EVE_LIVE_SERVER=reference is EVE.js), see docs/project/MITM_COMPARE.md.
"""

import os
import re

import pytest

from tests.test_agents_live import new_pilot, run_id
from tests.test_gm_commands_live import Transcript
from tests.test_login_to_character import connect

CALDARI_NAVY = 1000035
BLOOD_RAIDER_FRIGATE = 36939

@pytest.mark.skipif(os.environ.get("EVE_LIVE_HOST") is None, reason="needs a running server (EVE_LIVE_HOST)")
def test_the_insider_calls_answer_and_do_what_they_say():
    run = run_id()
    walk = Transcript()
    with connect(f"ins-{run}") as pilot, connect(f"ins2-{run}") as other:
        character = new_pilot(pilot, run)
        other_character = new_pilot(other, f"i{run}")

        # the money, the standing and the skills the menus give
        walk.send(pilot, "/setisk 2000000")
        walk.expect("the wallet is 2,000,000 after /setisk", re.search(r"2,?000,?000", walk.send(pilot, "/wallet")) is not None)
        walk.send(pilot, f"/setstanding 5 {CALDARI_NAVY}")
        walk.expect("/givestarterskills gives the starting skills", walk.send(pilot, "/givestarterskills me").startswith("Gave the starting skills"))
        walk.send(pilot, "/giveskill me all 5")
        walk.expect("/skillscopy copies the donor's skills to the taker", walk.send(pilot, f"/skillscopy {character} {other_character}").startswith("Copied"))
        walk.send(pilot, "/removeskill me Rifter")

        # the GM helpers: the dogma reload, a kill right, a skyhook, the heal and the ship
        walk.expect("/dogmaupdate reloads the dogma", walk.send(pilot, "/dogmaupdate").startswith("Dogma reloaded"))
        walk.expect("/killright makes the kill right", walk.send(pilot, f"/killright {other_character}").startswith("Made kill right"))
        walk.send(pilot, f"/killright {other_character}")
        walk.send(pilot, f"/killright me")
        walk.expect("/skyhook state answers for an unknown skyhook", walk.send(pilot, "/skyhook state 1 214").startswith("Skyhook not found"))
        walk.send(pilot, f"/entity deploy 2 {BLOOD_RAIDER_FRIGATE}")
        walk.send(pilot, "/asteroid rewards")
        walk.send(pilot, "/heal")
        walk.send(pilot, "/repair")
        walk.send(pilot, "/ship Cockroach")
        walk.expect("/solar Jita moves the pilot to Jita", walk.send(pilot, "/solar Jita").startswith("Teleported to Jita"))

        # random, so last: its levels are not the same on each run
        walk.expect("/randomizeskills randomizes the skills", walk.send(pilot, "/randomizeskills").startswith("Randomized the published skills"))
    assert walk.lines, "no command was answered"
    assert not walk.missed, f"outcomes not reached: {walk.missed}"

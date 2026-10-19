"""A faction kill lowers the standing of the pilot that made it, and the pilot is told the new standing (CH-14).

    EVE_LIVE_SLOW=1 EVE_LIVE_HOST=host:port pytest tests/test_faction_standing_live.py

A new pilot undocks with its starting gun and shoots a Pithi Fugitive (Guristas, faction 500010, a 250 m/s frigate of about 160 points of hull and shield) dead through the real combat path
(tests/npc_kill.py). The reference lowers the killer's standing with that faction by one raw change per kill, and tells the pilot
`OnStandingSet(faction, pilot, standing)` (EVE.js standingRuntime.notifyStandingSet). The test asserts that notification and that the new
standing is below zero. It needs the simulation (EVE_LIVE_SLOW=1). Skipped unless EVE_LIVE_HOST is set.
"""

import os
import re
import uuid

import pytest

from tests import test_login_to_character as base
from tests.npc_kill import kill_with_gun, rat_of
from tests.test_death import DEATH
from tests.test_others import watch
from tests.test_target import plain_race
from tests.test_undock import STATION, SYSTEM, arrive

LIVE = os.environ.get("EVE_LIVE_HOST", "")
pytestmark = [
    pytest.mark.skipif(not LIVE, reason="set EVE_LIVE_HOST=host:port to test against a running server"),
    pytest.mark.skipif(os.environ.get("EVE_LIVE_SLOW") != "1", reason="the kill needs the simulation: set EVE_LIVE_SLOW=1"),
]

GURISTAS = 500010

def test_a_pilot_that_shoots_a_faction_npc_dead_is_told_its_new_standing_with_the_faction():
    race = plain_race(DEATH["races"][0], "victim")
    run = uuid.uuid4().hex[:10]
    with base.connect(f"walk-faction-standing-{run}") as who:
        _, ship_id, modules, _ = arrive(who, race, run)
        gun = modules[1]
        service = who.call("ship", "MachoBindObject", (STATION, 15), None)
        who.call_bound(service[0], "Undock", ship_id, False)
        ballpark = who.call("beyonce", "MachoBindObject", SYSTEM, None)
        watch({"events": who}, 3.0)
        dogma = who.call("dogmaIM", "MachoBindObject", (SYSTEM, 5), None)
        watch({"events": who}, 45.0, until=lambda seen: any(e["name"] == "OnInvulnCancelled" for e in seen["events"]))

        reply = who.call("slash", "SlashCmd", "/npc 1 Pithi Fugitive")
        text = getattr(reply, "value", reply)
        text = text.decode() if isinstance(text, bytes) else str(text)
        assert text.startswith("Spawned 1 hull from Pithi Fugitive"), text
        arrived = watch({"events": who}, 20.0, until=lambda seen: rat_of(seen["events"], ship_id) != 0)["events"]
        rat = rat_of(arrived, ship_id)
        assert rat, ("the rat did not come into space", text, sorted({e["name"] for e in arrived})[:40], str(arrived)[:600])

        events = kill_with_gun(who, ballpark, dogma, gun, rat)
        names = [e["name"] for e in events]
        standing = [e for e in events + watch({"events": who}, 3.0)["events"] if e["name"] == "OnStandingSet"]
        assert standing, ("the pilot was not told its standing with the faction", sorted(set(names)))
        # the notification carries (faction, pilot, standing): the faction is the rat's, and the standing is below zero after one kill
        payload = repr(standing[-1]["args"])
        assert str(GURISTAS) in payload, payload
        new_standing = float(re.findall(r"-?\d+\.\d+(?:e-?\d+)?", payload)[-1])
        assert new_standing < 0, payload

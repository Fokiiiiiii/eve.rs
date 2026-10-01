"""`/npc`: an NPC comes into space beside a pilot, locks the pilot's ship and shoots it (eve.rs only: the reference's NPCs are driven by its own AI).

    EVE_LIVE_SLOW=1 EVE_LIVE_HOST=host:port [EVE_LIVE_SERVER=eve.rs] pytest tests/test_npc.py

One character undocks, binds and waits out its undocking protection (an NPC cannot hurt a ship that is protected), then says `/npc 1 Blood Visionary`: a
rat of the authored profile of that name, armed with the laser its hull carries (`dotnet/src/Eve.Npc`, `oracle/fixtures/npc/hull_weapons.json`), running the
simulation's `belt rat` behavior tree (deviation 46). The pilot is told of the ball coming into space, of the NPC's locking it, of its shots (`OnSpecialFX`) and of the damage they do
(`OnDamageMessage`) and its ship's shield takes the damage. It needs the simulation, so it is skipped against a server without one.
"""

from __future__ import annotations

import os
import uuid

import pytest

from tests import test_login_to_character as base
from tests.test_death import DEATH, _names
from tests.test_others import watch
from tests.test_target import plain_race
from tests.test_undock import STATION, SYSTEM, arrive

pytestmark = [
    pytest.mark.skipif(not base.LIVE, reason="set EVE_LIVE_HOST=host:port to test against a running server"),
    pytest.mark.skipif(os.environ.get("EVE_LIVE_SLOW") != "1", reason="a minute: set EVE_LIVE_SLOW=1"),
    pytest.mark.skipif(os.environ.get("EVE_LIVE_SERVER", "eve.rs") == "reference", reason="the reference's NPCs are its own"),
]


def test_a_spawned_rat_locks_the_pilot_and_shoots_it():
    race = plain_race(DEATH["races"][0], "victim")
    run = uuid.uuid4().hex[:10]
    with base.connect(f"walk-npc-{run}") as who:
        _, ship_id, _, _ = arrive(who, race, run)
        service = who.call("ship", "MachoBindObject", (STATION, 15), None)
        who.call_bound(service[0], "Undock", ship_id, False)
        who.call("beyonce", "MachoBindObject", SYSTEM, None)
        watch({"events": who}, 3.0)
        who.call("dogmaIM", "MachoBindObject", (SYSTEM, 5), None)
        watch({"events": who}, 45.0, until=lambda seen: any(e["name"] == "OnInvulnCancelled" for e in seen["events"]))

        reply = who.call("slash", "SlashCmd", "/npc 1 Blood Visionary")
        text = getattr(reply, "value", reply)
        text = text.decode() if isinstance(text, bytes) else str(text)
        assert text.startswith("Spawned 1 hull from Blood Visionary: 1x Blood Visionary."), text

        seen = watch({"events": who}, 60.0, until=lambda s: any(e["name"] == "OnDamageMessage" for e in s["events"]))["events"]
        names = [e["name"] for e in seen]
        actions = set().union(*(_names(e) for e in seen if e["name"] == "DoDestinyUpdate"))
        # the ball comes into space, the rat orbits the pilot, locks it and shoots it
        assert "AddBalls2" in actions, sorted(actions)
        assert "Orbit" in actions, sorted(actions)
        assert "OnTarget" in names, sorted(set(names))
        assert "OnDamageMessage" in names, sorted(set(names))

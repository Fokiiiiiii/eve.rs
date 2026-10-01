"""Belt rats: a pilot whose warp ends at an asteroid belt finds a group of pirates there (eve.rs only: the reference's rats are its own).

    EVE_START_STATION=60005332 EVE_BELT_RAT_CHANCE=1 EVE_LIVE_SLOW=1 EVE_LIVE_HOST=host:port [EVE_LIVE_SERVER=eve.rs] pytest tests/test_belt_rats.py

The character starts in Hjortur (0.86, high security: one frigate of the faction that holds the region) and warps to its first asteroid belt. The simulation
tells the host the warp ended at the belt (link message `Arrived`), the host rolls the director (the chance is made certain by EVE_BELT_RAT_CHANCE=1, as
the host is started with it) and spawns the group around the belt: the pilot is told of the balls coming into space. The host must have been started with
the same EVE_START_STATION (tools/dev/run-eve-live.sh passes its environment on), and the data has to hold the system (`eve-sde ensure`).
"""

from __future__ import annotations

import os
import uuid

import pytest

from tests import test_login_to_character as base
from tests import test_undock
from tests.test_death import DEATH, _names
from tests.test_others import watch
from tests.test_target import plain_race
from tests.test_undock import arrive
from tests.test_weapon import slims_in

STATION = "60005332"
SYSTEM = 30003400
BELT = 40215635
# `npcIdBase` of the host: what the NPCs it makes are numbered from
NPC_FIRST = 4_100_000_000_000_000

pytestmark = [
    pytest.mark.skipif(not base.LIVE, reason="set EVE_LIVE_HOST=host:port to test against a running server"),
    pytest.mark.skipif(os.environ.get("EVE_LIVE_SLOW") != "1", reason="a few minutes: set EVE_LIVE_SLOW=1"),
    pytest.mark.skipif(os.environ.get("EVE_LIVE_SERVER", "eve.rs") == "reference", reason="the reference's rats are its own"),
    pytest.mark.skipif(os.environ.get("EVE_START_STATION") != STATION, reason=f"start the server with EVE_START_STATION={STATION} EVE_BELT_RAT_CHANCE=1"),
]


def test_a_warp_to_a_belt_brings_the_rats_of_the_belt(monkeypatch):
    monkeypatch.setattr(test_undock, "STATION", int(STATION))
    race = plain_race(DEATH["races"][0], "victim")
    run = uuid.uuid4().hex[:10]
    with base.connect(f"walk-belt-{run}") as who:
        _, ship_id, _, _ = arrive(who, race, run)
        service = who.call("ship", "MachoBindObject", (int(STATION), 15), None)
        who.call_bound(service[0], "Undock", ship_id, False)
        ballpark = who.call("beyonce", "MachoBindObject", SYSTEM, None)
        watch({"events": who}, 3.0)
        who.call("dogmaIM", "MachoBindObject", (SYSTEM, 5), None)

        who.call_bound(ballpark[0], "CmdWarpToStuff", "item", BELT, minRange=0)
        # the warp, then the rats: the ball of the ship that comes into space after the ship came to rest
        seen = watch({"events": who}, 240.0, until=lambda s: any(item >= NPC_FIRST for item, _ in slims_in(s["events"])))["events"]
        actions = set().union(*(_names(e) for e in seen if e["name"] == "DoDestinyUpdate"))
        assert "WarpTo" in actions, sorted(actions)
        assert "Stop" in actions, sorted(actions)
        rats = {item for item, _ in slims_in(seen) if item >= NPC_FIRST}
        assert rats, f"no rat came into space: {sorted(actions)}"
        assert "AddBalls2" in actions, sorted(actions)

        # the rats go for the pilot: a rat circles its target, locks it and shoots (the belt is 2.5 km from the pilot, the rats come 30 km off it)
        later = watch({"events": who}, 150.0, until=lambda s: any(e["name"] == "OnDamageMessage" for e in s["events"]))["events"]
        names = {e["name"] for e in later}
        later_actions = set().union(*(_names(e) for e in later if e["name"] == "DoDestinyUpdate"))
        assert "OnTarget" in names, (sorted(names), sorted(later_actions))
        assert "OnDamageMessage" in names, (sorted(names), sorted(later_actions))

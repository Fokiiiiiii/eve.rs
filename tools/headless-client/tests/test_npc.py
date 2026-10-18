"""`/npc`: an NPC comes into space beside a pilot, locks the pilot's ship and shoots it (eve.rs only: the reference's NPCs are driven by its own AI).

    EVE_LIVE_SLOW=1 EVE_LIVE_HOST=host:port [EVE_LIVE_SERVER=eve.rs] pytest tests/test_npc.py

One character undocks, binds and waits out its undocking protection (an NPC cannot hurt a ship that is protected), then says `/npc 1 Blood Visionary`: a
rat of the authored profile of that name, armed with the laser its hull carries (`dotnet/src/Eve.Npc`, `oracle/fixtures/npc/hull_weapons.json`), running the
simulation's `belt rat` behavior tree (deviation 46). The pilot is told of the ball coming into space, of the NPC's locking it, of its shots (`OnSpecialFX`) and of the damage they do
(`OnDamageMessage`) and its ship's shield takes the damage. It needs the simulation, so it is skipped against a server without one.
"""

from __future__ import annotations

import os
import re
import uuid

import pytest

from evecli import packet

from tests import dbseed
from tests import test_login_to_character as base
from tests.test_death import DEATH, _names
from tests.test_others import hold_still, watch
from tests.test_target import plain_race
from tests.test_weapon import shots_of, slims_in
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
        dbseed.set_hull(ship_id, dbseed.COCKROACH)
        service = who.call("ship", "MachoBindObject", (STATION, 15), None)
        who.call_bound(service[0], "Undock", ship_id, False)
        ballpark = who.call("beyonce", "MachoBindObject", SYSTEM, None)
        watch({"events": who}, 3.0)
        who.call("dogmaIM", "MachoBindObject", (SYSTEM, 5), None)
        watch({"events": who}, 45.0, until=lambda seen: any(e["name"] == "OnInvulnCancelled" for e in seen["events"]))
        hold_still(who, ballpark[0])

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


def test_a_pilot_who_shoots_a_rat_dead_is_told_of_its_bounty():
    """The pilot locks the smallest armed rat there is (15 points of hull in all: `Petty Thief`) and fires its gun until the rat is gone: the rat's ball is
    taken out of space and the pilot is told what it will be paid (`OnBountyAddedToPayout`)."""
    race = plain_race(DEATH["races"][0], "victim")
    run = uuid.uuid4().hex[:10]
    with base.connect(f"walk-npc-kill-{run}") as who:
        _, ship_id, modules, _ = arrive(who, race, run)
        dbseed.set_hull(ship_id, dbseed.COCKROACH)
        gun = modules[1]
        service = who.call("ship", "MachoBindObject", (STATION, 15), None)
        who.call_bound(service[0], "Undock", ship_id, False)
        ballpark = who.call("beyonce", "MachoBindObject", SYSTEM, None)
        watch({"events": who}, 3.0)
        dogma = who.call("dogmaIM", "MachoBindObject", (SYSTEM, 5), None)
        watch({"events": who}, 45.0, until=lambda seen: any(e["name"] == "OnInvulnCancelled" for e in seen["events"]))
        hold_still(who, ballpark[0])

        reply = who.call("slash", "SlashCmd", "/npc 1 Petty Thief")
        text = getattr(reply, "value", reply)
        text = text.decode() if isinstance(text, bytes) else str(text)
        assert text.startswith("Spawned 1 hull from Petty Thief"), text
        arrived = watch({"events": who}, 6.0)["events"]
        rats = [int(item) for event in arrived for item, _ in re.findall(r'\["itemID", \{"\$long": (\d+)\}\], \["typeID", (\d+)\]', str(event).replace("'", '"')) if int(item) != ship_id]
        assert rats, "the rat did not come into space"
        rat = rats[0]

        who.call_bound(dogma[0], "AddTarget", rat)
        watch({"events": who}, 20.0, until=lambda seen: rat in [t if not isinstance(t, dict) else t["$long"] for t in map(lambda x: getattr(x, "value", x), who.call_bound(dogma[0], "GetTargets"))])
        # a rat circles its target at ten kilometres (the client's own default for a fighting NPC) and the gun reaches seven: the pilot goes to the rat
        who.call_bound(ballpark[0], "CmdOrbit", rat, 2000)
        watch({"events": who}, 8.0)
        who.call_bound(dogma[0], "Activate", gun, "targetAttack", rat, 1000)
        seen = watch({"events": who}, 120.0, until=lambda s: any(e["name"] == "OnBountyAddedToPayout" for e in s["events"]))["events"]
        names = [e["name"] for e in seen]
        counts = {n: names.count(n) for n in set(names)}
        hits = [str(shot.get("damage")) for shot in shots_of(seen)]
        assert "OnBountyAddedToPayout" in names, (counts, hits[:12], sorted(set().union(*(_names(e) for e in seen if e["name"] == "DoDestinyUpdate"))))
        actions = set().union(*(_names(e) for e in seen if e["name"] == "DoDestinyUpdate"))
        assert "RemoveBalls" in actions, sorted(actions)

        # the wreck the host made comes into space where the rat was (a little after the bounty: it is an item first), the pilot opens it, and what it holds is what its slim item says
        # it holds (`isEmpty`); what a table gives is a draw, so what is in it is not said here (the unit tests roll known numbers)
        wrecks = [item for event in seen for item, _ in slims_in([event]) if item not in (ship_id, rat)]
        if not wrecks:
            wrecks = [item for event in watch({"events": who}, 6.0)["events"] for item, _ in slims_in([event]) if item not in (ship_id, rat)]
        assert wrecks, "the wreck of the rat did not come into space"
        wreck = wrecks[0]
        # the pilot is where the fight was: it goes to the wreck, and may open it within two and a half kilometres of its surface (`TargetTooFar` before that)
        who.call_bound(ballpark[0], "CmdOrbit", wreck, 500)
        opened = None
        for _ in range(12):
            watch({"events": who}, 2.5)
            try:
                opened = who.call("invbroker", "MachoBindObject", (SYSTEM, 5), ("GetInventoryFromId", (wreck, 0), None))[1]
                break
            except packet.MachoError as error:
                continue
        assert opened is not None, "the pilot could not open the wreck of the rat after going to it"
        rows = [row.fields() for row in who.call_bound(opened, "List")]
        assert all(int(row["locationID"]) == wreck for row in rows), rows
        shown_empty = re.search(r'\["isEmpty", (True|False|true|false|1|0)\]', str([e for e in seen if wreck in [i for i, _ in slims_in([e])]]).replace("'", '"'))
        if shown_empty is not None:
            assert (shown_empty.group(1).lower() in ("true", "1")) == (not rows), (shown_empty.group(1), rows)


def test_a_rat_that_launches_missiles_shows_the_pilot_each_missile_and_the_missiles_hurt_it():
    """A hull with `missileLaunchingForEntity` (the Guristas Invader: Scourge Rockets) launches missiles of its own: the pilot is shown each as a ball with a launch, and
    the damage of one that lands is told at the hit quality of a missile (`oracle/fixtures/npc/hull_weapons.json`: the launcher is what the reference makes of the hull)."""
    race = plain_race(DEATH["races"][0], "victim")
    run = uuid.uuid4().hex[:10]
    with base.connect(f"walk-npc-missile-{run}") as who:
        _, ship_id, _, _ = arrive(who, race, run)
        dbseed.set_hull(ship_id, dbseed.COCKROACH)
        service = who.call("ship", "MachoBindObject", (STATION, 15), None)
        who.call_bound(service[0], "Undock", ship_id, False)
        ballpark = who.call("beyonce", "MachoBindObject", SYSTEM, None)
        watch({"events": who}, 3.0)
        who.call("dogmaIM", "MachoBindObject", (SYSTEM, 5), None)
        watch({"events": who}, 45.0, until=lambda seen: any(e["name"] == "OnInvulnCancelled" for e in seen["events"]))
        hold_still(who, ballpark[0])

        reply = who.call("slash", "SlashCmd", "/npc 1 Guristas Invader still")
        text = getattr(reply, "value", reply)
        text = text.decode() if isinstance(text, bytes) else str(text)
        assert text.startswith("Spawned 1 hull from Guristas Invader"), text
        arrived = watch({"events": who}, 6.0)["events"]
        rats = [int(item) for event in arrived for item, _ in re.findall(r'\["itemID", \{"\$long": (\d+)\}\], \["typeID", (\d+)\]', str(event).replace("'", '"')) if int(item) != ship_id]
        assert rats, "the rat did not come into space"
        # the rat attacks what comes within 4,750 m of it and comes out 5 km off: the pilot goes to it
        who.call_bound(ballpark[0], "CmdOrbit", rats[0], 2000)

        seen = watch({"events": who}, 90.0, until=lambda s: any(e["name"] == "OnDamageMessage" for e in s["events"]))["events"]
        actions = [name for e in seen if e["name"] == "DoDestinyUpdate" for name in _names(e)]
        hits = shots_of_others(seen)
        assert "LaunchMissile" in actions, (text[:500], sorted(set(actions)), sorted({e["name"] for e in seen}), hits[:2])
        assert "AddBalls2" in actions, sorted(set(actions))
        assert hits, "no missile landed"
        assert {h["hitQuality"] for h in hits} == {3}, hits


def shots_of_others(events: list) -> list:
    """The combat-log messages of attacks on the pilot (`attackType` `otherPlayerWeapons`), as `{key: value}`."""
    from tests.test_undock import args_of

    out = []
    for event in events:
        if event["name"] == "OnDamageMessage":
            message = {k: v for k, v in args_of(event)["$tuple"][0]["$dict"]}
            if message.get("attackType") == "otherPlayerWeapons":
                out.append(message)
    return out

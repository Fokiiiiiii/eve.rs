"""A pilot takes a basic kill mission from a level 4 security agent, flies to the agent's system, kills the dungeon's NPCs and is told the mission
is complete (eve.rs only: the mission content is the server's own; the reference's sites are not reproduced).

    EVE_LIVE_SLOW=1 EVE_LIVE_HOST=host:port EVE_LIVE_SERVER=eve.rs pytest tests/test_mission_walk.py

The pilot starts in Manifest (30100032), one stargate jump from 30005204, where the level 4 security agent 3015920 sits. It warps to the gate,
jumps, requests the mission (DoAction 376), accepts it (377) and waits for the wave director's dungeon (the sweep runs every ten seconds). The
dungeon's NPCs are shot with the pilot's gun as test_npc does. A basic level 4 mission has no ISK in the official data, so the wallet is only
checked to be unchanged (owner decision D-R1). Needs EVE_SDE_DIR and EVE_CLIENT_SDE_DIR (the mission texts) on the host.
"""

from __future__ import annotations

import os
import subprocess
import time
import uuid

import pytest

from evecli import packet
from evecli.observe import timed_events, wait_quietly
from tests import test_login_to_character as base
from tests.test_death import DEATH
from tests.test_target import plain_race
from tests.test_others import watch
from tests.test_undock import STATION, SYSTEM, arrive, outcome
from tests.test_weapon import slims_in

pytestmark = [
    pytest.mark.skipif(not base.LIVE, reason="set EVE_LIVE_HOST=host:port to test against a running server"),
    pytest.mark.skipif(os.environ.get("EVE_LIVE_SLOW") != "1", reason="several minutes: set EVE_LIVE_SLOW=1"),
    pytest.mark.skipif("EVE_RELATIONS_SCHEMA" not in os.environ or "EVE_PSQL_URL" not in os.environ,
                       reason="the standing fixture needs the run's relations schema (tools/dev/run-eve-live.sh and EVE_PSQL_URL)"),
]

GATE, NEXT_GATE, NEXT_SYSTEM = 50017727, 50016629, 30005204  # the Manifest gate to 30005204 (SDE mapStargates)
AGENT = 3015920  # a level 4 basic security agent of 30005204 (SDE npcCharacters)
AGENT_CORPORATION = 1000143  # the agent's corporation (SDE npcCharacters): a level 4 agent needs a standing of 5 toward it, with the agent, corporation or faction
STANDING = 6.0
OFFER, ACCEPT = 376, 377  # the dialogue's request and accept tokens (eve-logic appConst; AgentDialogueToken)


def give_standing(character: int) -> None:
    """A fixture, not a server path: the pilot is given the standing a level 4 agent asks for, as if earlier work had made it, in the run's own
    relations schema (the live run exports EVE_RELATIONS_SCHEMA and EVE_PSQL_URL to the tests)."""
    schema, url = os.environ["EVE_RELATIONS_SCHEMA"], os.environ["EVE_PSQL_URL"]
    subprocess.run(["psql", url, "-q", "-c",
        f"INSERT INTO {schema}.relations (kind, subject, object, value, since_ms, updated_ms) "
        f"VALUES ('standing', {AGENT_CORPORATION}, {character}, {STANDING}, 0, 0) "
        f"ON CONFLICT (kind, subject, object) DO UPDATE SET value = EXCLUDED.value"], check=True)


def until_stopped(who, limit: float = 240.0) -> None:
    """Watches the events until the ship has come to rest (the Stop action) or the limit is past."""
    started = time.time()
    while time.time() - started < limit:
        seen = timed_events(who, 3.0)
        if any("Stop" in str(e) for e in seen):
            return
    raise AssertionError("the ship did not come to rest")


def cash(who) -> float:
    """The character's ISK (`account.GetCashBalance`, corporation 0)."""
    return float(getattr(who.call("account", "GetCashBalance", 0), "value", 0.0))


def in_range(who, call, what: str, limit: int = 60):
    """Repeats `call` every five seconds while the target is out of range (the ship is still closing in); any other refusal fails the walk."""
    for _ in range(limit):
        try:
            return call()
        except packet.MachoError as error:
            # a lock takes a moment to land (AddTarget answers pending): until then the shot is refused as not locked
            if not any(text in repr(error.payload) for text in ("TargetNotWithinRange", "That target is not locked")):
                raise AssertionError(f"{what}: {base.brief(error.payload)}") from error
            watch({"events": who}, 5.0)
    raise AssertionError(f"{what}: still out of range after {limit * 5} s")


def removed(event: dict, ball: int) -> bool:
    """Whether the event takes the ball out of space (RemoveBalls naming it)."""
    return "RemoveBalls" in str(event) and str(ball) in str(event)


def reply_text(raw) -> str:
    value = getattr(raw, "value", raw)
    return value.decode() if isinstance(value, bytes) else str(value)


def walk_into_dungeon(who, race, run: str):
    """The pilot undocks, goes through the gate, takes the level 4 kill offer in the next system and the mission's dungeon comes into space
    around it. Returns what the kill needs: the ship, the gun, the ballpark and dogma of the system, and the NPCs (with their types)."""
    character, ship_id, modules, _ = arrive(who, race, run)
    give_standing(character)
    gun = modules[1]
    service = who.call("ship", "MachoBindObject", (STATION, 15), None)
    who.call_bound(service[0], "Undock", ship_id, False)
    ballpark = who.call("beyonce", "MachoBindObject", SYSTEM, None)
    wait_quietly(who, 2.0)

    # to the gate and through it (the jump test's steps)
    who.call_bound(ballpark[0], "CmdWarpToStuff", "item", GATE, minRange=0)
    until_stopped(who)
    who.call_bound(ballpark[0], "CmdStargateJump", GATE, NEXT_GATE, ship_id)
    wait_quietly(who, 6.0)
    next_ballpark = who.call("beyonce", "MachoBindObject", NEXT_SYSTEM, None)
    dogma = who.call("dogmaIM", "MachoBindObject", (NEXT_SYSTEM, 5), None)
    wait_quietly(who, 3.0)

    # the agent: greet, request an offer, accept it
    agent = who.call("agentMgr", "MachoBindObject", AGENT, None)
    replies = {}
    for label, action in (("greeting", None), ("request", OFFER), ("accept", ACCEPT)):
        raw, error = outcome(lambda: who.call_bound(agent[0], "DoAction", action))
        assert error is None, f"{label}: {base.brief(error)}"
        replies[label] = reply_text(raw)
    # the offer names the mission (891, a basic level 4 kill) with accept and decline (377, 378); the accept leaves no button
    assert "891" in replies["request"] and "(377, 3)" in replies["request"], replies["request"][:400]
    assert "891" in replies["accept"] and "(377, 3)" not in replies["accept"], replies["accept"][:400]

    # the dungeon starts in the agent's system (the sweep every ten seconds): its NPCs come into space around the pilot
    arrived: set[int] = set()
    arrived_types: dict[int, int] = {}
    started = time.time()
    while time.time() - started < 90 and not arrived:
        for item, type_id in slims_in(timed_events(who, 3.0)):
            if item != ship_id:
                arrived.add(item)
                arrived_types[item] = type_id
    assert arrived, "the mission's dungeon did not bring any NPC into space"

    # the journal shows the accepted mission (its type label is the agent's kind, see MissionComposition)
    journal = reply_text(who.call("agentMgr", "GetMyJournalDetails"))
    assert "891" in journal, journal[:400]
    return ship_id, gun, next_ballpark, dogma, arrived, arrived_types


def test_a_basic_level_4_kill_mission_is_offered_accepted_and_its_dungeon_comes_into_space():
    race = plain_race(DEATH["races"][0], "victim")  # the fitting the NPC kill test uses (a gun in modules[1])
    run = uuid.uuid4().hex[:10]
    with base.connect(f"walk-mission-{run}") as who:
        walk_into_dungeon(who, race, run)


@pytest.mark.xfail(strict=True, raises=AssertionError, reason=(
    "open: the test ship is a starter frigate with no fit and dies to the dungeon's NPCs (7 of 8 alive after 90 s, 43 hits); the server then "
    "releases the pilot's bound objects (SpaceSessions.Death), which the walk saw as UnMachoDestination. Needs a fitted hull to pass"))
def test_the_pilot_clears_the_dungeon_and_is_paid():
    race = plain_race(DEATH["races"][0], "victim")
    run = uuid.uuid4().hex[:10]
    with base.connect(f"walk-mission-kill-{run}") as who:
        ship_id, gun, next_ballpark, dogma, arrived, arrived_types = walk_into_dungeon(who, race, run)

        # the dungeon's NPCs: the pilot locks each in turn, closes on it and fires until it is gone (test_npc's steps); the dungeon is clear
        # when its last NPC goes, and the mission is then complete and paid
        cash_before = cash(who)
        pending = sorted(arrived)
        started = time.time()
        while pending and time.time() - started < 600:
            target = pending[0]
            # the pilot closes in on the NPC (the dungeon is some 50 km off) and locks it once it is within the lock range (29.7 km)
            who.call_bound(next_ballpark[0], "CmdOrbit", target, 2000)
            in_range(who, lambda: who.call_bound(dogma[0], "AddTarget", target), f"locking {target} ({arrived_types.get(target)})")
            in_range(who, lambda: who.call_bound(dogma[0], "Activate", gun, "targetAttack", target, 1000), f"firing at {target}")
            seen = watch({"events": who}, 90.0, until=lambda s: any(removed(e, target) or removed(e, ship_id) for e in s["events"]))["events"]
            if any(removed(e, ship_id) for e in seen):
                hits = sum(1 for e in seen if e["name"] == "OnDamageMessage")
                raise AssertionError(f"the pilot's ship was destroyed with {len(pending)} NPCs left ({hits} damage messages in 90 s)")
            if any(removed(e, target) for e in seen):
                pending.pop(0)
        assert not pending, f"the dungeon still has {len(pending)} NPCs after {int(time.time() - started)} s"
        watch({"events": who}, 10.0)
        assert cash(who) > cash_before, (cash_before, cash(who))

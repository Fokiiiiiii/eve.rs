"""A pilot takes a basic kill mission from a level 4 security agent, flies to the agent's system, kills the dungeon's NPCs and is told the mission
is complete (eve.rs only: the mission content is the server's own; the reference's sites are not reproduced).

    EVE_LIVE_SLOW=1 EVE_LIVE_HOST=host:port EVE_LIVE_SERVER=eve.rs pytest tests/test_mission_walk.py

The pilot starts in Manifest (30100032), one stargate jump from 30005204, where the level 4 security agent 3015920 sits. It warps to the gate,
jumps, requests the mission (DoAction 376), accepts it (377) and waits for the wave director's dungeon (the sweep runs every ten seconds). The
dungeon's NPCs are shot with the pilot's gun as test_npc does. A basic level 4 mission has no ISK in the official data, so the wallet is only
checked to be unchanged (owner decision D-R1). Needs EVE_SDE_DIR on the host; the mission texts come from the embedded client data.
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
AGENT = 3015920
AGENT_CORPORATION = 1000143
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

COCKROACH = 11019
QA_SMARTBOMB = 18642
POLARIS = 9955  # the skill the QA Smartbomb requires (its required-skill attribute), at level 5
POLARIS_LEVEL5_POINTS = 128000  # level 5 is 256,000 x the rank, and the SDE's rank of Polaris (skillTimeConstant, attribute 275) is 0.5

def ledger_of(ship_id: int) -> str:
    """The pilot's wallet balance and standings (the run's wallet and relations schemas), for the payout's message."""
    url = os.environ["EVE_PSQL_URL"]
    owner = subprocess.run(["psql", url, "-At", "-c", f"SELECT owner_id FROM {os.environ['EVE_INVENTORY_SCHEMA']}.items WHERE id = {ship_id}"],
                           check=True, capture_output=True, text=True).stdout.split()[0]
    balance = subprocess.run(["psql", url, "-At", "-c", f"SELECT owner_id, isk FROM {os.environ['EVE_WALLET_SCHEMA']}.balances WHERE owner_id = {owner}"],
                             check=True, capture_output=True, text=True).stdout.strip()
    standings = subprocess.run(["psql", url, "-At", "-c", f"SELECT kind, subject, object, value FROM {os.environ['EVE_RELATIONS_SCHEMA']}.relations "
                                f"WHERE object = {owner}"], check=True, capture_output=True, text=True).stdout.strip()
    return f"wallet {balance!r}, standings {standings!r}"

ARC_ENTRY_AGENT = 3019356
ARC_TALK_AGENT = 3019369
ARNON, TAR = 30005001, 30005199
# the gates of the routes (SDE mapStargates, the shortest path): (gate, the gate it leads to, the system it leads to)
MANIFEST_TO_ARNON = [(50017727, 50016629, 30005204), (50003290, 50003261, 30005203), (50003260, 50002615, 30005201),
                     (50002614, 50002747, 30005202), (50002749, 50003638, ARNON)]
ARNON_TO_TAR = [(50003638, 50002749, 30005202), (50002747, 50002614, 30005201), (50002613, 50001390, TAR)]

def fly(who, ship_id: int, system: int, hops) -> tuple:
    """Flies the gates of a route in turn: warp to the gate, jump, and bind the next system's ballpark and dogma. Returns the ballpark and the
    dogma of the system the route ends in."""
    ballpark = who.call("beyonce", "MachoBindObject", system, None)
    dogma = None
    for gate, dest_gate, dest_system in hops:
        who.call_bound(ballpark[0], "CmdWarpToStuff", "item", gate, minRange=0)
        until_stopped(who)
        who.call_bound(ballpark[0], "CmdStargateJump", gate, dest_gate, ship_id)
        wait_quietly(who, 6.0)
        ballpark = who.call("beyonce", "MachoBindObject", dest_system, None)
        dogma = who.call("dogmaIM", "MachoBindObject", (dest_system, 5), None)
        wait_quietly(who, 3.0)
    return ballpark, dogma

def journal_of(ship_id: int) -> list[str]:
    """The actions of the pilot's agent journal (the run's agents schema), oldest first: `completed` once the mission is complete."""
    url = os.environ["EVE_PSQL_URL"]

    def sql(text: str) -> str:
        return subprocess.run(["psql", url, "-At", "-c", text], check=True, capture_output=True, text=True).stdout

    owner = sql(f"SELECT owner_id FROM {os.environ['EVE_INVENTORY_SCHEMA']}.items WHERE id = {ship_id}").split()[0]
    return sql(f"SELECT action FROM {os.environ['EVE_AGENTS_SCHEMA']}.journal WHERE character_id = {owner} ORDER BY id").split()

def seed_cockroach(ship_id: int) -> int:
    """A fixture, not a server path: the character's ship row in the run's inventory schema takes the Cockroach's type, and QA Smartbombs
    are fitted in its free high slots (dbseed.seed_qa_arsenal). Returns the first one's item id. The live run exports EVE_INVENTORY_SCHEMA and EVE_PSQL_URL."""
    schema, url = os.environ["EVE_INVENTORY_SCHEMA"], os.environ["EVE_PSQL_URL"]

    def sql(text: str) -> str:
        return subprocess.run(["psql", url, "-At", "-c", text], check=True, capture_output=True, text=True).stdout.strip()

    # the QA Smartbomb needs Polaris at level 5 to be fitted (its required-skill attributes): the character is given that skill
    owner = sql(f"SELECT owner_id FROM {schema}.items WHERE id = {ship_id}").split()[0]
    # the smartbomb lights only at the red safety in low, high and safe space (the client's GetRequiredSafetyLevelForEffect): 0 is none
    sql(f"INSERT INTO {os.environ['EVE_CRIMEWATCH_SCHEMA']}.safety (character_id, level, updated_ms) VALUES ({owner}, 0, {int(time.time() * 1000)}) "
        f"ON CONFLICT (character_id) DO UPDATE SET level = 0, updated_ms = EXCLUDED.updated_ms")
    sql(f"INSERT INTO {os.environ['EVE_SKILLS_SCHEMA']}.character_skills (character_id, type_id, level, skill_points) "
        f"VALUES ({owner}, {POLARIS}, 5, {POLARIS_LEVEL5_POINTS}) ON CONFLICT (character_id, type_id) DO UPDATE SET level = 5, skill_points = {POLARIS_LEVEL5_POINTS}")
    # psql -c prints the rows and then the command tag ("UPDATE 1"): the first token is the answer
    assert sql(f"UPDATE {schema}.items SET type_id = {COCKROACH} WHERE id = {ship_id} RETURNING id").split()[0] == str(ship_id), \
        f"the ship row {ship_id} was not found in {schema}"
    from tests import dbseed  # the SDE high slots of the ship (smartbomb: hiPower), the bombs of the arsenal; the walk activates the first
    return dbseed.seed_qa_arsenal(ship_id)[0]

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
    raw = who.call("account", "GetCashBalance", 0)
    return float(getattr(raw, "value", raw))

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
    bomb = seed_cockroach(ship_id) if "EVE_INVENTORY_SCHEMA" in os.environ else None  # the fixture needs eve.rs's database
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

    agent = who.call("agentMgr", "MachoBindObject", AGENT, None)
    replies = {}
    for label, action in (("greeting", None), ("request", OFFER), ("accept", ACCEPT)):
        raw, error = outcome(lambda: who.call_bound(agent[0], "DoAction", action))
        assert error is None, f"{label}: {base.brief(error)}"
        replies[label] = reply_text(raw)
    # the offer names the mission (891, a basic level 4 kill) with accept and decline (377, 378); the accept leaves no button
    assert "891" in replies["request"] and "(377, 3)" in replies["request"], replies["request"][:400]
    assert "891" in replies["accept"] and "(377, 3)" not in replies["accept"], replies["accept"][:400]

    arrived: set[int] = set()
    arrived_types: dict[int, int] = {}
    started = time.time()
    while time.time() - started < 90 and not arrived:
        for item, type_id in slims_in(timed_events(who, 3.0)):
            if item != ship_id:
                arrived.add(item)
                arrived_types[item] = type_id
    assert arrived, "the mission's dungeon did not bring any NPC into space"

    journal = reply_text(who.call("agentMgr", "GetMyJournalDetails"))
    assert "891" in journal, journal[:400]
    return ship_id, bomb, next_ballpark, dogma, arrived, arrived_types

def test_a_basic_level_4_kill_mission_is_offered_accepted_and_its_dungeon_comes_into_space():
    race = plain_race(DEATH["races"][0], "victim")  # the fitting the NPC kill test uses (a gun in modules[1])
    run = uuid.uuid4().hex[:10]
    with base.connect(f"walk-mission-{run}") as who:
        walk_into_dungeon(who, race, run)

@pytest.mark.timeout(1800)
def test_the_pilot_clears_the_dungeon_and_is_paid():
    """The owner's standard test ship (the Cockroach, effectively invulnerable) with the QA Smartbomb clears the dungeon: each cycle hits every NPC
    of the dungeon in its 250 km field, until the last NPC is gone; the mission is then complete and paid."""
    race = plain_race(DEATH["races"][0], "victim")
    run = uuid.uuid4().hex[:10]
    with base.connect(f"walk-mission-kill-{run}") as who:
        ship_id, bomb, next_ballpark, dogma, arrived, arrived_types = walk_into_dungeon(who, race, run)
        cash_before = cash(who)
        # the smartbomb is refused within max(5 km, its own range) of a station or gate (SMARTBOMB_NEAR_STATION, DEVIATIONS): the QA bomb's range is
        # its 250 km field, so the pilot flies 260 km from the gate at full speed (1,000 m/s, about 260 s), still within the field of the dungeon (+x)
        who.call_bound(next_ballpark[0], "CmdGotoDirection", 1.0, 0.0, 0.0)
        wait_quietly(who, 270.0)
        # the module is fitted offline: the client switches it on with SetModuleOnline(shipID, moduleID)
        _, error = outcome(lambda: who.call_bound(dogma[0], "SetModuleOnline", ship_id, bomb))
        assert error is None, f"bringing the smartbomb online: {base.brief(error)}"
        # the dungeon's waves come one after another: the bomb fires until the journal records the mission completed (the dungeon's last wave is down)
        started = time.time()
        while "completed" not in journal_of(ship_id) and time.time() - started < 900:
            in_range(who, lambda: who.call_bound(dogma[0], "Activate", bomb, "empWave", None, 1000), "the smartbomb")
            seen = watch({"events": who}, 8.0)["events"]
            if any(removed(e, ship_id) for e in seen):
                hits = sum(1 for e in seen if e["name"] == "OnDamageMessage")
                raise AssertionError(f"the pilot's ship was destroyed ({hits} damage messages in 8 s); journal {journal_of(ship_id)}")
        actions = journal_of(ship_id)
        assert "completed" in actions, f"the mission did not complete in {int(time.time() - started)} s: journal {actions}"
        watch({"events": who}, 10.0)
        assert cash(who) > cash_before, (cash_before, cash(who), actions, ledger_of(ship_id))

@pytest.mark.timeout(1800)
def test_arc_29_steps_1_and_2_are_completed_in_space():
    """Arc 29 (The Blood-Stained Stars), steps 1 and 2 on the owner's test ship: the entry agent at Arnon offers the arc's first mission (14117,
    a kill in dungeon 3509), the pilot accepts it and clears the dungeon with the QA Smartbomb; then it flies to Tar, where step 2 (14118) is
    completed by talking, in space, to the agent of the next step (3019369)."""
    race = plain_race(DEATH["races"][0], "victim")
    run = uuid.uuid4().hex[:10]
    with base.connect(f"walk-arc29-{run}") as who:
        _, ship_id, _, _ = arrive(who, race, run)
        bomb = seed_cockroach(ship_id) if "EVE_INVENTORY_SCHEMA" in os.environ else None  # the fixture needs eve.rs's database
        service = who.call("ship", "MachoBindObject", (STATION, 15), None)
        who.call_bound(service[0], "Undock", ship_id, False)
        wait_quietly(who, 2.0)
        ballpark, dogma = fly(who, ship_id, SYSTEM, MANIFEST_TO_ARNON)

        agent = who.call("agentMgr", "MachoBindObject", ARC_ENTRY_AGENT, None)
        for label, action in (("greeting", None), ("request", OFFER), ("accept", ACCEPT)):
            raw, error = outcome(lambda: who.call_bound(agent[0], "DoAction", action))
            assert error is None, f"arc 29 {label}: {base.brief(error)}"
        assert "14117" in reply_text(who.call("agentMgr", "GetMyJournalDetails")), "the journal does not name mission 14117"

        # the pilot flies 260 km from the gate (the smartbomb's reach rule, DEVIATIONS), then switches the bomb on and fires until the first
        # mission is completed
        who.call_bound(ballpark[0], "CmdGotoDirection", 1.0, 0.0, 0.0)
        wait_quietly(who, 270.0)
        _, error = outcome(lambda: who.call_bound(dogma[0], "SetModuleOnline", ship_id, bomb))
        assert error is None, f"bringing the smartbomb online: {base.brief(error)}"
        started = time.time()
        while journal_of(ship_id).count("completed") < 1 and time.time() - started < 900:
            in_range(who, lambda: who.call_bound(dogma[0], "Activate", bomb, "empWave", None, 1000), "the smartbomb")
            seen = watch({"events": who}, 8.0)["events"]
            if any(removed(e, ship_id) for e in seen):
                raise AssertionError(f"the pilot's ship was destroyed; journal {journal_of(ship_id)}")
        assert journal_of(ship_id).count("completed") >= 1, f"step 1 did not complete in {int(time.time() - started)} s: {journal_of(ship_id)}"

        for label, action in ("greeting", None), ("request", OFFER), ("accept", ACCEPT):
            raw, error = outcome(lambda: who.call_bound(agent[0], "DoAction", action))
            assert error is None, f"arc 29 next step, {label}: {base.brief(error)}"
        assert "14118" in reply_text(who.call("agentMgr", "GetMyJournalDetails")), "the journal does not name mission 14118"
        ballpark, dogma = fly(who, ship_id, ARNON, ARNON_TO_TAR)
        talk = who.call("agentMgr", "MachoBindObject", ARC_TALK_AGENT, None)
        raw, error = outcome(lambda: who.call_bound(talk[0], "DoAction", None))
        assert error is None, f"the talk in Tar: {base.brief(error)}"
        started = time.time()
        while journal_of(ship_id).count("completed") < 2 and time.time() - started < 60:
            wait_quietly(who, 3.0)
        assert journal_of(ship_id).count("completed") >= 2, f"step 2 did not complete: journal {journal_of(ship_id)}"

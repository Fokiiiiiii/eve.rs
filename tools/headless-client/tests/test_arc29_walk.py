"""Arc 29, The Blood-Stained Stars: every step of the arc, in the order the server offers it (the lowest mission ID that is offerable).

    EVE_LIVE_SLOW=1 EVE_MISSION_INCLUDE_GENERIC_ARCS=1 EVE_LIVE_HOST=host:port EVE_PG_URL=postgres://... \
        EVE_LIVE_HOST=... pytest -s tests/test_arc29_walk.py

Needs tools/dev/run-eve-live.sh (the scratch schemas, EVE_PG_URL) and the generic switch, because the arc's kill sites are family-derived
(mission-site-status.v1.json). The path table is the arc's steps from the SDE build (sde_mission.epicArcs, sde_mission.missions) walked by the
server's offer rule (EpicArcOffers.Pick). Per step the pilot is seeded where the step needs it: docked at the agent's station, or in space in
the agent's system (the in-space agents). The seeding is the database state a pilot would have; the server answers from it.

Kill steps: the pilot shoots the dungeon with the QA Smartbomb of the Cockroach (test ship standard). Fetch kills: the objective item is
handed in at the agent (a turn-in). Talk steps complete when the pilot talks to the next step's agent.

Courier steps (14134, 14140, 14386, 14413) are not built on this branch: the walk stops there unless ARC29_SEED_COURIER=1, which records
the courier completion in the journal (a seeded completion, reported as such). ARC29_FROM and ARC29_TO (indexes into the path) let a long
walk run in parts: the steps before ARC29_FROM are seeded as completed in the journal, because the scratch schemas of a run are dropped at its end.
"""

from __future__ import annotations

import collections
import json
import os
import re
import subprocess
import time
import uuid
from collections import namedtuple
from contextlib import contextmanager

import pytest

from evecli import packet
from evecli.describe import describe
from evecli.observe import timed_events, wait_quietly
from tests import test_login_to_character as base
from tests.test_agents_live import new_pilot
from tests.test_mission_walk import ACCEPT, OFFER, removed, reply_text
from tests.test_others import watch
from tests.test_weapon import slims_in

PG = os.environ.get("EVE_PG_URL", "")
pytestmark = [
    pytest.mark.skipif(not (base.LIVE and PG), reason="set EVE_LIVE_HOST=host:port and EVE_PG_URL to test against a running server"),
    pytest.mark.skipif(os.environ.get("EVE_LIVE_SLOW") != "1", reason="a long walk: set EVE_LIVE_SLOW=1"),
]

COCKROACH = 11019
QA_SMARTBOMB = 18642
POLARIS, POLARIS_LEVEL5_POINTS = 9955, 128000
HANGAR, SPACE_FLAG = 4, 0
FAR_FUTURE_MS = 4_102_444_800_000
REWARD_REF_TYPE = 33  # the wallet journal reference type of an agent mission reward (MissionDungeonRuntime.AgentMissionRewardRefType)
COMPLETED, ACCEPTED = 4, 2  # AgentMissionState

Step = namedtuple("Step", "mission agent system in_space station kind dungeon objective quantity reward next_agents drop_off", defaults=((), None))
# the arc on the offer path: mission, agent, the system the agent is in (its in-space system when it is in space), whether it is in space,
# its station, the kind (kill, talk or courier), the kill's dungeon, the objective item and quantity (fetch), the ISK reward (SDE)
ARC = int(os.environ.get("EVE_WALK_ARC", "29"))  # the epic arc walked (29: The Blood-Stained Stars)


def arc_path(arc: int) -> list:
    """The arc's steps in the order the server offers them: the lowest offerable mission ID first, a fork resolved by that rule (the
    EpicArcOffers.Pick rule without failures or restarts). Each step carries its agent's place (in space in the agent's system when the
    agent is in space, else its station) and its kind; the kill's dungeon, objective and reward are the SDE's."""
    doc = json.loads(sql(f"SELECT doc::text FROM sde_mission.\"epicArcs\" WHERE key = '{arc}'"))
    steps = {row["_key"]: row for row in doc["missions"]}
    predecessors = {key: [] for key in steps}
    for key, row in steps.items():
        for nxt in row.get("nextMissions", []):
            if nxt in predecessors:
                predecessors[nxt].append(key)
    done: list = []
    while True:
        offerable = []
        for key in steps:
            if key in done:
                continue
            leading = predecessors[key]
            if leading and not any(parent in done for parent in leading):
                continue
            if any(sibling != key and sibling in done for parent in leading for sibling in steps[parent].get("nextMissions", [])):
                continue
            offerable.append(key)
        if not offerable:
            break
        done.append(min(offerable))
        if not steps[done[-1]].get("nextMissions"):
            break  # a terminal step ends the run: the arc cools down for its restart interval, so later entries wait for the next run
    path = []
    for key in done:
        mission = json.loads(sql(f"SELECT (doc - 'messages' - 'name')::text FROM sde_mission.missions WHERE key = '{key}'"))
        agent = steps[key]["agentID"]
        places = sql(f"SELECT doc->>'solarSystemID' FROM sde.\"agentsInSpace\" WHERE key = '{agent}'").splitlines()
        in_space = bool(places and places[0])
        character = json.loads(sql(f"SELECT doc::text FROM sde.\"npcCharacters\" WHERE key = '{agent}'"))
        station = int(character["locationID"])
        system = int(places[0]) if in_space else int(json.loads(sql(f"SELECT doc::text FROM sde.\"npcStations\" WHERE key = '{station}'"))["solarSystemID"])
        kill = mission.get("killMission", {})
        courier = mission.get("courierMission", {})
        kind = "courier" if courier else ("kill" if kill else "talk")
        reward_row = mission.get("missionRewards", {}).get("reward", {})
        reward = int(reward_row.get("rewardQuantity", 0) or 0) if reward_row.get("rewardTypeID") == 29 else 0  # ISK only (type 29)
        if courier:  # the cargo of a courier: picked up at the agent, delivered to the drop-off station
            objective, quantity = courier.get("objectiveTypeID"), courier.get("objectiveQuantity")
        elif kill and "objectiveTypeID" in kill:
            objective, quantity = kill.get("objectiveTypeID"), kill.get("objectiveQuantity")
        else:
            objective, quantity = None, None
        next_agents = tuple(sorted({steps[nxt]["agentID"] for nxt in steps[key].get("nextMissions", []) if nxt in steps}))
        path.append(Step(key, agent, system, in_space, station, kind, kill.get("dungeonID"), objective, quantity, reward, next_agents))
    return path

ARC_WALK_RUN = uuid.uuid4().hex[:10]
ACCOUNT = f"arc29-{ARC_WALK_RUN}"


def schema(module: str) -> str:
    return os.environ[f"EVE_{module}_SCHEMA"]


def sql(text: str) -> str:
    done = subprocess.run(["psql", PG, "-At", "-F", "|", "-v", "ON_ERROR_STOP=1", "-c", text], check=True, capture_output=True, text=True)
    # psql prints the command tag ("INSERT 0 1") after the rows: the rows are the answer
    return "\n".join(line for line in done.stdout.splitlines() if not line.startswith(("INSERT ", "UPDATE ", "DELETE "))).strip()


def ship_of(character: int) -> int:
    return int(sql(f"SELECT ship_id FROM {schema('CHARACTERS')}.characters WHERE id = {character}"))


def place_rows(step: Step) -> tuple[str, str]:
    """The SDE constellation and region of the step's system."""
    row = sql(f"SELECT doc->>'constellationID', doc->>'regionID' FROM sde.\"mapSolarSystems\" WHERE key = '{step.system}'")
    constellation, region = row.split("|")
    return constellation, region


def seed_place(character: int, ship: int, step: Step) -> None:
    """The pilot as a pilot who logged off there keeps it: docked in the station (ship in its hangar), or in space in the system (the
    ship's space pose, as in test_pochven_live)."""
    items, characters, poses = f"{schema('INVENTORY')}.items", f"{schema('CHARACTERS')}.characters", f"{schema('SPACE')}.space_items"
    constellation, region = place_rows(step)
    if step.in_space:
        sql(f"UPDATE {items} SET location_id = {step.system}, flag = {SPACE_FLAG} WHERE id = {ship}")
        sql(f"UPDATE {characters} SET solar_system_id = {step.system}, constellation_id = {constellation}, region_id = {region} WHERE id = {character}")
        sql(f"DELETE FROM {poses} WHERE item_id = {ship}")
        sql(f"INSERT INTO {poses} (item_id, solar_system_id, x, y, z, dx, dy, dz, radius, group_id, launcher_id, character_id, corporation_id, "
            f"alliance_id, war_faction_id, expires_at_ms) SELECT {ship}, {step.system}, 0, 0, 0, 1, 0, 0, 42, 25, 0, {character}, corporation_id, 0, 0, "
            f"{FAR_FUTURE_MS} FROM {characters} WHERE id = {character}")
        print("ARC29 seeded space", step.mission, sql(f"SELECT count(*) FROM {poses} WHERE item_id = {ship}"), sql(f"SELECT location_id, flag FROM {items} WHERE id = {ship}"))
    else:
        sql(f"UPDATE {items} SET location_id = {step.station}, flag = {HANGAR} WHERE id = {ship}")
        sql(f"UPDATE {characters} SET station_id = {step.station}, solar_system_id = {step.system}, constellation_id = {constellation}, "
            f"region_id = {region} WHERE id = {character}")
        sql(f"DELETE FROM {poses} WHERE item_id = {ship}")


@contextmanager
def session(character: int, ship: int, step: Step, where: str):
    """A session of the pilot where the step needs it: seeded, then selected."""
    time.sleep(3.0)  # the server still closes the previous session: a pose seeded before that can be removed by it
    seed_place(character, ship, step)
    with base.connect(ACCOUNT) as who:
        answer = who.call("charUnboundMgr", "SelectCharacterID", charID=character, secondChoiceID=None, skipTutorial=False)
        print("ARC29 selected", character, step.mission, step.in_space, sql(f"SELECT solar_system_id, station_id FROM {schema('CHARACTERS')}.characters WHERE id = {character}"), str(answer)[:120])
        wait_quietly(who, 1.0)
        yield who


def fit_smartbomb(character: int, ship: int) -> int:
    """The Cockroach's QA Smartbombs in its free high slots (dbseed.seed_qa_arsenal: the SDE's slot, the red safety and the Polaris skill that
    fitting needs); the walk activates the first."""
    from tests import dbseed
    return dbseed.seed_qa_arsenal(ship)[0]


def _old_fit_smartbomb_unused(character: int, ship: int) -> int:
    items = f"{schema('INVENTORY')}.items"
    owner = int(sql(f"SELECT owner_id FROM {items} WHERE id = {ship}"))
    # the red safety: a smartbomb lights only there in low, high and safe space (crimewatch.safety, 0 is none)
    sql(f"INSERT INTO {schema('CRIMEWATCH')}.safety (character_id, level, updated_ms) VALUES ({owner}, 0, {int(time.time() * 1000)}) "
        f"ON CONFLICT (character_id) DO UPDATE SET level = 0, updated_ms = EXCLUDED.updated_ms")
    sql(f"INSERT INTO {schema('SKILLS')}.character_skills (character_id, type_id, level, skill_points) VALUES ({owner}, {POLARIS}, 5, {POLARIS_LEVEL5_POINTS}) "
        f"ON CONFLICT (character_id, type_id) DO UPDATE SET level = 5, skill_points = {POLARIS_LEVEL5_POINTS}")
    used = {int(flag) for flag in sql(f"SELECT flag FROM {items} WHERE location_id = {ship}").split() if flag}
    flag = next(slot for slot in range(19, 27) if slot not in used)
    return int(sql(f"INSERT INTO {items} (type_id, owner_id, location_id, flag, quantity, singleton) VALUES ({QA_SMARTBOMB}, {owner}, {ship}, {flag}, 1, true) RETURNING id"))


def mission_state(character: int, agent: int):
    row = sql(f"SELECT state, mission_id FROM {schema('AGENTS')}.missions WHERE character_id = {character} AND agent_id = {agent}")
    return tuple(int(x) for x in row.split("|")) if row else None


def completed(character: int, mission: int) -> int:
    return int(sql(f"SELECT count(*) FROM {schema('AGENTS')}.journal WHERE character_id = {character} AND mission_id = {mission} AND action = 'completed'"))


def reward_total(character: int) -> int:
    return int(float(sql(f"SELECT coalesce(sum(amount), 0) FROM {schema('WALLET')}.journal WHERE owner_id = {character} AND ref_type_id = {REWARD_REF_TYPE}")))


def held(character: int, type_id: int) -> int:
    return int(float(sql(f"SELECT coalesce(sum(quantity), 0) FROM {schema('INVENTORY')}.items WHERE owner_id = {character} AND type_id = {type_id}")))


def seed_agent_standings(character: int, path: list) -> None:
    """A fixture: the standing a pilot has from earlier work with each agent's corporation (the client's CanUseAgent rule asks for one from a
    level 4 agent: "Your current standings are not high enough", AgentDialogue). Only the corporation's standing is seeded; the arc's own
    completions then move the standings the SDE gives."""
    for agent in sorted({step.agent for step in path}):
        corporation = int(sql(f"SELECT doc->>'corporationID' FROM sde.\"npcCharacters\" WHERE key = '{agent}'"))
        sql(f"INSERT INTO {schema('RELATIONS')}.relations (kind, subject, object, value, since_ms, updated_ms) "
            f"VALUES ('standing', {corporation}, {character}, 10, 0, 0) ON CONFLICT (kind, subject, object) DO UPDATE SET value = EXCLUDED.value")


def seed_completed(character: int, step: Step) -> None:
    """A completion the walk does not play: the journal row the server writes when the step is completed (see the module docstring)."""
    sql(f"INSERT INTO {schema('AGENTS')}.journal (character_id, agent_id, mission_id, from_state, to_state, action, reference) "
        f"VALUES ({character}, {step.agent}, {step.mission}, {ACCEPTED}, {COMPLETED}, 'completed', 'seed:arc29:{step.mission}:{character}')")


def do(who, agent: int, action):
    handle = who.call("agentMgr", "MachoBindObject", agent, None)
    return reply_text(who.call_bound(handle[0], "DoAction", action))


def offer(who, character: int, step: Step) -> None:
    """Greets the agent (which completes a talk step waiting for this agent), requests the offer, accepts it; the journal must hold it."""
    do(who, step.agent, None)
    requested = do(who, step.agent, OFFER)
    if str(step.mission) not in requested:
        rows = sql(f"SELECT mission_id, action, to_state FROM {schema('AGENTS')}.journal WHERE character_id = {character} AND agent_id = {step.agent} ORDER BY id")
        raise AssertionError(f"mission {step.mission} was not offered by {step.agent}: state {mission_state(character, step.agent)}; "
                             f"journal with this agent {rows!r}; said {requested[:160]!r}")
    do(who, step.agent, ACCEPT)
    assert mission_state(character, step.agent) == (ACCEPTED, step.mission), f"mission {step.mission} is not accepted: {mission_state(character, step.agent)}"


def stop(who, dogma, bomb: int) -> None:
    """Asks the smartbomb's effect to stop (the client's Deactivate: it stops at the end of its cycle). A refusal here is not a walk failure."""
    try:
        who.call_bound(dogma[0], "Deactivate", bomb, "empWave")
    except packet.MachoError:
        pass


def _has_table(module: str, table: str) -> bool:
    return sql(f"SELECT to_regclass('{schema(module)}.{table}') IS NOT NULL") == "t"


def fire(who, dogma, bomb: int, mission: int) -> None:
    """One activation of the smartbomb. A refusal for range or lock waits and tries again (as in_range); any other refusal fails the walk
    with what the server said."""
    for _ in range(60):
        try:
            who.call_bound(dogma[0], "Activate", bomb, "empWave", None, 1000)
            return
        except packet.MachoError as error:
            if not any(text in repr(error.payload) for text in ("TargetNotWithinRange", "That target is not locked", "EffectAlreadyActive2")):
                raise AssertionError(f"mission {mission}: the smartbomb refused: {describe(error.payload)}") from error
            # a cycle left running (the bomb repeats) is asked to stop; it stops at the end of its cycle, the next try lands then
            stop(who, dogma, bomb)
            watch({"events": who}, 5.0)
    raise AssertionError(f"mission {mission}: the smartbomb stayed out of range")


def fight(character: int, ship: int, step: Step, bomb: int) -> None:
    """The pilot in space in the agent's system shoots the dungeon with the QA Smartbomb until the step's outcome is in the database."""
    fetch = step.objective is not None
    reward_before = reward_total(character)
    standing_before = standings_of(character)
    with session(character, ship, step._replace(in_space=True), "fight") as who:  # the dungeon starts for a pilot in space in the system
        ballpark = who.call("beyonce", "MachoBindObject", step.system, None)  # the bind that starts the scene's messages (test_pochven_live)
        arrived = False
        names: collections.Counter = collections.Counter()
        started = time.time()
        while time.time() - started < 120 and not arrived:
            seen = timed_events(who, 3.0)
            names.update(str(e.get("name")) for e in seen)
            arrived = any(item != ship for item, _ in slims_in(seen))
        if not arrived:
            pose = sql(f"SELECT solar_system_id, x FROM {schema('SPACE')}.space_items WHERE item_id = {ship}")
            journal = sql(f"SELECT action FROM {schema('AGENTS')}.journal WHERE character_id = {character} ORDER BY id")
            raise AssertionError(f"mission {step.mission}: the dungeon {step.dungeon} brought no NPC into space; events {dict(names)}; "
                                 f"pose {pose!r}; journal {journal!r}")
        dogma = who.call("dogmaIM", "MachoBindObject", (step.system, 5), None)
        _, error = outcome(lambda: who.call_bound(dogma[0], "SetModuleOnline", ship, bomb))
        assert error is None, f"mission {step.mission}: bringing the smartbomb online: {error}"
        started = time.time()
        while time.time() - started < 900:
            done = held(character, step.objective) >= step.quantity if fetch else completed(character, step.mission) >= 1
            if done:
                break
            fire(who, dogma, bomb, step.mission)
            seen = watch({"events": who}, 8.0)["events"]
            if any(removed(e, ship) for e in seen):
                raise AssertionError(f"mission {step.mission}: the pilot's ship was destroyed")
        else:
            alive = sql(f"SELECT count(*) FROM {schema('SPACE')}.dungeon_wave_npcs") if _has_table("SPACE", "dungeon_wave_npcs") else "n/a"
            contents = sql(f"SELECT kind, state, count(*) FROM {schema('CONTENT')}.instances GROUP BY kind, state") if _has_table("CONTENT", "instances") else "n/a"
            sample = sql(f"SELECT definition, state, result FROM {schema('CONTENT')}.instances WHERE kind = 'dungeon' AND owner = {character} ORDER BY id")
            raise AssertionError(f"mission {step.mission}: the dungeon {step.dungeon} was not cleared in 900 s; NPC rows {alive!r}; content {contents!r}; "
                                 f"failed sample {sample!r}; character {character}")
        stop(who, dogma, bomb)  # the repeating bomb is left off, so the next session can start it
    if fetch:
        assert held(character, step.objective) >= step.quantity, f"mission {step.mission}: the objective item was not granted"
        assert completed(character, step.mission) == 0, f"mission {step.mission}: a fetch step completed without the turn-in"
    else:
        assert completed(character, step.mission) == 1, f"mission {step.mission}: the kill did not complete"
        assert reward_total(character) - reward_before == step.reward, (step.mission, step.reward, reward_total(character) - reward_before)
        for faction, gain in extra_standings_of(step.mission):
            change = standings_of(character).get(faction, 0.0) - standing_before.get(faction, 0.0)
            assert abs(change - gain / 10) < 1e-6, f"mission {step.mission}: the standing toward {faction} moved {change}, expected {gain / 10}"


def standings_of(character: int) -> dict:
    """The character's standings by entity (the relations rows of kind standing toward the character)."""
    rows = sql(f"SELECT subject, value FROM {schema('RELATIONS')}.relations WHERE kind = 'standing' AND object = {character}")
    return {int(subject): float(value) for subject, value in (line.split("|") for line in rows.splitlines() if line)}


def extra_standings_of(mission: int) -> list:
    """The SDE's extraStandings of a mission as (entity, value in tenths)."""
    raw = sql(f"SELECT doc->'extraStandings' FROM sde_mission.missions WHERE key = '{mission}'")
    if not raw or raw == "":
        return []
    return [(int(item["_key"]), float(item["_value"])) for item in json.loads(raw)]


def drop_off_of(who, agent: int, cargo: int, quantity: int) -> int:
    """The courier's drop-off station, from the mission's transport objective (the client's contract: pickup, then drop-off, each with its
    locationID). The pickup is checked to be the agent's station, and the cargo to be held at the pickup."""
    handle = who.call("agentMgr", "MachoBindObject", agent, None)[0]
    text = reply_text(who.call_bound(handle, "GetMissionObjectiveInfo"))
    start = text.find("transport")
    assert start >= 0, f"no transport objective in the objective: {text[:300]}"
    found = [int(x) for x in re.findall(r"locationID[^0-9]{0,40}(\d+)", text[start:])]
    assert len(found) >= 2, f"the transport objective names no pickup and drop-off: {text[start:start + 300]}"
    return found[1]


def deliver(character: int, ship: int, step: Step, pickup_check: bool) -> None:
    """A courier: the cargo is held (taken at the agent's station when the mission was accepted), the pilot flies to the drop-off station's
    system (seeded in space 20 km from the station) and docks there; docking completes the mission and takes the cargo."""
    assert held(character, step.objective) >= step.quantity, f"mission {step.mission}: the cargo was not handed over at the pickup"
    drop = step.drop_off
    system, x, y, z = sql(f"SELECT doc->>'solarSystemID', doc->'position'->>'x', doc->'position'->>'y', doc->'position'->>'z' "
                          f"FROM sde.\"npcStations\" WHERE key = '{drop}'").split("|")
    reward_before = reward_total(character)
    seed_place(character, ship, step._replace(system=int(system), in_space=True))
    poses = f"{schema('SPACE')}.space_items"
    sql(f"UPDATE {poses} SET x = {float(x) + 20_000.0}, y = {float(y)}, z = {float(z)} WHERE item_id = {ship}")
    with base.connect(ACCOUNT) as who:
        who.call("charUnboundMgr", "SelectCharacterID", charID=character, secondChoiceID=None, skipTutorial=False)
        ballpark = who.call("beyonce", "MachoBindObject", int(system), None)
        wait_quietly(who, 2.0)
        # the ship approaches the station first: while it is on its way the dock answers DockingApproach (with the distance), and it is asked again
        for _ in range(60):
            try:
                who.call_bound(ballpark[0], "CmdDock", drop)
                break
            except packet.MachoError as refusal:
                if "DockingApproach" not in repr(refusal.payload):
                    raise AssertionError(f"mission {step.mission}: the dock at {drop} was refused: {describe(refusal.payload)}") from refusal
                wait_quietly(who, 5.0)
        else:
            raise AssertionError(f"mission {step.mission}: the ship did not reach {drop} to dock")
        deadline = time.time() + 240
        while completed(character, step.mission) == 0 and time.time() < deadline:
            timed_events(who, 3.0)
    assert completed(character, step.mission) == 1, f"mission {step.mission}: the delivery at {drop} did not complete the courier mission"
    assert held(character, step.objective) == 0, f"mission {step.mission}: the cargo was not taken at the drop-off"
    assert reward_total(character) - reward_before == step.reward, (step.mission, step.reward, reward_total(character) - reward_before)


def turn_in(character: int, ship: int, step: Step) -> None:
    """The fetch step's turn-in: the pilot talks to the step's agent with the objective items held; the step completes and the items go."""
    with session(character, ship, step, "turn-in") as who:
        reward_before = reward_total(character)
        said = do(who, step.agent, None)
    deadline = time.time() + 10
    while completed(character, step.mission) == 0 and time.time() < deadline:
        time.sleep(1)
    if completed(character, step.mission) != 1:
        journal = sql(f"SELECT mission_id, action, reference FROM {schema('AGENTS')}.journal WHERE character_id = {character} ORDER BY id")
        raise AssertionError(f"mission {step.mission}: the turn-in did not complete the step; agent said {said[:200]!r}; "
                             f"state {mission_state(character, step.agent)}; held {held(character, step.objective)}; journal {journal!r}")
    assert held(character, step.objective) == 0, f"mission {step.mission}: the objective items were not taken"
    assert reward_total(character) - reward_before == step.reward, (step.mission, reward_total(character) - reward_before)


def outcome(call):
    try:
        return call(), None
    except Exception as failure:  # the client's refusal, shown in the failure
        return None, str(failure)


def check_talk_done(character: int, step: Step) -> None:
    assert completed(character, step.mission) == 1, f"talk step {step.mission}: not completed by talking to its next agents {step.next_agents}"


def test_arc_is_walked_step_by_step_in_the_order_the_server_offers_it():
    PATH = arc_path(ARC)
    FROM = int(os.environ.get("ARC29_FROM", "0"))
    TO = int(os.environ.get("ARC29_TO", str(len(PATH))))
    seed_courier = os.environ.get("ARC29_SEED_COURIER") == "1"
    with base.connect(ACCOUNT) as client:
        character = new_pilot(client, uuid.uuid4().hex[:10])
    ship = ship_of(character)
    bomb = fit_smartbomb(character, ship)
    seed_agent_standings(character, PATH)
    for step in PATH[:FROM]:
        seed_completed(character, step)
    pending_talks: list = []
    for index in range(FROM, TO):
        step = PATH[index]
        started = time.time()
        if step.kind == "courier" and seed_courier:
            seed_completed(character, step)
            print(f"ARC29 step {index} mission {step.mission} courier: SEEDED completion (not walked)")
            continue
        # the fetch step before this one is handed in at its agent before the next step is offered
        if index > FROM and PATH[index - 1].objective is not None and PATH[index - 1].kind == "kill":
            turn_in(character, ship, PATH[index - 1])
        # a talk step completes when the pilot talks to one of the agents of the steps after it: the greeting in this session may do that
        with session(character, ship, step, "offer") as who:
            do(who, step.agent, None)
            for talk in [t for t in pending_talks if step.agent in t.next_agents]:
                check_talk_done(character, talk)
                pending_talks.remove(talk)
            offer(who, character, step)
            if step.kind == "courier":
                step = step._replace(drop_off=drop_off_of(who, step.agent, step.objective, step.quantity))
        if step.kind == "talk":
            pending_talks.append(step)
        if step.kind == "kill":
            fight(character, ship, step, bomb)
        if step.kind == "courier":
            deliver(character, ship, step, pickup_check=True)
        print(f"ARC29 step {index} mission {step.mission} {step.kind} ok in {time.time() - started:.0f} s")

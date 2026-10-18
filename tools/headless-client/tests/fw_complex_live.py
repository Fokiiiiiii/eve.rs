"""Live helpers of the factional warfare complex capture walk (slice 1, tests/test_fw_capture_walk.py).

The walk drives the client. These read the server's own state through the run's database (the schemas and EVE_PSQL_URL that
tools/dev/run-eve-live.sh exports) and the static data the server reads (EVE_SDE_DIR, the JSONL export of the pinned build). They do not write
game state: the enlistment and the flight are the client's calls, the complex is the server's.
"""

from __future__ import annotations

import collections
import json
import os
import subprocess
import time

GATE_ID_BASE = 8_000_000_000_000_000  # SpaceContainers.AccelerationGateIdBase (the id of the acceleration gate of slot 0)
ID_SLOTS_PER_INSTANCE = 256  # SpaceContainers.IdSlotsPerInstance
SCOUT = 1  # ComplexSize.Scout (the smallest complex: one room, one acceleration gate)
# the two warzones' factions (FwWarzones.cs): the occupier of a system fights the other faction of its warzone
WARZONE_FACTIONS = {1: (500003, 500002), 2: (500001, 500004)}  # Amarr, Minmatar / Caldari, Gallente


def psql(sql: str) -> str:
    return subprocess.run(["psql", os.environ["EVE_PSQL_URL"], "-At", "-c", sql], check=True, capture_output=True, text=True).stdout.strip()


def fw(table: str) -> str:
    return f"{os.environ['EVE_FACTIONWARFARE_SCHEMA']}.{table}"


def occupied_systems() -> dict[int, tuple[int, int]]:
    """Each occupied system of the server's factionwarfare state: solar system id -> (warzone id, occupier faction id)."""
    out = {}
    for line in psql(f"SELECT solar_system_id, warzone_id, occupier_id FROM {fw('fw_systems')}").splitlines():
        system, warzone, occupier = (int(value) for value in line.split("|"))
        out[system] = (warzone, occupier)
    return out


def enemy_of(warzone: int, occupier: int) -> int:
    """The occupier's enemy: the other faction of its warzone (FwFactions.OccupationEnemyOf)."""
    return next(faction for faction in WARZONE_FACTIONS[warzone] if faction != occupier)


def systems_with_open_scout() -> list[int]:
    """The systems that have an open scout complex (the complexes are spawned by the tick after the start; a site starts with a pilot in the system)."""
    return [int(value) for value in psql(f"SELECT DISTINCT solar_system_id FROM {fw('fw_complexes')} "
                                         f"WHERE size = {SCOUT} AND closed_ms IS NULL").split()]


def open_scout(system: int) -> int:
    """The open scout complex of a system: its complex id."""
    return int(psql(f"SELECT complex_id FROM {fw('fw_complexes')} WHERE solar_system_id = {system} AND size = {SCOUT} AND closed_ms IS NULL"))


def wait_for_site(complex_id: int, limit: float = 60.0) -> int:
    """The dungeon instance of a complex, once its site is started (the tick after a pilot is in the system); its gate is the instance's."""
    started = time.time()
    while True:
        instance = psql(f"SELECT COALESCE(instance_id, 0) FROM {fw('fw_complexes')} WHERE complex_id = {complex_id}")
        if int(instance) > 0:
            return int(instance)
        if time.time() - started > limit:
            raise AssertionError(f"complex {complex_id} has no site {int(limit)} s after a pilot came into its system")
        time.sleep(2.0)


def gate_of(instance: int, index: int = 0) -> int:
    """The acceleration gate of a dungeon instance (the server's AccelerationGateDeployableId: the instance's slot and the gate's index)."""
    return GATE_ID_BASE + instance * ID_SLOTS_PER_INSTANCE + index


def complex_progress(complex_id: int) -> tuple[int, int | None]:
    """The complex's capture timer in milliseconds (attackers positive) and its closing time (None while it is open)."""
    progress, closed = psql(f"SELECT progress_ms, COALESCE(closed_ms::text, '') FROM {fw('fw_complexes')} WHERE complex_id = {complex_id}").split("|")
    return int(progress), (int(closed) if closed else None)


def attacker_points(system: int) -> int:
    return int(psql(f"SELECT attacker_points FROM {fw('fw_systems')} WHERE solar_system_id = {system}"))


def route_to_nearest(start: int, targets: set[int], sde_dir: str) -> list[tuple[int, int, int]]:
    """The stargate hops from start to the nearest target system (breadth first over the SDE's stargates): (gate, the gate it leads to, the
    system it leads to) for each hop, the shape the walk's flight takes."""
    adjacency: dict[int, list[tuple[int, int, int]]] = collections.defaultdict(list)
    with open(os.path.join(sde_dir, "mapStargates.jsonl"), encoding="utf-8") as stream:
        for line in stream:
            gate = json.loads(line)
            adjacency[gate["solarSystemID"]].append((gate["_key"], gate["destination"]["stargateID"], gate["destination"]["solarSystemID"]))
    previous: dict[int, tuple[int, int, int] | None] = {start: None}
    queue = collections.deque([start])
    found = None
    while queue:
        system = queue.popleft()
        if system in targets and system != start:
            found = system
            break
        for gate, to_gate, to_system in adjacency[system]:
            if to_system not in previous:
                previous[to_system] = (system, gate, to_gate)
                queue.append(to_system)
    if found is None:
        raise AssertionError(f"no target system is reachable from {start} by stargates")
    hops = []
    system = found
    while previous[system] is not None:
        parent, gate, to_gate = previous[system]
        hops.append((gate, to_gate, system))
        system = parent
    return list(reversed(hops))


def deployed_record(deployable: int) -> str:
    """The server's deployed record of a deployable (the host keeps the gate of a room there while its dungeon runs): kind, system, or empty."""
    record = psql(f"SELECT kind || ' in ' || solar_system_id FROM {os.environ['EVE_SPACE_SCHEMA']}.deployed WHERE deployable_id = {deployable}")
    kinds = psql(f"SELECT kind || '=' || count(*) FROM {os.environ['EVE_SPACE_SCHEMA']}.deployed GROUP BY kind ORDER BY kind").replace("\n", ", ")
    return f"{record or 'none'}; by kind: {kinds or 'none'}"


def site_state(instance: int) -> str:
    """The dungeon site's state on the server: its content instance, the waves made and the NPCs of each (for a walk that needs its defenders)."""
    content = os.environ["EVE_CONTENT_SCHEMA"]
    space = os.environ["EVE_SPACE_SCHEMA"]
    state = psql(f"SELECT state || ' ' || COALESCE(result, '') FROM {content}.instances WHERE id = {instance}")
    waves = psql(f"SELECT wave_index || ':' || npc_count FROM {space}.dungeon_wave_spawns WHERE instance_id = {instance} ORDER BY wave_index")
    complex_row = psql(f"SELECT progress_ms || ' / updated ' || progress_updated_ms || ' / closed ' || COALESCE(closed_ms::text, '-') FROM {fw('fw_complexes')} WHERE instance_id = {instance}")
    now_ms = psql("SELECT (extract(epoch from now()) * 1000)::bigint")
    return f"instance {state!r}, waves {waves.split() or 'none'}, complex {complex_row}, now {now_ms}"



def open_battlefields() -> dict[int, tuple[int, int, int]]:
    """Each open battlefield of the server: solar system id -> (battlefield id, occupier faction id, site instance id)."""
    out = {}
    for line in psql(f"SELECT solar_system_id, battlefield_id, occupier_id, instance_id FROM {fw('fw_battlefields')} WHERE closed_ms IS NULL").splitlines():
        system, battlefield, occupier, instance = (int(value) for value in line.split("|"))
        out[system] = (battlefield, occupier, instance)
    return out


def wait_for_open_battlefields(limit: float = 180.0) -> dict[int, tuple[int, int, int]]:
    """The open battlefields, once the first tick after the start has spawned them (one per occupied system, at most two)."""
    started = time.time()
    while True:
        battlefields = open_battlefields()
        if battlefields or time.time() - started > limit:
            return battlefields
        time.sleep(5.0)


def wait_for_battlefield_site(battlefield: int, limit: float = 90.0) -> int:
    """The site instance of a battlefield, once its first pilot is in the system (the tick after the pilot comes in starts the site)."""
    started = time.time()
    while True:
        instance = int(psql(f"SELECT instance_id FROM {fw('fw_battlefields')} WHERE battlefield_id = {battlefield}"))
        if instance > 0:
            return instance
        if time.time() - started > limit:
            raise AssertionError(f"battlefield {battlefield} has no site {int(limit)} s after a pilot came into its system")
        time.sleep(2.0)


BATTLEFIELD_ID_BASE = 4_300_000_000_000_000  # FwBattlefields.IdBase
LP_EACH = 5_000  # FwBattlefields.LpEach (150,000 over the first 30 winners)


def point_deployable(battlefield: int, index: int) -> int:
    """The deployable id of a battlefield's capture point (FwBattlefields.PointId: the id base, four slots per battlefield, the point's index)."""
    return BATTLEFIELD_ID_BASE + battlefield * 4 + index


def scoreboard_deployable(battlefield: int) -> int:
    """The deployable id of a battlefield's scoreboard (FwBattlefields.ScoreboardId: the fourth slot of the battlefield)."""
    return BATTLEFIELD_ID_BASE + battlefield * 4 + 3


def battlefield_winner_rows(battlefield: int) -> list[tuple[int, int]]:
    """The pilots of a battlefield's winning side and their victory points on the daily rows (character id, victory points)."""
    rows = psql(f"SELECT p.character_id, COALESCE(SUM(d.victory_points), 0) FROM {fw('fw_battlefield_pilots')} p "
                f"LEFT JOIN {fw('fw_pilot_daily')} d ON d.character_id = p.character_id "
                f"JOIN {fw('fw_battlefields')} b ON b.battlefield_id = p.battlefield_id AND p.faction_id = b.winner_id "
                f"WHERE p.battlefield_id = {battlefield} GROUP BY p.character_id ORDER BY p.character_id")
    return [tuple(int(value) for value in line.split("|")) for line in rows.splitlines()]


def battlefield_lp(battlefield: int) -> list[tuple[int, int]]:
    """The loyalty points a battlefield credited in the wallet's journal (holder id, delta), by its reference."""
    rows = psql(f"SELECT holder_id, delta FROM {os.environ['EVE_WALLET_SCHEMA']}.lp_journal "
                f"WHERE reason = 'factionwarfare_battlefield' AND reference LIKE 'factionwarfare:battlefield:{battlefield}:%' ORDER BY id")
    return [tuple(int(value) for value in line.split("|")) for line in rows.splitlines()]


def battlefield_points(battlefield: int) -> list[tuple[int, int, int]]:
    """The three capture points of a battlefield: (point index, hold time in ms into the tick, holder faction id: 0 for none)."""
    rows = psql(f"SELECT point_index, hold_ms, holder_id FROM {fw('fw_battlefield_points')} WHERE battlefield_id = {battlefield} ORDER BY point_index")
    return [tuple(int(value) for value in line.split("|")) for line in rows.splitlines()]


def battlefield_row(battlefield: int) -> tuple[int, int | None, int | None]:
    """The battlefield's score and, once it is closed, its closing time and winner (None while open)."""
    score, closed, winner = psql(f"SELECT score, COALESCE(closed_ms::text, ''), COALESCE(winner_id::text, '') FROM {fw('fw_battlefields')} "
                                 f"WHERE battlefield_id = {battlefield}").split("|")
    return int(score), (int(closed) if closed else None), (int(winner) if winner else None)


SITE_OFFSET_METERS = 300_000  # FwBattlefields.SiteOffsetMeters: a battlefield's site point, along x from the complexes' point
SITE_CLEARANCE_METERS = 1_000_000_000  # FwComplexSites.SiteClearanceMeters: the site point is this far outside the star's surface
ROOM_ENTRY_OFFSET_METERS = 15_000  # DungeonRoomLayout.EntryOffsetMeters: a pilot arrives this far from the room's anchor (room 0: along +x)


def battlefield_entry(system: int) -> list[float]:
    """The point a pilot arrives at in a battlefield's room (its entry): the battlefield's origin moved 15 km along x (the SDE star of the system sets its height)."""
    radius = float(psql(f"SELECT (doc::jsonb ->> 'radius')::double precision FROM sde.\"mapStars\" WHERE doc::jsonb ->> 'solarSystemID' = '{system}'"))
    origin = [float(SITE_OFFSET_METERS), 0.0, radius + SITE_CLEARANCE_METERS]
    return [origin[0] + ROOM_ENTRY_OFFSET_METERS, origin[1], origin[2]]

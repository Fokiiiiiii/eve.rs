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


"""Seeding rows for live walks, straight into the scratch schemas that tools/dev/run-eve-live.sh creates (live_<pid>_<module>).

The runner exports EVE_<MODULE>_SCHEMA for each module and EVE_PG_URL. Rows go in with psql before the walk asks the server about them, so the
server answers from the database the way it does for any character; there is no server backdoor. A walk creates its characters through the
client's own calls first, then seeds (the character's ship, items, ISK), then selects the character.

    from tests import dbseed
    ship = dbseed.new_item(11019, owner=character, location=STATION, flag=HANGAR, singleton=True)
    dbseed.set_ship(character, ship)
    dbseed.set_isk(character, 5_000_000)
"""

from __future__ import annotations

import os
import subprocess

HANGAR = 4  # the client's inventory flag for a station hangar (the rows the server lists as flagID 4)
COCKROACH = 11019  # Cockroach, the frigate the trade walk flies


class SeedError(RuntimeError):
    pass


def _url() -> str:
    url = os.environ.get("EVE_PG_URL")
    if not url:
        raise SeedError("EVE_PG_URL is not set: run the walk through tools/dev/run-eve-live.sh")
    return url


def schema(module: str) -> str:
    name = os.environ.get(f"EVE_{module}_SCHEMA")
    if not name:
        raise SeedError(f"EVE_{module}_SCHEMA is not set: the run exports one schema per module")
    return name


def sql(statement: str) -> str:
    done = subprocess.run(["psql", _url(), "-At", "-v", "ON_ERROR_STOP=1", "-c", statement], capture_output=True, text=True, check=False)
    if done.returncode != 0:
        raise SeedError(f"seeding failed: {done.stderr.strip()}")
    # psql adds the command tag ("INSERT 0 1") after the rows; the rows are the answer
    return chr(10).join(line for line in done.stdout.splitlines() if not line.startswith(("INSERT ", "UPDATE ", "DELETE ")))


def new_item(type_id: int, *, owner: int, location: int, flag: int = HANGAR, singleton: bool = False, quantity: int = 1) -> int:
    """An item row (inventory.items) in a location; returns its id."""
    return int(sql(
        f"insert into {schema('INVENTORY')}.items (type_id, owner_id, location_id, flag, quantity, singleton) "
        f"values ({int(type_id)}, {int(owner)}, {int(location)}, {int(flag)}, {int(quantity)}, {'true' if singleton else 'false'}) returning id"))


def set_ship(character: int, ship: int) -> None:
    """The character's active ship (characters.ship_id)."""
    sql(f"update {schema('CHARACTERS')}.characters set ship_id = {int(ship)} where id = {int(character)}")


def set_isk(character: int, isk: int) -> None:
    """The character's ISK balance (wallet.balances)."""
    wallet = schema('WALLET')
    sql(f"insert into {wallet}.balances (owner_id, isk, plex) select {int(character)}, 0, 0 "
        f"where not exists (select 1 from {wallet}.balances where owner_id = {int(character)})")
    sql(f"update {wallet}.balances set isk = {int(isk)} where owner_id = {int(character)}")


def items_of(owner: int, type_id: int) -> list[int]:
    """The ids of an owner's items of a type, newest first."""
    rows = sql(f"select id from {schema('INVENTORY')}.items where owner_id = {int(owner)} and type_id = {int(type_id)} order by id desc")
    return [int(row) for row in rows.split() if row]

HIGH_SLOTS = range(27, 35)  # the client's flags for the high slots (HiSlot0 to HiSlot7); the SDE gives the smartbomb the hiPower effect
POLARIS, POLARIS_LEVEL5_POINTS = 9955, 128000  # the skill the QA Smartbomb requires to be fitted, at level 5 (as test_mission_walk)


def sde_type_id(name: str) -> int:
    """The type id of an SDE type by its English name (sde.types); unpublished QA modules are found this way too."""
    found = [int(row) for row in sql(f"select key from sde.types where doc->'name'->>'en' = '{name}'").split() if row]
    if len(found) != 1:
        raise SeedError(f"the SDE has {len(found)} types named {name!r}, expected one")
    return found[0]


def hi_slot_count(type_id: int) -> int:
    """The high slots of a ship type (its hiSlots attribute, id 14, in sde.typeDogma)."""
    rows = sql('select elem->>\'value\' from sde."typeDogma", jsonb_array_elements(doc->\'dogmaAttributes\') elem '
               f"where key::text = '{int(type_id)}' and elem->>'attributeID' = '14'")
    return int(float(rows))


def seed_qa_arsenal(ship: int, *, names: tuple = ("QA Smartbomb",)) -> list:
    """Fits the named SDE QA modules in every free high slot of the ship (a fixture: rows in inventory.items, plus the
    Polaris skill that fitting needs). The server refuses a second activation of one effect at a time, so the walks
    activate one module at a time. Returns the new module item ids."""
    owner = owner_of(ship)
    ship_type = int(sql(f"select type_id from {schema('INVENTORY')}.items where id = {int(ship)}"))
    slots = list(HIGH_SLOTS)[:hi_slot_count(ship_type)]
    sql(f"insert into {schema('SKILLS')}.character_skills (character_id, type_id, level, skill_points) "
        f"values ({owner}, {POLARIS}, 5, {POLARIS_LEVEL5_POINTS}) on conflict (character_id, type_id) "
        f"do update set level = 5, skill_points = {POLARIS_LEVEL5_POINTS}")
    used = {int(flag) for flag in sql(f"select flag from {schema('INVENTORY')}.items where location_id = {int(ship)}").split()}
    types = [sde_type_id(name) for name in names]
    return [new_item(types[n % len(types)], owner=owner, location=ship, flag=flag, singleton=True)
            for n, flag in enumerate(f for f in slots if f not in used)]


def owner_of(item: int) -> int:
    """The owner of an item row (inventory.items)."""
    return int(sql(f"select owner_id from {schema('INVENTORY')}.items where id = {int(item)}"))


def isk_of(character: int) -> float:
    """The ISK balance of a character (wallet.balances)."""
    return float(sql(f"select isk from {schema('WALLET')}.balances where owner_id = {int(character)}") or 0)


def corporation_of(character: int) -> int:
    """The corporation a character belongs to (characters.corporation_id)."""
    return int(sql(f"select corporation_id from {schema('CHARACTERS')}.characters where id = {int(character)}"))


def lock_item(item: int, *, corporation: int, type_id: int, location: int) -> None:
    """A corporation's item lock (corporations.item_locks), as a director's vote to lock a blueprint writes it."""
    sql(f"insert into {schema('CORPORATIONS')}.item_locks (item_id, corporation_id, type_id, location_id, locked_filetime) "
        f"values ({int(item)}, {int(corporation)}, {int(type_id)}, {int(location)}, 133000000000000000)")

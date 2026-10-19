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
import time

HANGAR = 4  # the client's inventory flag for a station hangar (the rows the server lists as flagID 4)
COCKROACH = 11019  # Cockroach, the frigate the trade walk flies

class SeedError(RuntimeError):
    pass

def _url() -> str:
    # psql takes the postgres:// form; the run script exports it as EVE_PSQL_URL when EVE_PG_URL is the Npgsql form
    url = os.environ.get("EVE_PSQL_URL") or os.environ.get("EVE_PG_URL")
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

QA_SMARTBOMB = 18642  # QA Smartbomb (SDE types row 18642): 5,000 of each damage type every 3 s, a 250 km field
HIGH_SLOTS = range(27, 35)  # the client's flags for the high slots (HiSlot0 to HiSlot7); the SDE gives the smartbomb the hiPower effect (12)
POLARIS, POLARIS_LEVEL5_POINTS = 9955, 128000  # the skill the QA Smartbomb requires to be fitted, at level 5 (as test_mission_walk)
SAFETY_NONE = 0  # the client's red safety (crimewatch.safety: 0 none, 1 partial, 2 full): a smartbomb in low, high or safe space is lit only here

def set_safety(character: int, level: int = SAFETY_NONE) -> None:
    """The character's ship safety level (crimewatch.safety, one row per character)."""
    sql(f"insert into {schema('CRIMEWATCH')}.safety (character_id, level, updated_ms) values ({int(character)}, {int(level)}, {int(time.time() * 1000)}) "
        f"on conflict (character_id) do update set level = excluded.level, updated_ms = excluded.updated_ms")

def seed_qa_arsenal(ship: int, type_id: int = QA_SMARTBOMB) -> list:
    """A QA module in every free high slot of the ship (as many as its hiSlots attribute); returns their item ids. Each one is its own module:
    the server runs them together (the QA Smartbomb has no maxGroupActive in the SDE) and refuses only the same module twice. The pilot's safety
    is set to none and the Polaris skill that fitting needs is given (both fixtures; the walks need them, not the server)."""
    owner = owner_of(ship)
    set_safety(owner)
    sql(f"insert into {schema('SKILLS')}.character_skills (character_id, type_id, level, skill_points) "
        f"values ({owner}, {POLARIS}, 5, {POLARIS_LEVEL5_POINTS}) on conflict (character_id, type_id) "
        f"do update set level = 5, skill_points = {POLARIS_LEVEL5_POINTS}")
    ship_type = int(sql(f"select type_id from {schema('INVENTORY')}.items where id = {int(ship)}"))
    slots = list(HIGH_SLOTS)[:hi_slot_count(ship_type)]
    used = {int(flag) for flag in sql(f"select flag from {schema('INVENTORY')}.items where location_id = {int(ship)}").split()}
    return [new_item(type_id, owner=owner, location=ship, flag=slot, singleton=True) for slot in slots if slot not in used]

ROLE_GML = 1 << 54  # the client's game master role (serviceConst ROLE_GML): the slash commands are refused to an account without a staff role

def grant_roles(user_name: str, roles: int = ROLE_GML) -> None:
    """The role bits of an account (identity.accounts.role, the same grant EVE_GM_ACCOUNTS makes). The bits are read at a login."""
    rows = sql(f"update {schema('IDENTITY')}.accounts set role = role | {int(roles)} where user_name = '{user_name}' returning id")
    if not rows:
        raise SeedError(f"no account {user_name!r} in {schema('IDENTITY')}: it is created by its first login")

def set_hull(ship: int, type_id: int) -> None:
    """A ship item's hull (inventory.items.type_id): the walks fly the Cockroach whatever hull the server gave the character."""
    rows = sql(f"update {schema('INVENTORY')}.items set type_id = {int(type_id)} where id = {int(ship)} returning id")
    if rows != str(int(ship)):
        raise SeedError(f"the ship {ship} was not found in {schema('INVENTORY')}")

def set_ship(character: int, ship: int) -> None:
    """The character's active ship (characters.ship_id)."""
    sql(f"update {schema('CHARACTERS')}.characters set ship_id = {int(ship)} where id = {int(character)}")

def set_isk(character: int, isk: int) -> None:
    """The character's ISK balance (wallet.balances)."""
    wallet = schema('WALLET')
    sql(f"insert into {wallet}.balances (owner_id, isk, plex) select {int(character)}, 0, 0 "
        f"where not exists (select 1 from {wallet}.balances where owner_id = {int(character)})")
    sql(f"update {wallet}.balances set isk = {int(isk)} where owner_id = {int(character)}")

def found_corporation(station: int, system: int, ceo: int, key: str) -> int:
    """A player corporation with the character as its chief executive (owner_names, corporations), as CorporationStore.CreateAsync writes it."""
    corporation = int(sql(f"select nextval('{schema('CORPORATIONS')}.corporation_ids')"))
    name = f"Industry {key}"
    sql(f"insert into {schema('CORPORATIONS')}.owner_names (owner_id, name_key, ticker_key) values ({corporation}, '{name.lower()}', '{key[:5]}')")
    sql(f"insert into {schema('CORPORATIONS')}.corporations (id, name, ticker, ceo_id, creator_id, station_id, solar_system_id, tax_rate, "
        f"lp_tax_rate, description, url, member_limit, created_ms, settings) values ({corporation}, '{name}', '{key[:5]}', {int(ceo)}, {int(ceo)}, "
        f"{int(station)}, {int(system)}, 0, 0, '', '', 100, {int(time.time() * 1000)}, '{{}}'::jsonb)")
    return corporation

def join_corporation(character: int, corporation: int, roles: int) -> None:
    """A member of the corporation with the given role bits (members, and the employment that goes with them)."""
    now = int(time.time() * 1000)
    sql(f"update {schema('CORPORATIONS')}.employment set ended_ms = {now} where character_id = {int(character)} and ended_ms is null")
    sql(f"insert into {schema('CORPORATIONS')}.employment (character_id, corporation_id, started_ms) values ({int(character)}, {int(corporation)}, {now})")
    sql(f"update {schema('CHARACTERS')}.characters set corporation_id = {int(corporation)} where id = {int(character)}")
    sql(f"insert into {schema('CORPORATIONS')}.members (character_id, corporation_id, started_ms, roles) values "
        f"({int(character)}, {int(corporation)}, {now}, {int(roles)}) on conflict (character_id) do update set "
        f"corporation_id = excluded.corporation_id, started_ms = excluded.started_ms, roles = excluded.roles")

def found_alliance(creator: int, corporation: int, key: str) -> int:
    alliance = int(sql(f"select nextval('{schema('CORPORATIONS')}.alliance_ids')"))
    now = int(time.time() * 1000)
    name = f"Walk Alliance {key}"
    ticker = f"w{key[:4]}"
    sql(f"insert into {schema('CORPORATIONS')}.owner_names (owner_id, name_key, ticker_key) "
        f"values ({alliance}, '{name.lower()}', '{ticker}')")
    sql(f"insert into {schema('CORPORATIONS')}.alliances "
        f"(id, name, short_name, description, url, executor_corporation_id, creator_corporation_id, "
        f"creator_character_id, created_ms, founding_key) values "
        f"({alliance}, '{name}', '{ticker}', '', '', {int(corporation)}, {int(corporation)}, "
        f"{int(creator)}, {now}, 'walk-{key}')")
    return alliance

def join_alliance(corporation: int, alliance: int, executor: int) -> None:
    now = int(time.time() * 1000)
    sql(f"update {schema('CORPORATIONS')}.alliance_employment set ended_ms = {now} "
        f"where corporation_id = {int(corporation)} and ended_ms is null")
    sql(f"insert into {schema('CORPORATIONS')}.alliance_members "
        f"(alliance_id, corporation_id, started_ms, chosen_executor_id) "
        f"values ({int(alliance)}, {int(corporation)}, {now}, {int(executor)})")
    sql(f"insert into {schema('CORPORATIONS')}.alliance_employment "
        f"(corporation_id, alliance_id, started_ms) values ({int(corporation)}, {int(alliance)}, {now})")
    sql(f"update {schema('CORPORATIONS')}.corporations set alliance_id = {int(alliance)} "
        f"where id = {int(corporation)}")
    sql(f"update {schema('CHARACTERS')}.characters set alliance_id = {int(alliance)} "
        f"where corporation_id = {int(corporation)}")

def fund_account(owner: int, isk: int, account_key: int = 1000) -> None:
    """The ISK of one of an owner's wallet accounts (a corporation's division: wallet.balances by account key)."""
    sql(f"insert into {schema('WALLET')}.balances (owner_id, isk, plex, account_key) values ({int(owner)}, {int(isk)}, 0, {int(account_key)}) "
        f"on conflict (owner_id, account_key) do update set isk = excluded.isk")

def account_of(owner: int, account_key: int = 1000) -> float:
    """The ISK of an owner's wallet account."""
    return float(sql(f"select isk from {schema('WALLET')}.balances where owner_id = {int(owner)} and account_key = {int(account_key)}") or 0)

def flag_of(item: int) -> int:
    """The flag of an item row (its hangar or corporation division)."""
    return int(sql(f"select flag from {schema('INVENTORY')}.items where id = {int(item)}"))

def items_of(owner: int, type_id: int) -> list[int]:
    """The ids of an owner's items of a type, newest first."""
    rows = sql(f"select id from {schema('INVENTORY')}.items where owner_id = {int(owner)} and type_id = {int(type_id)} order by id desc")
    return [int(row) for row in rows.split() if row]

def hi_slot_count(type_id: int) -> int:
    """The high slots of a ship type (its hiSlots attribute, id 14, in sde.typeDogma)."""
    rows = sql('select elem->>\'value\' from sde."typeDogma", jsonb_array_elements(doc->\'dogmaAttributes\') elem '
               f"where key::text = '{int(type_id)}' and elem->>'attributeID' = '14'")
    return int(float(rows))

def location_of(item: int) -> int:
    """The location of an item row (inventory.items): a station or structure id, or a ship's id."""
    return int(sql(f"select location_id from {schema('INVENTORY')}.items where id = {int(item)}"))

def owner_of(item: int) -> int:
    """The owner of an item row (inventory.items)."""
    return int(sql(f"select owner_id from {schema('INVENTORY')}.items where id = {int(item)}"))

def isk_of(character: int) -> float:
    """The ISK balance of a character's main wallet (wallet.balances, account 1000; the other divisions are separate rows)."""
    return float(sql(f"select isk from {schema('WALLET')}.balances where owner_id = {int(character)} and account_key = 1000") or 0)

def corporation_of(character: int) -> int:
    """The corporation a character belongs to (characters.corporation_id)."""
    return int(sql(f"select corporation_id from {schema('CHARACTERS')}.characters where id = {int(character)}"))

def lock_item(item: int, *, corporation: int, type_id: int, location: int) -> None:
    """A corporation's item lock (corporations.item_locks), as a director's vote to lock a blueprint writes it."""
    sql(f"insert into {schema('CORPORATIONS')}.item_locks (item_id, corporation_id, type_id, location_id, locked_filetime) "
        f"values ({int(item)}, {int(corporation)}, {int(type_id)}, {int(location)}, 133000000000000000)")

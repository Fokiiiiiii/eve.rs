"""Seeding and reads for live walks against the EVE.js reference server (EVE_LIVE_SERVER=reference).

EVE.js keeps its runtime state in an in-memory cache over SQLite (<data root>/gameStore/gamestore.sqlite), so a row written
into that file while the server runs is not seen by the server. The seeds therefore go through the server itself: the
game-master chat commands it implements for these fixtures, each a real authoritative write (/setisk: the wallet balance
through setCharacterBalanceAsync; /setstanding: standingRuntime.setCharacterStanding; /giveskill: the skill grant that
tests/test_agents_live.py already uses). Reads go to the SQLite file, which the server flushes two seconds after a write
(FLUSH_DELAY_MS in gameStore/index.js), so reads wait for that flush first.

Time: a research project accrues points per day from its start (researchRuntime.getAccruedPoints, which reads Date.now).
The reference runs with tools/dev/evejs-timeshift.js preloaded, which adds the milliseconds in EVE_TIMESHIFT_FILE to the
server clock; advance_clock() moves that offset forward, the equivalent of the eve.rs walk moving started_at_ms back.

Exported by tools/dev/run-evejs-live.sh: EVE_EVEJS_DATA_ROOT (the data root of the run) and EVE_TIMESHIFT_FILE.
"""

from __future__ import annotations

import json
import os
import sqlite3
import time

from evecli import observe
from evecli import packet as observe_p

FLUSH_WAIT_SECONDS = 3.0  # the store flushes 2 s after the last write (FLUSH_DELAY_MS); a little margin
US = "\x1f"  # the row-group separator of the SQLite backend (sqliteStore.js: group<US>entityID)

class SeedError(RuntimeError):
    pass

def _db_path() -> str:
    root = os.environ.get("EVE_EVEJS_DATA_ROOT")
    if not root:
        raise SeedError("EVE_EVEJS_DATA_ROOT is not set: run the walk through tools/dev/run-evejs-live.sh")
    return os.path.join(root, "gameStore", "gamestore.sqlite")

def _rows(table: str) -> list[tuple[str, str]]:
    uri = f"file:{_db_path()}?mode=ro"
    connection = sqlite3.connect(uri, uri=True)
    try:
        return connection.execute(f"SELECT key, json FROM \"{table}\"").fetchall()
    finally:
        connection.close()

def settle() -> None:
    """Wait until the server has flushed its last write to SQLite."""
    time.sleep(FLUSH_WAIT_SECONDS)

def command(client, text: str) -> None:
    """A game-master chat command, as the client sends it (the reply is chat text, not checked here)."""
    client.call("slash", "SlashCmd", text)

def set_isk(client, isk: int) -> None:
    """The selected character's wallet balance (/setisk, the absolute amount)."""
    command(client, f"/setisk {int(isk)}")

def set_standing(client, owner: int, value: float = 10) -> None:
    """The selected character's standing toward an owner id (an agent or a corporation), /setstanding <value> <owner id>."""
    command(client, f"/setstanding {value:g} {int(owner)}")

def agent_name(agent_id: int) -> str:
    """The English name of an NPC agent (npcCharacters.jsonl), the owner name /setstanding resolves for an agent."""
    return _sde_record("npcCharacters.jsonl", agent_id)["name"]["en"]

def set_agent_standing(client, agent_id: int, value: float = 10) -> None:
    """The selected character's personal standing toward an agent: /setstanding takes the agent by its name (a numeric id does not resolve)."""
    command(client, f'/setstanding {value:g} "{agent_name(agent_id)}"')

def give_skills(client, level: int = 5) -> None:
    """Every skill at the given level (/giveskill me all <level>), as the eve.rs walk gives them."""
    command(client, f"/giveskill me all {int(level)}")

def advance_clock(milliseconds: int) -> None:
    """Move the server clock forward by milliseconds (the offset file the preload reads)."""
    path = os.environ.get("EVE_TIMESHIFT_FILE")
    if not path:
        raise SeedError("EVE_TIMESHIFT_FILE is not set: run the walk through tools/dev/run-evejs-live.sh")
    current = 0
    if os.path.exists(path):
        with open(path, encoding="utf8") as handle:
            current = int(handle.read().strip() or 0)
    with open(path, "w", encoding="utf8") as handle:
        handle.write(str(current + int(milliseconds)))
    time.sleep(0.5)  # the preload re-reads the file at most every 100 ms

def _cash_in(value, character: int):
    """The balance of an OnAccountChange('cash', characterID, balance) found anywhere in a decoded notification."""
    if isinstance(value, dict):
        value = list(value.values())
    if isinstance(value, (list, tuple)):
        if len(value) == 3 and value[0] == "cash" and value[1] == int(character):
            return float(value[2])
        for item in value:
            found = _cash_in(item, character)
            if found is not None:
                return found
    return None

def cash_balance(client, character: int) -> float:
    """The cash the client last heard for a character: the OnAccountChange balances the server sent it, the client's own wallet view.

    The reference keeps the balance in its wallet process, which the SQLite file does not show at once; the notification is
    what the client itself reads. Notifications are not consumed: the walk still reads them with notifications_of."""
    client.ping()
    balance = 0.0
    for packet in client.notifications:
        if packet.command != observe_p.NOTIFICATION:
            continue
        event = observe._notification_event(packet)
        if event.get("name") != "OnAccountChange":
            continue
        found = _cash_in(event.get("args"), character)
        if found is not None:
            balance = found
    return balance

def isk_of(character: int) -> float:
    """The wallet balance of a character (characters row, json.balance), after the flush."""
    settle()
    for key, document in _rows("characters"):
        if key == str(int(character)):
            return float(json.loads(document).get("balance") or 0)
    return 0.0

def research_project_count(character: int, until: int | None = None) -> int:
    """The R&D projects a character runs (researchRuntimeState: projectsByAgentID of its character entry).

    The store flushes two seconds after a write, so the read polls: it returns as soon as the count is `until` (when given),
    and otherwise after the flush window."""
    deadline = time.monotonic() + FLUSH_WAIT_SECONDS * 4
    count = _project_count_now(character)
    while until is not None and count != until and time.monotonic() < deadline:
        time.sleep(0.5)
        count = _project_count_now(character)
    if until is None:
        settle()
        count = _project_count_now(character)
    return count

def _project_count_now(character: int) -> int:
    count = 0
    for key, document in _rows("researchRuntimeState"):
        value = json.loads(document)
        if not isinstance(value, dict):
            continue
        entries = value.get("charactersByID", {})
        if key.startswith(f"charactersByID{US}"):
            entries = {key.split(US, 1)[1]: value}
        entry = entries.get(str(int(character))) if isinstance(entries, dict) else None
        if isinstance(entry, dict):
            count += len(entry.get("projectsByAgentID") or {})
    return count

def _sde_record(file_name: str, key: int) -> dict:
    sde = os.environ.get("EVE_SDE_DIR")
    if not sde:
        raise SeedError("EVE_SDE_DIR is not set: run the walk through tools/dev/run-evejs-live.sh")
    with open(os.path.join(sde, file_name), encoding="utf8") as handle:
        for line in handle:
            record = json.loads(line)
            if int(record.get("_key", 0)) == int(key):
                return record
    raise SeedError(f"{key} is not in {file_name}")

def research_skill_count(agent_id: int) -> int:
    """The distinct skills an R&D agent offers in the SDE (npcCharacters.jsonl: skills of its record)."""
    return len({int(skill["typeID"]) for skill in _sde_record("npcCharacters.jsonl", agent_id).get("skills", [])})

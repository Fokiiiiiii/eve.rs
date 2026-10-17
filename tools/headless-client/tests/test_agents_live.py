"""The agents' dialogs against a running server: the LP store purchase, the research walk, and the locator walk.

    EVE_LIVE_HOST=host:port EVE_PG_URL=postgres://... pytest tests/test_agents_live.py

A new account enters a new character (as in test_store_entitlements_live). The walks need state the game gives over
time or through a store, so the test seeds it in the run's scratch PostgreSQL schemas (the EVE_*_SCHEMA variables the live
script exports): ISK and LP balances, one store offer, the standings the agents' access rule reads. Skills come from the
game master's /giveskill. Each dialog the server raises is answered by the client (`client.answer`), so the test checks what
the client reads back. Skipped unless EVE_LIVE_HOST and EVE_PG_URL are set.
"""

import os
import re
import subprocess
import time
import uuid

import pytest

from evecli import marshal as m
from evecli.observe import notifications_of

from tests.test_login_to_character import WALK, connect, revive

LIVE = os.environ.get("EVE_LIVE_HOST", "")
PG_URL = os.environ.get("EVE_PG_URL", "")
pytestmark = pytest.mark.skipif(not (LIVE and PG_URL), reason="set EVE_LIVE_HOST=host:port and EVE_PG_URL to test against a running server")

STORE_CORPORATION = 1000125  # an NPC corporation: the store the offer belongs to
OFFER = 990001
TRITANIUM = 34
RESEARCH_AGENT_TYPE = 4
COCKROACH = 11019  # the GM frigate (the pinned SDE's "Cockroach"): the standard live-walk ship
START_RESEARCH, CANCEL_RESEARCH, BUY_DATACORES = 830, 831, 832  # the action tokens (AgentDialogueToken)
REQUEST, ACCEPT, DECLINE = 376, 377, 378
DAY_MS = 86_400_000


def new_pilot(client, run: str) -> int:
    """A new character (the creation of login_to_character.json) with the Cockroach as its active ship, then selected.

    The session's shipid is not sent at selection: the server defers a docked ship until the client's dogma bootstrap
    (CharUnboundMgrService / SpaceSessions deferredDockedShipId, not a client-visible attribute). So the ship is checked in
    the database, not in the session."""
    creation = next(step for step in WALK["steps"] if step["call"][1] == "CreateCharacterWithDoll" and "ok" in step)
    service, method, described = creation["call"]
    character_id = m.plain(client.call(service, method, *[revive(value, run) for value in described]))
    seed_ship(character_id)
    client.notifications.clear()
    client.call("charUnboundMgr", "SelectCharacterID", charID=character_id, secondChoiceID=None, skipTutorial=False)
    # the pilot's active ship is the Cockroach, in the station hangar
    ship = int(psql(f"SELECT ship_id FROM {schema('EVE_CHARACTERS_SCHEMA')}.characters WHERE id = {character_id}"))
    assert int(psql(f"SELECT type_id FROM {schema('EVE_INVENTORY_SCHEMA')}.items WHERE id = {ship} AND flag = 4")) == COCKROACH
    return character_id


def seed_ship(character_id: int) -> int:
    """A Cockroach in the pilot's station hangar (flag 4, singleton), made the active ship through the characters row. The pilot's
    safety is set to none (the red level), which a smartbomb needs in low, high and safe space."""
    psql(f"INSERT INTO {schema('EVE_CRIMEWATCH_SCHEMA')}.safety (character_id, level, updated_ms) VALUES ({character_id}, 0, {int(time.time() * 1000)}) "
         f"ON CONFLICT (character_id) DO UPDATE SET level = 0, updated_ms = EXCLUDED.updated_ms")
    items, characters = f"{schema('EVE_INVENTORY_SCHEMA')}.items", f"{schema('EVE_CHARACTERS_SCHEMA')}.characters"
    psql(f"WITH ship AS (INSERT INTO {items} (type_id, owner_id, location_id, flag, quantity, singleton) "
         f"SELECT {COCKROACH}, {character_id}, station_id, 4, 1, true FROM {characters} WHERE id = {character_id} RETURNING id) "
         f"UPDATE {characters} SET ship_id = (SELECT id FROM ship) WHERE id = {character_id}")
    return int(psql(f"SELECT ship_id FROM {characters} WHERE id = {character_id}"))


def psql(statement: str) -> str:
    done = subprocess.run(["psql", PG_URL, "-v", "ON_ERROR_STOP=1", "-tAqc", statement], capture_output=True, text=True)
    assert done.returncode == 0, f"{statement}\n{done.stderr}"
    return done.stdout.strip()


def schema(name: str) -> str:
    return os.environ[name]


def isk_of(character_id: int) -> float:
    return float(psql(f"SELECT isk FROM {schema('EVE_WALLET_SCHEMA')}.balances WHERE owner_id = {character_id}"))


# the seeds update the row or insert it: the scratch schemas do not all carry the unique keys an upsert needs
def seed_isk(character_id: int, amount: int) -> None:
    table = f"{schema('EVE_WALLET_SCHEMA')}.balances"
    psql(f"WITH u AS (UPDATE {table} SET isk = isk + {amount} WHERE owner_id = {character_id} RETURNING 1) "
         f"INSERT INTO {table} (owner_id, isk, plex) SELECT {character_id}, {amount}, 0 WHERE NOT EXISTS (SELECT 1 FROM u)")


def seed_loyalty_points(character_id: int, corporation_id: int, amount: int) -> None:
    table = f"{schema('EVE_WALLET_SCHEMA')}.lp_balances"
    psql(f"WITH u AS (UPDATE {table} SET amount = {amount} WHERE holder_kind = 'character' AND holder_id = {character_id} "
         f"AND issuer_corp_id = {corporation_id} RETURNING 1) INSERT INTO {table} (holder_kind, holder_id, issuer_corp_id, amount) "
         f"SELECT 'character', {character_id}, {corporation_id}, {amount} WHERE NOT EXISTS (SELECT 1 FROM u)")


def seed_offer(corporation_id: int, offer_id: int, type_id: int, lp_cost: int, isk_cost: int) -> None:
    table = f"{schema('EVE_WALLET_SCHEMA')}.lp_store_offers"
    psql(f"INSERT INTO {table} (corporation_id, offer_id, type_id, quantity, lp_cost, isk_cost) "
         f"SELECT {corporation_id}, {offer_id}, {type_id}, 1, {lp_cost}, {isk_cost} "
         f"WHERE NOT EXISTS (SELECT 1 FROM {table} WHERE corporation_id = {corporation_id} AND offer_id = {offer_id})")


def seed_standing(subject: int, character_id: int, value: float = 10) -> None:
    """A standing toward an agent or its corporation (the relations rows the agents' access rule reads)."""
    table = f"{schema('EVE_RELATIONS_SCHEMA')}.relations"
    psql(f"WITH u AS (UPDATE {table} SET value = {value} WHERE kind = 'standing' AND subject = {subject} AND object = {character_id} "
         f"RETURNING 1) INSERT INTO {table} (kind, subject, object, value, since_ms, updated_ms) "
         f"SELECT 'standing', {subject}, {character_id}, {value}, 0, 0 WHERE NOT EXISTS (SELECT 1 FROM u)")


def agents(client) -> list:
    """The agent rows: a rowset (name, {"header", "lines"}), each line a dict by column name."""
    _, rowset = m.plain(client.call("agentMgr", "GetAgents"))
    names = [str(name) for name in rowset["header"]]
    return [dict(zip(names, line)) for line in rowset["lines"]]


def research_skill_count(agent_id: int) -> int:
    """The distinct skills an R&D agent offers in the SDE (npcCharacters.skills of its agentTypeID 4 record)."""
    return int(psql(f"SELECT count(DISTINCT s->>'typeID') FROM sde.\"npcCharacters\", jsonb_array_elements(doc->'skills') s WHERE key = {agent_id}"))


def character_name(character_id: int) -> str:
    return psql(f"SELECT name FROM {schema('EVE_CHARACTERS_SCHEMA')}.characters WHERE id = {character_id}")


def names(client) -> set:
    return {event["name"] for event in notifications_of(client)}


def do_action(client, handle, action: int):
    """DoAction: ((agentSays, actions), lastActionInfo). Returns (says, [(action, button), ...], lastActionInfo)."""
    reply = m.plain(client.call_bound(handle, "DoAction", action))
    says, actions = reply[0]
    return says, [tuple(pair) for pair in actions], reply[1]


def says_label(says) -> str:
    message = says[0]
    return message[0] if isinstance(message, (tuple, list)) else str(message)


def run_id() -> str:
    return uuid.uuid4().hex[:10]


def test_lp_store_purchase_takes_lp_and_isk_and_grants_the_item():
    wallet = schema("EVE_WALLET_SCHEMA")
    with connect(f"lp-{run_id()}") as client:
        character_id = new_pilot(client, run_id())
        seed_isk(character_id, 1_000_000)
        seed_loyalty_points(character_id, STORE_CORPORATION, 5000)
        seed_offer(STORE_CORPORATION, OFFER, TRITANIUM, lp_cost=100, isk_cost=1000)
        isk_before = isk_of(character_id)
        client.notifications.clear()

        assert m.plain(client.call("LPStoreMgr", "TakeOfferForCharacter", STORE_CORPORATION, OFFER, 1)) is True

        assert isk_before - isk_of(character_id) == 1000
        lp = psql(f"SELECT amount FROM {wallet}.lp_balances WHERE holder_id = {character_id} AND issuer_corp_id = {STORE_CORPORATION}")
        assert int(lp) == 4900
        assert "OnItemChange" in names(client)


def test_research_start_cancel_and_datacores_over_the_dialogs():
    with connect(f"rnd-{run_id()}") as client:
        character_id = new_pilot(client, run_id())
        client.call("slash", "SlashCmd", "/giveskill me all 5")
        seed_isk(character_id, 1_000_000)
        agent = next(row for row in agents(client) if row["agentTypeID"] == RESEARCH_AGENT_TYPE and not row["isInSpace"])
        seed_standing(agent["corporationID"], character_id)
        seed_standing(agent["agentID"], character_id)
        handle = client.call("agentMgr", "MachoBindObject", agent["agentID"])

        # start: the client picks the first field, and the project runs
        client.answer("SingleChoiceBox", (True, "radioboxOption1Selected"))
        says, actions, _ = do_action(client, handle, START_RESEARCH)
        assert (BUY_DATACORES, 14) in actions and (CANCEL_RESEARCH, 13) in actions, (says_label(says), actions, client.dialogs)
        assert "ResearchStarted" in says_label(says), (says_label(says), client.dialogs)
        # the field list is the agent's own skills (npcCharacters.skills of the SDE), one choice each: all five by /giveskill
        choices = [args for method, args in client.dialogs if method == "SingleChoiceBox"][-1][2]
        assert len(choices) == research_skill_count(agent["agentID"]), (len(choices), client.dialogs)

        # the points accrue: two days in, a datacore costs 100 RP and 10,000 ISK, and the affordable quantity is asked
        psql(f"UPDATE {schema('EVE_AGENTS_SCHEMA')}.research_projects SET started_at_ms = started_at_ms - {2 * DAY_MS} "
             f"WHERE character_id = {character_id}")
        isk_before = isk_of(character_id)
        client.answer("GetQuantity", 1)
        says, actions, _ = do_action(client, handle, BUY_DATACORES)
        assert "BoughtDatacores" in says_label(says)
        assert isk_before - isk_of(character_id) == 10_000

        # cancel: a yes ends the project and the idle buttons offer to start again
        client.answer("YesNo", True)
        says, actions, _ = do_action(client, handle, CANCEL_RESEARCH)
        assert "ResearchCancelled" in says_label(says)
        assert (START_RESEARCH, 12) in actions
        assert psql(f"SELECT count(*) FROM {schema('EVE_AGENTS_SCHEMA')}.research_projects WHERE character_id = {character_id}") == "0"


def test_locator_locates_a_second_pilot_charges_the_fee_and_tells_the_requester():
    run = run_id()
    with connect(f"loc-{run}") as requester, connect(f"target-{run}") as target:
        character_id = new_pilot(requester, run)
        target_id = new_pilot(target, f"t{run}")
        seed_isk(character_id, 1_000_000)
        locator = next(row for row in agents(requester) if row["isLocatorAgent"] and row["level"] >= 3 and not row["isInSpace"])
        seed_standing(locator["corporationID"], character_id)
        seed_standing(locator["agentID"], character_id)
        handle = requester.call("agentMgr", "MachoBindObject", locator["agentID"])
        requester.notifications.clear()

        # the name is asked; the offer names the fee; accept takes it and sends the result
        requester.answer("RemoteNamePopup", character_name(target_id))
        says, actions, _ = do_action(requester, handle, 15)
        assert re.search(r"for ([\d,]+) ISK", says_label(says)), (says_label(says), actions, requester.dialogs)
        fee = int(re.search(r"for ([\d,]+) ISK", says_label(says)).group(1).replace(",", ""))
        assert (16, 16) in actions and (17, 17) in actions
        isk_before = isk_of(character_id)

        do_action(requester, handle, 16)
        assert isk_before - isk_of(character_id) == fee

        # the located notification reaches the requester through the event outbox (a few moments after the commit)
        seen = set()
        deadline = time.monotonic() + 20
        while time.monotonic() < deadline and "OnNotificationReceived" not in seen:
            seen |= names(requester)
            time.sleep(0.5)
        assert "OnNotificationReceived" in seen


def test_agent_decline_asks_first_and_a_yes_declines_the_offer():
    with connect(f"dec-{run_id()}") as client:
        character_id = new_pilot(client, run_id())
        # the first agent that offers a mission to this pilot (only the epic-arc agents offer any in this build)
        for agent in agents(client):
            if agent["agentTypeID"] == RESEARCH_AGENT_TYPE or agent["isLocatorAgent"] or agent["isInSpace"]:
                continue
            seed_standing(agent["corporationID"], character_id)
            seed_standing(agent["agentID"], character_id)
            handle = client.call("agentMgr", "MachoBindObject", agent["agentID"])
            _, actions, _ = do_action(client, handle, REQUEST)
            if (ACCEPT, 3) in actions:
                break
        else:
            pytest.skip("no agent offers a mission to a new pilot on this server")

        # decline asks the client first; a no keeps the offer, a yes declines it
        client.answer("YesNo", False)
        _, actions, info = do_action(client, handle, DECLINE)
        assert (ACCEPT, 3) in actions and not info.get("missionDeclined")
        client.answer("YesNo", True)
        _, actions, info = do_action(client, handle, DECLINE)
        assert info.get("missionDeclined") is True

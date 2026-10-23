"""A pilot kills another pilot in a low-security system, and the kill is claimed and paid (CH-18, live).

    EVE_LIVE_HOST=host:port EVE_LIVE_SLOW=1 EVE_PG_URL=... EVE_RUN='pytest tests/test_player_kill_live.py' tools/dev/run-eve-live.sh

Two pilots are made from the death walk's recorded race and their location is seeded to Futzchag (30000017, security 0.17, with an NPC
station): no CONCORD answers there, so the shots are the pilots' own. The victim flies the SDE Rifter (type 587, base price 400,000 ISK),
which gives the kill a priced loss. A third pilot places its bounty (`bountyProxy.AddToBounty`, 2,000,000 ISK). The victim's ship is damaged
through the host's `/dmg heavy` repair-test command before undocking.
The hunter is given the skills for a real 125mm Railgun I, loaded with Antimatter Charge S, and closes on the stopped victim during its
undocking protection; the hunter stops after locking and before activation and shots. The kill is then claimed
(EVE University wiki: a fifth of the loss, paid from the victim's pool): the payout `kill:<ship>`
is pending for the hunter, its job credits the hunter's wallet once, and the victim's pool is drawn down by exactly the payout.

Skipped unless EVE_LIVE_HOST, EVE_PG_URL and EVE_LIVE_SLOW=1 are set (the kill needs the simulation).
"""

from __future__ import annotations

import os
import re
import time
import uuid

import pytest

from evecli import marshal as m
from evecli import packet as p
from evecli.describe import describe
from evecli.observe import wait_quietly
from tests import dbseed
from tests import test_login_to_character as base
from tests.test_agents_live import new_pilot
from tests.test_death import DEATH
from tests.test_others import watch
from tests.test_target import plain_race
from tests.runid import run_token
from tests.test_undock import STATION, args_of, keyval, number

LIVE = os.environ.get("EVE_LIVE_HOST", "")
PG_URL = os.environ.get("EVE_PG_URL", "")
pytestmark = [
    pytest.mark.skipif(not (LIVE and PG_URL), reason="set EVE_LIVE_HOST=host:port and EVE_PG_URL to test against a running server"),
    pytest.mark.skipif(os.environ.get("EVE_LIVE_SLOW") != "1", reason="the kill needs the simulation: set EVE_LIVE_SLOW=1"),
]

# Futzchag: a low-security system (0.17) with an NPC station (sde.npcStations), its constellation and region (sde.mapSolarSystems)
LOW_STATION, LOW_SYSTEM, LOW_CONSTELLATION, LOW_REGION = 60014071, 30000017, 20000003, 10000001
PLACED = 2_000_000
SAFETY_NONE = 0  # the client's red safety: the pilots may shoot each other in low-security space
RAILGUN, ANTIMATTER, MINER = 10678, 222, 3651
RIFTER = 587


def arrive_low(who, race: dict, run: str, *, hull_type_id: int | None = None) -> tuple[int, int, list]:
    """test_undock.arrive, but the character is seeded to the low-security station before it is selected: `(character, ship id, fitted modules)`."""
    recorded_run = re.search(r"(\d{6,})$", race["create"]["call"][2][0]).group(1)
    service, method, *args = [race["create"]["call"][0], race["create"]["call"][1],
                              *[base.revive(a, run, {}, recorded_run) for a in race["create"]["call"][2]]]
    kind, made = base.attempt(who, service, method, *args)
    assert kind == "ok", base.brief(made)
    character = int(made)
    dbseed.sql(f"update {dbseed.schema('CHARACTERS')}.characters set station_id = {LOW_STATION}, solar_system_id = {LOW_SYSTEM}, "
               f"constellation_id = {LOW_CONSTELLATION}, region_id = {LOW_REGION} where id = {character}")
    dbseed.sql(f"update {dbseed.schema('INVENTORY')}.items set location_id = {LOW_STATION} where owner_id = {character} and location_id = {STATION}")
    if hull_type_id is not None:
        ship_id = int(dbseed.sql(f"select ship_id from {dbseed.schema('CHARACTERS')}.characters where id = {character}"))
        dbseed.set_hull(ship_id, hull_type_id)
    dbseed.set_safety(character, SAFETY_NONE)
    assert base.attempt(who, "charUnboundMgr", "SelectCharacterID", character, None, False) == ("ok", None)
    manager = who.call("invbroker", "MachoBindObject", (LOW_STATION, 15), None)[0]
    hangar = who.call_bound(manager, "GetInventory", 10004)
    ship_id = int(who.call_bound(hangar, "List")[0].fields()["itemID"])
    info = who.call("dogmaIM", "MachoBindObject", (LOW_STATION, 15), ("GetAllInfo", (True, True, None), None))
    fitted = [number(k) for k, _ in keyval(describe(info[1]))["shipInfo"]["$dict"]][1:]
    wait_quietly(who, 1.0)
    return character, ship_id, fitted


def undock_low(who, ship_id: int, *, stop: bool = False, follow: int | None = None) -> tuple[list, list]:
    """Undocks the ship at the low-security station and binds the system: `(ballpark, dogma)` bound objects."""
    service = who.call("ship", "MachoBindObject", (LOW_STATION, 15), None)
    who.call_bound(service[0], "Undock", ship_id, False)
    ballpark = who.call("beyonce", "MachoBindObject", LOW_SYSTEM, None)
    if follow is not None:
        # Move into railgun range while the hunter has its thirty-second undocking protection.
        who.call_bound(ballpark[0], "CmdFollowBall", follow, 1000)
    watch({"events": who}, 3.0)
    dogma = who.call("dogmaIM", "MachoBindObject", (LOW_SYSTEM, 5), None)
    # the undocking protection ends (an invulnerable ship cannot be shot)
    watch({"events": who}, 45.0, until=lambda seen: any(e["name"] == "OnInvulnCancelled" for e in seen["events"]))
    if stop:
        # Two rookie ships undock at the same maximum speed; the hunter cannot close on a victim that keeps that speed.
        who.call_bound(ballpark[0], "CmdStop")
        watch({"events": who}, 20.0)
    return ballpark, dogma


def fit_hunter_railgun(who, ship_id: int) -> int:
    """Fit and load a real small railgun, with the pilot's actual skills applied by dogma."""
    for skill in ("Gunnery", "Small Hybrid Turret", "Motion Prediction", "Rapid Firing", "Surgical Strike"):
        reply = m.plain(who.call("slash", "SlashCmd", f"/giveskill me {skill} 5"))
        assert isinstance(reply, str) and " to level V." in reply, (skill, reply)

    unfit = m.plain(who.call("slash", "SlashCmd", f"/unfit me {MINER}"))
    assert isinstance(unfit, str) and unfit.startswith("Unfitted"), unfit
    fitted = m.plain(who.call("slash", "SlashCmd", f"/fit me {RAILGUN}"))
    assert isinstance(fitted, str) and fitted.startswith("Created and fitted"), fitted
    given = m.plain(who.call("slash", "SlashCmd", f"/giveitem {ANTIMATTER} 500"))
    assert isinstance(given, str) and "was added to your station hangar" in given, given

    docked_dogma = who.call("dogmaIM", "MachoBindObject", (LOW_STATION, 15), None)
    info = describe(who.call_bound(docked_dogma[0], "GetAllInfo", True, True, None))
    ship_info = dict(info["args"]["$dict"])["shipInfo"]["$dict"]
    gun = None
    for key, value in ship_info:
        if isinstance(key, dict) and "$tuple" in key:
            continue
        row = dict(value["args"]["$dict"])["invItem"]["$row"]
        if row["typeID"] == RAILGUN:
            gun = int(key["$long"] if isinstance(key, dict) else key)
            break
    assert gun is not None, "the 125mm Railgun I is not fitted to the hunter's ship"

    manager = who.call("invbroker", "MachoBindObject", (LOW_STATION, 15), None)[0]
    hangar = who.call_bound(manager, "GetInventory", 10004)
    rows = who.call_bound(hangar, "List")
    ammo = next((int(row.fields()["itemID"]) for row in rows if int(row.fields()["typeID"]) == ANTIMATTER), None)
    assert ammo is not None, "the Antimatter Charge S was not in the hunter's hangar"
    who.call_bound(docked_dogma[0], "LoadAmmo", ship_id, m.List([gun]), m.List([ammo]), LOW_STATION)
    wait_quietly(who, 1.0)
    return gun


def shoot_pilot_until_dead(who, ballpark, dogma, gun: int, target: int, seconds: float = 180.0) -> list:
    deadline = time.monotonic() + seconds
    events: list = []

    def locked() -> bool:
        return target in [t if not isinstance(t, dict) else t["$long"]
                          for t in map(lambda value: getattr(value, "value", value), who.call_bound(dogma[0], "GetTargets"))]

    refusal = None
    while not locked():
        try:
            who.call_bound(dogma[0], "AddTarget", target)
        except p.MachoError as error:
            refusal = error
        events.extend(watch({"events": who}, 2.0)["events"])
        assert time.monotonic() < deadline, (f"the victim was never locked; refused: {refusal!r}", sorted({e["name"] for e in events}))

    # The victim and hunter have both stopped in range. Keep them stationary so the real turret roll has no angular or falloff penalty.
    who.call_bound(ballpark[0], "CmdStop")
    watch({"events": who}, 1.0)
    try:
        who.call_bound(dogma[0], "Activate", gun, "targetAttack", target, 1000)
    except p.MachoError as error:
        raise AssertionError(f"the loaded railgun was refused against the victim: {error!r}") from error

    while time.monotonic() < deadline:
        events.extend(watch({"events": who}, 2.0)["events"])
        if any("RemoveBalls" in str(event) and str(target) in str(event) for event in events):
            break
    damage = []
    for event in events:
        if event["name"] != "OnDamageMessage":
            continue
        fields = dict(args_of(event)["$tuple"][0]["$dict"])
        if number(fields["target"]) == target and float(fields["damage"]) > 0 and int(fields["hitQuality"]) > 0:
            damage.append(fields)
    assert damage, "the client received no successful turret hit against the victim"
    assert any("RemoveBalls" in str(event) and str(target) in str(event) for event in events), (
        f"the victim was not destroyed after {len(damage)} successful hits; told {sorted({e['name'] for e in events})}"
    )
    return events


def wallet(character: int) -> float:
    return dbseed.isk_of(character)


def pool(target: int) -> float:
    return float(dbseed.sql(f"select bounty from {dbseed.schema('CRIMEWATCH')}.player_bounty_pools where target_id = {int(target)}") or 0)


def test_a_pilot_that_shoots_a_pilot_dead_in_low_security_has_the_kill_claimed_and_paid_to_it():
    race = plain_race(DEATH["races"][0], "victim")
    run = uuid.uuid4().hex[:10]
    with base.connect(f"kill-victim-{run}") as victim_client, base.connect(f"kill-hunter-{run}") as hunter_client:
        # each pilot's name comes from its own run token (the recorded race fixes the name's base)
        victim, victim_ship, _ = arrive_low(victim_client, race, run_token(), hull_type_id=RIFTER)
        hunter, hunter_ship, _ = arrive_low(hunter_client, race, run_token())
        assert victim != hunter
        damaged = m.plain(victim_client.call("slash", "SlashCmd", "/dmg heavy"))
        assert isinstance(damaged, str) and damaged.startswith("Applied heavy test damage to your active ship"), damaged
        victim_ballpark, _ = undock_low(victim_client, victim_ship, stop=True)
        gun = fit_hunter_railgun(hunter_client, hunter_ship)
        hunter_ballpark, hunter_dogma = undock_low(hunter_client, hunter_ship, follow=victim_ship)

        with base.connect(f"kill-placer-{run}") as placer_client:
            placer = new_pilot(placer_client, f"{run}p")
            dbseed.set_isk(placer, 5_000_000)
            name, bounty = m.plain(placer_client.call("bountyProxy", "AddToBounty", victim, PLACED))
            assert name == "util.KeyVal" and bounty["bounty"] == PLACED
        notification_rows = m.plain(victim_client.call("notificationMgr", "GetAllNotifications"))
        placed_notifications = [row[1] for row in notification_rows if row[1]["typeID"] == 112]
        assert len(placed_notifications) == 1, placed_notifications
        placed_notification = placed_notifications[0]
        assert placed_notification["senderID"] == placer and placed_notification["receiverID"] == victim
        assert placed_notification["data"]["bountyPlacerID"] == placer
        assert placed_notification["data"]["bounty"] == PLACED
        pool_before = pool(victim)
        assert pool_before == PLACED, pool_before
        isk_before = wallet(hunter)

        events = shoot_pilot_until_dead(hunter_client, hunter_ballpark, hunter_dogma, gun, victim_ship)
        assert events, "the victim's ship was not shot"

        # the claim is a pending payout for the hunter, credited by its job (the payout's id is kill:<ship id>)
        rows = ""
        deadline = time.monotonic() + 60
        while time.monotonic() < deadline:
            rows = dbseed.sql(f"select payout_id, hunter_id, amount, status from {dbseed.schema('CRIMEWATCH')}.player_bounty_payouts "
                              f"where payout_id = 'kill:{victim_ship}'")
            if rows.strip():
                break
            time.sleep(2)
        assert rows.strip(), f"no payout for the kill of ship {victim_ship}"
        payout_id, hunter_id, amount, status = rows.split("|")
        assert int(hunter_id) == hunter, rows
        amount = float(amount)
        assert amount > 0, rows

        # the job credits the hunter once and the payout is paid
        deadline = time.monotonic() + 60
        while time.monotonic() < deadline and (wallet(hunter) - isk_before < amount - 0.01 or status != "paid"):
            time.sleep(2)
            status = dbseed.sql(f"select status from {dbseed.schema('CRIMEWATCH')}.player_bounty_payouts where payout_id = 'kill:{victim_ship}'")
        assert status == "paid", f"the payout was not paid: {status}"
        assert abs((wallet(hunter) - isk_before) - amount) < 0.01, (isk_before, wallet(hunter), amount)
        journal = dbseed.sql(f"select amount, ref_type_id, reason from {dbseed.schema('WALLET')}.journal "
                             f"where owner_id = {hunter} and ref_type_id = 17 and reason = 'Player bounty payout'")
        assert journal.strip(), f"no player-bounty wallet journal entry for hunter {hunter}"
        entries = journal.splitlines()
        assert len(entries) == 1, entries
        journal_amount, ref_type, reason = entries[0].split("|")
        assert abs(float(journal_amount) - amount) < 0.01, entries
        assert int(ref_type) == 17 and reason == "Player bounty payout", entries

        # the victim's pool is drawn down by exactly the payout (the pilot's pool holds more than a fifth of a rookie ship)
        assert abs((pool_before - pool(victim)) - amount) < 0.01, (pool_before, pool(victim), amount)

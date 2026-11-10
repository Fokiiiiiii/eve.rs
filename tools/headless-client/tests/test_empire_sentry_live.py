"""An illegal lowsec ship attack draws station-sentry fire beyond the player ship lock cap.

Run with EVE_LIVE_SLOW=1 through tools/dev/run-eve-live.sh. The client creates both pilots and attacks through real V24 RPCs;
the Host records Crimewatch state and updates the simulation, which sends the sentry target, turret and damage notifications.
"""

from __future__ import annotations

import math
import os
import struct
import time
import uuid

import pytest

from evecli.describe import buffer_bytes
from evecli.describe import describe
from evecli import packet
from tests import dbseed
from tests import test_login_to_character as base
from tests.test_death import DEATH
from tests.test_others import watch
from tests.test_player_kill_live import LOW_STATION, LOW_SYSTEM, arrive_low, fit_hunter_railgun
from tests.test_target import plain_race
from tests.test_undock import add_ball_slim, args_of, keyval, number
from tests.test_weapon import slims_in

LIVE = os.environ.get("EVE_LIVE_HOST", "")
pytestmark = [
    pytest.mark.skipif(not (LIVE and os.environ.get("EVE_PG_URL")), reason="set EVE_LIVE_HOST and EVE_PG_URL"),
    pytest.mark.skipif(os.environ.get("EVE_LIVE_SLOW") != "1", reason="the sentry shot needs the simulation"),
]

EMPIRE_SENTRY_TYPES = {1_194, 3_739, 3_740, 3_741, 3_742, 3_743}
PLAYER_LOCK_RANGE_CAP_METERS = 300_000.0
PRE_PORT_AI_RANGE_METERS = 550_000.0
EMPIRE_SENTRY_RANGE_METERS = 1_000_000.0
TARGET_OFFSET_METERS = 600_000.0


def _target_update_is(event: dict, what: str, item: int) -> bool:
    if event["name"] != "OnTarget":
        return False
    args = args_of(event)["$tuple"]
    return len(args) > 1 and args[0] == what and number(args[1]) == item


def _station_sentry_positions(events: list[dict]) -> dict[int, tuple[float, float, float]]:
    positions: dict[int, tuple[float, float, float]] = {}
    for event in events:
        if event["name"] != "DoDestinyUpdate":
            continue
        destiny = event.get("destiny", {})
        for action in destiny.get("actions", []):
            if action.get("name") != "AddBalls2":
                continue
            buffer_and_entries = action["args"]["$tuple"][0]["$tuple"]
            buffer, entries = buffer_and_entries
            raw = buffer_bytes(buffer)
            if raw is None or not raw or raw[0] != 1:
                continue
            for entry in entries:
                fields = keyval(add_ball_slim(entry))
                item_id = number(fields.get("itemID", 0))
                type_id = number(fields.get("typeID", 0))
                if type_id not in EMPIRE_SENTRY_TYPES:
                    continue
                record = struct.pack("<q", item_id)
                offset = 5
                while (at := raw.find(record, offset)) >= 0:
                    offset = at + 1
                    if at + 37 > len(raw):
                        continue
                    mode = raw[at + 8]
                    radius = struct.unpack_from("<f", raw, at + 9)[0]
                    point = struct.unpack_from("<3d", raw, at + 13)
                    if mode <= 15 and math.isfinite(radius) and radius >= 0 and all(math.isfinite(v) for v in point):
                        positions[item_id] = point
                        break
    return positions


def _damage_fields(event: dict) -> dict:
    return {key: value for key, value in args_of(event)["$tuple"][0]["$dict"]}


def _damage_state_is_for(event: dict, ship_id: int) -> bool:
    if event["name"] != "DoDestinyUpdate":
        return False
    for action in event.get("destiny", {}).get("actions", []):
        if action.get("name") != "OnDamageStateChange":
            continue
        args = action.get("args", {}).get("$tuple", [])
        if args and number(args[0]) == ship_id:
            return True
    return False


def _undock_and_bind(client, ship_id: int):
    service = client.call("ship", "MachoBindObject", (LOW_STATION, 15), None)
    client.call_bound(service[0], "Undock", ship_id, False)
    ballpark = client.call("beyonce", "MachoBindObject", LOW_SYSTEM, None)
    dogma = client.call("dogmaIM", "MachoBindObject", (LOW_SYSTEM, 5), None)
    crimewatch = client.call("crimewatch", "MachoBindObject", (LOW_SYSTEM, 5), None)
    return ballpark, dogma, crimewatch


def _station_position() -> tuple[float, float, float]:
    row = dbseed.sql(
        f"select doc->'position'->>'x', doc->'position'->>'y', doc->'position'->>'z' "
        f"from sde.\"npcStations\" where key = '{LOW_STATION}'"
    )
    coordinates = tuple(float(value) for value in row.split("|"))
    assert len(coordinates) == 3, f"SDE station {LOW_STATION} did not have a position: {row!r}"
    return coordinates


def test_host_crimewatch_drives_station_sentry_fire_600km_from_the_station():
    run = uuid.uuid4().hex[:10]
    race = plain_race(DEATH["races"][0], "victim")
    with base.connect(f"sentry-victim-{run}") as victim_client, base.connect(f"sentry-attacker-{run}") as attacker_client:
        victim, victim_ship, _ = arrive_low(victim_client, race, f"{run}v")
        attacker, attacker_ship, _ = arrive_low(attacker_client, race, f"{run}a")
        assert attacker != victim
        gun = fit_hunter_railgun(attacker_client, attacker_ship)

        victim_ballpark, victim_dogma, _ = _undock_and_bind(victim_client, victim_ship)
        attacker_ballpark, attacker_dogma, attacker_crimewatch = _undock_and_bind(attacker_client, attacker_ship)
        initial_security_status = float(attacker_client.call_bound(attacker_crimewatch[0], "GetMySecurityStatus"))
        clients = {"attacker": attacker_client, "victim": victim_client}
        undock_events = watch(
            clients,
            55.0,
            until=lambda seen: all(
                any(event["name"] == "OnInvulnCancelled" for event in seen[name])
                for name in clients
            ),
        )
        assert all(
            any(event["name"] == "OnInvulnCancelled" for event in undock_events[name])
            for name in clients
        ), "the two ships did not finish their ordinary undock protection"

        sentry_positions = _station_sentry_positions(undock_events["attacker"])
        assert sentry_positions, "the real Host/simulation ballpark did not send the station's Empire sentry balls"

        x, y, z = _station_position()
        offense_position = (x + TARGET_OFFSET_METERS, y, z)
        distances = {item_id: math.dist(offense_position, point) for item_id, point in sentry_positions.items()}
        assert all(
            PRE_PORT_AI_RANGE_METERS < distance < EMPIRE_SENTRY_RANGE_METERS
            for distance in distances.values()
        ), f"the selected offense point must be outside the old 550 km AI range but inside the SDE range: {distances}"
        assert all(distance > PLAYER_LOCK_RANGE_CAP_METERS for distance in distances.values())

        for client in (attacker_client, victim_client):
            moved = str(client.call("slash", "SlashCmd", f"/tr me pos={offense_position[0]:.0f},{y:.0f},{z:.0f}"))
            assert "Transported me" in moved, f"the GM relocation to the offense point failed: {moved}"

        watch(clients, 2.0)
        # /tr is a real space relocation: it releases the previous ballpark and bound services.
        victim_ballpark = victim_client.call("beyonce", "MachoBindObject", LOW_SYSTEM, None)
        victim_dogma = victim_client.call("dogmaIM", "MachoBindObject", (LOW_SYSTEM, 5), None)
        attacker_ballpark = attacker_client.call("beyonce", "MachoBindObject", LOW_SYSTEM, None)
        attacker_dogma = attacker_client.call("dogmaIM", "MachoBindObject", (LOW_SYSTEM, 5), None)
        attacker_crimewatch = attacker_client.call("crimewatch", "MachoBindObject", (LOW_SYSTEM, 5), None)
        attacker_client.call_bound(attacker_ballpark[0], "CmdStop")
        victim_client.call_bound(victim_ballpark[0], "CmdStop")
        watch(clients, 2.0)

        lock_deadline = time.monotonic() + 20.0
        lock_refusal = None
        while time.monotonic() < lock_deadline:
            try:
                attacker_client.call_bound(attacker_dogma[0], "AddTarget", victim_ship)
            except packet.MachoError as error:
                lock_refusal = error
            if victim_ship in [number(getattr(value, "value", value)) for value in attacker_client.call_bound(attacker_dogma[0], "GetTargets")]:
                break
            watch(clients, 1.0)
        else:
            pytest.fail(f"attacker did not lock the victim at the GM offense point: {lock_refusal!r}")

        attacker_client.call_bound(attacker_dogma[0], "Activate", gun, "targetAttack", victim_ship, 1000)
        engaged = watch(
            clients,
            25.0,
            until=lambda seen: any(
                event["name"] == "OnTarget"
                and any(_target_update_is(event, "otheradd", sentry_id) for sentry_id in sentry_positions)
                for event in seen["attacker"]
            ),
        )
        sentry_target_events = [
            (index, event)
            for index, event in enumerate(engaged["attacker"])
            if event["name"] == "OnTarget"
            and any(_target_update_is(event, "otheradd", sentry_id) for sentry_id in sentry_positions)
        ]
        assert sentry_target_events, (
            "no station sentry acquired the attacker within the SDE range; "
            f"sentries={sorted(sentry_positions)}, "
            f"targets={[args_of(event) for event in engaged['attacker'] if event['name'] == 'OnTarget']}, "
            f"damage={[_damage_fields(event) for event in engaged['attacker'] if event['name'] == 'OnDamageMessage']}"
        )
        fired = watch(
            clients,
            100.0,
            until=lambda seen: any(
                event["name"] == "OnDamageMessage"
                and number(_damage_fields(event).get("target", 0)) == attacker_ship
                and number(_damage_fields(event).get("source", 0)) in sentry_positions
                and float(_damage_fields(event).get("damage", 0)) > 0
                for event in seen["attacker"]
            ),
        )
        events = [*engaged["attacker"], *fired["attacker"]]
        sentry_damage = [
            (index, _damage_fields(event))
            for index, event in enumerate(events)
            if event["name"] == "OnDamageMessage"
            and number(_damage_fields(event).get("target", 0)) == attacker_ship
            and number(_damage_fields(event).get("source", 0)) in sentry_positions
            and float(_damage_fields(event).get("damage", 0)) > 0
        ]
        assert sentry_damage, f"no Empire sentry dealt damage inside its SDE range: {sorted({e['name'] for e in events})}"

        damage_index, damage = sentry_damage[0]
        sentry_id = number(damage["source"])
        target_index = next(
            index for index, event in enumerate(events) if _target_update_is(event, "otheradd", sentry_id)
        )
        fx_index = next(
            index for index, event in enumerate(events)
            if event["name"] == "DoDestinyUpdate" and "OnSpecialFX" in str(event) and str(sentry_id) in str(event)
        )
        damage_state_index = next(
            index for index, event in enumerate(events)
            if _damage_state_is_for(event, attacker_ship)
        )
        # The damage-state action names its ship, not its shooter; several station sentries can lock together.
        first_sentry_target_index = min(
            index for index, event in enumerate(events)
            if event["name"] == "OnTarget"
            and any(_target_update_is(event, "otheradd", sentry_id) for sentry_id in sentry_positions)
        )
        security_index = next(index for index, event in enumerate(events) if event["name"] == "OnSecurityStatusUpdate")
        assert security_index < target_index < fx_index < damage_index, [event["name"] for event in events]
        assert first_sentry_target_index < damage_state_index < damage_index, [event["name"] for event in events]
        assert damage["attackType"] == "otherPlayerWeapons", damage

        saved_status = float(attacker_client.call_bound(attacker_crimewatch[0], "GetMySecurityStatus"))
        assert saved_status < initial_security_status, (initial_security_status, saved_status)
        transactions = describe(attacker_client.call_bound(attacker_crimewatch[0], "GetSecurityStatusTransactions"))
        assert isinstance(transactions, dict) and transactions.get("list"), transactions
        status_transaction = transactions["list"][-1]["$row"]
        assert number(status_transaction["eventTypeID"]) == 379, status_transaction
        assert number(status_transaction["otherOwnerID"]) == victim, status_transaction
        assert number(status_transaction["locationID"]) == LOW_SYSTEM, status_transaction
        saved_history = dbseed.sql(
            f"select new_value::text, reason, attrs->>'otherOwnerID', attrs->>'locationID' "
            f"from {dbseed.schema('RELATIONS')}.relation_history "
            f"where kind = 'security_status' and subject = {attacker} and object = 0 "
            f"order by id desc limit 1"
        )
        assert saved_history, f"Host did not persist the attacker's security-status penalty: {attacker}"
        saved_value, reason, other_owner, location = saved_history.split("|")
        assert reason == "aggression" and int(other_owner) == victim and int(location) == LOW_SYSTEM, saved_history
        assert float(saved_value) < 0 and saved_status == pytest.approx(float(saved_value)), (saved_status, saved_history)

        flag, solar_system_id, flag_until = dbseed.sql(
            f"select flag, solar_system_id, flag_until_ms from {dbseed.schema('CRIMEWATCH')}.combat_state "
            f"where character_id = {attacker}"
        ).split("|")
        assert int(flag) == 1 and int(solar_system_id) == LOW_SYSTEM and int(flag_until) > 0, (flag, solar_system_id, flag_until)

"""A module projected at another ship: a remote armor repairer mends the armor of the ship it is aimed at (eve.rs only: the reference has no recording of one).

    EVE_LIVE_SLOW=1 EVE_LIVE_HOST=host:port [EVE_LIVE_SERVER=eve.rs] pytest tests/test_projected.py

Two characters in the same station: the first has `/giveskill` give it the skills and `/fit me` put a Small Remote Armor Repairer I on its ship, the second is
damaged in the station (it leaves with the armor it had), both undock, the first locks the second and starts the module at it. The simulation runs the cycles
(what each does after dogma, how far it reaches, what it costs), the first pilot is told the effect starting and the second its armor coming back as the cycle ends.
Energy neutralizers and nosferatus are attacks, which the safety stops against another pilot in a system that is not null security: they are tested in the scene
and in the host. It needs the simulation: EVE_LIVE_SLOW=1.
"""

from __future__ import annotations

import os
import time
import uuid

import pytest

from evecli.describe import describe
from tests import test_login_to_character as base
from tests.test_death import DEATH
from tests.test_health import own_damage_state
from tests.test_others import watch
from tests.test_repair import armors_of, text_of
from tests.test_target import plain_race
from tests.test_undock import STATION, SYSTEM, arrive, number, outcome
from tests.test_weapon import effect_of, slims_in

REMOTE_ARMOR = 11355
WEBIFIER = 526
pytestmark = [
    pytest.mark.skipif(not base.LIVE, reason="set EVE_LIVE_HOST=host:port to test against a running server"),
    pytest.mark.skipif(os.environ.get("EVE_LIVE_SLOW") != "1", reason="a minute: set EVE_LIVE_SLOW=1"),
    pytest.mark.skipif(os.environ.get("EVE_LIVE_SERVER", "eve.rs") == "reference", reason="the reference has no recording of these modules"),
]


def fitted(who, ship_dogma) -> dict:
    """The modules in the ship by type: `{type id: item id}`."""
    info = who.call_bound(ship_dogma, "GetAllInfo", True, True, None)
    items = {}
    for key, value in {k: v for k, v in describe(info)["args"]["$dict"]}["shipInfo"]["$dict"]:
        if isinstance(key, dict) and "$tuple" in key:
            continue
        row = {k: v for k, v in value["args"]["$dict"]}["invItem"]["$row"]
        items[row["typeID"]] = int(key["$long"] if isinstance(key, dict) else key)
    return items


def test_a_remote_armor_repairer_mends_the_armor_of_the_ship_it_is_aimed_at():
    race = DEATH["races"][0]
    run = uuid.uuid4().hex[:10]
    with base.connect(f"walk-projected-a-{run}") as a, base.connect(f"walk-projected-b-{run}") as b:
        _, a_ship, _, _ = arrive(a, plain_race(race, "victim"), run)
        _, b_ship, _, _ = arrive(b, plain_race(race, "watcher"), run)
        both = {"a": a, "b": b}
        slash = lambda who, command: text_of(who.call("slash", "SlashCmd", command))  # noqa: E731

        for skill in ("Remote Armor Repair Systems", "Mechanics", "Repair Systems"):
            assert f"{skill}(" in slash(a, f"/giveskill me {skill} 1")
        slash(a, "/unfit me all")
        watch(both, 1.0)
        fit = slash(a, f"/fit me {REMOTE_ARMOR}")
        assert fit.startswith("Created and fitted"), fit
        watch(both, 1.5)
        # the one to be mended has had damage in the station (it leaves with the armor it had)
        assert slash(b, "/dmg heavy").startswith("Applied heavy test damage")
        watch(both, 1.0)
        a_modules = fitted(a, a.call("dogmaIM", "MachoBindObject", (STATION, 15), None)[0])
        assert REMOTE_ARMOR in a_modules, sorted(a_modules)

        a_service = a.call("ship", "MachoBindObject", (STATION, 15), None)
        b_service = b.call("ship", "MachoBindObject", (STATION, 15), None)
        a.call_bound(a_service[0], "Undock", a_ship, False)
        a.call("beyonce", "MachoBindObject", SYSTEM, None)
        watch(both, 2.5)
        b.call_bound(b_service[0], "Undock", b_ship, False)
        b.call("beyonce", "MachoBindObject", SYSTEM, None)
        got = watch(both, 3.0)
        state = own_damage_state(got["b"], b_ship, {})
        a_dogma = a.call("dogmaIM", "MachoBindObject", (SYSTEM, 5), None)
        b.call("dogmaIM", "MachoBindObject", (SYSTEM, 5), None)
        watch(both, 1.0)

        # a ship that has just undocked cannot be locked until its protection ends (thirty seconds)
        watch(both, 45.0, until=lambda s: any(e["name"] == "OnInvulnCancelled" for e in s["b"]))
        # the first pilot locks the second
        _, refused = outcome(lambda: a.call_bound(a_dogma[0], "AddTarget", b_ship))
        assert refused is None, base.brief(refused)
        watch(both, 8.0, until=lambda s: any(e["name"] == "OnTarget" for e in s["a"]))

        # the remote repairer: the second pilot is told its armor coming back as the cycle ends
        assert state and state[1] < 1, f"the second ship came into space with its armor at {state}"
        _, refused = outcome(lambda: a.call_bound(a_dogma[0], "Activate", a_modules[REMOTE_ARMOR], "shipModuleRemoteArmorRepairer", b_ship, 1000))
        assert refused is None, base.brief(refused)
        mended = watch(both, 14.0, until=lambda s: any(x > state[1] for x in armors_of(s["b"], b_ship)))
        assert any(x > state[1] for x in armors_of(mended["b"], b_ship)), f"the armor of the target did not come back: {armors_of(mended['b'], b_ship)}"
        started = [effect_of(e) for e in mended["a"] if e["name"] == "OnGodmaShipEffect" and number(effect_of(e)[0]) == a_modules[REMOTE_ARMOR]]
        assert started and started[0][3], f"the repairer did not start: {started}"
        # the target's HUD: the timer and the icon begin
        names = [e["name"] for e in mended["b"]]
        assert "OnJamStart" in names and "OnEwarStart" in names, sorted(set(names))
        a.call_bound(a_dogma[0], "Deactivate", a_modules[REMOTE_ARMOR], "shipModuleRemoteArmorRepairer")
        ended = watch(both, 10.0, until=lambda seen: any(e["name"] == "OnJamEnd" for e in seen["b"]))
        assert any(e["name"] == "OnJamEnd" for e in ended["b"]) and any(e["name"] == "OnEwarEnd" for e in ended["b"]), "the target was not told it ended"


def speeds_of(events: list, ball: int) -> list[float]:
    """The top speeds the stream tells of this ball (`SetMaxSpeed` of its own), in order."""
    told = []
    for event in events:
        if event["name"] != "DoDestinyUpdate":
            continue
        try:
            actions = event["args"]["$tuple"][1]["$tuple"][1]["$tuple"][0]
        except (KeyError, TypeError):
            continue
        for action in actions:
            name, payload = action["$tuple"][1]["$tuple"][0], action["$tuple"][1]["$tuple"][1]["$tuple"]
            if name == "SetMaxSpeed" and number(payload[0]) == ball:
                told.append(float(payload[1]))
    return told


def test_a_stasis_webifier_slows_the_ship_it_is_aimed_at_for_as_long_as_it_runs():
    race = DEATH["races"][0]
    run = uuid.uuid4().hex[:10]
    with base.connect(f"walk-web-{run}") as who:
        _, ship, _, _ = arrive(who, plain_race(race, "victim"), run)
        only = {"who": who}
        slash = lambda command: text_of(who.call("slash", "SlashCmd", command))  # noqa: E731

        for skill in ("Propulsion Jamming",):
            assert f"{skill}(" in slash(f"/giveskill me {skill} 1")
        slash("/unfit me all")
        watch(only, 1.0)
        fit = slash(f"/fit me {WEBIFIER}")
        assert fit.startswith("Created and fitted"), fit
        watch(only, 1.5)
        dogma = who.call("dogmaIM", "MachoBindObject", (STATION, 15), None)[0]
        web = fitted(who, dogma)[WEBIFIER]

        service = who.call("ship", "MachoBindObject", (STATION, 15), None)
        who.call_bound(service[0], "Undock", ship, False)
        watch(only, 2.5)
        beyonce = who.call("beyonce", "MachoBindObject", SYSTEM, None)
        watch(only, 3.0)
        dogma = who.call("dogmaIM", "MachoBindObject", (SYSTEM, 5), None)
        who.call_bound(dogma[0], "GetAllInfo", True, True, None)
        watch(only, 1.0)
        watch(only, 45.0, until=lambda seen: any(e["name"] == "OnInvulnCancelled" for e in seen["who"]))

        # a rat comes into space, the pilot locks it
        assert slash("/npc 1 Blood Visionary").startswith("Spawned 1 hull from Blood Visionary")
        spawn = watch(only, 8.0)["who"]
        rat = next((item for item, _ in slims_in(spawn) if item != ship), 0)
        assert rat, "the rat did not come into space"

        def locked() -> bool:
            try:
                return rat in [number(t) for t in who.call_bound(dogma[0], "GetTargets")]
            except Exception:  # noqa: BLE001
                return False

        # the rat is locked and the web reaches ten kilometers: the pilot flies at the rat and tries again until it is in reach
        outcome(lambda: who.call_bound(beyonce[0], "CmdFollowBall", rat, 2000))
        deadline = time.monotonic() + 90.0
        while True:
            slash("/heal")
            if not locked():
                answer, trouble = outcome(lambda: who.call_bound(dogma[0], "AddTarget", rat))
                watch(only, 3.0, until=lambda seen: locked())
                assert time.monotonic() < deadline, f"the rat was never locked: {base.brief(trouble) if trouble else base.brief(answer)}"
                continue
            _, refused = outcome(lambda: who.call_bound(dogma[0], "Activate", web, "remoteWebifierFalloff", rat, 1000))
            if refused is None:
                break
            assert time.monotonic() < deadline, f"the web was never accepted: {base.brief(refused)}"
            watch(only, 2.0)
        slowed = watch(only, 6.0, until=lambda seen: len(speeds_of(seen["who"], rat)) >= 1)
        speeds = speeds_of(slowed["who"], rat)
        assert speeds, "the pilot was not told the rat's top speed changing"
        slash("/heal")
        who.call_bound(dogma[0], "Deactivate", web, "remoteWebifierFalloff")
        freed = watch(only, 8.0, until=lambda seen: any(x > min(speeds) for x in speeds_of(seen["who"], rat)))
        after = speeds_of(freed["who"], rat)
        assert any(x > min(speeds) for x in after), f"the rat's top speed did not come back: {speeds} then {after}"

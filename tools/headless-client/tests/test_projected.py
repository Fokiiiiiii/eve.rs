"""A module projected at another ship: a remote armor repairer mends the armor of the ship it is aimed at (eve.rs only: the reference has no recording of one).

    EVE_LIVE_SLOW=1 EVE_LIVE_HOST=host:port [EVE_LIVE_SERVER=eve.rs] pytest tests/test_projected.py

Two characters in the same station: the first has `/giveskill` give it the skills and `/fit me` put a Small Remote Armor Repairer I on its ship, the second is
damaged in the station (it leaves with the armor it had), both undock, the first locks the second and starts the module at it. The simulation runs the cycles
(what each does after dogma, how far it reaches, what it costs), the first pilot is told the effect starting and the second its armor coming back as the cycle ends.
Energy neutralizers and nosferatus are attacks, which the safety stops against another pilot in a system that is not null security: they are tested in the scene
and in the host. It needs the simulation: EVE_LIVE_SLOW=1.
"""

from __future__ import annotations

import contextlib
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
RADAR_JAMMER = 1956
TRACKING_DISRUPTOR = 2108
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


class Fight:
    """A pilot in space with one module fitted and a belt rat spawned next to it: what the tests of a module aimed at a rat share."""

    def __init__(self, who, ship, slash, only, dogma, beyonce, module, rat):
        self.who, self.ship, self.slash, self.only, self.dogma, self.beyonce, self.module, self.rat = who, ship, slash, only, dogma, beyonce, module, rat

    def locked(self) -> bool:
        try:
            return self.rat in [number(t) for t in self.who.call_bound(self.dogma[0], "GetTargets")]
        except Exception:  # noqa: BLE001
            return False

    def reach(self, effect_name: str, seconds: float = 90.0) -> None:
        """Flies at the rat, locks it and starts the module at it as soon as the rat is in the reach of both (the rat may be further off than a lock or a module reaches)."""
        outcome(lambda: self.who.call_bound(self.beyonce[0], "CmdFollowBall", self.rat, 2000))
        deadline = time.monotonic() + seconds
        while True:
            self.slash("/heal")
            if not self.locked():
                answer, trouble = outcome(lambda: self.who.call_bound(self.dogma[0], "AddTarget", self.rat))
                watch(self.only, 3.0, until=lambda seen: self.locked())
                assert time.monotonic() < deadline, f"the rat was never locked: {base.brief(trouble) if trouble else base.brief(answer)}"
                continue
            _, refused = outcome(lambda: self.who.call_bound(self.dogma[0], "Activate", self.module, effect_name, self.rat, 1000))
            if refused is None:
                return
            assert time.monotonic() < deadline, f"the module was never accepted: {base.brief(refused)}"
            watch(self.only, 2.0)


@contextlib.contextmanager
def fight(name: str, skills: tuple, module_type: int):
    race = DEATH["races"][0]
    run = uuid.uuid4().hex[:10]
    with base.connect(f"walk-{name}-{run}") as who:
        _, ship, _, _ = arrive(who, plain_race(race, "victim"), run)
        only = {"who": who}
        slash = lambda command: text_of(who.call("slash", "SlashCmd", command))  # noqa: E731
        for skill in skills:
            assert f"{skill}(" in slash(f"/giveskill me {skill} 1")
        slash("/unfit me all")
        watch(only, 1.0)
        fit = slash(f"/fit me {module_type}")
        assert fit.startswith("Created and fitted"), fit
        watch(only, 1.5)
        module = fitted(who, who.call("dogmaIM", "MachoBindObject", (STATION, 15), None)[0])[module_type]

        service = who.call("ship", "MachoBindObject", (STATION, 15), None)
        who.call_bound(service[0], "Undock", ship, False)
        watch(only, 2.5)
        beyonce = who.call("beyonce", "MachoBindObject", SYSTEM, None)
        watch(only, 3.0)
        dogma = who.call("dogmaIM", "MachoBindObject", (SYSTEM, 5), None)
        who.call_bound(dogma[0], "GetAllInfo", True, True, None)
        watch(only, 1.0)
        watch(only, 45.0, until=lambda seen: any(e["name"] == "OnInvulnCancelled" for e in seen["who"]))
        # a rat comes into space
        assert slash("/npc 1 Blood Visionary").startswith("Spawned 1 hull from Blood Visionary")
        spawn = watch(only, 8.0)["who"]
        rat = next((item for item, _ in slims_in(spawn) if item != ship), 0)
        assert rat, "the rat did not come into space"
        yield Fight(who, ship, slash, only, dogma, beyonce, module, rat)


def test_a_stasis_webifier_slows_the_ship_it_is_aimed_at_for_as_long_as_it_runs():
    with fight("web", ("Propulsion Jamming",), WEBIFIER) as f:
        f.reach("remoteWebifierFalloff")
        slowed = watch(f.only, 6.0, until=lambda seen: len(speeds_of(seen["who"], f.rat)) >= 1)
        speeds = speeds_of(slowed["who"], f.rat)
        assert speeds, "the pilot was not told the rat's top speed changing"
        f.slash("/heal")
        f.who.call_bound(f.dogma[0], "Deactivate", f.module, "remoteWebifierFalloff")
        freed = watch(f.only, 8.0, until=lambda seen: any(x > min(speeds) for x in speeds_of(seen["who"], f.rat)))
        after = speeds_of(freed["who"], f.rat)
        assert any(x > min(speeds) for x in after), f"the rat's top speed did not come back: {speeds} then {after}"


def test_an_ecm_jammer_runs_against_a_rat_and_its_cycles_cost_the_pilot_its_capacitor():
    """Whether a jam lands is a roll (the strength of the jammer for the kind of sensor the rat has over that sensor's strength), and the rat is an NPC that is told nothing:
    this holds what is certain: the module is accepted at the rat, starts, and cycles for the length dogma gives, with the pilot told each cycle's cost."""
    with fight("ecm", ("Electronic Warfare",), RADAR_JAMMER) as f:
        f.reach("remoteECMFalloff")
        started = watch(f.only, 4.0)
        starts = [effect_of(e) for e in started["who"] if e["name"] == "OnGodmaShipEffect" and number(effect_of(e)[0]) == f.module]
        assert starts and starts[0][3], f"the jammer did not start: {starts}"
        f.slash("/heal")
        f.who.call_bound(f.dogma[0], "Deactivate", f.module, "remoteECMFalloff")
        ended = watch(f.only, 30.0, until=lambda seen: any(e["name"] == "OnGodmaShipEffect" and number(effect_of(e)[0]) == f.module and not effect_of(e)[3] for e in seen["who"]))
        stops = [effect_of(e) for e in ended["who"] if e["name"] == "OnGodmaShipEffect" and number(effect_of(e)[0]) == f.module and not effect_of(e)[3]]
        assert stops, "the jammer did not stop at the end of its cycle"


def test_a_tracking_disruptor_runs_against_a_rat():
    """What it does to the rat's guns is rolled into its shots, which the pilot sees only as damage; this holds that the module is accepted at the rat, starts and stops."""
    with fight("disrupt", ("Weapon Disruption",), TRACKING_DISRUPTOR) as f:
        f.reach("shipModuleTrackingDisruptor")
        started = watch(f.only, 4.0)
        starts = [effect_of(e) for e in started["who"] if e["name"] == "OnGodmaShipEffect" and number(effect_of(e)[0]) == f.module]
        assert starts and starts[0][3], f"the disruptor did not start: {starts}"
        f.slash("/heal")
        f.who.call_bound(f.dogma[0], "Deactivate", f.module, "shipModuleTrackingDisruptor")
        ended = watch(f.only, 15.0, until=lambda seen: any(e["name"] == "OnGodmaShipEffect" and number(effect_of(e)[0]) == f.module and not effect_of(e)[3] for e in seen["who"]))
        assert any(e["name"] == "OnGodmaShipEffect" and number(effect_of(e)[0]) == f.module and not effect_of(e)[3] for e in ended["who"]), "the disruptor did not stop"

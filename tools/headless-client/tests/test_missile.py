"""A pilot fires a rocket launcher at a rat until the rockets are spent, compared with what the reference was recorded doing.

    EVE_LIVE_SLOW=1 EVE_LIVE_HOST=host:port [EVE_LIVE_SERVER=reference|eve.rs] pytest tests/test_missile.py

`oracle/fixtures/scenarios/missile.json` (scripts/record_character_scenarios.py --only missile) holds one character with a Rocket Launcher I in the place of the miner and
three Scourge Rockets: the launcher loaded in the station, then in space with a rat beside it (`/npc`) fired until the rockets are spent and once more with none. The replay
does the same on any server and compares what the pilot's client hears of the launcher in space: the effect going on and off (the stop names the launcher that has no charges),
the missiles it sees fly (each is a ball of its own, told with a `LaunchMissile` in the update that adds it: the slim item, the fields of the ball and what the launch says), the
balls that are taken away, and the combat log of each rocket that lands (the weapon, the hit quality of a missile). What the rat does is its own (DEVIATIONS #53), and how much
damage a rocket does is what the rat's resistances and signature make of it; where the rocket was fired from and when it lands depend on how near the pilot is when it fires. It needs
the simulation and two minutes, so it runs only with EVE_LIVE_SLOW=1.
"""

from __future__ import annotations

import os
import struct
import uuid

import pytest

from evecli import marshal as m
from evecli.describe import describe
from tests import dbseed
from tests import test_login_to_character as base
from tests.test_ammo import TOLERANCE, of_the_gun, rows_call
from tests.test_others import hold_still, watch
from tests.test_target import compare_answer, plain_race, timing
from tests.test_undock import STATION, SYSTEM, arrive, compare_events, number, outcome
from tests.test_weapon import effect_of, shots_of, slims_in

MISSILE = base.load("missile.json")
pytestmark = [
    pytest.mark.skipif(not base.LIVE, reason="set EVE_LIVE_HOST=host:port to test against a running server"),
    pytest.mark.skipif(os.environ.get("EVE_LIVE_SLOW") != "1", reason="two minutes: set EVE_LIVE_SLOW=1"),
]

LAUNCHER, ROCKET = 10629, 266
MISSILE_MODE, FREE_AND_MASSIVE = 5, 5

def actions_of(event: dict) -> list:
    """The `(name, args)` of every action of a `DoDestinyUpdate`."""
    try:
        actions = event["args"]["$tuple"][1]["$tuple"][1]["$tuple"][0]
    except (KeyError, TypeError):
        return []
    return [(a["$tuple"][1]["$tuple"][0], a["$tuple"][1]["$tuple"][1]["$tuple"]) for a in actions]

def plain(value):
    """A described value as plain python: tuples and lists as lists, longs as ints, dictionaries as dicts."""
    if isinstance(value, dict):
        if "$long" in value:
            return int(value["$long"])
        if "$tuple" in value:
            return [plain(v) for v in value["$tuple"]]
        if "$dict" in value:
            return {plain(k): plain(v) for k, v in value["$dict"]}
        return value
    if isinstance(value, list):
        return [plain(v) for v in value]
    return value

def ball_of(buffer: str) -> dict:
    """The fields of a missile ball (`encodeFreeBall` in `MISSILE` mode) a client reads; the places and the speed it starts at depend on where the pilot fired from."""
    raw = bytes.fromhex(buffer)
    out, offset = {}, 5  # the kind and the stamp of the state buffer

    def read(fmt: str):
        nonlocal offset
        value = struct.unpack_from("<" + fmt, raw, offset)
        offset += struct.calcsize("<" + fmt)
        return value if len(value) > 1 else value[0]

    out["id"], out["mode"], out["radius"], _position, out["flags"], out["mass"], out["cloak"] = read("q"), read("B"), read("f"), read("3d"), read("B"), read("d"), read("B")
    out["harmonic"], out["corporation"], out["alliance"], out["maxVelocity"] = read("q"), read("i"), read("i"), read("f")
    _velocity, out["inertia"], out["speedFraction"], _marker = read("3d"), read("f"), read("f"), read("B")
    out["followTarget"], out["followRange"], out["owner"], out["effectStamp"] = read("q"), read("f"), read("q"), read("i")
    return out

def launches_of(events: list) -> list:
    """The missiles the pilot is shown: the ball, the slim item and the launch of each, from the updates that add them."""
    found = []
    for event in events:
        if event["name"] != "DoDestinyUpdate":
            continue
        actions = actions_of(event)
        names = [name for name, _ in actions]
        if "AddBalls2" not in names or "LaunchMissile" not in names:
            continue
        added = plain(dict(actions)["AddBalls2"][0])
        buffer = dict(actions)["AddBalls2"][0]["$tuple"][0]["$buffer"]
        found.append({"ball": ball_of(buffer), "slim": added[1][0], "launch": plain(dict(actions)["LaunchMissile"]), "names": names})
    return found

def removals_of(events: list, missiles: set) -> int:
    """How many of the missiles' balls are taken away."""
    count = 0
    for event in events:
        if event["name"] == "DoDestinyUpdate":
            for name, args in actions_of(event):
                if name == "RemoveBalls":
                    count += sum(1 for ball in plain(args[0]) if ball in missiles)
    return count

def test_a_pilot_fires_a_launcher_and_sees_its_missiles_as_it_did_in_the_reference():
    race = MISSILE["races"][0]
    roles = race["roles"]
    run = uuid.uuid4().hex[:10]
    problems: list[str] = []
    steps = {step["label"]: step for step in race["steps"]}
    with base.connect(f"walk-missile-{run}") as who:
        character, ship, _modules, mapping = arrive(who, plain_race({"pilot": race}, "pilot"), run)
        dbseed.set_hull(ship, dbseed.COCKROACH)
        only = {"events": who}
        slash = lambda command: (lambda: who.call("slash", "SlashCmd", command))  # noqa: E731

        def docked_step(label: str, call, seconds: float = 1.5):
            """A step in the station: only the answer is compared (the game master's commands tell the client in their own ways: DEVIATIONS #58)."""
            raw, error = outcome(call)
            problems.extend(compare_answer(label, steps[label], raw, error, mapping))
            watch(only, seconds)
            return raw

        for skill in ("Missile Launcher Operation", "Rockets"):
            docked_step(f"the pilot is given {skill}", slash(f"/giveskill me {skill} 1"))
        docked_step("the pilot is given rockets", slash("/giveitem Scourge Rocket 3"))
        docked_step("the pilot takes the miner out", slash("/unfit me Civilian Miner"))
        docked_step("the pilot fits a launcher", slash("/fit me Rocket Launcher I"))

        bound = who.call("dogmaIM", "MachoBindObject", (STATION, 15), None)
        info = describe(who.call_bound(bound[0], "GetAllInfo", True, True, None))
        ship_info = {k: v for k, v in info["args"]["$dict"]}["shipInfo"]["$dict"]
        fitted = {}
        for key, value in ship_info:
            if isinstance(key, dict) and "$tuple" in key:
                continue
            row = {k: v for k, v in value["args"]["$dict"]}["invItem"]["$row"]
            fitted[row["typeID"]] = int(key["$long"] if isinstance(key, dict) else key)
        launcher = fitted[LAUNCHER]
        rockets = {int(r.fields()["typeID"]): int(r.fields()["itemID"]) for r in rows_call(who)}[ROCKET]
        mapping[roles["launcher"]] = launcher
        mapping[roles["rockets"]] = rockets
        docked_step("the pilot loads the launcher", lambda: who.call_bound(bound[0], "LoadAmmo", ship, m.List([launcher]), m.List([rockets]), STATION), 2.0)

        # in space: the launcher fires until the rockets are spent
        service = who.call("ship", "MachoBindObject", (STATION, 15), None)
        who.call_bound(service[0], "Undock", ship, False)
        watch(only, 2.5)
        ballpark = who.call("beyonce", "MachoBindObject", SYSTEM, None)
        watch(only, 3.0)
        dogma = who.call("dogmaIM", "MachoBindObject", (SYSTEM, 5), None)
        who.call_bound(dogma[0], "GetAllInfo", True, True, None)
        watch(only, 1.0)
        watch(only, 45.0, until=lambda seen: any(e["name"] == "OnInvulnCancelled" for e in seen["events"]))
        hold_still(who, ballpark[0])
        text = who.call("slash", "SlashCmd", "/npc 1 Blood Visionary still")
        spawn = watch(only, 8.0)["events"]
        text = text.decode() if isinstance(text, bytes) else str(getattr(text, "value", text))
        assert text.startswith("Spawned 1 hull from Blood Visionary"), text
        rat = next((item for item, _ in slims_in(spawn) if item != ship), 0)
        assert rat, "the rat did not come into space"
        mapping[roles["rat"]] = rat
        who.call_bound(dogma[0], "AddTarget", rat)

        def locked(_seen):
            try:
                return rat in [number(t) for t in who.call_bound(dogma[0], "GetTargets")]
            except Exception:  # noqa: BLE001
                return False

        watch(only, 20.0, until=locked)
        who.call("slash", "SlashCmd", "/heal")
        watch(only, 1.0)
        who.call_bound(ballpark[0], "CmdFollowBall", rat, 300)
        # the pilot closes in on the rat before it fires: a rocket from the edge of the launcher's reach runs out of flight time short of a rat that does not move
        watch(only, 12.0)
        stopped = lambda seen: any(e["name"] == "OnGodmaShipEffect" and effect_of(e)[3] == 0 for e in seen)  # noqa: E731

        # a rocket flies a few kilometres: the pilot closes in on the rat, mended as it goes, and fires when it is near enough (the answer names how far a missile goes)
        fired, error = None, None
        for _ in range(40):
            who.call("slash", "SlashCmd", "/heal")
            fired, error = outcome(lambda: who.call_bound(dogma[0], "Activate", launcher, "missileLaunching", rat, 1000))
            if error is None or "TargetNotWithinRange" not in base.brief(error):
                break
            watch(only, 2.0)
        label = "the pilot fires the launcher until the rockets are spent"
        problems.extend(compare_answer(label, steps[label], fired, error, mapping))
        got = watch(only, 40.0, lambda seen: stopped(seen["events"]))["events"]
        theirs = [e for e in steps[label]["events"] if e["name"] != "OnInvulnCancelled"]
        ours = [e for e in got if e["name"] != "OnInvulnCancelled"]

        # the effect going on and off, with the special effect that plays it and the numbers of the launcher and of the rounds in its slot
        recorded_gun, our_gun = of_the_gun(theirs, roles["launcher"], int(race["shipID"])), of_the_gun(ours, launcher, ship)
        problems.extend(compare_events(recorded_gun, our_gun, label, mapping))
        problems.extend(timing(recorded_gun, our_gun, label, TOLERANCE * 2, ("OnGodmaShipEffect",)))

        # the missiles the pilot sees
        recorded_missiles, our_missiles = launches_of(theirs), launches_of(ours)
        if len(recorded_missiles) != len(our_missiles):
            problems.append(f"{label}: {len(our_missiles)} missiles launched in place of {len(recorded_missiles)}")
        for index, (a, b) in enumerate(zip(recorded_missiles, our_missiles)):
            where = f"{label}: missile {index}"
            if a["names"] != b["names"]:
                problems.append(f"{where}: told as {b['names']} in place of {a['names']}")
            if list(a["slim"]) != list(b["slim"]):
                problems.append(f"{where}: slim item has {list(b['slim'])} in place of {list(a['slim'])}")
            for key in ("typeID", "name", "groupID", "categoryID"):
                if a["slim"].get(key) != b["slim"].get(key):
                    problems.append(f"{where}: slim {key} {b['slim'].get(key)!r} in place of {a['slim'].get(key)!r}")
            mapped = lambda value: mapping.get(value, value)  # noqa: E731
            if b["slim"]["sourceShipID"] != ship or b["slim"]["launchModules"] != [launcher] or b["slim"]["ownerID"] != mapped(a["slim"]["ownerID"]):
                problems.append(f"{where}: slim item names {b['slim']['sourceShipID']} / {b['slim']['launchModules']} / {b['slim']['ownerID']}")
            for key in ("mode", "radius", "flags", "mass", "cloak", "harmonic", "corporation", "alliance", "maxVelocity", "inertia", "followRange", "effectStamp"):
                if abs(float(a["ball"][key]) - float(b["ball"][key])) > 1e-3:
                    problems.append(f"{where}: ball {key} {b['ball'][key]} in place of {a['ball'][key]}")
            if (b["ball"]["mode"], b["ball"]["flags"]) != (MISSILE_MODE, FREE_AND_MASSIVE):
                problems.append(f"{where}: ball is mode {b['ball']['mode']} with flags {b['ball']['flags']}")
            if (b["ball"]["followTarget"], b["ball"]["owner"]) != (rat, ship):
                problems.append(f"{where}: ball follows {b['ball']['followTarget']} for {b['ball']['owner']}")
            if b["launch"][0] != b["ball"]["id"] or b["launch"][1:] != [rat, ship, 1, 1]:
                problems.append(f"{where}: the launch is {b['launch']}")

        after_label = "the pilot fires the launcher with nothing in it"
        raw, error = outcome(lambda: who.call_bound(dogma[0], "Activate", launcher, "missileLaunching", rat, 1000))
        problems.extend(compare_answer(after_label, steps[after_label], raw, error, mapping))
        late = watch(only, 3.0)["events"]
        theirs_all, ours_all = theirs + steps[after_label]["events"], ours + late

        # the balls of the missiles are taken away, and the combat log tells each rocket that landed: the weapon and the hit quality of a missile (what they did depends on the rat)
        ours_ids = {ball["ball"]["id"] for ball in our_missiles}
        gone = removals_of(ours_all, ours_ids)
        if gone != len(ours_ids):
            problems.append(f"{label}: {gone} of {len(ours_ids)} missiles were taken away")
        recorded_hits, our_hits = shots_of(theirs_all), shots_of(ours_all)
        if len(recorded_hits) != len(our_hits):
            problems.append(f"{label}: {len(our_hits)} rockets landed in place of {len(recorded_hits)}")
        for index, (a, b) in enumerate(zip(recorded_hits, our_hits)):
            if list(a) != list(b):
                problems.append(f"{label}: hit {index} has {list(b)} in place of {list(a)}")
            elif (number(a["weapon"]), a["hitQuality"]) != (number(b["weapon"]), b["hitQuality"]) or number(b["target"]) != rat:
                problems.append(f"{label}: hit {index} of {number(b['weapon'])} quality {b['hitQuality']} at {number(b['target'])}")
    assert problems == [], "\n".join(problems[:40])

"""A pilot launches a drone, sends it at a rat, brings it home and into the bay, compared with what the reference was recorded doing.

    EVE_LIVE_SLOW=1 EVE_LIVE_HOST=host:port [EVE_LIVE_SERVER=reference|eve.rs] pytest tests/test_drone_flight.py

`oracle/fixtures/scenarios/drone_flight.json` (scripts/record_character_scenarios.py --only drone_flight) holds one character with the Drones skill and a Hobgoblin I in the
drone bay of its ship, in space with a rat beside it (`/npc`, the smallest armed rat there is): the drone launched (`ship.LaunchDrones`), sent at the rat (`entity.CmdEngage`)
until the rat is dead, brought home (`CmdReturnHome`) and into the bay (`CmdReturnBay`). The replay does the same on any server and compares what the pilot is told of the drone:
the dogma it is primed with, the item that leaves the bay and comes back, its state and what it does, and the balls it is sent (its arrival, its orbit, its removal), by their
order and the moments (the ids of the recording are changed for this run's). What the rat does, and what the drone does of its own accord while the rat shoots (it takes up a fight
by itself in the reference, DEVIATIONS #119), is not compared: the first second after each order is. It needs the simulation and two minutes, so it runs only with EVE_LIVE_SLOW=1.
"""

from __future__ import annotations

import os
import uuid

import pytest

from evecli import marshal as m
from evecli.describe import describe, differences
from tests import test_login_to_character as base
from tests.test_others import watch
from tests.test_target import plain_race
from tests.test_undock import STATION, SYSTEM, actions_of, arrive, args_of, ids, number, outcome
from tests.test_weapon import slims_in

FLIGHT = base.load("drone_flight.json")
pytestmark = [
    pytest.mark.skipif(not base.LIVE, reason="set EVE_LIVE_HOST=host:port to test against a running server"),
    pytest.mark.skipif(os.environ.get("EVE_LIVE_SLOW") != "1", reason="two minutes: set EVE_LIVE_SLOW=1"),
]

HOBGOBLIN = 2454
# what the pilot is told of its drone besides the balls: the dogma it is primed with, the item that moves, and what it is doing
TOLD = ("OnGodmaPrimeItem", "OnItemChange", "OnDroneStateChange", "OnDroneActivityChange")
# the actions of a destiny update that are about the drone's ball
BALLS = ("OnDroneStateChange", "AddBalls2", "Orbit", "FollowBall", "RemoveBalls")
# the moments an event may come after or before the recording's: the stream of the simulation is in quarters of a second and the settle of the drone's item in halves
TOLERANCE = 0.9


def numbers_in(described) -> set:
    """Every whole number a description holds (the ids an event is about)."""
    if isinstance(described, list):
        return set().union(*[numbers_in(v) for v in described]) if described else set()
    if isinstance(described, dict):
        if "$long" in described and isinstance(described["$long"], int):
            return {described["$long"]}
        return set().union(*[numbers_in(v) for v in described.values()]) if described else set()
    if isinstance(described, int) and not isinstance(described, bool):
        return {described}
    return set()


def about_the_drone(events: list, drone: int, window: float | None = None, balls: bool = True) -> list:
    """What the pilot is told of this drone, in order: `{name, at, args}` (the balls' actions by their names, with no arguments)."""
    out = []
    for event in events:
        if window is not None and event["at"] > window:
            continue
        if event["name"] in TOLD:
            args = args_of(event)["$tuple"]
            if drone in numbers_in(args):
                out.append({"name": event["name"], "at": event["at"], "args": args})
        elif balls and event["name"] == "DoDestinyUpdate":
            actions, _ = actions_of(event)
            for _, action, args in actions:
                if action not in BALLS:
                    continue
                if action == "AddBalls2" and drone not in [item for item, _ in slims_in([event])]:
                    continue
                if action != "AddBalls2" and drone not in numbers_in(args):
                    continue
                out.append({"name": f"destiny {action}", "at": event["at"], "args": None})
    return out


def compare(recorded: list, ours: list, where: str, mapping: dict) -> list[str]:
    wanted = [e["name"] for e in recorded]
    got = [e["name"] for e in ours]
    if wanted != got:
        return [f"{where}: told {got} in place of {wanted}"]
    out: list[str] = []
    for index, (a, b) in enumerate(zip(recorded, ours)):
        label = f"{where}: {index} {a['name']}"
        if a["args"] is not None:
            out += differences(base.blank_times(ids(a["args"], mapping)), base.blank_times(b["args"]), label)
        if abs(a["at"] - b["at"]) > TOLERANCE:
            out.append(f"{label} after {b['at']} s, in the recording after {a['at']} s")
    return out


def in_space_with_a_drone(who, ship: int, only: dict) -> dict:
    """A pilot with the Drones skill and a Hobgoblin I in the drone bay of its ship undocks, waits for the protection of the undock to end and calls a rat (`/npc`): what it needs to
    launch the drone and send it at the rat."""
    slash = lambda command: (lambda: who.call("slash", "SlashCmd", command))  # noqa: E731
    # the game master's commands word their answers with the character's name: only that they are answered is what the setup needs
    slash("/giveskill me Drones 1")()
    watch(only, 1.5)
    slash("/giveitem Hobgoblin I 1")()
    watch(only, 1.5)
    manager = who.call("invbroker", "MachoBindObject", (STATION, 15), None)[0]
    hangar = who.call_bound(manager, "GetInventory", 10004)
    stack = next(int(row.fields()["itemID"]) for row in who.call_bound(hangar, "List") if int(row.fields()["typeID"]) == HOBGOBLIN)
    docked = who.call("invbroker", "MachoBindObject", (STATION, 15), ("GetInventoryFromId", (ship, 0), None))[1]
    who.call_bound(docked, "Add", stack, STATION, qty=1, flag=87)
    watch(only, 1.5)
    service = who.call("ship", "MachoBindObject", (STATION, 15), None)
    who.call_bound(service[0], "Undock", ship, False)
    watch(only, 2.5)
    who.call("beyonce", "MachoBindObject", SYSTEM, None)
    watch(only, 3.0)
    space = who.call("dogmaIM", "MachoBindObject", (SYSTEM, 5), None)
    watch(only, 1.0)
    watch(only, 45.0, until=lambda seen: any(e["name"] == "OnInvulnCancelled" for e in seen["events"]))
    text = who.call("slash", "SlashCmd", "/npc 1 Petty Thief")
    spawn = watch(only, 8.0)["events"]
    text = getattr(text, "value", text)
    text = text.decode() if isinstance(text, bytes) else str(text)
    assert text.startswith("Spawned 1 hull from Petty Thief"), text
    rat = next((item for item, _ in slims_in(spawn) if item != ship), 0)
    assert rat, "the rat did not come into space"
    ship_in_space = who.call("ship", "MachoBindObject", (SYSTEM, 5), None)
    entity = who.call("entity", "MachoBindObject", SYSTEM, None)
    bay = who.call("invbroker", "MachoBindObject", (SYSTEM, 5), ("GetInventoryFromId", (ship, 0), None))[1]
    in_bay = next(int(row.fields()["itemID"]) for row in who.call_bound(bay, "List", 87))
    assert in_bay == stack, "the drone in the bay is not the drone that was put there"
    watch(only, 1.0)
    return {"space": space, "ship_in_space": ship_in_space, "entity": entity, "bay": bay, "drone": stack, "rat": rat}


def test_a_pilot_flies_a_drone_as_in_the_reference():
    race = FLIGHT["races"][0]
    roles = race["roles"]
    run = uuid.uuid4().hex[:10]
    problems: list[str] = []
    steps = {step["label"]: step for step in race["steps"]}
    with base.connect(f"walk-drones-{run}") as who:
        _, ship, _, mapping = arrive(who, plain_race({"pilot": race}, "pilot"), run)
        only = {"events": who}
        slash = lambda command: (lambda: who.call("slash", "SlashCmd", command))  # noqa: E731

        def answered(label: str, call):
            """What a step is answered, against the recording's (the ids by the mapping)."""
            raw, error = outcome(call)
            recorded = steps[label]
            if "error" in recorded or error is not None:
                if (error is None) != ("error" not in recorded) or error != recorded.get("error"):
                    problems.append(f"{label}: error {error} in place of {recorded.get('error')}")
            else:
                problems.extend(differences(base.blank_times(ids(recorded["ok"], mapping)), base.blank_times(describe(raw)), f"{label}: answer"))
            return raw

        def told(label: str, seconds: float, until=None, window: float | None = None, expect: bool = True) -> list:
            got = watch(only, seconds, until)["events"]
            if expect:
                theirs = about_the_drone(steps[label]["events"], roles["drone"], window)
                ours = about_the_drone(got, mapping[roles["drone"]], window)
                problems.extend(compare(theirs, ours, label, mapping))
            return got

        # the setup: the skill and the drone, the drone in the bay, the undock, the protection ended, a rat
        flown = in_space_with_a_drone(who, ship, only)
        space, ship_in_space, entity, bay = flown["space"], flown["ship_in_space"], flown["entity"], flown["bay"]
        stack, in_bay, rat = flown["drone"], flown["drone"], flown["rat"]
        mapping[roles["drone"]] = stack
        mapping[roles["rat"]] = rat

        # the launch: the dogma of the drone, the item that leaves the bay (and is sent again), the drone's ball and its circling
        answered("the pilot launches the drone", lambda: who.call_bound(ship_in_space[0], "LaunchDrones", m.List([(in_bay, 1)]), None, False))
        trail = told("the pilot launches the drone", 3.0, window=1.5)
        after = who.call_bound(bay, "List", 87)
        assert not (after if isinstance(after, list) else []), f"the bay holds {after} after the launch"
        who.call("slash", "SlashCmd", "/heal")
        trail += watch(only, 0.5)["events"]

        # the drone may have taken up the fight by itself as the rat shot at the pilot (it does, as in the reference: DEVIATIONS #119): when the rat is dead a new one is called, so
        # that the order has someone to go at
        if any(e["name"] == "OnBountyAddedToPayout" for e in trail):
            who.call("slash", "SlashCmd", "/npc 1 Petty Thief")
            called = watch(only, 4.0)["events"]
            rat = next((item for item, _ in slims_in(called) if item not in (ship, rat)), 0)
            assert rat, "a new rat did not come into space"
            mapping[roles["rat"]] = rat
        # the rat is locked, the drone is sent at it and goes on until the rat is dead (the pilot is healed as the rat shoots)
        who.call_bound(space[0], "AddTarget", rat)
        watch(only, 20.0, until=lambda seen: any(e["name"] == "OnTarget" and "otheradd" not in str(e) for e in seen["events"]))
        who.call("slash", "SlashCmd", "/heal")
        watch(only, 1.0)
        engaged = outcome(lambda: who.call_bound(entity[0], "CmdEngage", m.List([in_bay]), rat))
        assert engaged[1] is None, engaged
        recorded_engage = steps["the pilot sends the drone at the rat (mending the ship as it goes)"]
        if describe(engaged[0]) != recorded_engage["ok"]:
            problems.append(f"engage: answer {describe(engaged[0])} in place of {recorded_engage['ok']}")
        seen_engage: list = []
        for _ in range(25):
            seen_engage += watch(only, 2.0)["events"]
            if any(e["name"] == "OnBountyAddedToPayout" for e in seen_engage):
                break
            who.call("slash", "SlashCmd", "/heal")
        theirs = about_the_drone(recorded_engage["events"], roles["drone"], 0.5)
        ours = about_the_drone(seen_engage, stack, 0.5)
        problems.extend(compare(theirs[:2], ours[:2], "the engage order", mapping))
        # the rat's death is the drone's: it is told it is in combat, and the pilot is paid the bounty
        if not any(e["name"] == "OnBountyAddedToPayout" for e in seen_engage):
            states = [(round(e["at"], 1), args_of(e)["$tuple"][3:4] + args_of(e)["$tuple"][6:7]) for e in seen_engage if e["name"] == "OnDroneStateChange"]
            hits = [e["name"] for e in seen_engage if e["name"] in ("OnDamageMessage", "OnTarget")]
            problems.append(f"the drone did not kill the rat (the drone's states {states}; {len(hits)} damage messages and locks; the rat {rat})")
        who.call("slash", "SlashCmd", "/heal")
        watch(only, 1.0)

        # home, then into the bay: the dogma of the drone in space is sent first, the item comes back, the ball goes
        answered("the pilot brings the drone home", lambda: who.call_bound(entity[0], "CmdReturnHome", m.List([in_bay])))
        got = watch(only, 4.0)["events"]
        problems.extend(compare(about_the_drone(steps["the pilot brings the drone home"]["events"], roles["drone"], 0.5, balls=False), about_the_drone(got, stack, 0.5, balls=False), "the pilot brings the drone home", mapping))
        answered("the pilot sends the drone into the bay", lambda: who.call_bound(entity[0], "CmdReturnBay", m.List([in_bay])))
        got = watch(only, 15.0, until=lambda seen: any(e["name"] == "OnItemChange" and number(args_of(e)["$tuple"][0]["$row"]["itemID"]) == stack for e in seen["events"]))["events"]
        got += watch(only, 1.0)["events"]
        theirs = [e for e in about_the_drone(steps["the pilot sends the drone into the bay"]["events"], roles["drone"]) if e["name"] != "OnItemChange" or e["args"][0]["$row"]["flagID"] == 87]
        ours = [e for e in about_the_drone(got, stack) if e["name"] != "OnItemChange" or e["args"][0]["$row"]["flagID"] == 87]
        problems.extend(compare(theirs[:1], ours[:1], "the drone is sent into the bay", mapping))
        in_bay_again = who.call_bound(bay, "List", 87)
        rows = in_bay_again if isinstance(in_bay_again, list) else []
        if [int(row.fields()["itemID"]) for row in rows] != [stack]:
            problems.append(f"the drone is not in the bay at the end: {rows}")
    assert not problems, "\n".join(problems)


@pytest.mark.skipif(os.environ.get("EVE_LIVE_SERVER") == "reference", reason="the reference refuses an order that names no target (DEVIATIONS #119)")
def test_drones_sent_to_attack_with_nothing_locked_look_for_an_opponent_by_themselves():
    """An order to engage that names nothing (the pilot has locked nothing) sends the drones to find an opponent themselves: the drone is told it goes at the rat and fights it
    until it is dead (the pilot is paid the bounty), and an order with no one there to find is refused for the drone, as it is for a target that is not there."""
    race = FLIGHT["races"][0]
    run = uuid.uuid4().hex[:10]
    problems: list[str] = []
    with base.connect(f"walk-drones-search-{run}") as who:
        _, ship, _, _ = arrive(who, plain_race({"pilot": race}, "pilot"), run)
        only = {"events": who}
        flown = in_space_with_a_drone(who, ship, only)
        drone, rat, entity = flown["drone"], flown["rat"], flown["entity"]
        who.call("slash", "SlashCmd", "/heal")
        launched = who.call_bound(flown["ship_in_space"][0], "LaunchDrones", m.List([(drone, 1)]), None, False)
        assert describe(launched) == {"$dict": [[{"$long": drone} if drone >= 2**31 else drone, [{"$long": drone} if drone >= 2**31 else drone]]]}, describe(launched)
        watch(only, 1.0)
        # nothing is locked: the order names no target, and the drone is told it is going at the rat
        asked = who.call_bound(entity[0], "CmdEngage", m.List([drone]), None)
        assert describe(asked) == {"$dict": []}, describe(asked)
        seen: list = []
        for _ in range(25):
            seen += watch(only, 2.0)["events"]
            states = [args_of(e)["$tuple"] for e in seen if e["name"] == "OnDroneStateChange"]
            if any(e["name"] == "OnBountyAddedToPayout" for e in seen):
                break
            who.call("slash", "SlashCmd", "/heal")
        targets = [number(a[6]) for a in states if len(a) > 6 and a[6] is not None]
        if rat not in targets:
            problems.append(f"the drone was not sent at the rat {rat}: the targets it was told were {targets}")
        if not any(e["name"] == "OnBountyAddedToPayout" for e in seen):
            problems.append("the drone did not kill the rat it found")
        # the rat is dead and nobody is left: the order is refused for the drone
        who.call("slash", "SlashCmd", "/heal")
        watch(only, 2.0)
        nobody = who.call_bound(entity[0], "CmdEngage", m.List([drone]), None)
        refused = describe(nobody)
        if refused["$dict"] and refused["$dict"][0][1]["$tuple"][0] != "CustomNotify":
            problems.append(f"the order with no one to find is answered {refused}")
        if not refused["$dict"]:
            problems.append("the order with no one to find was taken")
    assert not problems, "\n".join(problems)

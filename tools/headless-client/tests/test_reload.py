"""A pilot loads and unloads a gun in space and fires it while it reloads, compared with what the reference was recorded doing.

    EVE_LIVE_SLOW=1 EVE_LIVE_HOST=host:port [EVE_LIVE_SERVER=reference|eve.rs] pytest tests/test_reload.py

`oracle/fixtures/scenarios/reload.json` (scripts/record_character_scenarios.py --only reload) holds one character with a gun that takes charges (a 125mm Railgun I in the
place of the miner), loaded in its station with more antimatter put in the hold; then in space with a rat beside it (`/npc`): the gun unloaded into the hold, loaded again
from it (the reference makes the pilot wait the gun's reload time: it tells the pilot at once that the module is ready at a moment and that a charge is being loaded, and
when the time has run it moves the rounds and tells what changed, last that the module is ready), loaded when it is full, most of its rounds taken out and the gun fired
until they are spent (the reference's turret does not reload by itself: it stops with `NoCharges` with more in the hold), fired while it waits for a reload (with nothing in
it: refused), and fired while it tops up (it goes on firing, and has the rounds when the time has run). The replay does the same on any server and compares what the client
hears of the gun and of the stacks of the hold, event by event, and the moments (the ids of the stacks are per run: they are compared by the order they first appear in).
What the rat does is its own (DEVIATIONS #53) and what a shot does is a roll. It needs the simulation and three minutes, so it runs only with EVE_LIVE_SLOW=1.
"""

from __future__ import annotations

import json
import os
import uuid

import pytest

from evecli import marshal as m
from evecli.describe import describe, differences
from tests import dbseed
from tests import test_login_to_character as base
from tests.test_ammo import ANTIMATTER, RAILGUN, canon, canon_events, of_the_gun
from tests.test_others import hold_still, watch
from tests.test_target import compare_answer, plain_race, timing
from tests.test_undock import STATION, SYSTEM, arrive, compare_events, ids, number, outcome
from tests.test_weapon import effect_of, shots_of, slims_in

RELOAD = base.load("reload.json")
pytestmark = [
    pytest.mark.skipif(not base.LIVE, reason="set EVE_LIVE_HOST=host:port to test against a running server"),
    pytest.mark.skipif(os.environ.get("EVE_LIVE_SLOW") != "1", reason="three minutes: set EVE_LIVE_SLOW=1"),
]

# the moments an event may come after or before the recording's: a cycle is a little under three seconds, a reload five, and this side works in quarters of a second
TOLERANCE = 0.8
RELOAD_EVENTS = ("OnItemsChanged", "OnGodmaPrimeItem", "OnChargeBeingLoadedToModule")


def of_the_reload(events: list, gun: int, ship: int) -> list:
    """What a reload and an unload make the pilot's client hear: the stacks of the hold that changed, the charge that is primed and announced, and the numbers of the
    module and of the rounds in its slot (the next activation time is one of them), with what the gun does."""
    out = []
    for event in events:
        if event["name"] in RELOAD_EVENTS:
            out.append(event)
        else:
            out.extend(of_the_gun([event], gun, ship))
    return out


def test_a_pilot_loads_a_gun_in_space_and_waits_for_the_reload_as_in_the_reference():
    race = RELOAD["races"][0]
    roles = race["roles"]
    run = uuid.uuid4().hex[:10]
    problems: list[str] = []
    steps = {step["label"]: step for step in race["steps"]}
    seen_ids: dict = {}
    seen_recorded: dict = {}
    with base.connect(f"walk-reload-{run}") as who:
        _, ship, _, mapping = arrive(who, plain_race({"pilot": race}, "pilot"), run)
        dbseed.set_hull(ship, dbseed.COCKROACH)
        known = dict(mapping)  # the ids both sides have from the start (the items made later are told apart by the order they appear in)
        only = {"events": who}
        slash = lambda command: (lambda: who.call("slash", "SlashCmd", command))  # noqa: E731
        manager = who.call("invbroker", "MachoBindObject", (STATION, 15), None)[0]
        hangar = who.call_bound(manager, "GetInventory", 10004)

        def ids_by_type(handle, flag=None) -> dict:
            rows = who.call_bound(handle, "List", flag) if flag else who.call_bound(handle, "List")
            return {int(row.fields()["typeID"]): int(row.fields()["itemID"]) for row in (rows if isinstance(rows, list) else [])}

        def docked_step(label: str, call, seconds: float = 1.5, events: bool = True):
            raw, error = outcome(call)
            recorded = steps[label]
            if "error" in recorded or error is not None or recorded.get("ok") is None:
                problems.extend(compare_answer(label, recorded, raw, error, mapping))
            else:
                # what is answered may be the id of a stack that was made (per run): told apart by the order the ids appear in, as in the events
                # the test ship is the Cockroach where the recording's was an Ibis: the answer names the hull it is fitted to (DEVIATIONS #59)
                expected = recorded["ok"].replace(" to Ibis ", " to Cockroach ") if isinstance(recorded["ok"], str) else recorded["ok"]
                problems.extend(differences(canon(base.blank_times(ids(expected, known)), seen_recorded), canon(base.blank_times(describe(raw)), seen_ids), label))
            got = watch(only, seconds)["events"]
            if events:
                problems.extend(compare_events(canon_events(steps[label]["events"], seen_recorded, known), canon_events(got, seen_ids), label, {}))
            return raw

        docked_step("the pilot is given antimatter", slash("/giveitem Antimatter Charge S 500"))
        # the game master's commands that make the setup tell the client in their own ways (DEVIATIONS #58): only their answers are compared
        docked_step("the pilot takes the miner out", slash("/unfit me Civilian Miner"), events=False)
        docked_step("the pilot fits a railgun", slash("/fit me 125mm Railgun I"), events=False)
        docked = who.call("dogmaIM", "MachoBindObject", (STATION, 15), None)
        info = describe(who.call_bound(docked[0], "GetAllInfo", True, True, None))
        fitted = {}
        for key, value in {k: v for k, v in info["args"]["$dict"]}["shipInfo"]["$dict"]:
            if isinstance(key, dict) and "$tuple" in key:
                continue
            row = {k: v for k, v in value["args"]["$dict"]}["invItem"]["$row"]
            fitted[row["typeID"]] = int(key["$long"] if isinstance(key, dict) else key)
        gun = fitted[RAILGUN]
        antimatter = ids_by_type(hangar)[ANTIMATTER]
        mapping[roles["gun"]] = gun
        mapping[roles["antimatter"]] = antimatter
        hold_docked = who.call("invbroker", "MachoBindObject", (STATION, 15), ("GetInventoryFromId", (ship, 0), None))[1]
        docked_step("the pilot loads the railgun in the station", lambda: who.call_bound(docked[0], "LoadAmmo", ship, m.List([gun]), m.List([antimatter]), STATION), 2.0)
        docked_step("the pilot puts more antimatter in the hold", lambda: who.call_bound(hold_docked, "Add", ids_by_type(hangar)[ANTIMATTER], STATION, qty=40, flag=5), 1.5)

        # in space: the protection of the undock ends, a rat comes, the pilot locks it and heals
        service = who.call("ship", "MachoBindObject", (STATION, 15), None)
        who.call_bound(service[0], "Undock", ship, False)
        watch(only, 2.5)
        ballpark = who.call("beyonce", "MachoBindObject", SYSTEM, None)
        watch(only, 3.0)
        space = who.call("dogmaIM", "MachoBindObject", (SYSTEM, 5), None)
        who.call_bound(space[0], "GetAllInfo", True, True, None)
        watch(only, 1.0)
        watch(only, 45.0, until=lambda seen: any(e["name"] == "OnInvulnCancelled" for e in seen["events"]))
        hold_still(who, ballpark[0])
        hold = who.call("invbroker", "MachoBindObject", (SYSTEM, 5), ("GetInventoryFromId", (ship, 0), None))[1]
        text = who.call("slash", "SlashCmd", "/npc 1 Blood Visionary")
        spawn = watch(only, 8.0)["events"]
        text = getattr(text, "value", text)
        text = text.decode() if isinstance(text, bytes) else str(text)
        assert text.startswith("Spawned 1 hull from Blood Visionary"), text
        rat = next((item for item, _ in slims_in(spawn) if item != ship), 0)
        assert rat, "the rat did not come into space"
        mapping[roles["rat"]] = rat
        held = {"rat": rat}
        who.call_bound(space[0], "AddTarget", rat)
        watch(only, 20.0, until=lambda seen: any(e["name"] == "OnTarget" and "otheradd" not in str(e) for e in seen["events"]))
        who.call("slash", "SlashCmd", "/heal")
        watch(only, 1.0)

        def locked(target: int) -> bool:
            try:
                return target in [number(t) for t in who.call_bound(space[0], "GetTargets")]
            except Exception:  # noqa: BLE001
                return False

        def a_rat_that_is_locked(fresh: bool = False) -> int:
            """The rat the gun is fired at. What the rat does is its own (DEVIATIONS #53): the reference's survives the whole recording, ours may be killed or lose the lock
            by the end of it, and then a new one is called and locked, with the gun fired at that (`fresh`: before a step that must not end with the rat killed, a new one
            even if the rat is alive: it may be about to die)."""
            if not fresh and locked(held["rat"]):
                return held["rat"]
            watch(only, 0.5)
            called = who.call("slash", "SlashCmd", "/npc 1 Blood Visionary")
            fresh = next((item for item, _ in slims_in(watch(only, 8.0)["events"]) if item not in (ship, held["rat"])), 0)
            assert fresh, f"a new rat did not come into space ({called})"
            who.call_bound(space[0], "AddTarget", fresh)
            watch(only, 20.0, until=lambda seen: locked(fresh))
            who.call("slash", "SlashCmd", "/heal")
            watch(only, 1.0)
            held["rat"] = fresh
            mapping[roles["rat"]] = fresh
            return fresh

        stopped = lambda seen: any(e["name"] == "OnGodmaShipEffect" and effect_of(e)[3] == 0 for e in seen)  # noqa: E731

        def watch_and_heal(seconds: float, until=None, every: float = 2.5) -> list:
            """Everything the pilot is sent in this many seconds (less when `until` holds of what it was sent), with the moments counted from the start. The rat shoots, and
            how well is its own (DEVIATIONS #53): the pilot is healed between the stretches, or a step that is long could end with the ship destroyed."""
            events: list = []
            spent = 0.0
            while spent < seconds:
                chunk = min(every, seconds - spent)
                got = watch(only, chunk)["events"]
                events.extend({**e, "at": e["at"] + spent} for e in got)
                spent += chunk
                if until is not None and until(events):
                    break
                if spent < seconds:
                    who.call("slash", "SlashCmd", "/heal")
            return events

        def space_step(label: str, call, seconds: float = 1.0, until=None, answer: bool = True, events: bool = True):
            """What the pilot is answered, and what it is told of the gun and of the hold, in order, with the moments of what is told."""
            raw, error = outcome(call)
            recorded = steps[label]
            if "error" in recorded or error is not None:
                problems.extend(compare_answer(label, recorded, raw, error, mapping))
            elif answer:
                theirs_answer = canon(base.blank_times(ids(recorded["ok"], known)), seen_recorded)
                ours_answer = canon(base.blank_times(describe(raw)), seen_ids)
                problems.extend(differences(theirs_answer, ours_answer, label))
            got = watch_and_heal(seconds, until)
            if os.environ.get("EVE_RELOAD_DUMP"):  # what this side was sent, step by step, to read beside the recording when they differ
                with open(os.environ["EVE_RELOAD_DUMP"], "a", encoding="utf-8") as dump:
                    dump.write(json.dumps({"label": label, "events": got}, default=str) + "\n")
            if not events:
                return raw
            theirs = [e for e in recorded["events"] if e["name"] != "OnInvulnCancelled"]
            ours = [e for e in got if e["name"] != "OnInvulnCancelled"]
            recorded_view, our_view = of_the_reload(theirs, roles["gun"], int(race["shipID"])), of_the_reload(ours, gun, ship)
            # the rat is told apart by the id this run gave it (it is not an item: its id is not of the range the items' are told apart in)
            problems.extend(compare_events(canon_events(recorded_view, seen_recorded, {**known, roles["rat"]: held["rat"]}), canon_events(our_view, seen_ids), label, {}))
            problems.extend(timing(recorded_view, our_view, label, TOLERANCE, ("OnGodmaShipEffect", "DoDestinyUpdate", "OnItemsChanged", "OnChargeBeingLoadedToModule", "OnGodmaPrimeItem")))
            recorded_shots, our_shots = shots_of(theirs), shots_of(got)
            if abs(len(recorded_shots) - len(our_shots)) > 1:
                problems.append(f"{label}: {len(our_shots)} shots in the combat log in place of {len(recorded_shots)}")
            return raw

        unload = lambda quantity: (lambda: who.call_bound(space[0], "UnloadAmmo", ship, m.List([gun]), ship, quantity))  # noqa: E731
        load = lambda charge: (lambda: who.call_bound(space[0], "LoadAmmo", ship, m.List([gun]), m.List([charge]), ship))  # noqa: E731
        activate = lambda: who.call_bound(space[0], "Activate", gun, "projectileFired", held["rat"], 1000)  # noqa: E731
        deactivate = lambda: who.call_bound(space[0], "Deactivate", gun, "projectileFired")  # noqa: E731
        heal = lambda: who.call("slash", "SlashCmd", "/heal")  # noqa: E731

        space_step("the pilot asks for its hold in space", lambda: who.call_bound(hold, "List", 5), 0.5)
        space_step("the pilot unloads the railgun into the hold", unload(None), 2.0)
        space_step("the pilot asks for its hold with the rounds back", lambda: who.call_bound(hold, "List", 5), 0.5)
        in_hold = ids_by_type(hold, 5)[ANTIMATTER]
        space_step("the pilot loads the railgun from the hold", load(in_hold), 12.0)
        space_step("the pilot asks for its hold after the reload", lambda: who.call_bound(hold, "List", 5), 0.5)
        space_step("the pilot loads the railgun when it is full", load(in_hold), 3.0)
        heal()
        watch(only, 1.0)
        a_rat_that_is_locked()  # before the steps whose moments are compared: a rat that is called takes its time
        space_step("the pilot unloads most of the rounds", unload(75), 2.0)
        space_step("the pilot fires the railgun until the rounds are spent", activate, 45.0, until=stopped)
        space_step("the pilot stops the railgun", deactivate, 3.0)
        space_step("the pilot asks for its hold after the fight", lambda: who.call_bound(hold, "List", 5), 0.5)
        heal()
        watch(only, 1.0)
        a_rat_that_is_locked()  # before the steps whose moments are compared: a rat that is called takes its time
        space_step("the pilot unloads what is left in the railgun", unload(None), 2.0)
        in_the_hold = ids_by_type(hold, 5)
        assert ANTIMATTER in in_the_hold, f"the hold has {in_the_hold} (all of it: {ids_by_type(hold)}); problems so far: {problems[:10]}"
        in_hold = in_the_hold[ANTIMATTER]
        space_step("the pilot loads the railgun again", load(in_hold), 0.8)
        space_step("the pilot fires the railgun while it reloads", activate, 8.0)
        a_rat_that_is_locked(fresh=True)  # before the steps whose moments are compared: a rat that is called takes its time
        space_step("the pilot unloads most of the rounds again", unload(75), 2.0)
        space_step("the pilot tops the railgun up", load(in_hold), 0.8)
        space_step("the pilot fires the railgun while it tops up", activate, 9.0)
        # what the rat is doing by now is its own (the gun may be stopped by the lock lost: told in the other order, as the reference does for that): only the answer is compared
        space_step("the pilot stops the railgun after the top up", deactivate, 4.0, events=False)
        space_step("the pilot asks for its hold at the end", lambda: who.call_bound(hold, "List", 5), 0.5)
    assert problems == [], "\n".join(problems[:60])

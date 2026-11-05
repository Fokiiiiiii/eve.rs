"""A ship warps to a planet, compared with what the reference was recorded doing.

    EVE_LIVE_SLOW=1 EVE_LIVE_HOST=host:port [EVE_LIVE_SERVER=reference|eve.rs] pytest tests/test_warp.py

`oracle/fixtures/scenarios/warp.json` (scripts/record_character_scenarios.py --only warp) holds a new character's undock, `CmdWarpToStuff` to
a planet two AU away with every event the client was sent while the ship went (with the seconds each arrived at), the same order given again
by the planet and one to a place that is not there. The update that starts the warp is compared value for value (the point the client warps
in at is worked out from the planet's place and id, and the warp factor from the ship's warp speed); when the ship drops out and when it comes
to rest, within a couple of seconds of the recording. What the reference adds (the customs offices and props it takes out of the ballpark
while the ship is in warp, DEVIATIONS.md #16) is left out. It takes a minute and a half, so it runs only with EVE_LIVE_SLOW=1.
"""

from __future__ import annotations

import copy
import os
import time
import uuid

import pytest

from evecli.describe import describe
from evecli.observe import timed_events, wait_quietly
from tests import test_login_to_character as base
from tests.test_undock import (
    FIRST_BALL_ID, STATION, SYSTEM, UNDOCK, actions_of, add_ball_slim, arrive,
    compare_destiny_updates, compare_events, keyval, number, outcome,
)

WARP = base.load("warp.json")
pytestmark = [
    pytest.mark.skipif(not base.LIVE, reason="set EVE_LIVE_HOST=host:port to test against a running server"),
    pytest.mark.skipif(os.environ.get("EVE_LIVE_SLOW") != "1", reason="a minute and a half: set EVE_LIVE_SLOW=1"),
]

WAITS = {"bind the ship service": 0.5, "undock": 2.0, "bind the ballpark": 2.5}
# how far the seconds of a milestone may be from the recording's: both servers work them out from a ship that lines up as the physics of the
# integrator says, and a tick of theirs is a second
TOLERANCE = 3.0

def names(event: dict) -> list:
    if event["name"] != "DoDestinyUpdate":
        return []
    try:
        actions, _ = actions_of(event)
    except (KeyError, TypeError):
        return []
    return [action[1] for action in actions]

def milestones(events: list) -> dict:
    """The seconds after the call at which the ship dropped out (the massless updates) and came to rest (the stop), from the events."""
    out: dict = {}
    for event in events:
        for name in names(event):
            if name == "SetBallMassive" and "dropout" not in out:
                out["dropout"] = event["at"]
            if name == "Stop":
                out["rest"] = event["at"]
    return out

def without_ballpark_noise(events: list, reference_props: set[int] | frozenset[int] = frozenset()) -> list:
    """The events without the state the polling asks for and the balls the reference takes out of the ballpark in warp (DEVIATIONS #16)."""
    keep = []
    for event in events:
        if names(event) in (["SetState"], ["PackagedAction"]):
            continue
        event = copy.deepcopy(event)
        for _, name, args in actions_of(event)[0] if event["name"] == "DoDestinyUpdate" else []:
            if name == "RemoveBalls":
                removed = args["$tuple"][0]
                args["$tuple"][0] = [item for item in removed if number(item) not in reference_props]
        keep.append(event)
    return keep

def in_order(events: list) -> list:
    return sorted(events, key=lambda e: e.get("at", 0.0))

@pytest.mark.parametrize("race", WARP["races"], ids=lambda r: f"race-{r['raceID']}")
def test_a_ship_warps_to_a_planet_as_the_reference_did(race):
    run = uuid.uuid4().hex[:10]
    problems: list[str] = []
    steps = {step["label"]: step for step in race["steps"]}
    recent = next(r for r in UNDOCK["races"] if r["raceID"] == race["raceID"])
    recent_steps = {step["label"]: step for step in recent["steps"]}
    planet = race["planetID"]
    with base.connect(f"walk-warp-{run}") as who:
        _, ship_id, _, mapping = arrive(who, race, run)
        ship = who.call("ship", "MachoBindObject", (STATION, 15), None)
        wait_quietly(who, 0.5)
        raw, error = outcome(lambda: who.call_bound(ship[0], "Undock", ship_id, False))
        assert error is None, base.brief(error)
        undock_events = wait_quietly(who, 2.0)
        problems += compare_events(steps["undock"]["events"], undock_events, "undock", mapping)
        ballpark = who.call("beyonce", "MachoBindObject", SYSTEM, None)
        bind_events = wait_quietly(who, 2.5)
        reference_mapping = {
            **mapping,
            int(recent["characterID"]): mapping[int(race["characterID"])],
            int(recent["shipID"]): ship_id,
            **{int(old): mapping[int(original)] for old, original in zip(recent["moduleIDs"], race["moduleIDs"])},
        }
        def initial(events):
            return [event for event in events if event["name"] == "DoDestinyUpdate"]
        problems += compare_destiny_updates(
            initial(recent_steps["undock"]["events"] + recent_steps["bind the ballpark"]["events"]),
            initial(undock_events + bind_events), "initial destiny updates", reference_mapping,
        )
        mapping.update(reference_mapping)
        problems += compare_events(
            [e for e in steps["bind the ballpark"]["events"] if e["name"] != "DoDestinyUpdate"],
            [e for e in bind_events if e["name"] != "DoDestinyUpdate"], "bind the ballpark", mapping,
        )

        # the warp: the answer, then every event until the ship stops (asking for the state every three seconds, as the recording did)
        where = "warp to a planet"
        recorded = steps[where]
        started = time.time()
        raw, error = outcome(lambda: who.call_bound(ballpark[0], "CmdWarpToStuff", "item", planet, minRange=0))
        if error is not None:
            problems.append(f"{where}: error {base.brief(error)}")
        elif "error" in recorded or describe(raw) != recorded["ok"]:
            problems.append(f"{where}: answered {base.brief(describe(raw))} in place of {recorded['ok']}")
        else:
            assert who.call("achievementTrackerMgr", "HasEverWarped") is True
        events = timed_events(who, 1.0)
        while time.time() - started < 150 and not any("Stop" in names(e) for e in events):
            who.call_bound(ballpark[0], "UpdateStateRequest")
            seen = timed_events(who, 3.0)
            for event in seen:
                event["at"] = round(event["at"] + (time.time() - started) - 3.0, 2)
            events += seen
        reference_props = set()
        for event in steps["bind the ballpark"]["events"]:
            if event["name"] != "DoDestinyUpdate":
                continue
            for _, name, args in actions_of(event)[0]:
                if name == "AddBalls2":
                    for entry in args["$tuple"][0]["$tuple"][1]:
                        fields = keyval(add_ball_slim(entry))
                        item_id = number(fields["itemID"])
                        if fields.get("categoryID") == 2 and item_id >= FIRST_BALL_ID:
                            reference_props.add(item_id)
        ours = without_ballpark_noise(in_order(events))
        theirs = without_ballpark_noise(in_order(recorded["events"]), reference_props)
        problems += compare_events(theirs, ours, where, mapping)
        got, expected = milestones(ours), milestones(theirs)
        for name in ("dropout", "rest"):
            if name not in got:
                problems.append(f"{where}: the ship never came to {name}")
            elif abs(got[name] - expected[name]) > TOLERANCE:
                problems.append(f"{where}: {name} after {got[name]} s, in the recording after {expected[name]} s")

        # the ship stands by the planet: another warp there is a message, as is one to what is not there
        for label, target in (("warp to it again, in warp range", planet), ("a warp to what is not there", 99999999)):
            recorded = steps[label]
            raw, error = outcome(lambda: who.call_bound(ballpark[0], "CmdWarpToStuff", "item", target, minRange=0))
            timed_events(who, 1.0)
            if error is None:
                problems.append(f"{label}: answered {base.brief(describe(raw))} in place of the message")
            elif error != recorded["error"]["payload"]:
                problems.append(f"{label}: error {base.brief(error)} in place of {base.brief(recorded['error']['payload'])}")
    assert problems == [], "\n".join(problems[:40])

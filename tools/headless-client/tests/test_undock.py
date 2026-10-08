"""A character undocks, is sent the solar system, flies and docks again, compared with what the reference was recorded doing.

    EVE_LIVE_HOST=host:port [EVE_LIVE_SERVER=reference|eve.rs] pytest tests/test_undock.py

`oracle/fixtures/scenarios/undock.json` (made by scripts/record_character_scenarios.py --only undock) holds each call from the ship
service bound to the station to `CmdDock`, the answer, and every event the client was sent while it waited, with the destiny actions
opened. This replays the calls and compares. What a server may do differently is listed in docs/reference/compat/DEVIATIONS.md (#15 to #17) and
marked DEVIATION below: where the ship appears, the reference's customs offices and props, and the bound objects announced twice.

Skipped unless EVE_LIVE_HOST is set. tools/dev/run-eve-live.sh starts eve.rs (with its space simulation) and runs this.
"""

from __future__ import annotations

import re
import uuid

import pytest

from evecli import marshal as m
from evecli import packet as p
from evecli.describe import describe, differences, shape
from evecli.observe import wait_quietly
from tests import test_login_to_character as base

UNDOCK = base.load("undock.json")
REFERENCE = base.REFERENCE
pytestmark = pytest.mark.skipif(not base.LIVE, reason="set EVE_LIVE_HOST=host:port to test against a running server")

STATION = 60015249
SYSTEM = 30100032
# an event is compared to what was recorded by its name, how it is addressed and what it carries; these are the parts of what it carries
# that are per run, or that this server may give differently (DEVIATIONS.md)
POSITION_ACTIONS = {"SetBallPosition", "SetBallVelocity", "GotoDirection"}  # #15: where the ship appears
FIRST_BALL_ID = 10**12  # #16: the reference's customs offices and props have ids from here (the ship's is 9.9e12: it is the ego)


def keyval(described) -> dict:
    return {k: v for k, v in described["args"]["$dict"]}


def number(described):
    return described["$long"] if isinstance(described, dict) else described


def args_of(event: dict) -> dict:
    """What an event carries, as a `$tuple`: the body is `(0, (1, arguments))`."""
    body = event["args"]["$tuple"]
    return body[1]["$tuple"][1] if isinstance(body[1], dict) and "$tuple" in body[1] else event["args"]


def ids(described, mapping: dict):
    """A description with the ids of the recording replaced by this run's, and nothing else touched: a number that only happens to fit
    32 bits (a duration that is a long) stays what it was."""
    if isinstance(described, list):
        return [ids(v, mapping) for v in described]
    if isinstance(described, dict):
        if "$long" in described:
            given = mapping.get(described["$long"], described["$long"])
            return given if described["$long"] in mapping and -(2**31) <= given < 2**31 else {"$long": given}
        return {k: ids(v, mapping) for k, v in described.items()}
    if isinstance(described, int) and not isinstance(described, bool):
        return mapping.get(described, described)
    return described


def collapse(events: list[dict]) -> list[dict]:
    """The events with the bound objects that are let go, one after another, made one: how many an server announces depends on how many
    it kept (DEVIATION #18: the reference announces every object of the session again at a docking, one that has been announced once
    is not announced twice here)."""
    out: list[dict] = []
    for event in events:
        if event["name"] == "OnMachoObjectDisconnect" and out and out[-1]["name"] == "OnMachoObjectDisconnect":
            continue
        out.append(event)
    return out


def actions_of(event: dict):
    """The `(stamp, (name, args))` actions of a DoDestinyUpdate and whether the client waits: `(actions, wait)`."""
    actions, wait = args_of(event)["$tuple"]
    return [(a["$tuple"][0], a["$tuple"][1]["$tuple"][0], a["$tuple"][1]["$tuple"][1]) for a in actions], wait


def stamps_relative(actions) -> list[int]:
    """The stamps of actions as steps from the first: two servers run on different clocks."""
    first = actions[0][0]
    return [a[0] - first for a in actions]


def slim_by_id(slims: list) -> dict:
    return {number(keyval(s)["itemID"]): s for s in slims}


def compare_state(recorded: dict, ours: dict, where: str, mapping: dict) -> list[str]:
    """A `SetState`: the same fields, the same ego, and the slim item of each body both servers have equal (the buffer of balls is checked
    against the recording by the simulation's own tests)."""
    out: list[str] = []
    expected, actual = keyval(recorded["$tuple"][0]), keyval(ours["$tuple"][0])
    if list(expected) != list(actual):
        return [f"{where}: fields {list(actual)} in place of {list(expected)}"]
    for name in ("industryLevel", "researchLevel", "dbuffState", "aggressors", "droneState", "solItem", "effectStates", "allianceBridges"):
        out += differences(base.blank_times(expected[name]), base.blank_times(actual[name]), f"{where}.{name}")
    if number(actual["ego"]) != mapping.get(number(expected["ego"]), number(expected["ego"])):
        out.append(f"{where}: ego {number(actual['ego'])} in place of the ship")
    ours_slims = slim_by_id(actual["slims"])
    recorded_slims = slim_by_id(ids(expected["slims"], mapping))
    # the reference's own additions (DEVIATION #16) are the only slim items missing here
    missing = [i for i in recorded_slims if i not in ours_slims and i < FIRST_BALL_ID]
    extra = [i for i in ours_slims if i not in recorded_slims]
    if missing or extra:
        out.append(f"{where}: slim items missing {missing[:5]} unexpected {extra[:5]}")
    for item_id in recorded_slims.keys() & ours_slims.keys():
        out += differences(recorded_slims[item_id], ours_slims[item_id], f"{where}.slims.{item_id}")
    if len(ours_slims) < 60:
        out.append(f"{where}: only {len(ours_slims)} slim items")
    return out


def compare_actions(recorded: list, ours: list, where: str, mapping: dict) -> list[str]:
    """The destiny actions of one update: the same names in the same order at the same steps of the stamp, and the same values (the ship's
    numbers, not where it is: DEVIATION #15)."""
    out: list[str] = []
    if [a[1] for a in recorded] != [a[1] for a in ours]:
        return [f"{where}: actions {[a[1] for a in ours]} in place of {[a[1] for a in recorded]}"]
    if stamps_relative(recorded) != stamps_relative(ours):
        out.append(f"{where}: stamps {stamps_relative(ours)} in place of {stamps_relative(recorded)}")
    for (_, name, expected), (_, _, actual) in zip(recorded, ours):
        expected = ids(expected, mapping)
        if name in POSITION_ACTIONS:
            if shape(expected) != shape(actual):
                out.append(f"{where}.{name}: shape {shape(actual)} in place of {shape(expected)}")
        elif name == "SetState":
            out += compare_state(expected, actual, f"{where}.SetState", mapping)
        elif name == "AddBalls2":
            out += compare_add_balls(expected, actual, f"{where}.AddBalls2")
        else:
            out += differences(base.blank_times(expected), base.blank_times(actual), f"{where}.{name}")
    return out


def compare_add_balls(recorded: dict, ours: dict, where: str) -> list[str]:
    """`AddBalls2` of the ship: its slim item and damage state equal; the ball's bytes hold the place (DEVIATION #15) and are as long."""
    (buffer_r, extra_r), (buffer_o, extra_o) = recorded["$tuple"][0]["$tuple"], ours["$tuple"][0]["$tuple"]
    out = []
    if len(buffer_r["$buffer"]) != len(buffer_o["$buffer"]):
        out.append(f"{where}: ball of {len(buffer_o['$buffer']) // 2} bytes in place of {len(buffer_r['$buffer']) // 2}")
    out += differences(base.blank_times(extra_r), base.blank_times(extra_o), f"{where}.slims")
    return out


def compare_event(recorded: dict, ours: dict, where: str, mapping: dict) -> list[str]:
    if (recorded["name"], recorded["idType"]) != (ours["name"], ours["idType"]):
        return [f"{where}: {ours['name']} ({ours['idType']}) in place of {recorded['name']} ({recorded['idType']})"]
    name = recorded["name"]
    if name == "$sessionchange":
        return _session_changes(recorded, ours, where, mapping)
    if name == "DoDestinyUpdate":
        (actions_r, wait_r), (actions_o, wait_o) = actions_of(recorded), actions_of(ours)
        out = [] if wait_r == wait_o else [f"{where}: wait {wait_o} in place of {wait_r}"]
        return out + compare_actions(actions_r, actions_o, where, mapping)
    if name == "OnMachoObjectDisconnect":
        # the object and the client are per run; the shape is what the client reads
        return [] if shape(args_of(recorded)) == shape(args_of(ours)) else [f"{where}: {shape(args_of(ours))} in place of {shape(args_of(recorded))}"]
    return differences(base.blank_times(ids(args_of(recorded), mapping)), base.blank_times(args_of(ours)), f"{where}.{name}")


def _session_changes(recorded, ours, where, mapping) -> list[str]:
    names = lambda d: [k for k, _ in d["$tuple"][4]["$tuple"][1]["$tuple"][1]["$dict"]]  # noqa: E731
    changed = lambda d: d["$tuple"][4]["$tuple"][1]["$tuple"][1]  # noqa: E731
    out = []
    if names(recorded["args"]) != names(ours["args"]):
        out.append(f"{where}: attributes {names(ours['args'])} in place of {names(recorded['args'])}")
    else:
        out += differences(ids(changed(recorded["args"]), mapping), changed(ours["args"]), f"{where}.changes")
    return out


def compare_events(recorded: list[dict], ours: list[dict], where: str, mapping: dict) -> list[str]:
    recorded, ours = collapse(recorded), collapse(ours)
    # DEVIATION #16: the reference sends the props of the stations one stamp after the state
    recorded = [e for e in recorded if not _is_props(e)]
    ours = [e for e in ours if not _is_props(e)]
    if [(e["name"], e["idType"]) for e in recorded] != [(e["name"], e["idType"]) for e in ours]:
        return [f"{where}: events {[e['name'] for e in ours]} in place of {[e['name'] for e in recorded]}"]
    out: list[str] = []
    for index, (a, b) in enumerate(zip(recorded, ours)):
        out += compare_event(a, b, f"{where}: event {index} {a['name']}", mapping)
    return out


def _is_props(event: dict) -> bool:
    if event["name"] != "DoDestinyUpdate":
        return False
    actions, _ = actions_of(event)
    return len(actions) == 1 and actions[0][1] == "AddBalls2" and len(actions[0][2]["$tuple"][0]["$tuple"][1]) > 1


def compare_answer(label: str, recorded: dict, ours: dict, where: str) -> list[str]:
    if label == "formations" and not REFERENCE:
        # DEVIATION #19: the reference hands out a cache object the client fetches from its proxy cache; eve.rs answers with the formations
        # inline, which is what the reference itself does for a result it does not cache at the proxy. The class and the check are the same.
        head = lambda d: (d["$object"], d["args"]["$tuple"][0])  # noqa: E731
        return [] if head(recorded) == head(ours) else [f"{where}: {head(ours)} in place of {head(recorded)}"]
    if REFERENCE and label not in ("bind the ship service", "undock", "bind the ballpark", "formations", "dock"):
        return differences(recorded, ours, where)
    # the objects and the times are per run: the shape is what the client reads
    return [] if shape(recorded) == shape(ours) else [f"{where}: answer {shape(ours)} in place of {shape(recorded)}"]


def arrive(who, race: dict, run: str) -> tuple[int, int, list, dict]:
    """A character like the recorded one is created and chosen and stands in its station with its ship: `(character id, ship id, the
    fitted modules' ids, the mapping from the recording's ids to this run's)`."""
    recorded_run = re.search(r"(\d{6,})$", race["create"]["call"][2][0]).group(1)
    service, method, *args = [race["create"]["call"][0], race["create"]["call"][1], *[base.revive(a, run, {}, recorded_run) for a in race["create"]["call"][2]]]
    kind, made = base.attempt(who, service, method, *args)
    assert kind == "ok", base.brief(made)
    character = int(made)
    assert base.attempt(who, "charUnboundMgr", "SelectCharacterID", character, None, False) == ("ok", None)
    manager = who.call("invbroker", "MachoBindObject", (STATION, 15), None)[0]
    hangar = who.call_bound(manager, "GetInventory", 10004)
    ship_id = int(who.call_bound(hangar, "List")[0].fields()["itemID"])
    info = who.call("dogmaIM", "MachoBindObject", (STATION, 15), ("GetAllInfo", (True, True, None), None))
    fitted = [number(k) for k, _ in keyval(describe(info[1]))["shipInfo"]["$dict"]][1:]
    mapping = {int(race["characterID"]): character, int(race["shipID"]): ship_id, **dict(zip(race["moduleIDs"], fitted))}
    wait_quietly(who, 1.0)  # what the choosing of the character and the binds sent
    return character, ship_id, fitted, mapping


def outcome(call):
    """What a call comes to: `(raw answer, None)` or `(None, the error described)`."""
    try:
        return call(), None
    except p.MachoError as failure:
        return None, describe(failure.payload)


@pytest.mark.parametrize("race", UNDOCK["races"], ids=lambda r: f"race-{r['raceID']}")
def test_a_ship_undocks_flies_and_docks_as_the_reference_did(race):
    run = uuid.uuid4().hex[:10]
    problems: list[str] = []
    with base.connect(f"walk-undock-{run}") as who:
        _, ship_id, fitted, mapping = arrive(who, race, run)

        calls = {
            "bind the ship service": (lambda: who.call("ship", "MachoBindObject", (STATION, 15), None), 0.5, None),
            "undock": (lambda: who.call_bound(ship[0], "Undock", ship_id, False, onlineModules=m.List(fitted)), 2.0, None),
            "bind the ballpark": (lambda: who.call("beyonce", "MachoBindObject", SYSTEM, None), 2.5, None),
            "formations": (lambda: who.call_bound(ballpark[0], "GetFormations"), 0.5, None),
            "update state": (lambda: who.call_bound(ballpark[0], "UpdateStateRequest"), 2.0, None),
            "speed fraction": (lambda: who.call_bound(ballpark[0], "CmdSetSpeedFraction", 0.5), 1.5, None),
            "go in a direction": (lambda: who.call_bound(ballpark[0], "CmdGotoDirection", 1.0, 0.0, 0.0), 1.5, None),
            "go in another direction": (lambda: who.call_bound(ballpark[0], "CmdGotoDirection", 0.0, 0.0, 1.0), 1.5, None),
            "stop": (lambda: who.call_bound(ballpark[0], "CmdStop"), 1.5, None),
            "dock": (lambda: who.call_bound(ballpark[0], "CmdDock", STATION), 9.0, lambda seen: any(e["name"] == "$sessionchange" for e in seen)),
        }
        ship = ballpark = None
        for step in race["steps"]:
            label = step["label"]
            where = f"race {race['raceID']} {label}"
            call, seconds, until = calls[label]
            raw, error = outcome(call)
            if error is not None:
                problems.append(f"{where}: error {base.brief(error)}")
                continue
            answer = describe(raw)
            if label == "bind the ship service":
                ship = raw
            if label == "bind the ballpark":
                ballpark = raw
            events = wait_quietly(who, seconds, until)
            if "ok" in step:
                problems += compare_answer(label, step["ok"], answer, where)
            problems += compare_events(step["events"], events, where, mapping)
    assert problems == [], "\n".join(problems[:40])

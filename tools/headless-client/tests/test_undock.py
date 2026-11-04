"""A character undocks, is sent the solar system, flies and docks again, compared with what the reference was recorded doing.

    EVE_LIVE_HOST=host:port [EVE_LIVE_SERVER=reference|eve.rs] pytest tests/test_undock.py

`oracle/fixtures/scenarios/undock.json` (made by scripts/record_character_scenarios.py --only undock) holds each call from the ship
service bound to the station to `CmdDock`, the answer, and every event the client was sent while it waited, with the destiny actions
opened. This replays the calls and compares. What a server may do differently is listed in docs/reference/compat/DEVIATIONS.md (#15 to #17) and
marked DEVIATION below: where the ship appears, the reference's customs offices and props, and the bound objects announced twice.

Skipped unless EVE_LIVE_HOST is set. tools/dev/run-eve-live.sh starts eve.rs (with its space simulation) and runs this.
"""

from __future__ import annotations

import json
import re

import pytest

from tests.runid import run_token

from evecli import marshal as m
from evecli import packet as p
from evecli.describe import describe, differences, shape
from evecli.observe import _expand_destiny_actions, wait_quietly
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
    fields = described.get("args", described)
    return {k: v for k, v in fields["$dict"]}

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
    """The actions expanded the same way Michelle.DoDestinyUpdate expands PackagedAction."""
    actions, wait = args_of(event)["$tuple"]
    expanded = _expand_destiny_actions(actions)
    return [(number(action["stamp"]), action["name"], action["args"]) for action in expanded], wait

def stamps_relative(actions) -> list[int]:
    """The stamps of actions as steps from the first: two servers run on different clocks."""
    first = actions[0][0]
    return [a[0] - first for a in actions]

def slim_by_id(slims: list) -> dict:
    return {number(keyval(s)["itemID"]): s for s in slims}

def is_sentry_slim(slim: dict) -> bool:
    return number(keyval(slim).get("groupID", 0)) == 99

def add_ball_slim(entry: dict) -> dict:
    return entry["$tuple"][0] if "$tuple" in entry else entry

def add_ball_damage(entry: dict):
    return entry["$tuple"][1] if "$tuple" in entry else None

def map_equivalent_slim_ids(recorded: list, ours: list, mapping: dict) -> None:
    """Map entity IDs only when the complete slim item otherwise matches."""
    recorded_slims = slim_by_id(ids(recorded, mapping))
    ours_slims = slim_by_id(ours)

    def signature(slim: dict) -> str:
        fields = keyval(slim)
        fields.pop("itemID")
        return json.dumps(base.blank_times(fields), sort_keys=True, separators=(",", ":"))

    recorded_by_signature: dict[str, list[int]] = {}
    ours_by_signature: dict[str, list[int]] = {}
    for item_id, slim in recorded_slims.items():
        if item_id not in ours_slims:
            recorded_by_signature.setdefault(signature(slim), []).append(item_id)
    for item_id, slim in ours_slims.items():
        if item_id not in recorded_slims:
            ours_by_signature.setdefault(signature(slim), []).append(item_id)
    for key, recorded_ids in recorded_by_signature.items():
        for recorded_id, item_id in zip(sorted(recorded_ids), sorted(ours_by_signature.get(key, []))):
            mapping[recorded_id] = item_id

def compare_state(recorded: dict, ours: dict, where: str, mapping: dict) -> list[str]:
    """Compare non-sentry state; active sentries are checked against their AddBalls2 entries."""
    out: list[str] = []
    expected, actual = keyval(recorded["$tuple"][0]), keyval(ours["$tuple"][0])
    if list(expected) != list(actual):
        return [f"{where}: fields {list(actual)} in place of {list(expected)}"]
    for name in ("industryLevel", "researchLevel", "dbuffState", "aggressors", "droneState", "solItem", "effectStates", "allianceBridges"):
        out += differences(base.blank_times(expected[name]), base.blank_times(actual[name]), f"{where}.{name}")
    if number(actual["ego"]) != mapping.get(number(expected["ego"]), number(expected["ego"])):
        out.append(f"{where}: ego {number(actual['ego'])} in place of the ship")
    expected_slims = [slim for slim in expected["slims"] if not is_sentry_slim(slim)]
    actual_slims = [slim for slim in actual["slims"] if not is_sentry_slim(slim)]
    map_equivalent_slim_ids(expected_slims, actual_slims, mapping)
    ours_slims = slim_by_id(actual_slims)
    recorded_slims = slim_by_id(ids(expected_slims, mapping))
    missing = [i for i in recorded_slims if i not in ours_slims and i < FIRST_BALL_ID]
    extra = [i for i in ours_slims if i not in recorded_slims]
    if missing or extra:
        out.append(f"{where}: slim items missing {missing[:5]} unexpected {extra[:5]}")
    for item_id in recorded_slims.keys() & ours_slims.keys():
        out += differences(recorded_slims[item_id], ours_slims[item_id], f"{where}.slims.{item_id}")
    state_sentries = slim_by_id([slim for slim in actual["slims"] if is_sentry_slim(slim)])
    added_sentries = mapping.get("_sentry_slims", {})
    if set(state_sentries) != set(added_sentries):
        out.append(f"{where}: state sentries {sorted(state_sentries)} differ from added sentries {sorted(added_sentries)}")
    for item_id in state_sentries.keys() & added_sentries.keys():
        added_fields, state_fields = keyval(added_sentries[item_id]), keyval(state_sentries[item_id])
        if list(added_fields) != list(state_fields):
            out.append(f"{where}.sentries.{item_id}: fields {list(state_fields)} in place of {list(added_fields)}")
        else:
            for field in added_fields:
                out += differences(
                    base.blank_times(added_fields[field]),
                    base.blank_times(state_fields[field]),
                    f"{where}.sentries.{item_id}.{field}",
                )
    if len(actual["slims"]) < 60:
        out.append(f"{where}: only {len(actual['slims'])} slim items")
    return out

def compare_actions(recorded: list, ours: list, where: str, mapping: dict) -> list[str]:
    """The destiny actions of one update: the same names in the same order at the same steps of the stamp, and the same values (the ship's
    numbers, not where it is: DEVIATION #15)."""
    out: list[str] = []
    if [a[1] for a in recorded] != [a[1] for a in ours]:
        def summary(actions):
            return [
                f"{action[1]}({number(action[2]['$tuple'][0])}, {action[2]['$tuple'][1]})"
                if action[1] in {"SetBallAgility", "SetBallMass", "SetMaxSpeed"}
                else action[1]
                for action in actions
            ]

        return [f"{where}: actions {summary(ours)} in place of {summary(recorded)}"]
    if recorded:
        recorded_groups = [i for i, action in enumerate(recorded) if i == 0 or action[0] != recorded[i - 1][0]]
        actual_groups = [i for i, action in enumerate(ours) if i == 0 or action[0] != ours[i - 1][0]]
        if recorded_groups != actual_groups:
            out.append(f"{where}: stamp groups {actual_groups} in place of {recorded_groups}")
        else:
            recorded_stamps = [recorded[i][0] for i in recorded_groups]
            actual_stamps = [ours[i][0] for i in actual_groups]
            expected_gaps = [b - a for a, b in zip(recorded_stamps, recorded_stamps[1:])]
            actual_gaps = [b - a for a, b in zip(actual_stamps, actual_stamps[1:])]
            if any(abs(a - b) > 1 for a, b in zip(expected_gaps, actual_gaps)):
                out.append(f"{where}: stamp gaps {actual_gaps} in place of {expected_gaps}")
    for (_, name, expected), (_, _, actual) in zip(recorded, ours):
        expected = ids(expected, mapping)
        if name in POSITION_ACTIONS:
            if shape(expected) != shape(actual):
                out.append(f"{where}.{name}: shape {shape(actual)} in place of {shape(expected)}")
        elif name == "SetState":
            out += compare_state(expected, actual, f"{where}.SetState", mapping)
        elif name == "AddBalls2":
            out += compare_add_balls(expected, actual, f"{where}.AddBalls2", mapping)
        else:
            out += differences(base.blank_times(expected), base.blank_times(actual), f"{where}.{name}")
    return out

def compare_destiny_updates(recorded: list, ours: list, where: str, mapping: dict) -> list[str]:
    """Compare what the client's FlushState sees, independent of notification packet boundaries."""
    def groups(events: list) -> list[tuple[list, bool]]:
        by_stamp: dict[int, dict] = {}
        for event in events:
            actions, wait = actions_of(event)
            update_stamps: dict[int, list] = {}
            for action in actions:
                update_stamps.setdefault(action[0], []).append(action)
            for stamp in sorted(update_stamps):
                if stamp in by_stamp:
                    by_stamp[stamp]["actions"].extend(update_stamps[stamp])
                    by_stamp[stamp]["wait"] = False
                else:
                    by_stamp[stamp] = {"actions": update_stamps[stamp], "wait": bool(wait)}
                    wait = False
        return [(entry["actions"], entry["wait"]) for _, entry in sorted(by_stamp.items())]

    expected, actual = groups(recorded), groups(ours)
    out: list[str] = []
    if [wait for _, wait in expected] != [wait for _, wait in actual]:
        out.append(f"{where}: wait groups {[wait for _, wait in actual]} in place of {[wait for _, wait in expected]}")
    expected_actions = [action for actions, _ in expected for action in actions]
    actual_actions = [action for actions, _ in actual for action in actions]
    return out + compare_actions(expected_actions, actual_actions, where, mapping)

def compare_add_balls(recorded: dict, ours: dict, where: str, mapping: dict) -> list[str]:
    """Compare ball IDs and SDE identity; EveJS sends visual sentries while eve.rs sends active sentry NPCs."""
    (_, expected_entries), (_, actual_entries) = recorded["$tuple"][0]["$tuple"], ours["$tuple"][0]["$tuple"]
    expected_sentries = [add_ball_slim(entry) for entry in expected_entries if is_sentry_slim(add_ball_slim(entry))]
    actual_sentries = [add_ball_slim(entry) for entry in actual_entries if is_sentry_slim(add_ball_slim(entry))]
    out: list[str] = []

    def sentries_by_type(entries: list[dict]) -> dict[int, list[dict]]:
        grouped: dict[int, list[dict]] = {}
        for slim in entries:
            grouped.setdefault(number(keyval(slim)["typeID"]), []).append(slim)
        return grouped

    expected_by_type, actual_by_type = sentries_by_type(expected_sentries), sentries_by_type(actual_sentries)
    if {type_id: len(items) for type_id, items in expected_by_type.items()} != {
        type_id: len(items) for type_id, items in actual_by_type.items()
    }:
        out.append(f"{where}: sentry type counts { {k: len(v) for k, v in actual_by_type.items()} } in place of { {k: len(v) for k, v in expected_by_type.items()} }")

    map_equivalent_slim_ids(expected_sentries, actual_sentries, mapping)
    expected_sentry_rows = {
        number(keyval(slim)["itemID"]): slim for slim in ids(expected_sentries, mapping)
    }
    actual_sentry_rows = {
        number(keyval(slim)["itemID"]): slim for slim in actual_sentries
    }
    for type_id in expected_by_type.keys() & actual_by_type.keys():
        expected_unmatched = [
            slim for item_id, slim in expected_sentry_rows.items()
            if item_id not in actual_sentry_rows and number(keyval(slim)["typeID"]) == type_id
        ]
        actual_unmatched = [
            slim for item_id, slim in actual_sentry_rows.items()
            if item_id not in expected_sentry_rows and number(keyval(slim)["typeID"]) == type_id
        ]
        for expected_slim, actual_slim in zip(
            sorted(expected_unmatched, key=lambda slim: number(keyval(slim)["itemID"])),
            sorted(actual_unmatched, key=lambda slim: number(keyval(slim)["itemID"])),
        ):
            mapping[number(keyval(expected_slim)["itemID"])] = number(keyval(actual_slim)["itemID"])
            expected_sentry_rows[number(keyval(actual_slim)["itemID"])] = ids(expected_slim, mapping)
    expected_sentry_rows = {
        number(keyval(slim)["itemID"]): slim for slim in ids(expected_sentries, mapping)
    }
    if set(expected_sentry_rows) != set(actual_sentry_rows):
        out.append(f"{where}: sentry IDs {sorted(actual_sentry_rows)} in place of {sorted(expected_sentry_rows)}")
    for item_id in expected_sentry_rows.keys() & actual_sentry_rows.keys():
        expected_fields, actual_fields = keyval(expected_sentry_rows[item_id]), keyval(actual_sentry_rows[item_id])
        for field in ("typeID", "groupID", "categoryID"):
            if expected_fields.get(field) != actual_fields.get(field):
                out.append(f"{where}.sentries.{item_id}.{field}: {actual_fields.get(field)!r} in place of {expected_fields.get(field)!r}")
    mapping["_sentry_slims"] = actual_sentry_rows

    expected_other = {}
    for entry in expected_entries:
        slim = add_ball_slim(entry)
        if is_sentry_slim(slim):
            continue
        item_id = number(keyval(slim)["itemID"])
        mapped_id = mapping.get(item_id, item_id)
        if mapped_id >= FIRST_BALL_ID:
            continue
        expected_other[mapped_id] = (ids(slim, mapping), add_ball_damage(entry))
    actual_other = {
        number(keyval(slim)["itemID"]): (slim, damage)
        for slim, damage in (
            (add_ball_slim(entry), add_ball_damage(entry)) for entry in actual_entries
        )
        if not is_sentry_slim(slim)
    }
    if set(expected_other) != set(actual_other):
        out.append(f"{where}: ball IDs {sorted(actual_other)} in place of {sorted(expected_other)}")
    for item_id in expected_other.keys() & actual_other.keys():
        expected_slim, expected_damage = expected_other[item_id]
        actual_slim, actual_damage = actual_other[item_id]
        out += differences(base.blank_times(expected_slim), base.blank_times(actual_slim), f"{where}.slims.{item_id}")
        if expected_damage is not None and actual_damage is not None:
            out += differences(base.blank_times(expected_damage), base.blank_times(actual_damage), f"{where}.damage.{item_id}")
    # The reference-only customs offices and station props (#16) make the opaque ball buffers differ in length.
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
        expected, actual = args_of(recorded), args_of(ours)
        expected_shape, actual_shape = shape(expected), shape(actual)
        expected_args, actual_args = expected.get("$tuple", []), actual.get("$tuple", [])
        def integer(value):
            return type(value) is int or (isinstance(value, dict) and set(value) == {"$long"} and type(value["$long"]) is int)
        # The client accepts either Python integer type for clientID.
        if len(expected_args) == len(actual_args) == 3 and integer(expected_args[1]) and integer(actual_args[1]):
            actual_shape["$tuple"][1] = expected_shape["$tuple"][1]
        return [] if expected_shape == actual_shape else [f"{where}: {shape(actual)} in place of {shape(expected)}"]
    return differences(base.blank_times(ids(args_of(recorded), mapping)), base.blank_times(args_of(ours)), f"{where}.{name}")

def _session_changes(recorded, ours, where, mapping) -> list[str]:
    names = lambda d: [k for k, _ in d["$tuple"][4]["$tuple"][1]["$tuple"][1]["$dict"]]  # noqa: E731
    changed = lambda d: d["$tuple"][4]["$tuple"][1]["$tuple"][1]  # noqa: E731
    expected_changes, actual_changes = changed(recorded["args"]), changed(ours["args"])
    expected_names, actual_names = names(recorded["args"]), names(ours["args"])
    out = []
    if expected_names != actual_names:
        out.append(f"{where}: attributes {actual_names} in place of {expected_names}")
    else:
        out += differences(ids(expected_changes, mapping), actual_changes, f"{where}.changes")
    return out

def compare_events_by_source(recorded: list[dict], ours: list[dict], where: str, mapping: dict) -> list[str]:
    """Compare ordered streams while allowing simulation and inventory notifications to interleave."""
    problems: list[str] = []
    for is_simulation in (True, False):
        problems.extend(compare_events(
            [event for event in recorded if (event["name"] == "DoDestinyUpdate") == is_simulation],
            [event for event in ours if (event["name"] == "DoDestinyUpdate") == is_simulation],
            f"{where} ({'simulation' if is_simulation else 'session'})",
            mapping,
        ))
    return problems


def compare_events(recorded: list[dict], ours: list[dict], where: str, mapping: dict) -> list[str]:
    recorded, ours = collapse(recorded), collapse(ours)
    # The simulation and the invulnerability timer publish independently. Keep checking the timer
    # notice and its payload, but do not require it to arrive before or after an unrelated update.
    recorded_timer = [event for event in recorded if event["name"] == "OnInvulnCancelled"]
    ours_timer = [event for event in ours if event["name"] == "OnInvulnCancelled"]
    recorded = [event for event in recorded if event["name"] != "OnInvulnCancelled"]
    ours = [event for event in ours if event["name"] != "OnInvulnCancelled"]
    # DEVIATION #16: the reference sends the props of the stations one stamp after the state
    recorded = [e for e in recorded if not _is_props(e)]
    ours = [e for e in ours if not _is_props(e)]
    if [(e["name"], e["idType"]) for e in recorded] != [(e["name"], e["idType"]) for e in ours]:
        return [f"{where}: events {[e['name'] for e in ours]} in place of {[e['name'] for e in recorded]}"]
    out: list[str] = []
    for index, (a, b) in enumerate(zip(recorded, ours)):
        out += compare_event(a, b, f"{where}: event {index} {a['name']}", mapping)
    if [(e["name"], e["idType"]) for e in recorded_timer] != [(e["name"], e["idType"]) for e in ours_timer]:
        out.append(f"{where}: timer events {[e['name'] for e in ours_timer]} in place of {[e['name'] for e in recorded_timer]}")
    else:
        for index, (a, b) in enumerate(zip(recorded_timer, ours_timer)):
            out += compare_event(a, b, f"{where}: timer event {index} {a['name']}", mapping)
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

def arrive(who, race: dict, run: str, before_select=None) -> tuple[int, int, list, dict]:
    """A character like the recorded one is created and chosen and stands in its station with its ship: `(character id, ship id, the
    fitted modules' ids, the mapping from the recording's ids to this run's)`."""
    recorded_run = re.search(r"(\d{6,})$", race["create"]["call"][2][0]).group(1)
    service, method, *args = [race["create"]["call"][0], race["create"]["call"][1], *[base.revive(a, run, {}, recorded_run) for a in race["create"]["call"][2]]]
    kind, made = base.attempt(who, service, method, *args)
    assert kind == "ok", base.brief(made)
    character = int(made)
    if before_select is not None:
        before_select(character)
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
    run = run_token()
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
        expected_destiny_updates = []
        actual_destiny_updates = []
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
                expected = None if label == "dock" else step["ok"]
                problems += compare_answer(label, expected, answer, where)
            expected_destiny_updates.extend(event for event in step["events"] if event["name"] == "DoDestinyUpdate")
            actual_destiny_updates.extend(event for event in events if event["name"] == "DoDestinyUpdate")
            # The client handles DoDestinyUpdate as an asynchronous ballpark stream, independent of the current RPC reply.
            problems += compare_events(
                [event for event in step["events"] if event["name"] != "DoDestinyUpdate"],
                [event for event in events if event["name"] != "DoDestinyUpdate"],
                where,
                mapping,
            )
        problems += compare_destiny_updates(
            expected_destiny_updates,
            actual_destiny_updates,
            f"race {race['raceID']} destiny updates",
            mapping,
        )
    assert problems == [], "\n".join(problems[:40])

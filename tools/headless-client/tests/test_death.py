"""Ships that die beside a pilot, compared with what the reference was recorded doing.

    EVE_LIVE_SLOW=1 EVE_LIVE_HOST=host:port [EVE_LIVE_SERVER=reference|eve.rs] pytest tests/test_death.py

`oracle/fixtures/scenarios/death.json` (scripts/record_character_scenarios.py --only death) holds two characters by the same station: the watcher undocks
and binds, then the victim does, both bind dogma and wait out their undocking protection. The watcher detonates a hull beside both (`/deathtest 1`: a hull
nobody flies comes into space, and two seconds on it blows up and leaves a wreck) and, at the end, the victim destroys its own ship (`/suicide`). Each step
holds what the victim was sent (`events`) and what the watcher was sent (`seen`), with the seconds each arrived at. The replay does the same on any server
and compares the answers, the events and their order, and the moments. What a server may do differently is in docs/reference/compat/DEVIATIONS.md. The hull is a
transient ship and its wreck an item the reference makes in its database: the ids of both are per run and compared by type only.
It waits out the undocking protection first, so it runs only with EVE_LIVE_SLOW=1.
"""

from __future__ import annotations

import os
import re
import uuid

import pytest

from evecli.describe import shape
from tests import test_login_to_character as base
from tests.test_others import names, watch
from tests.test_target import plain_race, timing
from tests.test_undock import STATION, SYSTEM, arrive, collapse, compare_events, outcome

DEATH = base.load("death.json")
pytestmark = [
    pytest.mark.skipif(not base.LIVE, reason="set EVE_LIVE_HOST=host:port to test against a running server"),
    pytest.mark.skipif(os.environ.get("EVE_LIVE_SLOW") != "1", reason="a minute: set EVE_LIVE_SLOW=1"),
]

# the moment a ball goes may differ from the recording's by this much: the reference blows a hull up about 3.7 s after it comes (its own two seconds of
# simulation time and the way its scene delivers them), eve.rs two seconds on (docs/reference/compat/DEVIATIONS.md #49)
TOLERANCE = 1.6


def without_protection(events: list) -> list:
    return [e for e in events if e["name"] != "OnInvulnCancelled"]


HULL = 4_200_000_000_000_000
WRECK_ITEM = 9_988_400_002_415


def canonical(node):
    """The events with what is per run made the same: the id of a transient hull (the reference numbers them from 4.2e15, eve.rs from one past), the item id of
    the wreck (a row of the reference's database, a number of the simulation's in eve.rs), and where the hull happened to face (the random part of the
    wreck's `dunRotation`)."""
    if isinstance(node, list):
        return [canonical(item) for item in node]
    if not isinstance(node, dict):
        return node
    if "$long" in node and 4_200_000_000_000_000 <= int(node["$long"]) < 4_400_000_000_000_000:
        return {"$long": HULL}
    if "$dict" in node:
        pairs = node["$dict"]
        is_wreck = any(k == "groupID" and v == 186 for k, v in pairs)
        out = []
        for key, value in pairs:
            if is_wreck and key == "itemID":
                value = {"$long": WRECK_ITEM}
            elif is_wreck and key == "dunRotation":
                value = {"$tuple": [0, 0, 0]}
            out.append([key, canonical(value)])
        return {"$dict": out}
    return {k: canonical(v) for k, v in node.items()}


def test_a_hull_that_blows_up_beside_two_pilots_is_seen_as_it_was_in_the_reference():
    race = DEATH["races"][0]
    run = uuid.uuid4().hex[:10]
    problems: list[str] = []
    steps = {step["label"]: step for step in race["steps"]}
    with base.connect(f"walk-death-a-{run}") as a, base.connect(f"walk-death-b-{run}") as b:
        _, a_ship, _, a_mapping = arrive(a, plain_race(race, "watcher"), run)
        _, b_ship, _, b_mapping = arrive(b, plain_race(race, "victim"), run)
        mapping = {**a_mapping, **b_mapping}
        both = {"events": b, "seen": a}
        a_service = a.call("ship", "MachoBindObject", (STATION, 15), None)
        b_service = b.call("ship", "MachoBindObject", (STATION, 15), None)
        watch(both, 0.5)
        a.call_bound(a_service[0], "Undock", a_ship, False)
        a.call("beyonce", "MachoBindObject", SYSTEM, None)
        watch(both, 2.5)
        b.call_bound(b_service[0], "Undock", b_ship, False)
        b.call("beyonce", "MachoBindObject", SYSTEM, None)
        watch(both, 3.0)
        a.call("dogmaIM", "MachoBindObject", (SYSTEM, 5), None)
        b.call("dogmaIM", "MachoBindObject", (SYSTEM, 5), None)
        watch(both, 45.0, until=lambda seen: any(e["name"] == "OnInvulnCancelled" for e in seen["events"]))

        label = "the watcher detonates a hull beside both"
        raw, error = outcome(lambda: a.call("slash", "SlashCmd", "/deathtest 1"))
        if error is not None:
            problems.append(f"{label}: an error {error}")
        elif shape(steps[label]["ok"]) != shape(m_text(raw)):
            problems.append(f"{label}: answered {raw!r} in place of the recording's text")
        got = watch(both, 40.0, until=lambda seen: sum(1 for e in seen["events"] + seen["seen"] if _names(e) & {"RemoveBalls"}) >= 2)
        for who, key in (("the victim", "events"), ("the watcher", "seen")):
            theirs, ours = canonical(without_protection(steps[label][key])), canonical(without_protection(got[key]))
            problems.extend(compare_events(theirs, ours, f"{label} ({who})", mapping))
            problems.extend(timing(theirs, ours, f"{label} ({who})", TOLERANCE, ("DoDestinyUpdate",)))
    assert problems == [], "\n".join(problems[:40])


def m_text(raw):
    from evecli.describe import describe

    return describe(raw)


def _names(event: dict) -> set:
    """The names of the destiny actions of an event."""
    try:
        actions = event["args"]["$tuple"][1]["$tuple"][1]["$tuple"][0]
    except (KeyError, TypeError):
        return set()
    return {a["$tuple"][1]["$tuple"][0] for a in actions}


# ── the pilot destroys its own ship ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────

KEEP_ALIVE = {"SetBallPosition", "SetBallVelocity", "SetSpeedFraction", "Stop"}


def victim_view(events: list) -> list:
    """What the pilot was told, as both servers can be compared: the events in order but for what a server may do differently (docs/reference/compat/DEVIATIONS.md
    #50): the reference lets go of the bound objects and announces the capsule before it sends the explosion, eve.rs after (the explosion goes first
    in both views); the reference repeats a position of the capsule every few seconds, eve.rs does not."""
    view = []
    for event in collapse(without_protection(events)):
        name = event["name"]
        if name == "DoDestinyUpdate":
            acts = names(event)
            if set(acts) <= KEEP_ALIVE:
                continue
            view.append((name, tuple(acts)))
        else:
            view.append((name, event["idType"]))
    explosion = [v for v in view if v[1] and v[1][0] == "TerminalPlayDestructionEffect"]
    return explosion + [v for v in view if v not in explosion]


def item_rows(events: list) -> list:
    """The rows of the `OnItemsChanged` events (in order) and where each came from."""
    out = []
    for event in events:
        if event["name"] != "OnItemsChanged":
            continue
        rows, moved_from, _ = event["args"]["$tuple"][1]["$tuple"][1]["$tuple"]
        out.append((rows[0]["$row"] if "$row" in rows[0] else rows[0], moved_from))
    return out


def test_a_pilot_who_destroys_its_own_ship_is_left_in_a_capsule_as_in_the_reference():
    race = DEATH["races"][0]
    run = uuid.uuid4().hex[:10]
    problems: list[str] = []
    label = "the victim destroys its ship"
    step = {s["label"]: s for s in race["steps"]}[label]
    with base.connect(f"walk-suicide-a-{run}") as a, base.connect(f"walk-suicide-b-{run}") as b:
        _, a_ship, _, a_mapping = arrive(a, plain_race(race, "watcher"), run)
        _, b_ship, b_fitted, b_mapping = arrive(b, plain_race(race, "victim"), run)
        both = {"events": b, "seen": a}
        a_service = a.call("ship", "MachoBindObject", (STATION, 15), None)
        b_service = b.call("ship", "MachoBindObject", (STATION, 15), None)
        watch(both, 0.5)
        a.call_bound(a_service[0], "Undock", a_ship, False)
        a.call("beyonce", "MachoBindObject", SYSTEM, None)
        watch(both, 2.5)
        b.call_bound(b_service[0], "Undock", b_ship, False)
        b.call("beyonce", "MachoBindObject", SYSTEM, None)
        watch(both, 3.0)
        a.call("dogmaIM", "MachoBindObject", (SYSTEM, 5), None)
        b.call("dogmaIM", "MachoBindObject", (SYSTEM, 5), None)
        watch(both, 45.0, until=lambda seen: any(e["name"] == "OnInvulnCancelled" for e in seen["events"]))

        raw, error = outcome(lambda: b.call("slash", "SlashCmd", "/suicide"))
        if error is not None:
            problems.append(f"{label}: an error {error}")
        got = watch(both, 30.0, until=lambda seen: sum(1 for e in seen["events"] if e["name"] == "DoDestinyUpdate" and "AddBalls2" in names(e)) >= 2)
        watch(both, 1.0)

        # the words, but for the ids that are per run
        text = m_text(raw)
        if re.sub(r"\d+", "#", str(text)) != re.sub(r"\d+", "#", str(step["ok"])):
            problems.append(f"{label}: answered {text!r} in place of {step['ok']!r}")

        mine, theirs = victim_view(got["events"]), victim_view(step["events"])
        if mine != theirs:
            problems.append(f"{label}: the victim was told {mine} in place of {theirs}")

        # the pilot is in a capsule: destroying that wakes it in its station, in a rookie ship, and leaves its corpse where the capsule was
        pod_label = "the victim destroys its capsule"
        pod_step = {s["label"]: s for s in race["steps"]}[pod_label]
        raw, error = outcome(lambda: b.call("slash", "SlashCmd", "/suicide"))
        if error is not None:
            problems.append(f"{pod_label}: an error {error}")
        else:
            text = re.sub(r"\d+", "#", str(m_text(raw)))
            recorded_text = re.sub(r"\d+", "#", str(pod_step["ok"]))
            # the pilot's name is per run (and has digits in both): the words around it are what is compared
            if re.sub(r"Corpse: .*? \(#\)", "Corpse: NAME (#)", text) != re.sub(r"Corpse: .*? \(#\)", "Corpse: NAME (#)", recorded_text):
                problems.append(f"{pod_label}: answered {m_text(raw)!r} in place of {pod_step['ok']!r}")
        pod = watch(both, 8.0, until=lambda seen: len(seen["seen"]) >= 2 and len(seen["events"]) >= 4)
        watch(both, 1.0)
        mine, theirs = pod_view(pod["events"]), pod_view(pod_step["events"])
        if mine != theirs:
            problems.append(f"{pod_label}: the victim was told {mine} in place of {theirs}")
        mine, theirs = [names(e) for e in pod["seen"]], [names(e) for e in pod_step["seen"]]
        if mine != theirs:
            problems.append(f"{pod_label}: the watcher was sent {mine} in place of {theirs}")
    assert problems == [], "\n".join(problems[:40])


def _row(event: dict) -> dict:
    """The fields of the item row of an `OnItemChange`, and what they changed from."""
    row, changed, _ = event["args"]["$tuple"][1]["$tuple"][1]["$tuple"]
    return row["$row"], dict(tuple(pair) for pair in changed["$dict"])


def pod_view(events: list) -> list:
    """What a pilot whose capsule is destroyed was told, with what is per run made the same: the session change by the names it changes, the items by what they
    are (the type, the group, the place as `space`, the junkyard or a number) and where they came from."""
    view = []
    for event in events:
        name = event["name"]
        if name == "$sessionchange":
            # DEVIATION #51: the reference announces `stationid2` (from nothing to the station); eve.rs kept it since the pilot chose the character
            view.append((name, tuple(key for key, _ in event["args"]["$tuple"][4]["$tuple"][1]["$tuple"][1]["$dict"] if key != "stationid2")))
        elif name == "OnItemChange":
            row, changed = _row(event)
            place = lambda value: "junkyard" if value == 6 else ("space" if value == SYSTEM else value)  # noqa: E731
            view.append((name, row["typeID"], row["groupID"], row["categoryID"], row["flagID"], place(row["locationID"]), tuple((k, place(v)) for k, v in changed.items())))
        elif name == "OnCfgDataChanged":
            table, values = event["args"]["$tuple"][1]["$tuple"][1]["$tuple"]
            view.append((name, table, values[1], "space" if values[2] == SYSTEM else values[2], tuple(values[3:])))
        else:
            view.append((name, event["idType"]))
    return view

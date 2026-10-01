"""A pilot fills and empties the cargo hold of its ship, compared with what the reference was recorded doing.

    EVE_LIVE_HOST=host:port [EVE_LIVE_SERVER=reference|eve.rs] pytest tests/test_cargo.py

`oracle/fixtures/scenarios/cargo.json` (scripts/record_character_scenarios.py --only cargo) holds one character in its station with a stack of tritanium given:
asked for the hold and the room in it, the stack put in the hold (all of it, then part of a second, then more than the hold takes), the hold stacked, some
taken back to the hangar. The replay does the same on any server and compares, step by step, the answer (or the user error with its values) and every event
the client hears, in order; the ids of the stacks are per run and are compared by the order they first appear in. The steps in space (the hold in flight, a stack
jettisoned, the can opened and emptied) need the simulation and a minute: they run only with EVE_LIVE_SLOW=1.
"""

from __future__ import annotations

import os
import uuid

import pytest

from evecli.describe import describe, differences
from tests import test_login_to_character as base
from tests.test_ammo import canon, canon_events
from tests.test_others import watch
from tests.test_target import compare_answer, plain_race
from tests.test_undock import STATION, arrive, ids, outcome

CARGO = base.load("cargo.json")
pytestmark = pytest.mark.skipif(not base.LIVE, reason="set EVE_LIVE_HOST=host:port to test against a running server")

TRITANIUM = 34
LAST_DOCKED_STEP = "the pilot asks for its hold after"


def test_a_pilot_fills_and_empties_its_hold_as_in_the_reference():
    race = CARGO["races"][0]
    run = uuid.uuid4().hex[:10]
    problems: list[str] = []
    steps = {step["label"]: step for step in race["steps"]}
    seen_ids: dict = {}
    seen_recorded: dict = {}
    with base.connect(f"walk-cargo-{run}") as who:
        _, ship, _, mapping = arrive(who, plain_race({"pilot": race}, "pilot"), run)
        known = dict(mapping)
        only = {"events": who}
        manager = who.call("invbroker", "MachoBindObject", (STATION, 15), None)[0]
        hangar = who.call_bound(manager, "GetInventory", 10004)
        docked = who.call("invbroker", "MachoBindObject", (STATION, 15), ("GetInventoryFromId", (ship, 0), None))[1]
        slash = lambda command: (lambda: who.call("slash", "SlashCmd", command))  # noqa: E731

        def step(label: str, call, seconds: float = 1.0):
            """The answer and the events of a step, each against the recording's (what the answer holds is compared by value, with the ids of items by the order
            they appear in; a user error by its name and its values)."""
            recorded = steps[label]
            raw, error = outcome(call)
            if "error" in recorded or error is not None:
                problems.extend(compare_answer(label, recorded, raw, error, mapping, exact=("available", "volume")))
            else:
                theirs = canon(base.blank_times(ids(recorded["ok"], known)), seen_recorded)
                ours = canon(base.blank_times(describe(raw)), seen_ids)
                problems.extend(differences(theirs, ours, label))
            got = watch(only, seconds)["events"]
            problems.extend(base_events(recorded["events"], got, label))
            return raw

        def base_events(recorded: list, got: list, label: str) -> list[str]:
            from tests.test_undock import compare_events

            return compare_events(canon_events(recorded, seen_recorded, known), canon_events(got, seen_ids), label, {})

        def stacks(handle, flag=None) -> dict:
            rows = who.call_bound(handle, "List", flag) if flag else who.call_bound(handle, "List")
            found: dict = {}
            for row in rows if isinstance(rows, list) else []:
                fields = row.fields()
                found.setdefault(int(fields["typeID"]), []).append(int(fields["itemID"]))
            return found

        step("the pilot asks for its empty hold", lambda: who.call_bound(docked, "List", 5))
        step("the pilot asks for the room in its hold", lambda: who.call_bound(docked, "GetCapacity", 5))
        step("the pilot is given tritanium", slash("/giveitem Tritanium 100"))
        first = stacks(hangar)[TRITANIUM][0]
        step("the pilot puts all the tritanium in the hold", lambda: who.call_bound(docked, "Add", first, STATION, qty=100, flag=5), 1.5)
        step("the pilot asks for its hold", lambda: who.call_bound(docked, "List", 5))
        step("the pilot asks for the room in its hold again", lambda: who.call_bound(docked, "GetCapacity", 5))
        step("the pilot is given more tritanium", slash("/giveitem Tritanium 40"))
        second = stacks(hangar)[TRITANIUM][0]
        step("the pilot puts part of it in the hold", lambda: who.call_bound(docked, "Add", second, STATION, qty=15, flag=5), 1.5)
        step("the pilot asks for its hold with two stacks", lambda: who.call_bound(docked, "List", 5))
        step("the pilot stacks its hold", lambda: who.call_bound(docked, "StackAll", 5), 1.5)
        step("the pilot asks for its hold stacked", lambda: who.call_bound(docked, "List", 5))
        step("the pilot is given more than the hold takes", slash("/giveitem Tritanium 20000"))
        big = max(stacks(hangar)[TRITANIUM])
        step("the pilot puts more than the hold takes in it", lambda: who.call_bound(docked, "Add", big, STATION, qty=20000, flag=5), 1.5)
        step("the pilot asks for the room in its hold at the end", lambda: who.call_bound(docked, "GetCapacity", 5))
        inside = stacks(docked, 5)[TRITANIUM][0]
        step("the pilot takes some back to the hangar", lambda: who.call_bound(hangar, "Add", inside, ship, qty=20, flag=4), 1.5)
        step(LAST_DOCKED_STEP, lambda: who.call_bound(docked, "List", 5))
        if os.environ.get("EVE_LIVE_SLOW") != "1":
            assert problems == [], "\n".join(problems[:40])
            return
        # in space: see test_cargo_in_space below
    assert problems == [], "\n".join(problems[:40])

"""A pilot uses the drone bay of its ship, compared with what the reference was recorded doing.

    EVE_LIVE_HOST=host:port [EVE_LIVE_SERVER=reference|eve.rs] pytest tests/test_drones.py

`oracle/fixtures/scenarios/drones.json` (scripts/record_character_scenarios.py --only drones) holds one character in its station with two drones given (and some
tritanium): the bay asked for and the room in it (the rookie ship's holds one drone: 5 m3), a drone put in it (the stack is split, the new stack is the answer), the bay
asked for, a second drone refused for want of room (`NotEnoughDroneBaySpace`: the type, what is free and what it takes), tritanium refused (`Only drones can be placed in the
Drone Bay.`), the drone taken back to the hangar and put in again. The replay does the same on any server and compares, step by step, the answer (or the user error with its
values) and every event the client hears, in order; the ids of the stacks are per run and are compared by the order they first appear in. The steps in space (the bay and the room
in it asked for after an undock) are compared by their answers only: the reference also primes the drone's dogma attributes for the client the first time the bay is listed in
space (`OnGodmaPrimeItem`), which eve.rs does when it launches drones (DEVIATIONS #105). They need the simulation and a minute: they run only with EVE_LIVE_SLOW=1.
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
from tests.test_undock import STATION, SYSTEM, arrive, compare_events, ids, outcome

DRONES = base.load("drones.json")
pytestmark = pytest.mark.skipif(not base.LIVE, reason="set EVE_LIVE_HOST=host:port to test against a running server")

HOBGOBLIN, TRITANIUM = 2454, 34
LAST_DOCKED_STEP = "the pilot puts a drone in the drone bay for the flight"


def test_a_pilot_uses_its_drone_bay_as_in_the_reference():
    race = DRONES["races"][0]
    run = uuid.uuid4().hex[:10]
    problems: list[str] = []
    steps = {step["label"]: step for step in race["steps"]}
    seen_ids: dict = {}
    seen_recorded: dict = {}
    with base.connect(f"walk-drones-{run}") as who:
        _, ship, _, mapping = arrive(who, plain_race({"pilot": race}, "pilot"), run)
        known = dict(mapping)
        only = {"events": who}
        manager = who.call("invbroker", "MachoBindObject", (STATION, 15), None)[0]
        hangar = who.call_bound(manager, "GetInventory", 10004)
        docked = who.call("invbroker", "MachoBindObject", (STATION, 15), ("GetInventoryFromId", (ship, 0), None))[1]
        slash = lambda command: (lambda: who.call("slash", "SlashCmd", command))  # noqa: E731

        def step(label: str, call, seconds: float = 1.0, events: bool = True):
            """The answer and the events of a step, each against the recording's (what the answer holds is compared by value, with the ids of items by the order they appear
            in; a user error by its name and its values)."""
            recorded = steps[label]
            raw, error = outcome(call)
            if "error" in recorded or error is not None:
                problems.extend(compare_answer(label, recorded, raw, error, mapping, exact=("free", "required")))
            else:
                theirs = canon(base.blank_times(ids(recorded["ok"], known)), seen_recorded)
                ours = canon(base.blank_times(describe(raw)), seen_ids)
                problems.extend(differences(theirs, ours, label))
            got = watch(only, seconds)["events"]
            if events:
                problems.extend(compare_events(canon_events([e for e in recorded["events"] if e["name"] != "OnInvulnCancelled"], seen_recorded, known), canon_events([e for e in got if e["name"] != "OnInvulnCancelled"], seen_ids), label, {}))
            return raw

        def stacks(handle, flag=None) -> dict:
            rows = who.call_bound(handle, "List", flag) if flag else who.call_bound(handle, "List")
            found: dict = {}
            for row in rows if isinstance(rows, list) else []:
                fields = row.fields()
                found.setdefault(int(fields["typeID"]), []).append(int(fields["itemID"]))
            return found

        step("the pilot asks for its empty drone bay", lambda: who.call_bound(docked, "List", 87))
        step("the pilot asks for the room in its drone bay", lambda: who.call_bound(docked, "GetCapacity", 87))
        step("the pilot is given two drones", slash("/giveitem Hobgoblin I 2"))
        drones = stacks(hangar)[HOBGOBLIN][0]
        step("the pilot is given tritanium", slash("/giveitem Tritanium 10"))
        trit = stacks(hangar)[TRITANIUM][0]
        step("the pilot puts a drone in the drone bay", lambda: who.call_bound(docked, "Add", drones, STATION, qty=1, flag=87), 1.5)
        step("the pilot asks for its drone bay", lambda: who.call_bound(docked, "List", 87))
        step("the pilot asks for the room in its drone bay again", lambda: who.call_bound(docked, "GetCapacity", 87))
        rest = max(stacks(hangar)[HOBGOBLIN])
        step("the pilot puts another drone in a drone bay that is full", lambda: who.call_bound(docked, "Add", rest, STATION, qty=1, flag=87), 1.5)
        step("the pilot puts tritanium in the drone bay", lambda: who.call_bound(docked, "Add", trit, STATION, qty=5, flag=87), 1.5)
        step("the pilot asks for its drone bay after the refusals", lambda: who.call_bound(docked, "List", 87))
        inside = stacks(docked, 87)[HOBGOBLIN][0]
        step("the pilot takes the drone back to the hangar", lambda: who.call_bound(hangar, "Add", inside, ship, qty=1, flag=4), 1.5)
        step("the pilot asks for its drone bay at the end", lambda: who.call_bound(docked, "List", 87))
        step(LAST_DOCKED_STEP, lambda: who.call_bound(docked, "Add", drones, STATION, qty=1, flag=87), 1.5)
        if os.environ.get("EVE_LIVE_SLOW") != "1":
            assert problems == [], "\n".join(problems[:40])
            return

        # in space: the bay and the room in it, by their answers
        service = who.call("ship", "MachoBindObject", (STATION, 15), None)
        who.call_bound(service[0], "Undock", ship, False)
        watch(only, 2.5)
        who.call("beyonce", "MachoBindObject", SYSTEM, None)
        watch(only, 3.0)
        dogma = who.call("dogmaIM", "MachoBindObject", (SYSTEM, 5), None)
        who.call_bound(dogma[0], "GetAllInfo", True, True, None)
        watch(only, 1.0)
        watch(only, 45.0, until=lambda seen: any(e["name"] == "OnInvulnCancelled" for e in seen["events"]))
        flying = who.call("invbroker", "MachoBindObject", (SYSTEM, 5), ("GetInventoryFromId", (ship, 0), None))[1]
        step("the pilot asks for its drone bay in space", lambda: who.call_bound(flying, "List", 87), events=False)
        step("the pilot asks for the room in its drone bay in space", lambda: who.call_bound(flying, "GetCapacity", 87), events=False)
    assert problems == [], "\n".join(problems[:40])

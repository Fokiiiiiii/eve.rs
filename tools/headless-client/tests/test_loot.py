"""A pilot takes everything out of a can with the loot window's "Loot All", compared with what the reference was recorded doing.

    EVE_LIVE_SLOW=1 EVE_LIVE_HOST=host:port [EVE_LIVE_SERVER=reference|eve.rs] pytest tests/test_loot.py

`oracle/fixtures/scenarios/loot.json` (scripts/record_character_scenarios.py --only loot) holds one character with tritanium and pyerite put in the hold of its ship, then in
space: both stacks jettisoned into one can, the can opened and everything taken at once (`invbroker` `MultiAdd` on the ship's inventory: the item ids, the can they are in, `flag=5`),
the hold, its room and the can (which is gone) asked for afterwards. The replay does the same on any server and compares, step by step, the answer and every event the client hears, in order (the
ids of the items are per run and are compared by the order they first appear in). What the reference's own rats do to the pilot meanwhile is not compared: they are the reference's.
It needs the simulation and a minute, so it runs only with EVE_LIVE_SLOW=1.
"""

from __future__ import annotations

import os
import uuid

import pytest

from evecli import marshal as m
from evecli.describe import describe, differences
from tests import test_login_to_character as base
from tests.test_ammo import canon, canon_events
from tests.test_others import watch
from tests.test_target import compare_answer, plain_race
from tests.test_undock import STATION, SYSTEM, arrive, compare_events, ids, outcome
from tests.test_weapon import slims_in

LOOT = base.load("loot.json")
pytestmark = [
    pytest.mark.skipif(not base.LIVE, reason="set EVE_LIVE_HOST=host:port to test against a running server"),
    pytest.mark.skipif(os.environ.get("EVE_LIVE_SLOW") != "1", reason="a minute: set EVE_LIVE_SLOW=1"),
]

TRITANIUM, PYERITE = 34, 35

# what the reference's own rats and the recharge of the pilot's ship send meanwhile, which no server has to send the same
NOISE = {"OnInvulnCancelled", "OnDamageMessage", "OnModuleAttributeChanges", "OnTarget"}


def test_a_pilot_loots_a_can_as_in_the_reference():
    race = LOOT["races"][0]
    run = uuid.uuid4().hex[:10]
    problems: list[str] = []
    steps = {step["label"]: step for step in race["steps"]}
    seen_ids: dict = {}
    seen_recorded: dict = {}
    with base.connect(f"walk-loot-{run}") as who:
        _, ship, _, mapping = arrive(who, plain_race({"pilot": race}, "pilot"), run)
        known = dict(mapping)
        only = {"events": who}
        last: dict = {"events": []}
        manager = who.call("invbroker", "MachoBindObject", (STATION, 15), None)[0]
        hangar = who.call_bound(manager, "GetInventory", 10004)
        docked = who.call("invbroker", "MachoBindObject", (STATION, 15), ("GetInventoryFromId", (ship, 0), None))[1]
        slash = lambda command: (lambda: who.call("slash", "SlashCmd", command))  # noqa: E731

        def quiet(events: list) -> list:
            """What the client is sent that no server has to send alike: its own rats' shots, the end of its protection, its ship's recharge."""
            kept = []
            for event in events:
                if event["name"] in NOISE:
                    continue
                if event["name"] == "DoDestinyUpdate" and _is_the_rats(event):
                    continue
                kept.append(event)
            return kept

        def step(label: str, call, seconds: float = 1.0, until=None):
            recorded = steps[label]
            raw, error = outcome(call)
            if "error" in recorded or error is not None:
                problems.extend(compare_answer(label, recorded, raw, error, mapping, exact=("available", "volume")))
            else:
                theirs = canon(base.blank_times(ids(recorded["ok"], known)), seen_recorded)
                ours = canon(base.blank_times(describe(raw)), seen_ids)
                problems.extend(differences(theirs, ours, label))
            got = watch(only, seconds, (lambda seen: until(seen["events"])) if until else None)["events"]
            last["events"] = got
            problems.extend(compare_events(canon_events(quiet(recorded["events"]), seen_recorded, known), canon_events(quiet(got), seen_ids), label, {}))
            return raw

        def stacks(handle, flag=None) -> dict:
            rows = who.call_bound(handle, "List", flag) if flag else who.call_bound(handle, "List")
            found: dict = {}
            for row in rows if isinstance(rows, list) else []:
                fields = row.fields()
                found.setdefault(int(fields["typeID"]), []).append(int(fields["itemID"]))
            return found

        who.call("slash", "SlashCmd", "/giveitem Tritanium 100")
        who.call("slash", "SlashCmd", "/giveitem Pyerite 60")
        trit, pye = stacks(hangar)[TRITANIUM][0], stacks(hangar)[PYERITE][0]
        who.call_bound(docked, "Add", trit, STATION, qty=100, flag=5)
        who.call_bound(docked, "Add", pye, STATION, qty=60, flag=5)
        watch(only, 1.5)
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
        step("the pilot asks for its hold in space", lambda: who.call_bound(flying, "List", 5))
        held = stacks(flying, 5)
        space_service = who.call("ship", "MachoBindObject", SYSTEM, None)
        step("the pilot jettisons both stacks", lambda: who.call_bound(space_service[0], "Jettison", m.List([held[TRITANIUM][0], held[PYERITE][0]])), 4.0)
        can = next((item for item, _ in slims_in(last["events"]) if item != ship), 0)
        assert can, f"the can did not come into space: {[e['name'] for e in last['events']]}\n" + "\n".join(problems[:20])
        last["can"] = can
        opened = who.call("invbroker", "MachoBindObject", (SYSTEM, 5), ("GetInventoryFromId", (can, 0), None))[1]
        step("the pilot opens the can", lambda: who.call_bound(opened, "List"))
        step("the pilot asks for the room in the can", lambda: who.call_bound(opened, "GetCapacity"))
        inside = stacks(opened)
        everything = [inside[TRITANIUM][0], inside[PYERITE][0]]
        step("the pilot loots everything", lambda: who.call_bound(flying, "MultiAdd", m.List(everything), can, flag=5), 2.0)
        step("the pilot asks for its hold after", lambda: who.call_bound(flying, "List", 5))
        step("the pilot asks for the room in its hold after", lambda: who.call_bound(flying, "GetCapacity", 5))
        step("the pilot asks for the can after", lambda: who.call_bound(opened, "List"))
    assert problems == [], "\n".join(problems[:40])


def _is_the_rats(event: dict) -> bool:
    """Whether a destiny update is about the rats (their shots, the repairs of the pilot's ship, the damage it takes) and not about the can."""
    text = str(event)
    return "OnDamageStateChange" in text or "effects.Laser" in text or "effects.ArmorRepair" in text

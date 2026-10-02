"""A pilot uses the crimewatch service the client binds for its place, compared with what the reference was recorded doing.

    EVE_LIVE_HOST=host:port [EVE_LIVE_SERVER=reference|eve.rs] pytest tests/test_crimewatch.py

`oracle/fixtures/scenarios/crimewatch.json` (scripts/record_character_scenarios.py --only crimewatch) holds one character in its station: `crimewatch` bound by
`(station, 15)` with `GetClientStates` as the nested call (the five combat timers, the engagements, the flagged characters, the ship safety level), the safety level
read, made partial, none, an unknown one (brought to the nearest) and full again, the character's security status and its transactions; then, in space, the states
again. The replay does the same on any server and compares each answer step by step, and every event the client hears. The steps in space need the simulation and
a minute: they run only with EVE_LIVE_SLOW=1.
"""

from __future__ import annotations

import os
import uuid

import pytest

from evecli.describe import describe, differences
from tests import test_login_to_character as base
from tests.test_others import watch
from tests.test_target import plain_race
from tests.test_undock import STATION, SYSTEM, arrive, compare_events, outcome

CRIMEWATCH = base.load("crimewatch.json")
pytestmark = pytest.mark.skipif(not base.LIVE, reason="set EVE_LIVE_HOST=host:port to test against a running server")

LAST_DOCKED_STEP = "the client asks for the transactions of its security status"


def test_a_pilot_asks_crimewatch_for_its_states_and_sets_its_safety_as_in_the_reference():
    race = CRIMEWATCH["races"][0]
    run = uuid.uuid4().hex[:10]
    problems: list[str] = []
    steps = {step["label"]: step for step in race["steps"]}
    with base.connect(f"walk-crimewatch-{run}") as who:
        character, ship, _, _ = arrive(who, plain_race({"pilot": race}, "pilot"), run)
        only = {"events": who}

        def compare(label: str, raw, error, seconds: float = 1.0):
            recorded = steps[label]
            if "error" in recorded or error is not None:
                problems.append(f"{label}: error {base.brief(error)} in place of {base.brief(recorded.get('ok', recorded.get('error')))}")
            else:
                problems.extend(differences(base.blank_times(recorded["ok"]), base.blank_times(describe(raw)), label))
            got = watch(only, seconds)["events"]
            wanted = [e for e in recorded["events"] if e["name"] != "OnInvulnCancelled"]
            problems.extend(compare_events(wanted, [e for e in got if e["name"] != "OnInvulnCancelled"], label, {}))

        def step(label: str, call, seconds: float = 1.0):
            raw, error = outcome(call)
            compare(label, raw, error, seconds)
            return raw

        bound = who.call("crimewatch", "MachoBindObject", (STATION, 15), ("GetClientStates", (), None))
        compare("the client binds crimewatch in the station and asks for its states", bound[1], None)
        handle = bound[0]
        step("the client asks for its safety level", lambda: who.call_bound(handle, "GetSafetyLevel"))
        step("the client makes its ship safety partial", lambda: who.call_bound(handle, "SetSafetyLevel", 1))
        step("the client asks for its states with the safety partial", lambda: who.call_bound(handle, "GetClientStates"))
        step("the client makes its ship safety none", lambda: who.call_bound(handle, "SetSafetyLevel", 0))
        step("the client asks for its safety level again", lambda: who.call_bound(handle, "GetSafetyLevel"))
        step("the client makes an unknown safety level", lambda: who.call_bound(handle, "SetSafetyLevel", 7))
        step("the client asks for its safety level after the unknown one", lambda: who.call_bound(handle, "GetSafetyLevel"))
        step("the client makes its ship safety full again", lambda: who.call_bound(handle, "SetSafetyLevel", 2))
        step("the client asks for its own security status", lambda: who.call_bound(handle, "GetMySecurityStatus"))
        step("the client asks for the security status of a character", lambda: who.call_bound(handle, "GetCharacterSecurityStatus", character))
        step(LAST_DOCKED_STEP, lambda: who.call_bound(handle, "GetSecurityStatusTransactions"))
        if os.environ.get("EVE_LIVE_SLOW") != "1":
            assert problems == [], "\n".join(problems[:40])
            return

        service = who.call("ship", "MachoBindObject", (STATION, 15), None)
        who.call_bound(service[0], "Undock", ship, False)
        watch(only, 2.5)
        who.call("beyonce", "MachoBindObject", SYSTEM, None)
        watch(only, 3.0)
        flying = who.call("crimewatch", "MachoBindObject", (SYSTEM, 5), ("GetClientStates", (), None))
        recorded = steps["the client binds crimewatch in space and asks for its states"]
        problems.extend(differences(base.blank_times(recorded["ok"]), base.blank_times(describe(flying[1])), "the client binds crimewatch in space and asks for its states"))
        step("the client asks for its safety level in space", lambda: who.call_bound(flying[0], "GetSafetyLevel"))
    assert problems == [], "\n".join(problems[:40])


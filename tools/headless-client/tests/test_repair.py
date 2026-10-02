"""Local repairers: an armor repairer started in space mends the armor as its cycle ends (eve.rs only: the reference has no recording of one).

    EVE_LIVE_SLOW=1 EVE_LIVE_HOST=host:port [EVE_LIVE_SERVER=eve.rs] pytest tests/test_repair.py

The character is docked with the starting ship: `/giveskill` gives the skills the repairer needs, `/fit me` puts it in a low slot, `/dmg heavy` (docked: the
reference's only way to hurt a ship on demand) damages the ship, which leaves the station with the armor it had, and `Activate(repairer, "armorRepair")` starts
the cycle. The simulation runs it (the points of the repairer after dogma, the cost in capacitor, the length), and the pilot is told the effect starting and, as
the cycle ends, the damage state with the armor mended. It needs the simulation: EVE_LIVE_SLOW=1.
"""

from __future__ import annotations

import os
import uuid

import pytest

from evecli.describe import describe
from tests import test_login_to_character as base
from tests.test_death import DEATH
from tests.test_health import own_damage_state
from tests.test_others import watch
from tests.test_target import plain_race
from tests.test_undock import STATION, SYSTEM, arrive, number
from tests.test_weapon import effect_of

REPAIRER = 523  # Small Armor Repairer I: Repair Systems and Mechanics at level 1
pytestmark = [
    pytest.mark.skipif(not base.LIVE, reason="set EVE_LIVE_HOST=host:port to test against a running server"),
    pytest.mark.skipif(os.environ.get("EVE_LIVE_SLOW") != "1", reason="a minute: set EVE_LIVE_SLOW=1"),
    pytest.mark.skipif(os.environ.get("EVE_LIVE_SERVER", "eve.rs") == "reference", reason="the reference has no recording of a repairer"),
]


def text_of(answer) -> str:
    answer = getattr(answer, "value", answer)
    return answer.decode() if isinstance(answer, bytes) else str(answer)


def armors_of(events: list, ship: int) -> list[float]:
    """The armor fractions the stream tells of this ship (`OnDamageStateChange` of its own), in order."""
    told = []
    for event in events:
        if event["name"] != "DoDestinyUpdate":
            continue
        try:
            actions = event["args"]["$tuple"][1]["$tuple"][1]["$tuple"][0]
        except (KeyError, TypeError):
            continue
        for action in actions:
            name, payload = action["$tuple"][1]["$tuple"][0], action["$tuple"][1]["$tuple"][1]["$tuple"]
            if name == "OnDamageStateChange" and number(payload[0]) == ship:
                told.append(payload[1]["$tuple"][1])
    return told


def test_an_armor_repairer_mends_the_armor_as_its_cycle_ends():
    race = plain_race(DEATH["races"][0], "victim")
    run = uuid.uuid4().hex[:10]
    with base.connect(f"walk-repair-{run}") as who:
        _, ship, fitted, _ = arrive(who, race, run)
        only = {"events": who}
        slash = lambda command: text_of(who.call("slash", "SlashCmd", command))  # noqa: E731
        for skill in ("Repair Systems", "Mechanics"):
            assert f"{skill}(" in slash(f"/giveskill me {skill} 1")
        slash("/unfit me all")  # the starting ship has two low slots, and modules in them
        watch(only, 1.0)
        fit = slash(f"/fit me {REPAIRER}")
        assert fit.startswith("Created and fitted"), fit
        watch(only, 1.5)
        hurt = slash("/dmg heavy")
        assert hurt.startswith("Applied heavy test damage"), hurt
        watch(only, 1.0)

        bound = who.call("dogmaIM", "MachoBindObject", (STATION, 15), None)
        info = who.call_bound(bound[0], "GetAllInfo", True, True, None)
        items = {}
        for key, value in {k: v for k, v in describe(info)["args"]["$dict"]}["shipInfo"]["$dict"]:
            if isinstance(key, dict) and "$tuple" in key:
                continue
            row = {k: v for k, v in value["args"]["$dict"]}["invItem"]["$row"]
            items[row["typeID"]] = int(key["$long"] if isinstance(key, dict) else key)
        assert REPAIRER in items, sorted(items)
        repairer = items[REPAIRER]

        service = who.call("ship", "MachoBindObject", (STATION, 15), None)
        who.call_bound(service[0], "Undock", ship, False)
        watch(only, 2.5)
        who.call("beyonce", "MachoBindObject", SYSTEM, None)
        state = own_damage_state(watch(only, 3.0)["events"], ship, {})  # the ship as the ballpark tells it when it comes into space
        before = [state[1]] if state else []
        dogma = who.call("dogmaIM", "MachoBindObject", (SYSTEM, 5), None)
        who.call_bound(dogma[0], "GetAllInfo", True, True, None)
        watch(only, 1.0)
        assert before and before[-1] < 1, f"the ship left the station with its armor at {before}"

        who.call_bound(dogma[0], "Activate", repairer, "armorRepair", None, 1000)
        seen = watch(only, 12.0, until=lambda s: any(a > before[-1] for a in armors_of(s["events"], ship)))["events"]
        effects = [effect_of(e) for e in seen if e["name"] == "OnGodmaShipEffect" and number(effect_of(e)[0]) == repairer]
        assert effects and effects[0][3], f"the effect did not start: {effects}"
        mended = armors_of(seen, ship)
        assert mended and max(mended) > before[-1], f"the armor is {mended} in place of more than {before[-1]}"

        who.call_bound(dogma[0], "Deactivate", repairer, "armorRepair")
        stopped = watch(only, 10.0, until=lambda s: any(e["name"] == "OnGodmaShipEffect" and not effect_of(e)[3] for e in s["events"]))["events"]
        assert any(e["name"] == "OnGodmaShipEffect" and not effect_of(e)[3] for e in stopped), "the effect did not stop"

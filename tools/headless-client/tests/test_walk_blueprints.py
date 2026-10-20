"""Blueprint manager walk: a pilot holding an original blueprint reads it, lists it with its owner's blueprints, and is refused an item it does not hold.

    EVE_LIVE_HOST=host:port tests/... (run through tools/dev/run-eve-live.sh so the schemas and EVE_PG_URL are exported)

The blueprint is seeded into the pilot's station hangar (dbseed): an original (quantity -1) of blueprint 681 (sde.blueprints). The client
asks the manager for the limit (GetLimits), a blueprint's data (GetBlueprintData), and the owner's list (GetBlueprintDataByOwner).
Only eve.rs has the seeded walk: EveJS has no database to seed, so its side is compared by reading its code (blueprintManager).
"""

from __future__ import annotations

import os
import uuid

import pytest

from evecli.describe import describe
from tests import dbseed
from tests import test_login_to_character as base
from tests.test_health import HEALTH
from tests.test_undock import STATION, arrive, number, outcome

BLUEPRINT = 681  # Clone Grade Beta Blueprint (SDE category 9, "Blueprint"): an original, the walk only reads it
pytestmark = [
    pytest.mark.skipif(not base.LIVE, reason="set EVE_LIVE_HOST=host:port to test against a running server"),
    pytest.mark.skipif(os.environ.get("EVE_LIVE_SERVER", "eve.rs") == "reference", reason="seeding needs the database: eve.rs only"),
]

def test_a_pilot_reads_its_original_blueprint_and_is_refused_one_it_does_not_hold():
    race = HEALTH["races"][0]
    run = uuid.uuid4().hex[:10]
    with base.connect(f"walk-blueprint-{run}") as who:
        character, _, _, _ = arrive(who, race, run)
        blueprint = dbseed.new_item(BLUEPRINT, owner=character, location=STATION, singleton=True, quantity=-1)

        limits = describe(who.call("blueprintManager", "GetLimits"))
        assert dict(limits["$dict"])["maxBlueprintResults"] == 500

        data = describe(who.call("blueprintManager", "GetBlueprintData", blueprint))
        fields = dict(data["args"]["$dict"])
        assert number(fields["typeID"]) == BLUEPRINT and number(fields["quantity"]) == -1

        owned = who.call("blueprintManager", "GetBlueprintDataByOwner", character, None)
        assert blueprint in [number(dict(describe(row)["args"]["$dict"])["itemID"]) for row in owned[0]], owned

        stranger = dbseed.new_item(BLUEPRINT, owner=character + 1, location=STATION, singleton=True, quantity=-1)
        answer, error = outcome(lambda: who.call("blueprintManager", "GetBlueprintData", stranger))
        assert answer is None and error is not None, answer  # IndustryBlueprintNotFound: a blueprint the pilot does not hold

"""Reprocessing walk: a pilot docked in a station reprocesses one portion of Veldspar it holds, and gets the tritanium the rule gives.

    EVE_LIVE_HOST=host:port tests/... (run through tools/dev/run-eve-live.sh so EVE_PG_URL and the schemas are exported)

The ore is seeded into the pilot's station hangar (dbseed). The client binds the reprocessing service at the station (MachoBindObject),
asks for a quote (GetQuotes), and reprocesses (Reprocess). The rule: a portion of Veldspar (100 units) yields 400 tritanium at 100%
(sde."typeMaterials", key 1230); the station efficiency from the quote scales it, and the yield is rounded down. Only eve.rs has the
seeded walk: EveJS has no database to seed, so its side is compared by reading its code.
"""

from __future__ import annotations

import math
import os
import uuid

import pytest

from evecli.describe import describe
from tests import dbseed
from tests import test_login_to_character as base
from tests.test_health import HEALTH
from tests.test_undock import STATION, arrive, number

VELDSPAR = 1230  # sde.typeMaterials: 400 tritanium (34) per portion
TRITANIUM = 34
PORTION = 100
HANGAR = dbseed.HANGAR
pytestmark = [
    pytest.mark.skipif(not base.LIVE, reason="set EVE_LIVE_HOST=host:port to test against a running server"),
    pytest.mark.skipif(os.environ.get("EVE_LIVE_SERVER", "eve.rs") == "reference", reason="seeding needs the database: eve.rs only"),
]


def pairs(value) -> list:
    """The (key, value) pairs of a dict or of a list of pairs, however the client decoded it."""
    return list(value.items()) if isinstance(value, dict) else [tuple(pair) for pair in value]


def station_efficiency(quote) -> float:
    """The quote's efficiency for the station (the entry keyed None)."""
    return float(next(value for key, value in pairs(quote[1]) if key is None))


def test_a_docked_pilot_reprocesses_a_portion_of_veldspar_into_the_rule_s_tritanium():
    race = HEALTH["races"][0]
    run = uuid.uuid4().hex[:10]
    with base.connect(f"walk-reprocess-{run}") as who:
        character, _, _, _ = arrive(who, race, run)
        ore = dbseed.new_item(VELDSPAR, owner=character, location=STATION, flag=HANGAR, quantity=PORTION)

        bound = who.call("reprocessingSvc", "MachoBindObject", STATION, None)
        quote = who.call_bound(bound[0], "GetQuotes", [ore])
        assert quote is not None, "the quote answers the item"

        processed, yields = who.call_bound(bound[0], "Reprocess", [ore], STATION, character, STATION, HANGAR)
        assert ore in [number(item) for item in processed], processed
        tritanium = dict((number(k), number(v)) for k, v in pairs(yields))[TRITANIUM]
        # the rule: 400 per portion, scaled by the station efficiency the quote gave, rounded down; the walk's efficiency is the
        # station's (the quote's first efficiency entry), read from the same answer
        efficiency = station_efficiency(quote)
        assert tritanium == math.floor(400 * efficiency), (tritanium, efficiency)

"""A ship from Turnur (30002086) warps to the stargate into Zarzakh (30100000) and jumps through it.

    EVE_LIVE_SLOW=1 EVE_LIVE_HOST=host:port EVE_START_STATION=60004723 pytest tests/test_zarzakh_jump.py

The host must start new characters in a station of Turnur (EVE_START_STATION=60004723). The gate lists, the toll and the deploy
blockers of Zarzakh come from the embedded client data (tools/dev/extract-client-sde.py). The character and its ship come from the
recorded jump walk (`oracle/fixtures/scenarios/jump.json`); the station and the system are Turnur's. No EveJS recording of this route
exists, so the test checks the outcome the client can see (the jump answer, a session change into Zarzakh) and the toll journal when the
database is named (EVE_PG_URL): the comparison with EVE.js is a separate run (DEVIATIONS, slice 3).
"""

from __future__ import annotations

import json
import os
import subprocess
import time
import uuid

import pytest

from evecli import marshal as m
from evecli.observe import timed_events, wait_quietly
from tests import test_login_to_character as base
from tests import test_undock
from tests.test_jump import JUMP
from tests.test_undock import arrive, outcome

TURNUR_STATION = 60004723
TURNUR = 30002086
ZARZAKH = 30100000
TOLL_GATE = 50016562  # the Turnur gate, type 77918: the toll of the incoming gate
ZARZAKH_GATE = 50016566
TOLL_REF_TYPE = 168  # the client's refAllignmentBasedGateToll

pytestmark = [
    pytest.mark.skipif(not base.LIVE, reason="set EVE_LIVE_HOST=host:port to test against a running server"),
    pytest.mark.skipif(os.environ.get("EVE_LIVE_SLOW") != "1", reason="a warp of about two minutes: set EVE_LIVE_SLOW=1"),
]

def toll_journal_rows() -> int | None:
    """The journal rows of the gate toll in the live run's wallet schema, or None when no database is named."""
    url = os.environ.get("EVE_PG_URL")
    if not url:
        return None
    schemas = subprocess.run(["psql", url, "-tAc", "SELECT nspname FROM pg_namespace WHERE nspname LIKE 'live_%_wallet'"],
                             capture_output=True, text=True, check=True).stdout.split()
    total = 0
    for schema in schemas:
        rows = subprocess.run(["psql", url, "-tAc", f"SELECT count(*) FROM {schema}.journal WHERE ref_type_id = {TOLL_REF_TYPE}"],
                              capture_output=True, text=True, check=True).stdout.strip()
        total += int(rows or 0)
    return total

def test_a_ship_from_turnur_warps_to_the_zarzakh_gate_and_jumps_into_zarzakh():
    race = JUMP["races"][0]
    run = uuid.uuid4().hex[:10]
    test_undock.STATION = TURNUR_STATION  # arrive() binds the station through the module
    with base.connect(f"walk-zarzakh-{run}") as who:
        character, ship_id, fitted, _ = arrive(who, race, run)
        ship = who.call("ship", "MachoBindObject", (TURNUR_STATION, 15), None)
        wait_quietly(who, 0.5)
        raw, error = outcome(lambda: who.call_bound(ship[0], "Undock", ship_id, False, onlineModules=m.List(fitted)))
        assert error is None, base.brief(error)
        ballpark = who.call("beyonce", "MachoBindObject", TURNUR, None)
        wait_quietly(who, 2.5)

        # the warp to the gate: until the ship stops by it
        started = time.time()
        raw, error = outcome(lambda: who.call_bound(ballpark[0], "CmdWarpToStuff", "item", TOLL_GATE, minRange=0))
        assert error is None, base.brief(error)
        events = timed_events(who, 1.0)
        while time.time() - started < 200 and not any(e["name"] == "DoDestinyUpdate" and "Stop" in json.dumps(e) for e in events):
            who.call_bound(ballpark[0], "UpdateStateRequest")
            events += timed_events(who, 3.0)

        # the jump into Zarzakh: the toll is taken before the simulation hears the jump, and the answer is the bound object and a time
        before = toll_journal_rows()
        raw, error = outcome(lambda: who.call_bound(ballpark[0], "CmdStargateJump", TOLL_GATE, ZARZAKH_GATE, ship_id))
        assert error is None, f"the jump was refused: {base.brief(error)}"
        seen = timed_events(who, 12.0, lambda e: any(x["name"] == "$sessionchange" for x in e))
        arrived = [e for e in seen if e["name"] == "$sessionchange" and str(ZARZAKH) in json.dumps(e)]
        assert arrived, "no session change into Zarzakh after the jump"
        after = toll_journal_rows()
        if before is not None:
            assert after - before == 1, f"the gate toll should be journaled once, the journal rows went from {before} to {after}"

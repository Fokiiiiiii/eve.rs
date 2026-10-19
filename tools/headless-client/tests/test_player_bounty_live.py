"""Player bounties against a running server (CH-13): placing, reading and refusing (eve.rs: EveJS bountyProxyService and playerBountyState).

    EVE_LIVE_HOST=host:port EVE_PG_URL=postgres://... EVE_RUN=... tools/dev/run-eve-live.sh (or pytest tests/test_player_bounty_live.py)

A target pilot is created on its own account; the placer's wallet is topped up in the run's scratch wallet schema (a new pilot starts with
100,000 ISK, and a pilot bounty is at least 1,000,000). `bountyProxy.AddToBounty` debits the placer once and stores the bounty; `GetBounties`
reads it back; a placement the placer cannot pay is refused and changes nothing.
"""

from __future__ import annotations

import os
import uuid

import pytest

from evecli import marshal as m
from evecli import packet as p
from tests.test_agents_live import new_pilot, psql, schema
from tests.test_login_to_character import connect

LIVE = os.environ.get("EVE_LIVE_HOST", "")
PG_URL = os.environ.get("EVE_PG_URL", "")
pytestmark = pytest.mark.skipif(not (LIVE and PG_URL), reason="set EVE_LIVE_HOST=host:port and EVE_PG_URL to test against a running server")

def isk(character: int) -> int:
    return int(float(psql(f"SELECT isk FROM {schema('EVE_WALLET_SCHEMA')}.balances WHERE owner_id = {character} AND account_key = 1000")))

def test_a_placed_bounty_debits_the_placer_once_and_reads_back_and_an_unpayable_one_is_refused():
    run = uuid.uuid4().hex[:8]
    with connect(f"bounty-target-{run}") as target_client:
        target = new_pilot(target_client, f"{run}t")
    with connect(f"bounty-placer-{run}") as client:
        placer = new_pilot(client, f"{run}p")
        psql(f"UPDATE {schema('EVE_WALLET_SCHEMA')}.balances SET isk = 5000000 WHERE owner_id = {placer} AND account_key = 1000")

        name, bounty = m.plain(client.call("bountyProxy", "AddToBounty", target, 2_000_000))
        assert name == "util.KeyVal" and bounty["targetID"] == target and bounty["bounty"] == 2_000_000
        assert isk(placer) == 3_000_000, "the placement debits the placer once"

        stored = m.plain(client.call("bountyProxy", "GetBounties", [target]))
        assert [entry[0] for entry in stored] == [target]
        assert stored[0][1][1]["bounty"] == 2_000_000

        mine = m.plain(client.call("bountyProxy", "GetMyBounties"))
        assert len(mine) == 1 and mine[0][1]["targetID"] == target and mine[0][1]["amount"] == 2_000_000

        with pytest.raises(p.MachoError) as refused:
            client.call("bountyProxy", "AddToBounty", target, 10_000_000)
        assert "NotEnoughMoney" in repr(refused.value.payload)
        assert isk(placer) == 3_000_000, "a refused placement changes nothing"
        assert m.plain(client.call("bountyProxy", "GetBounties", [target]))[0][1][1]["bounty"] == 2_000_000

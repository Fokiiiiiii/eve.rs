"""Name tokens for the live tests: unique within a run, and the same on both servers when EVE_LIVE_RUN_ID is set.

The captures of two servers can only be compared when the names the tests create are the same on both sides, so a run
that sets EVE_LIVE_RUN_ID (tools/mitm, docs/project/structures-phase/WIRE-COMPARE.md) gets a counter instead of random text.
"""

import itertools
import os
import uuid

_SEQUENCE = itertools.count(1)


def run_token() -> str:
    base = os.environ.get("EVE_LIVE_RUN_ID")
    if not base:
        return uuid.uuid4().hex[:10]
    return f"{base}{next(_SEQUENCE):03d}"

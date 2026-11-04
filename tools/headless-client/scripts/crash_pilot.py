"""Creates a character, undocks it and stays connected until it is killed: what `tools/dev/check-crash-recovery.sh` kills the host under.

    EVE_LIVE_HOST=host:port crash_pilot.py <file the "character ship" is written to>
"""
import sys
import time
import uuid
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from tests import test_login_to_character as base  # noqa: E402
from tests.test_death import DEATH  # noqa: E402
from tests.test_target import plain_race  # noqa: E402
from tests.test_undock import STATION, SYSTEM, arrive  # noqa: E402

run = uuid.uuid4().hex[:10]
with base.connect(f"crash-{run}") as who:
    character, ship, _, _ = arrive(who, plain_race(DEATH["races"][0], "victim"), run)
    service = who.call("ship", "MachoBindObject", (STATION, 15), None)
    who.call_bound(service[0], "Undock", ship, False)
    who.call("beyonce", "MachoBindObject", SYSTEM, None)
    Path(sys.argv[1]).write_text(f"{character} {ship}")
    time.sleep(120)

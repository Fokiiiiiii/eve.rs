"""The live kill helper: a pilot in space shoots a spawned rat dead with a gun through the real combat path.

The steps are the ones test_npc.py takes (the rat is locked, the pilot goes to where the gun reaches it, `targetAttack` is activated at it,
and the shots and the rat's removal are watched), so a test that needs a kill calls this instead of repeating them. Nothing is faked: the
simulation decides the hits, the destruction and the wreck; the helper only reports what the client was told.
"""

import re
import time

from evecli import packet
from tests.test_others import watch

def rat_of(events: list, ship: int) -> int:
    """The first ball in the events that is not the pilot's ship (the spawned rat), or 0."""
    for event in events:
        for item, _ in re.findall(r'\["itemID", \{"\$long": (\d+)\}\], \["typeID", (\d+)\]', str(event).replace("'", '"')):
            if int(item) != ship:
                return int(item)
    return 0

def kill_with_gun(who, ballpark, dogma, gun: int, rat: int, seconds: float = 240.0) -> list:
    """Shoots the rat dead with the gun: the pilot moves (a belt rat waits for a ship that moves near it and then comes to it), locks the rat
    as soon as the simulation accepts the lock (a lock that is out of reach is refused and tried again), orbits it at the distance the gun
    reaches, fires the gun at it and watches until the pilot is told of its bounty (the rat is dead).

    Returns the events watched while the rat was shot, so the caller can check what the client was told (the bounty, the standing).
    """
    deadline = time.monotonic() + seconds

    def locked() -> bool:
        return rat in [t if not isinstance(t, dict) else t["$long"]
                       for t in map(lambda x: getattr(x, "value", x), who.call_bound(dogma[0], "GetTargets"))]

    # the pilot moves at full speed: at half speed it cannot keep up with a rat that flies at 250 m/s or more (SDE max velocity)
    who.call_bound(ballpark[0], "CmdSetSpeedFraction", 1.0)
    events: list = []
    refusal = None
    while not locked():
        try:
            who.call_bound(dogma[0], "AddTarget", rat)
        except packet.MachoError as error:
            refusal = error
        events.extend(watch({"events": who}, 5.0)["events"])
        assert time.monotonic() < deadline, (f"the rat was never locked; refused: {getattr(refusal, 'payload', refusal)!r}"[:700],
                                             sorted({e["name"] for e in events}))
    # the gun is fired from where the simulation accepts it: while it refuses for range (its `actualDistance`), the pilot flies to the rat
    while True:
        try:
            who.call_bound(dogma[0], "Activate", gun, "targetAttack", rat, 1000)
            break
        except packet.MachoError as error:
            assert time.monotonic() < deadline, f"the gun was never accepted at the rat: {getattr(error, 'payload', error)!r}"[:600]
            who.call_bound(ballpark[0], "CmdFollowBall", rat, 1000)
            events.extend(watch({"events": who}, 3.0)["events"])
    while time.monotonic() < deadline:
        # the hit chance falls off past the gun's optimal range (4800 m) and its falloff (2000 m): the pilot closes again while the rat moves
        who.call_bound(ballpark[0], "CmdFollowBall", rat, 1000)
        events.extend(watch({"events": who}, 3.0)["events"])
        if any(e["name"] == "OnBountyAddedToPayout" for e in events) or removed(events, rat):
            return events
    from tests.test_undock import args_of
    kinds = [{k: str(v)[:60] for k, v in args_of(e)["$tuple"][0]["$dict"]} for e in events if e["name"] == "OnDamageMessage"]
    raise AssertionError(f"the rat was not destroyed: told {sorted({e['name'] for e in events})}; damage messages: {kinds[:3]} ... {kinds[-2:]}"[:1500])

def removed(events: list, rat: int) -> bool:
    """The rat's ball is taken out of space (its `RemoveBalls` in the destiny stream): the rat is dead (test_npc reads the same)."""
    return any("RemoveBalls" in str(event) and str(rat) in str(event) for event in events)

"""Implants and boosters of a pilot against a running server (CH-11): the client's Plug In, Unplug and Consume.

    EVE_LIVE_HOST=host:port EVE_PG_URL=postgres://... EVE_RUN=... tools/dev/run-eve-live.sh (or pytest tests/test_implant_live.py)

A new pilot docked at the start station trains Gunnery. A Standard Cerebral Accelerator (SDE 49539: +10 on every learning attribute, booster slot 10, one day) consumed from the
station hangar raises the rate of that training by (30 + 30/2) / (20 + 20/2) = 1.5; the queue the client reads shows it. An attribute enhancer
(SDE 9941, Memory Augmentation - Basic, which needs Cybernetics I) plugged in from the hangar moves to the pilot's implant slot: the implant list
has it, the client is told the list, the attributes and the brain, in the reference's order. A second implant for the same slot is refused with the
client's label. Unplugging destroys it. Skipped unless EVE_LIVE_HOST is set.
"""

import os
import time
import uuid

import pytest

from evecli import marshal as m
from evecli import packet as p
from evecli.observe import notifications_of
from tests import dbseed
from tests.test_agents_live import new_pilot, psql, schema
from tests.test_login_to_character import connect
from tests.test_market import _label_and_arguments
from tests.test_market import _label_and_arguments
from tests.test_undock import STATION

LIVE = os.environ.get("EVE_LIVE_HOST", "")
pytestmark = pytest.mark.skipif(not LIVE, reason="set EVE_LIVE_HOST=host:port to test against a running server")

GUNNERY = 3300
CYBERNETICS = 3411
CEREBRAL_ACCELERATOR = 49539
MEMORY_AUGMENTATION_BASIC = 9941
PILOT_IMPLANT_FLAG = 89
BOOSTER_FLAG = 88
HANGAR_FLAG = 4

def queue_lines(client) -> list[dict]:
    """The skill queue as the client reads it: each line is a util.KeyVal of the training fields."""
    return [line[1] for line in m.plain(client.call("skillHandler", "GetSkillQueue"))]

def training_rate(client) -> float:
    """Skill points per FILETIME tick of the line in training (its destination less its start, over its length)."""
    line = queue_lines(client)[0]
    return (line["trainingDestinationSP"] - line["trainingStartSP"]) / (line["trainingEndTime"] - line["trainingStartTime"])

def contains(value, wanted: int) -> bool:
    if isinstance(value, bool):
        return False
    if isinstance(value, int):
        return value == wanted
    if isinstance(value, dict):
        return any(contains(item, wanted) for item in value.values())
    if isinstance(value, (list, tuple)):
        return any(contains(item, wanted) for item in value)
    return False

def names_in_order(client, expected: list[str], seconds: float = 3.0) -> bool:
    """Whether the expected notifications arrive in this order (other notifications may come between them)."""
    deadline = time.monotonic() + seconds
    while True:
        names = [event.get("name") for event in notifications_of(client)]
        it = iter(names)
        if all(any(name == wanted for name in it) for wanted in expected):
            return True
        if time.monotonic() >= deadline:
            return False
        time.sleep(0.2)

def test_a_booster_speeds_the_training_then_an_implant_is_plugged_in_refused_twice_and_unplugged():
    run = uuid.uuid4().hex[:10]
    with connect(f"implant-{run}") as client:
        character = new_pilot(client, run)
        # a new pilot may start with Cybernetics: the level is raised to one, never lowered
        dbseed.sql(f"insert into {dbseed.schema('SKILLS')}.character_skills (character_id, type_id, level, skill_points) "
                   f"values ({character}, {CYBERNETICS}, 1, 250) on conflict (character_id, type_id) "
                   f"do update set level = greatest(character_skills.level, 1)")
        dogma = client.call("dogmaIM", "MachoBindObject", (STATION, 15), None)[0]
        client.call("skillHandler", "CharStartTrainingSkillByTypeID", GUNNERY, None)
        before = training_rate(client)

        # a booster from the station hangar: it is consumed into its slot, and the training that runs gets its attributes
        accelerator = dbseed.new_item(CEREBRAL_ACCELERATOR, owner=character, location=STATION, singleton=True)
        client.notifications.clear()
        try:
            client.call_bound(dogma, "UseBooster", accelerator, STATION)
        except p.MachoError as error:
            raise AssertionError(f"UseBooster refused: {_label_and_arguments(error)}") from error
        assert int(psql(f"SELECT flag FROM {schema('EVE_INVENTORY_SCHEMA')}.items WHERE id = {accelerator}")) == BOOSTER_FLAG
        assert contains(m.plain(client.call("skillHandler", "GetBoosters")), CEREBRAL_ACCELERATOR), "the client reads the active booster by its type"
        assert names_in_order(client, ["OnServerBoostersChanged", "OnServerBrainUpdated"])
        assert abs(training_rate(client) / before - 1.5) < 0.02, "the rate of the running training is the base plus the +10 of the accelerator"

        # an attribute enhancer from the hangar: it moves into the implant slot and the client is told in the reference's order
        implant = dbseed.new_item(MEMORY_AUGMENTATION_BASIC, owner=character, location=STATION, singleton=True)
        client.notifications.clear()
        client.call_bound(dogma, "InjectImplant", implant)
        assert int(psql(f"SELECT flag FROM {schema('EVE_INVENTORY_SCHEMA')}.items WHERE id = {implant}")) == PILOT_IMPLANT_FLAG
        assert int(psql(f"SELECT location_id FROM {schema('EVE_INVENTORY_SCHEMA')}.items WHERE id = {implant}")) == character
        assert contains(m.plain(client.call("skillHandler", "GetImplants")), implant)
        assert names_in_order(client, [
            "OnItemChange", "OnServerImplantsChanged", "OnJumpCloneCacheInvalidated", "OnModuleAttributeChanges", "OnServerBrainUpdated"])

        # a second implant for the same slot is refused, and nothing changes
        second = dbseed.new_item(MEMORY_AUGMENTATION_BASIC, owner=character, location=STATION, singleton=True)
        with pytest.raises(p.MachoError) as refused:
            client.call_bound(dogma, "InjectImplant", second)
        label, arguments = _label_and_arguments(refused.value)
        assert label == "CustomNotify"
        assert arguments["notify"] == "You already have an implant in that slot."
        assert int(psql(f"SELECT flag FROM {schema('EVE_INVENTORY_SCHEMA')}.items WHERE id = {second}")) == HANGAR_FLAG

        # Unplug destroys the implant the pilot wears: the item is gone, and the list and the attributes are told
        client.notifications.clear()
        client.call_bound(dogma, "DestroyImplant", implant)
        assert int(psql(f"SELECT count(*) FROM {schema('EVE_INVENTORY_SCHEMA')}.items WHERE id = {implant}")) == 0
        assert not contains(m.plain(client.call("skillHandler", "GetImplants")), implant)
        assert names_in_order(client, ["OnServerImplantsChanged", "OnJumpCloneCacheInvalidated", "OnServerBrainUpdated"])

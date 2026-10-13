"""Industry walk: a pilot installs, lists, cancels and delivers industry jobs through the industryManager service.

    EVE_LIVE_HOST=host:port tests/... (run through tools/dev/run-eve-live.sh so the schemas and EVE_PG_URL are exported)

The pilot is seeded with an original of blueprint 681 (sde.blueprints: 86 of type 38 make 165) and the materials in its station hangar.
The install payload is the client's job.dump(): the wallet account, the character and corporation IDs, and the input and output
locations as objects. A job's end is moved into the past in the database (the walk does not wait out the job's time); the
completion then runs the same delivery path as the client's Deliver button. Invention needs skills and a decryptor, which this walk
does not seed; the copy path is not walked (its completion would wait 480 seconds per run).
"""

from __future__ import annotations

import os
import time
import uuid

import pytest

from evecli.describe import describe
from evecli.observe import notifications_of
from tests import dbseed
from tests import test_login_to_character as base
from tests.test_health import HEALTH
from tests.test_undock import STATION, arrive, number, outcome

BLUEPRINT = 681  # the original of the manufacturing formula (sde.blueprints): 86 x 38 make 165
MATERIAL = 38
PRODUCT = 165
MANUFACTURING, RESEARCH_MATERIAL, RESEARCH_TIME, COPYING, INVENTION = 1, 4, 3, 5, 8  # the client's activity ids
INVENTION_BLUEPRINT = 683  # an original that invents 39581 (sde.blueprints); its 2 x 20416 and 2 x 25887 are the materials
INVENTION_PRODUCT = 39581
ACCELERANT = 21574  # Esoteric Accelerant (a decryptor): probability x1.2, +1 run, ME +2, TE +10
INVENTION_SKILLS = (11442, 11454, 21790)  # the skills of 683 (level 1 each; 21790 is on the lower-probability list)
pytestmark = [
    pytest.mark.skipif(not base.LIVE, reason="set EVE_LIVE_HOST=host:port to test against a running server"),
    pytest.mark.skipif(os.environ.get("EVE_LIVE_SERVER", "eve.rs") == "reference", reason="seeding needs the database: eve.rs only"),
]


def station_system() -> int:
    return int(dbseed.sql(f"select doc->>'solarSystemID' from sde.\"npcStations\" where key = '{STATION}'"))


SCIENTIFIC_NETWORKING = 24270  # the distance skill of the science activities: 5 jumps per level (the client's DISTANCE_PER_LEVEL)


def nearest_laboratory(origin: int) -> tuple[int, int, int]:
    """The nearest station with a Laboratory (service 15): `(station, its system, jumps from the origin system)`. The stargates
    of the SDE give the jumps; the search stops at 25 (the largest distance the rules allow)."""
    labs: dict[int, int] = {}
    for row in dbseed.sql("select s.key, s.doc->>'solarSystemID' from sde.\"npcStations\" s "
                          "join sde.\"stationOperations\" o on o.key = (s.doc->>'operationID') "
                          "where o.doc->'services' @> '[15]' order by s.key").splitlines():
        if row:
            station, system = row.split("|")
            labs.setdefault(int(system), int(station))
    graph: dict[int, list[int]] = {}
    for row in dbseed.sql('select doc->>\'solarSystemID\', doc->\'destination\'->>\'solarSystemID\' from sde."mapStargates"').splitlines():
        if row:
            a, b = (int(x) for x in row.split("|"))
            graph.setdefault(a, []).append(b)
    seen = {origin: 0}
    frontier = [origin]
    while frontier:
        system = frontier.pop(0)
        if system in labs:
            return labs[system], system, seen[system]
        if seen[system] >= 25:
            continue
        for neighbour in graph.get(system, []):
            if neighbour not in seen:
                seen[neighbour] = seen[system] + 1
                frontier.append(neighbour)
    raise AssertionError(f"no laboratory within 25 jumps of system {origin}")


def job_dump(character: int, blueprint: int, activity: int, system: int, runs: int = 1, materials: dict | None = None,
             facility: int = STATION, blueprint_type: int = BLUEPRINT) -> dict:
    """The client's job.dump(): the keys the industry window sends (the cost and time are the client's quote; the server recomputes them)."""
    return {
        "blueprintID": blueprint,
        "blueprintTypeID": blueprint_type,
        "activityID": activity,
        "facilityID": facility,
        "solarSystemID": system,
        "characterID": character,
        "corporationID": 0,
        "account": (character, 1000),
        "runs": runs,
        "cost": 0,
        "tax": 0,
        "time": 0,
        "materials": materials or {},
        "inputLocation": {"itemID": STATION, "flagID": dbseed.HANGAR},
        "outputLocation": {"itemID": STATION, "flagID": dbseed.HANGAR},
        "licensedRuns": 1,
        "productTypeID": None,
    }


def job_status(job: int) -> int:
    return int(dbseed.sql(f"select status from {dbseed.schema('INDUSTRY')}.jobs where job_id = {int(job)}"))


def ready_now(job: int) -> None:
    """Moves the job's end into the past, so its completion (the Deliver button) is allowed."""
    dbseed.sql(f"update {dbseed.schema('INDUSTRY')}.jobs set end_date_filetime = 1 where job_id = {int(job)}")


def efficiencies(who, blueprint: int) -> tuple[int, int]:
    """The blueprint's material and time efficiency as the blueprint manager reports them to the client."""
    fields = dict(describe(who.call("blueprintManager", "GetBlueprintData", blueprint))["args"]["$dict"])
    return number(fields["materialEfficiency"]), number(fields["timeEfficiency"])


def wait_for_notification(who, name: str, seconds: float = 3.0) -> bool:
    """Whether a named notification reaches the session within the time (the event pump delivers within its poll interval)."""
    deadline = time.monotonic() + seconds
    while True:
        if any(event.get("name") == name for event in notifications_of(who)):
            return True
        if time.monotonic() >= deadline:
            return False
        time.sleep(0.2)


def test_a_pilot_installs_lists_and_cancels_a_manufacturing_job():
    race = HEALTH["races"][0]
    run = uuid.uuid4().hex[:10]
    system = station_system()
    with base.connect(f"walk-industry-{run}") as who:
        character, _, _, _ = arrive(who, race, run)
        blueprint = dbseed.new_item(BLUEPRINT, owner=character, location=STATION, singleton=True, quantity=-1)
        dbseed.new_item(MATERIAL, owner=character, location=STATION, quantity=86)

        job = number(who.call("industryManager", "InstallJob", job_dump(character, blueprint, MANUFACTURING, system)))
        assert job_status(job) == 1
        assert dbseed.items_of(character, MATERIAL) == [], "the 86 units are consumed at install"
        # the client's job list is kept by OnIndustryJob, which the change subscriber sends to the owner's session
        assert wait_for_notification(who, "OnIndustryJob"), "no OnIndustryJob after the install"

        listed = who.call("industryManager", "GetJobsByOwner", character, False)
        assert job in [number(dict(describe(row)["args"]["$dict"])["jobID"]) for row in listed], listed

        who.call("industryManager", "CancelJob", job, system)
        assert job_status(job) == 102
        answer, error = outcome(lambda: who.call("industryManager", "CompleteJob", job, system))
        assert error is not None, answer  # a cancelled job cannot be delivered


def test_a_pilot_delivers_a_manufacturing_job_and_gets_the_product():
    race = HEALTH["races"][0]
    run = uuid.uuid4().hex[:10]
    system = station_system()
    with base.connect(f"walk-industry-{run}") as who:
        character, _, _, _ = arrive(who, race, run)
        blueprint = dbseed.new_item(BLUEPRINT, owner=character, location=STATION, singleton=True, quantity=-1)
        dbseed.new_item(MATERIAL, owner=character, location=STATION, quantity=86)
        job = number(who.call("industryManager", "InstallJob", job_dump(character, blueprint, MANUFACTURING, system)))

        ready_now(job)
        who.call("industryManager", "CompleteJob", job, system)

        assert job_status(job) == 100
        assert dbseed.items_of(character, PRODUCT), "the product is delivered to the output hangar"
        assert dbseed.sql(f"select count(*) from {dbseed.schema('INDUSTRY')}.jobs where job_id = {int(job)} and output_item_ids <> '{{}}'") == "1"


def test_research_raises_material_efficiency_then_time_efficiency_by_two():
    race = HEALTH["races"][0]
    run = uuid.uuid4().hex[:10]
    system = station_system()
    with base.connect(f"walk-industry-{run}") as who:
        character, _, _, _ = arrive(who, race, run)
        blueprint = dbseed.new_item(BLUEPRINT, owner=character, location=STATION, singleton=True, quantity=-1)

        lab, lab_system, jumps = nearest_laboratory(system)
        levels = max(1, -(-jumps // 5))  # the Scientific Networking level that reaches the laboratory's jumps
        dbseed.sql(f"insert into {dbseed.schema('SKILLS')}.character_skills (character_id, type_id, level, skill_points) "
                   f"values ({int(character)}, {SCIENTIFIC_NETWORKING}, {levels}, 1000)")
        me = number(who.call("industryManager", "InstallJob", job_dump(character, blueprint, RESEARCH_MATERIAL, lab_system, facility=lab)))
        ready_now(me)
        who.call("industryManager", "CompleteJob", me, lab_system)
        assert efficiencies(who, blueprint) == (1, 0)

        te = number(who.call("industryManager", "InstallJob", job_dump(character, blueprint, RESEARCH_TIME, lab_system, facility=lab)))
        ready_now(te)
        who.call("industryManager", "CompleteJob", te, lab_system)
        assert efficiencies(who, blueprint) == (1, 2)


def grant_skills(character: int, skills: list[tuple[int, int]]) -> None:
    for type_id, level in skills:
        dbseed.sql(f"insert into {dbseed.schema('SKILLS')}.character_skills (character_id, type_id, level, skill_points) "
                   f"values ({int(character)}, {int(type_id)}, {int(level)}, 1000)")


def test_a_pilot_copies_a_blueprint_at_a_laboratory_and_receives_the_copy():
    race = HEALTH["races"][0]
    run = uuid.uuid4().hex[:10]
    system = station_system()
    with base.connect(f"walk-industry-{run}") as who:
        character, _, _, _ = arrive(who, race, run)
        original = dbseed.new_item(BLUEPRINT, owner=character, location=STATION, singleton=True, quantity=-1)
        lab, lab_system, jumps = nearest_laboratory(system)
        grant_skills(character, [(SCIENTIFIC_NETWORKING, max(1, -(-jumps // 5)))])

        job = number(who.call("industryManager", "InstallJob",
                              job_dump(character, original, COPYING, lab_system, runs=1, facility=lab)))
        assert job_status(job) == 1
        ready_now(job)
        who.call("industryManager", "CompleteJob", job, lab_system)

        assert job_status(job) == 100
        copies = dbseed.sql(f"select count(*) from {dbseed.schema('INVENTORY')}.items "
                            f"where owner_id = {int(character)} and type_id = {BLUEPRINT} and blueprint_copy")
        assert copies == "1", copies


def test_an_invention_uses_the_decryptor_the_client_lists_and_its_skills():
    race = HEALTH["races"][0]
    run = uuid.uuid4().hex[:10]
    system = station_system()
    with base.connect(f"walk-industry-{run}") as who:
        character, _, _, _ = arrive(who, race, run)
        copy = dbseed.new_item(INVENTION_BLUEPRINT, owner=character, location=STATION, singleton=True, quantity=1)
        dbseed.sql(f"update {dbseed.schema('INVENTORY')}.items set blueprint_copy = true, blueprint_runs = 10, "
                   f"material_efficiency = 0, time_efficiency = 0 where id = {int(copy)}")
        dbseed.new_item(20416, owner=character, location=STATION, quantity=2)
        dbseed.new_item(25887, owner=character, location=STATION, quantity=2)
        dbseed.new_item(ACCELERANT, owner=character, location=STATION, quantity=1)
        lab, lab_system, jumps = nearest_laboratory(system)
        grant_skills(character, [(skill, 1) for skill in INVENTION_SKILLS] + [(SCIENTIFIC_NETWORKING, max(1, -(-jumps // 5)))])

        payload = job_dump(character, copy, INVENTION, lab_system, runs=1,
                           materials={20416: 2, 25887: 2, ACCELERANT: 1}, facility=lab, blueprint_type=INVENTION_BLUEPRINT)
        payload["productTypeID"] = INVENTION_PRODUCT
        answer, error = outcome(lambda: who.call("industryManager", "InstallJob", payload))
        assert error is None, f"invention refused: {error!r}"
        job = number(answer)

        # the decryptor's probability multiplier: mean 0.3 x (1 + 2/30 + 1/40) x 1.2 = 0.393
        assert abs(float(dbseed.sql(f"select probability from {dbseed.schema('INDUSTRY')}.jobs where job_id = {int(job)}")) - 0.393) < 1e-6
        assert dbseed.items_of(character, ACCELERANT) == [], "the listed decryptor is consumed at install"

        ready_now(job)
        who.call("industryManager", "CompleteJob", job, lab_system)
        assert job_status(job) == 100
        outputs = dbseed.sql(f"select blueprint_runs, material_efficiency, time_efficiency from {dbseed.schema('INVENTORY')}.items "
                             f"where owner_id = {int(character)} and type_id = {INVENTION_PRODUCT} and blueprint_copy")
        for row in [line for line in outputs.splitlines() if line]:
            assert row.split("|") == ["2", "4", "14"], row


def _entries(described) -> list:
    """The (key, value) pairs of a described dictionary, util.KeyVal object or Python dictionary."""
    if not isinstance(described, dict):
        return []
    if "$dict" in described:
        return [tuple(pair) for pair in described["$dict"]]
    if "$objectex" in described:
        return [tuple(pair) for pair in described["dict"]]
    if "$object" in described:
        return _entries(described["args"])
    return list(described.items())


def _fields(value) -> dict:
    """The fields of a result, by name (the values stay described)."""
    return dict(_entries(describe(value)))


def test_the_facility_selector_sees_the_station_its_activities_its_hangar_and_its_tax():
    race = HEALTH["races"][0]
    run = uuid.uuid4().hex[:10]
    system = station_system()
    lab, _, _ = nearest_laboratory(system)
    with base.connect(f"walk-industry-{run}") as who:
        character, _, _, _ = arrive(who, race, run)

        facility = _fields(who.call("facilityManager", "GetFacility", STATION))
        assert number(facility["facilityID"]) == STATION
        activities = {number(key) for key, _ in _entries(facility["activities"])}
        # the station has a Factory (14) and no Laboratory (15): manufacturing only, from the SDE operation
        assert 1 in activities and 3 not in activities, sorted(activities)

        lab_facility = _fields(who.call("facilityManager", "GetFacility", lab))
        lab_activities = {number(key) for key, _ in _entries(lab_facility["activities"])}
        assert {3, 4, 5, 8} <= lab_activities

        location = _fields(who.call("facilityManager", "GetFacilityLocations", STATION, character)[0])
        assert number(location["itemID"]) == STATION and number(location["flagID"]) == dbseed.HANGAR
        assert number(location["ownerID"]) == character

        taxes = _fields(who.call("facilityManager", "GetFacilityTaxes", STATION, 0))
        assert "taxCorporation" in taxes

        listed = describe(who.call("facilityManager", "GetFacilities"))
        ids = [number(dict(_entries(row))["facilityID"]) for row in listed]
        assert STATION in ids, ids

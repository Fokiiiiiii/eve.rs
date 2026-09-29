"""Login to a character, against a running server, compared with what the reference was recorded doing.

    EVE_LIVE_HOST=host:port [EVE_LIVE_SERVER=reference|eve.rs] pytest tests/test_login_to_character.py

The recordings (oracle/fixtures/scenarios, made by scripts/record_character_scenarios.py) hold every call of the
walk from a new account to a session with a character, and what the reference answered. This file replays the
calls against `EVE_LIVE_HOST` and compares each answer with the recorded one by shape and value. Per-run
values (identifiers, names, times, the place a character starts) are compared by type only.

`EVE_LIVE_SERVER` says which server is running. `reference` must reproduce its own recordings exactly (this
catches a recording that has drifted from the reference). `eve.rs` (the default) may differ only where
docs/compat/DEVIATIONS.md says so; each place is marked DEVIATION below and stated as what eve.rs does instead.

Skipped unless EVE_LIVE_HOST is set. tools/dev/run-eve-live.sh starts eve.rs and runs this.
"""

from __future__ import annotations

import json
import os
import re
import uuid

import pytest

from evecli import marshal as m
from evecli import packet as p
from evecli.client import Client
from evecli.describe import describe, differences, type_name

LIVE = os.environ.get("EVE_LIVE_HOST", "")
SERVER = os.environ.get("EVE_LIVE_SERVER", "eve.rs")
REFERENCE = SERVER == "reference"
pytestmark = pytest.mark.skipif(not LIVE, reason="set EVE_LIVE_HOST=host:port to test against a running server")

SCENARIOS = os.path.join(os.path.dirname(__file__), "..", "..", "..", "oracle", "fixtures", "scenarios")

# Values that differ per run, by account or by design (role: DEVIATIONS.md). Compared by type. Where a character
# starts is not among them: eve.rs starts everyone where the reference does.
VOLATILE = {
    "characterID", "characterName", "userName", "creationDate", "logoffDate",
    # in the session change
    "charid", "shipid", "role",
}
# the ids in a skill entry are this character's
ITEM_FIELDS = {"itemID", "ownerID", "locationID"}


def load(name):
    with open(os.path.join(SCENARIOS, name), encoding="utf-8") as handle:
        return json.load(handle)


WALK = load("login_to_character.json")
STARTERS = load("starters.json")["starters"]
RECORDED_RUN = re.search(r"(\d{6,})$", next(s["call"][2][0] for s in WALK["steps"] if s["call"][1] == "CreateCharacterWithDoll")).group(1)


def connect(name: str) -> Client:
    host, _, port = LIVE.partition(":")
    client = Client(host or "127.0.0.1", int(port or 26000), timeout=30.0)
    client.login(name, "walk-password")
    return client


def attempt(client: Client, service: str, method: str, *args):
    """("ok", description) or ("error", {"code", "payload"})."""
    try:
        return "ok", describe(client.call(service, method, *args))
    except p.MachoError as error:
        return "error", {"code": describe(error.code), "payload": describe(error.payload)}


def revive(value, run: str, ids: dict | None = None, recorded_run: str = RECORDED_RUN):
    """A recorded argument as a value to send. Names carry the run they were recorded in: they get this one, and
    a character id the recording made becomes the one this server gave (`ids`)."""
    if isinstance(value, str):
        return value.replace(recorded_run, run)
    if isinstance(value, bool) or value is None:
        return value
    if isinstance(value, int):
        return (ids or {}).get(value, value)
    if isinstance(value, list):
        return [revive(v, run, ids, recorded_run) for v in value]
    if isinstance(value, dict) and "$dict" in value:
        return m.Dict([(k, revive(v, run, ids, recorded_run)) for k, v in value["$dict"]])
    if isinstance(value, dict) and "$tuple" in value:
        return tuple(revive(v, run, ids, recorded_run) for v in value["$tuple"])
    raise ValueError(f"cannot replay {value!r}")


def brief(outcome) -> str:
    return json.dumps(outcome, ensure_ascii=False)[:300]


def as_dict(plain: dict):
    """A plain {name: description} mapping in the form `differences` compares."""
    return {"$dict": [[k, v] for k, v in plain.items()]}


def blank(described, *path):
    """A copy of a description with the value at `path` (indexes and keys) replaced by a marker."""
    copy = json.loads(json.dumps(described))
    node = copy
    for step in path[:-1]:
        node = node[step]
    node[path[-1]] = "$blanked"
    return copy


def same_way(expected, actual, where: str, volatile=VOLATILE) -> list[str]:
    return differences(expected, actual, where, volatile_keys=volatile)


# ── the walk ────────────────────────────────────────────────────────────────


def test_the_session_starts_with_the_recorded_keys_and_types():
    with connect(f"walk-init-{uuid.uuid4().hex[:8]}") as client:
        init = client.session.session_init
        assert isinstance(init, dict), init
        ours = {"$dict": [[k, describe(v)] for k, v in init.items()]}
    recorded = {"$dict": [[k, v] for k, v in WALK["login"]["sessionInit"].items()]}
    # every value is per run or per account: keys and types must agree, values need not
    assert same_way(recorded, ours, "sessionInit", volatile=set(WALK["login"]["sessionInit"])) == []


def test_the_recorded_walk_replays():
    run = uuid.uuid4().hex[:10]
    walker = connect(f"walk-{run}")
    other = None
    ids: dict[int, int] = {}  # a recorded character id -> the id this server gave
    made = 0
    problems: list[str] = []
    try:
        for index, step in enumerate(WALK["steps"]):
            service, method, described = step["call"]
            args = [revive(a, run) for a in described]
            where = f"step {index} {method}{json.dumps(described, ensure_ascii=False)[:80]}"

            if method == "SelectCharacterID" and isinstance(args[0], int):
                other_id = int(WALK["otherAccountCharacter"]["ok"])
                if args[0] == other_id and other is None:
                    # a character of another account, made by that account
                    other = connect(f"walk-other-{run}")
                    kind, made_other = attempt(other, *_call(WALK["otherAccountCharacter"], run))
                    assert kind == "ok", brief(made_other)
                    ids[other_id] = int(made_other)
                args = [ids.get(args[0], args[0])]

            if method == "SelectCharacterID":
                walker.notifications.clear()
            kind, outcome = attempt(walker, service, method, *args)
            recorded_kind = "ok" if "ok" in step else "error"

            if method == "CreateCharacterWithDoll" and recorded_kind == "ok":
                made += 1
                if made > 3 and not REFERENCE:
                    # DEVIATION (slots): eve.rs refuses a character beyond the three slots the screen shows; the
                    # reference lets the account keep making them.
                    problems += _expect_info_error(kind, outcome, where)
                    continue
                if kind == "ok":
                    ids[int(step["ok"])] = int(outcome)

            if kind != recorded_kind:
                problems.append(f"{where}: recorded {recorded_kind}, got {kind}: {brief(outcome)}")
                continue
            if kind == "error":
                problems += same_way(as_dict(step["error"]), as_dict(outcome), where)
                continue

            recorded_ok = step["ok"]
            if method == "GetValidRandomName" or (method == "CreateCharacterWithDoll") or (service == "machoNet" and method == "GetTime"):
                # a random name, a new id and the clock: only their type is the contract
                if type_name(recorded_ok) != type_name(outcome):
                    problems.append(f"{where}: {type_name(recorded_ok)} expected, got {type_name(outcome)}")
                continue
            if method == "GetNumCharacters" and not REFERENCE:
                recorded_ok = min(recorded_ok, 3)  # DEVIATION (slots), see above
            problems += same_way(recorded_ok, outcome, where)

        # what the reference sent when the character was chosen: the session change, then the skills notification
        walker.ping()
        seen = [describe(n.raw) for n in walker.notifications if n.command in (p.SESSIONCHANGENOTIFICATION, p.NOTIFICATION)]
        recorded_changes = WALK["afterSelect"]
        assert len(seen) == len(recorded_changes), f"{len(seen)} notifications after selecting, {len(recorded_changes)} recorded"
        problems += _compare_session_change(recorded_changes[0], seen[0])
        problems += _compare_skills_notification(recorded_changes[1], seen[1])
    finally:
        walker.close()
        if other is not None:
            other.close()
    assert problems == [], "\n".join(problems)


def _call(step, run):
    service, method, described = step["call"]
    return (service, method, *[revive(a, run) for a in described])


def _expect_info_error(kind, outcome, where) -> list[str]:
    if kind != "error":
        return [f"{where}: a fourth character was made; eve.rs allows three per account"]
    payload = outcome["payload"]["$objectex"]["$tuple"]
    if payload[1]["$tuple"][0] != "CustomInfo":
        return [f"{where}: expected CustomInfo, got {brief(outcome)}"]
    return []


def _compare_session_change(recorded, ours) -> list[str]:
    if ours is None:
        return ["no SessionChangeNotification after SelectCharacterID"]
    # who it is for and which session are per run
    for path in (("$tuple", 3), ("$tuple", 4, "$tuple", 0), ("$tuple", 2, "args", "$tuple", 1)):
        recorded, ours = blank(recorded, *path), blank(ours, *path)
    out = same_way(recorded, ours, "SessionChangeNotification")
    # the order of the attributes is the reference's too
    names = lambda d: [k for k, _ in d["$tuple"][4]["$tuple"][1]["$tuple"][1]["$dict"]]  # noqa: E731
    if names(recorded) != names(ours):
        out.append(f"SessionChangeNotification: attributes {names(ours)} in place of {names(recorded)}")
    return out


def unpack(notification):
    """A notification with the marshalled stream inside it decoded, so it can be compared as the values it holds."""
    copy = json.loads(json.dumps(notification))
    wrapper = copy["$tuple"][4]["$tuple"][0]["$tuple"]
    wrapper[1] = describe(m.decode(bytes.fromhex(wrapper[1]["$buffer"])))
    return copy


def _compare_skills_notification(recorded, ours) -> list[str]:
    recorded, ours = unpack(recorded), unpack(ours)
    # the user, and the time it was sent
    for path in (("$tuple", 3), ("$tuple", 4, "$tuple", 0, "$tuple", 1, "$tuple", 1, "$tuple", 1, "$tuple", 2)):
        recorded, ours = blank(recorded, *path), blank(ours, *path)
    return same_way(recorded, ours, "OnServerSkillsChanged", volatile=VOLATILE | ITEM_FIELDS)


# ── names ───────────────────────────────────────────────────────────────────


@pytest.mark.parametrize("entry", WALK["names"], ids=lambda e: repr(e["name"]))
def test_name_validation_answers_as_recorded(entry):
    expected = entry["ok"]
    if entry["name"] == "Elysian" and not REFERENCE:
        expected = 1  # DEVIATION (reserved name): eve.rs reserves only the staff prefixes; "Elysian" is the reference's own character
    with connect(f"walk-names-{uuid.uuid4().hex[:8]}") as client:
        assert client.call("charUnboundMgr", "ValidateNameEx", entry["name"]) == expected


# ── what each bloodline starts with ─────────────────────────────────────────


@pytest.mark.parametrize("starter", STARTERS, ids=lambda s: f"bloodline-{s['bloodlineID']}")
def test_each_bloodline_starts_as_recorded(starter):
    run = uuid.uuid4().hex[:10]
    with connect(f"walk-bl{starter['bloodlineID']}-{run}") as client:
        service, method, *args = _call(starter["create"], run)
        kind, made = attempt(client, service, method, *args)
        assert kind == "ok", brief(made)
        kind, selection = attempt(client, "charUnboundMgr", "GetCharacterSelectionData")
        assert kind == "ok", brief(selection)
    assert same_way(starter["selection"]["ok"], selection, f"bloodline {starter['bloodlineID']}") == []


def _item_ids_follow_the_scheme(skills, where: str) -> list[str]:
    """A skill's item id is its character's id times 100000 plus the skill's type (both servers number them so)."""
    out = []
    for type_id, entry in skills["$dict"]:
        state = {k: v for k, v in entry["$objectex"]["$tuple"][2]["$dict"]}
        item, owner = state["itemID"]["$long"], state["ownerID"]
        if item != owner * 100000 + type_id:
            out.append(f"{where}: skill {type_id} has item id {item}, expected {owner * 100000 + type_id}")
    return out


# ── the skills of a new character ───────────────────────────────────────────

SKILLS = load("skills.json")
RECORDED_SKILLS_RUN = re.search(r"(\d{6,})$", SKILLS["races"][0]["create"]["call"][2][0]).group(1)


def expand(step, shared):
    """A recorded answer with the list it shares with `GetSkills` put back."""
    text = json.dumps(step["ok"])
    if '"$same"' not in text:
        return step["ok"]
    return json.loads(text.replace('{"$same": "GetSkills"}', json.dumps(shared)))


@pytest.mark.parametrize("race", SKILLS["races"], ids=lambda r: f"race-{r['raceID']}")
def test_a_new_character_has_the_skills_the_reference_gives(race):
    run = uuid.uuid4().hex[:10]
    ids: dict[int, int] = {}
    with connect(f"walk-skills-{run}") as client:
        service, method, *args = [
            x for x in [race["create"]["call"][0], race["create"]["call"][1], *[revive(a, run, ids, RECORDED_SKILLS_RUN) for a in race["create"]["call"][2]]]
        ]
        kind, made = attempt(client, service, method, *args)
        assert kind == "ok", brief(made)
        ids[int(race["create"]["ok"])] = int(made)
        client.notifications.clear()
        assert attempt(client, "charUnboundMgr", "SelectCharacterID", int(made), None, False) == ("ok", None)
        client.ping()
        seen = [describe(n.raw) for n in client.notifications if n.command in (p.SESSIONCHANGENOTIFICATION, p.NOTIFICATION)]
        assert len(seen) == len(race["afterSelect"]) == 2
        problems = _compare_skills_notification(race["afterSelect"][1], seen[1])

        shared = next(step["ok"] for step in race["calls"] if step["call"][1] == "GetSkills")
        for step in race["calls"] + race["bind"]:
            service, method, described = step["call"]
            where = f"race {race['raceID']} {service}.{method}{json.dumps(described)}"
            kind, outcome = attempt(client, service, method, *[revive(a, run, ids, RECORDED_SKILLS_RUN) for a in described])
            if kind != "ok" or "ok" not in step:
                problems.append(f"{where}: recorded {'ok' if 'ok' in step else 'error'}, got {kind}: {brief(outcome)}")
                continue
            expected = expand(step, shared)
            if method == "GetMySkillHandler":
                # the moniker names the character it is for
                expected, outcome = blank(expected, "args", "$tuple", 2), blank(outcome, "args", "$tuple", 2)
            if method == "MachoBindObject":
                # a bound object is named by a counter and the time it was made
                expected, outcome = blank(expected, "$tuple", 0, "$substruct", "$substream", "$tuple"), blank(outcome, "$tuple", 0, "$substruct", "$substream", "$tuple")
            problems += same_way(expected, outcome, where, volatile=VOLATILE | ITEM_FIELDS)
            if method == "GetSkills":
                problems += _item_ids_follow_the_scheme(outcome, where)
    assert problems == [], "\n".join(problems[:20])


# ── what the character manager and the wallet say about a new character ─────

INFO = load("character_info.json")
RECORDED_INFO_RUN = re.search(r"(\d{6,})$", INFO["races"][0]["create"]["call"][2][0]).group(1)
TIMES = {"createDateTime", "startDateTime"}
DESCRIPTION = "Character created via EveJS Elysian"


def substitute(described, ids: dict):
    """A recorded answer with the character ids the recording made replaced by the ones this server gave."""
    if isinstance(described, bool):
        return described
    if isinstance(described, int):
        return ids.get(described, described)
    if isinstance(described, list):
        return [substitute(item, ids) for item in described]
    if isinstance(described, dict):
        return {key: substitute(value, ids) for key, value in described.items()}
    return described


def without_cache_time(described):
    """A cached method result without the time its version was made (the checksum after it is the marshalled result's)."""
    if isinstance(described, dict) and described.get("$object", "").endswith("CachedMethodCallResult"):
        return blank(described, "args", "$tuple", 2, 0)
    return described


def with_description(described, text: str):
    """A recorded public record whose description is `text` (the reference's own is its branding)."""
    copy = json.loads(json.dumps(described))
    records = copy if isinstance(copy, list) else [copy]
    for record in records:
        for pair in record["args"]["$dict"]:
            if pair[0] == "description":
                pair[1] = text
    return copy


@pytest.mark.parametrize("race", INFO["races"], ids=lambda r: f"race-{r['raceID']}")
def test_a_new_character_is_described_as_the_reference_describes_it(race):
    run = uuid.uuid4().hex[:10]
    ids: dict[int, int] = {}
    with connect(f"walk-info-{run}") as client:
        service, method, *args = [race["create"]["call"][0], race["create"]["call"][1], *[revive(a, run, ids, RECORDED_INFO_RUN) for a in race["create"]["call"][2]]]
        kind, made = attempt(client, service, method, *args)
        assert kind == "ok", brief(made)
        ids[int(race["create"]["ok"])] = int(made)
        assert attempt(client, "charUnboundMgr", "SelectCharacterID", int(made), None, False) == ("ok", None)

        problems = []
        for step in race["calls"]:
            service, method, described = step["call"]
            where = f"race {race['raceID']} {service}.{method}{json.dumps(described)}"
            kind, outcome = attempt(client, service, method, *[revive(a, run, ids, RECORDED_INFO_RUN) for a in described])
            if kind != ("ok" if "ok" in step else "error"):
                problems.append(f"{where}: recorded {'ok' if 'ok' in step else 'error'}, got {kind}: {brief(outcome)}")
                continue
            if kind == "error":
                problems += same_way(as_dict(step["error"]), as_dict(outcome), where)
                continue
            expected = step["ok"]
            if method == "GetCharacterCreationDate":
                if type_name(expected) != type_name(outcome):
                    problems.append(f"{where}: {type_name(expected)} expected, got {type_name(outcome)}")
                continue
            if not REFERENCE:
                # DEVIATION (description): the reference's default description is its own branding; eve.rs has none
                if method == "GetCharacterDescription":
                    expected = ""
                if method in ("GetPublicInfo", "GetPublicInfo3"):
                    expected = with_description(expected, "")
            expected, outcome = without_cache_time(substitute(expected, ids)), without_cache_time(outcome)
            problems += same_way(expected, outcome, where, volatile=VOLATILE | TIMES)
    assert problems == [], "\n".join(problems[:20])


# ── the stations of the game ────────────────────────────────────────────────

STATIONS = load("stations.json")["stations"]


def test_every_recorded_station_is_reported_as_the_reference_reported_it():
    with connect(f"walk-stations-{uuid.uuid4().hex[:8]}") as client:
        problems = []
        for station in STATIONS:
            where = f"station {station['id']}"
            kind, outcome = attempt(client, "stationSvc", "GetStation", station["id"])
            if kind != "ok":
                problems.append(f"{where}: {brief(outcome)}")
                continue
            # the checksum in the version is that of the marshalled record: the same bytes as the reference wrote
            problems += same_way(without_cache_time(station["call"]["ok"]), without_cache_time(outcome), where)
        assert problems == [], "\n".join(problems[:20])


def test_a_station_that_does_not_exist_is_not_made_up():
    if REFERENCE:
        pytest.skip("the reference answers a made-up station id with Jita IV (DEVIATIONS.md, row 9)")
    with connect(f"walk-nostation-{uuid.uuid4().hex[:8]}") as client:
        assert client.call("stationSvc", "GetStation", 1) is None
        assert client.call("stationSvc", "GetStation", 60003759) is None


# ── the inventories of a docked character ───────────────────────────────────

INVENTORY = load("inventory.json")
RECORDED_INVENTORY_RUN = re.search(r"(\d{6,})$", INVENTORY["races"][0]["create"]["call"][2][0]).group(1)


def unwrap_longs(described):
    """The same description with `{"$long": n}` as `n`: an item id is a long or an int by its size, and the client reads both alike."""
    if isinstance(described, dict):
        if set(described) == {"$long"}:
            return described["$long"]
        return {key: unwrap_longs(value) for key, value in described.items()}
    if isinstance(described, list):
        return [unwrap_longs(item) for item in described]
    return described


def as_rows(described):
    """`util.Row` and `Rowset` answers as the packed rows they hold, so that they compare with the same rules."""
    if isinstance(described, list):
        return [as_rows(item) for item in described]
    if isinstance(described, dict) and described.get("$object") == "util.Row":
        parts = {key: value for key, value in described["args"]["$dict"]}
        return {"$row": dict(zip(parts["header"]["$tuple"], parts["line"]["$tuple"]))}
    if isinstance(described, dict) and described.get("$object", "").endswith("rowset.Rowset"):
        parts = {key: value for key, value in described["args"]["$dict"]}
        header = parts["header"]
        return {"$tuple": [{"$row": dict(zip(header, line["$tuple"]))} for line in parts["lines"]]}
    return described


def inventory_form(described):
    return unwrap_longs(as_rows(described))


def starter_modules(race_id: int) -> list[tuple[int, int]]:
    """(type, flag) of what a new ship of this race is fitted with, from the dogma recording, in the order of its slots."""
    race = next(r for r in DOGMA["races"] if r["raceID"] == race_id)
    info = keyval(race["bind"]["ok"]["$tuple"][1])
    ship, *modules = pairs(info["shipInfo"])
    rows = [keyval(item)["invItem"]["$row"] for _, item in modules]
    return sorted(((row["typeID"], row["flagID"]) for row in rows), key=lambda t: t[1])


def ship_contents(race_id: int, method: str, described: list, outcome, where: str) -> list[str]:
    """What eve.rs answers a ship's List, ListByFlags and GetContainerContents with: the modules fitted to it."""
    kit = starter_modules(race_id)
    if method == "GetContainerContents":
        lines = next(v for k, v in outcome["args"]["$dict"] if k == "lines")
        got = sorted(((line["$tuple"][1], number(line["$tuple"][4])) for line in lines), key=lambda t: t[1])
        return [] if got == kit else [f"{where}: fitted {got}, expected {kit}"]
    flags = None
    if method == "List" and described and described[0] not in (None, 0):
        flags = {described[0]}
    if method == "ListByFlags":
        flags = set(described[0]["$tuple"])
    wanted = [(t, f) for t, f in kit if flags is None or f in flags]
    if not wanted and method == "List":
        empty_set = {"$objectex": {"$tuple": [{"$token": "__builtin__.set"}, {"$tuple": [[]]}]}, "list": [], "dict": []}
        return [] if outcome == empty_set else [f"{where}: expected the empty set, got {brief(outcome)}"]
    if not isinstance(outcome, list):
        return [f"{where}: expected the modules, got {brief(outcome)}"]
    got = sorted(((row["$row"]["typeID"], row["$row"]["flagID"]) for row in outcome), key=lambda t: t[1])
    return [] if got == wanted else [f"{where}: fitted {got}, expected {wanted}"]


@pytest.mark.parametrize("race", INVENTORY["races"], ids=lambda r: f"race-{r['raceID']}")
def test_a_new_characters_inventories_are_those_of_the_reference(race):
    run = uuid.uuid4().hex[:10]
    ids: dict[int, int] = {}
    with connect(f"walk-inventory-{run}") as client:
        service, method, *args = [race["create"]["call"][0], race["create"]["call"][1], *[revive(a, run, ids, RECORDED_INVENTORY_RUN) for a in race["create"]["call"][2]]]
        kind, made = attempt(client, service, method, *args)
        assert kind == "ok", brief(made)
        ids[int(race["create"]["ok"])] = int(made)
        assert attempt(client, "charUnboundMgr", "SelectCharacterID", int(made), None, False) == ("ok", None)

        problems = []
        kind, descriptor = attempt(client, "invbroker", "GetItemDescriptor")
        problems += same_way(race["descriptor"]["ok"], descriptor, "GetItemDescriptor") if kind == "ok" else [f"GetItemDescriptor: {brief(descriptor)}"]

        hangar = client.call("invbroker", "GetInventory", 10004)
        ship_id = client.call_bound(hangar, "List")[0].fields()["itemID"]
        ids[race["shipID"]] = int(ship_id)
        for label, inventory in race["inventories"].items():
            opener, argument = inventory["open"]["call"][1], revive(inventory["open"]["call"][2][0], run, ids, RECORDED_INVENTORY_RUN)
            handle = client.call("invbroker", opener, argument)
            opened = describe(handle)
            problems += same_way(
                blank(inventory["open"]["ok"], "$substruct", "$substream", "$tuple"), blank(opened, "$substruct", "$substream", "$tuple"), f"{label}: {opener}")
            for step in inventory["calls"]:
                _, method, described = step["call"]
                where = f"race {race['raceID']} {label}.{method}{json.dumps(described)}"
                args = [tuple(a["$tuple"]) if isinstance(a, dict) else a for a in described]
                try:
                    outcome = describe(client.call_bound(handle, method, *args))
                except p.MachoError as error:
                    problems.append(f"{where}: error {brief(describe(error.payload))}")
                    continue
                expected = step["ok"]
                if not REFERENCE:
                    # GAP (capacity): the inventory's capacity and volume come with dogma; eve.rs has no answer yet
                    if method == "GetCapacity":
                        if outcome is not None:
                            problems.append(f"{where}: eve.rs has no capacity yet, got {brief(outcome)}")
                        continue
                    if label == "ship":
                        # DEVIATION (ship contents): the reference answers null for what is in a ship and lists the ship among its own
                        # contents. eve.rs lists what is fitted to it (the client iterates the answer): the starter modules of the race,
                        # each in its slot, and a flag with nothing in it is the empty set a hangar answers with (DEVIATIONS.md #10)
                        if method in ("List", "ListByFlags", "GetContainerContents"):
                            problems += ship_contents(race["raceID"], method, described, outcome, where)
                            continue
                problems += same_way(inventory_form(expected), inventory_form(outcome), where, volatile=VOLATILE | ITEM_FIELDS)
    assert problems == [], "\n".join(problems[:20])


# ── dogma: the ship, its fitting and the character ──────────────────────────

DOGMA = load("dogma.json")
RECORDED_DOGMA_RUN = re.search(r"(\d{6,})$", DOGMA["races"][0]["create"]["call"][2][0]).group(1)
# a Windows FILETIME of these years is a time, whatever the run: it is compared by shape only
FILETIME_RANGE = (10**17, 2 * 10**17)
# two servers work out the attributes separately: to nine digits is the same number
DOGMA_TOLERANCE = 1e-9


def number(described):
    return described["$long"] if isinstance(described, dict) else described


def keyval(described) -> dict:
    return {k: v for k, v in described["args"]["$dict"]}


def pairs(described) -> list:
    return described["$dict"]


def dogma_ids(info) -> list[int]:
    """The ids of a GetAllInfo answer that belong to a run: the character, the ship and what is fitted to it."""
    fields = keyval(info)
    (character, _), = pairs(fields["charInfo"]["$tuple"][0])
    return [number(character)] + [number(key) for key, _ in pairs(fields["shipInfo"])]


def substitute(described, mapping: dict[int, int]):
    """A description with the ids of a recording replaced by the ones this run was given."""
    if isinstance(described, bool) or described is None:
        return described
    if isinstance(described, int):
        return mapping.get(described, described)
    if isinstance(described, list):
        return [substitute(v, mapping) for v in described]
    if isinstance(described, dict):
        if "$long" in described:
            given = mapping.get(described["$long"], described["$long"])
            # a number is written as JavaScript's marshal writes it: a long only when it does not fit 32 bits
            return {"$long": given} if not -(2**31) <= given < 2**31 else given
        return {k: substitute(v, mapping) for k, v in described.items()}
    return described


def blank_times(described):
    """A description with every FILETIME replaced by a marker: when something started is not part of what it is."""
    if isinstance(described, list):
        return [blank_times(v) for v in described]
    if isinstance(described, dict):
        if "$long" in described and isinstance(described["$long"], int) and FILETIME_RANGE[0] <= described["$long"] <= FILETIME_RANGE[1]:
            return "$time"
        return {k: blank_times(v) for k, v in described.items()}
    return described


def set_attribute(attributes: list, attribute: int, value) -> None:
    for pair in attributes:
        if pair[0] == attribute:
            pair[1] = value
            return
    raise AssertionError(f"attribute {attribute} is not in the recording")


@pytest.mark.parametrize("race", DOGMA["races"], ids=lambda r: f"race-{r['raceID']}")
def test_a_new_characters_dogma_is_that_of_the_reference(race):
    run = uuid.uuid4().hex[:10]
    ids: dict[int, int] = {}
    with connect(f"walk-dogma-{run}") as client:
        service, method, *args = [race["create"]["call"][0], race["create"]["call"][1], *[revive(a, run, ids, RECORDED_DOGMA_RUN) for a in race["create"]["call"][2]]]
        kind, made = attempt(client, service, method, *args)
        assert kind == "ok", brief(made)
        assert attempt(client, "charUnboundMgr", "SelectCharacterID", int(made), None, False) == ("ok", None)

        service, method, call_args = race["bind"]["call"]
        outcome = describe(client.call(service, method, *[revive(a, run, ids, RECORDED_DOGMA_RUN) for a in call_args]))

    handle_shape = ("$substruct", "$substream", "$tuple")
    expected = race["bind"]["ok"]
    problems = same_way(blank(expected["$tuple"][0], *handle_shape), blank(outcome["$tuple"][0], *handle_shape), "MachoBindObject", volatile=set())
    expected_info, actual_info = expected["$tuple"][1], outcome["$tuple"][1]

    recorded_ids, given_ids = dogma_ids(expected_info), dogma_ids(actual_info)
    assert len(recorded_ids) == len(given_ids), f"ids {recorded_ids} against {given_ids}"
    expected_info = substitute(expected_info, dict(zip(recorded_ids, given_ids)))
    if not REFERENCE:
        # DEVIATION (drone limits on the ship): the reference also gives the ship the character's two drone limits, which the client
        # reads from the character (docs/compat/DEVIATIONS.md #12)
        ship_attributes = pairs(keyval(pairs(keyval(expected_info)["shipInfo"])[0][1])["attributes"])
        ship_attributes[:] = [pair for pair in ship_attributes if pair[0] not in (352, 458)]
        # DEVIATION (the character as the ship changes it): eve.rs applies every modifier that reaches the character: the trade
        # premium is 0.9 with Trade trained (the reference says 0, which its own brain contradicts), and the Gallente rookie ship's
        # mining-drone bonus is there (DEVIATIONS.md #11)
        character_attributes = pairs(keyval(keyval(expected_info)["shipModifiedCharAttribs"])["attributes"])
        set_attribute(character_attributes, 443, 0.9)
        if race["raceID"] == 8:
            set_attribute(character_attributes, 428, 120)
    problems += differences(
        blank_times(expected_info), blank_times(actual_info), "GetAllInfo", volatile_keys=set(), float_tolerance=DOGMA_TOLERANCE)
    assert problems == [], "\n".join(problems[:30])

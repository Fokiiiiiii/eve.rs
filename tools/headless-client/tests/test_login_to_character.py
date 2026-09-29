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


def revive(value, run: str):
    """A recorded argument as a value to send. Names carry the run they were recorded in: they get this one."""
    if isinstance(value, str):
        return value.replace(RECORDED_RUN, run)
    if value is None or isinstance(value, (bool, int)):
        return value
    if isinstance(value, dict) and "$dict" in value:
        return m.Dict([(k, revive(v, run)) for k, v in value["$dict"]])
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

        # the session change the reference sent when the character was chosen
        walker.ping()
        seen = [describe(n.raw) for n in walker.notifications if n.command in (p.SESSIONCHANGENOTIFICATION, p.NOTIFICATION)]
        recorded_changes = WALK["afterSelect"]
        problems += _compare_session_change(recorded_changes[0], seen[0] if seen else None)
        rest = [n for n in seen[1:]]
        if REFERENCE:
            assert len(seen) == len(recorded_changes), f"{len(seen)} notifications after selecting, {len(recorded_changes)} recorded"
        else:
            # GAP (skills): the reference also broadcasts OnServerSkillsChanged with the character's skills; eve.rs has
            # no skills module yet. Nothing else may arrive.
            assert rest == [], f"unexpected notifications: {brief(rest)}"
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

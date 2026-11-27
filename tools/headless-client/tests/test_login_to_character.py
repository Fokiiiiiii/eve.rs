"""Login to a character, against a running server, compared with what the reference was recorded doing.

    EVE_LIVE_HOST=host:port [EVE_LIVE_SERVER=reference|eve.rs] pytest tests/test_login_to_character.py

The recordings (oracle/fixtures/scenarios, made by scripts/record_character_scenarios.py) hold every call of the
walk from a new account to a session with a character, and what the reference answered. This file replays the
calls against `EVE_LIVE_HOST` and compares each answer with the recorded one by shape and value. Per-run
values (identifiers, names, times, the place a character starts) are compared by type only.

`EVE_LIVE_SERVER` says which server is running. `reference` must reproduce its own recordings exactly (this
catches a recording that has drifted from the reference). `eve.rs` (the default) may differ only where
docs/reference/compat/DEVIATIONS.md says so; each place is marked DEVIATION below and stated as what eve.rs does instead.

Skipped unless EVE_LIVE_HOST is set. tools/dev/run-eve-live.sh starts eve.rs and runs this.
"""

from __future__ import annotations

import base64
import copy
import json
import math
import os
import re
import time
import subprocess
import sys
import uuid
from datetime import datetime, timedelta, timezone

import pytest

from evecli import marshal as m
from evecli import packet as p
from evecli.client import Client
from evecli.describe import describe, differences, type_name
from evecli.observe import hangar_and_ship, notifications_of, ship_of, training_view, wait_for_events
from tests.runid import run_token

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
    "charid", "shipid", "role", "sn",
}
# the ids in a skill entry are this character's
ITEM_FIELDS = {"itemID", "ownerID", "locationID"}

def load(name):
    with open(os.path.join(SCENARIOS, name), encoding="utf-8") as handle:
        return json.load(handle)

WALK = load("login_to_character.json")
STARTERS = load("starters.json")["starters"]
RECORDED_RUN = re.search(r"(\d{6,})$", next(s["call"][2][0] for s in WALK["steps"] if s["call"][1] == "CreateCharacterWithDoll")).group(1)

def connect(name: str, *, timeout: float = 30.0) -> Client:
    host, _, port = LIVE.partition(":")
    address = (host or "127.0.0.1", int(port or 26000))
    if os.environ.get("EVE_IDENTITY_SCHEMA"):
        # eve.rs with its database: the account is made by its first login and its role bits are read at a login, so the walk's account is a
        # game master's (the slash commands need a staff role) by a fixture, and the walk logs in again after it
        first = Client(*address, timeout=timeout)
        first.login(name, "walk-password")
        first.close()
        from tests import dbseed  # noqa: PLC0415  (only the walks that run against the database need it)

        dbseed.grant_roles(name)
    client = Client(*address, timeout=timeout)
    client.login(name, "walk-password")
    return client


def notification_type_ids(value) -> list[int]:
    """Read the V24 notification manager's CRowset or the reference-compatible util.KeyVal list."""
    required_fields = {"notificationID", "typeID", "senderID", "receiverID", "processed", "created", "data"}
    if isinstance(value, m.ObjectEx):
        described = describe(value)
        object_header = described["$objectex"]["$tuple"][0]
        first = object_header.get("$tuple", [None])[0] if isinstance(object_header, dict) else None
        first = first.get("$tuple", [first])[0] if isinstance(first, dict) else first
        class_name = first.get("$token") if isinstance(first, dict) else None
        assert class_name == "carbon.common.script.sys.crowset.CRowset", class_name
        rows = value.items
        row_kind = "CRowset"
    else:
        assert isinstance(value, m.List), type(value).__name__
        rows = value
        row_kind = "list"

    result = []
    for row in rows:
        if isinstance(row, m.PackedRow):
            fields = row.fields()
            assert required_fields.issubset(fields), f"{row_kind} DTO is missing {required_fields - set(fields)}"
            result.append(int(m.plain(fields["typeID"])))
            continue
        assert isinstance(row, m.Object), f"{row_kind} row has marshal type {type(row).__name__}"
        name, fields = m.plain(row)
        assert name == "util.KeyVal", name
        assert required_fields.issubset(fields), f"{row_kind} DTO is missing {required_fields - set(fields)}"
        result.append(int(fields["typeID"]))
    return result


def packet_event_name(described: dict) -> str:
    packet = described["$tuple"]
    if packet[0] == p.SESSIONCHANGENOTIFICATION:
        return "$sessionchange"
    destination = packet[2]
    return destination["args"]["$tuple"][1]

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
    # This client's plain session view unwraps PyLong; the Host wire test checks role's actual type.
    # maxSessionTime and inDetention are not sent on purpose (SessionInitialState.cs): the reference's notification omits

    # (basesession.py GetDefaultValueOfAttribute; mailSvc.SendMsgDlg reads inDetention). Neither key is re-added here.
    not_sent = {"maxSessionTime", "inDetention"}
    assert not_sent.isdisjoint(init), sorted(not_sent & set(init))
    ours["$dict"] = [pair for pair in ours["$dict"] if pair[0] != "role"]
    recorded["$dict"] = [pair for pair in recorded["$dict"] if pair[0] not in ({"role"} | not_sent)]
    assert "role" in init
    # every value is per run or per account: keys and types must agree, values need not
    assert same_way(recorded, ours, "sessionInit", volatile=set(WALK["login"]["sessionInit"]) - not_sent) == []

def test_the_recorded_walk_replays():
    run = run_token()
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
            if method == "ToggleValidation":
                # The recording and this walk use a GM account (role bit 54), whose first toggle disables validation.
                # The headless-client fixture grants staff roles for this account; the elevated path is also pinned by
                # ValidationAndOmegaTests in the .NET suite.
                recorded_ok = False
            problems += same_way(recorded_ok, outcome, where)

        user_id = walker.session.session_init.get("userid")
        character_id = walker.session.session_init.get("charid")
        assert isinstance(user_id, int) and user_id > 0
        assert isinstance(character_id, int) and character_id > 0
        ever_warped = walker.call("achievementTrackerMgr", "HasEverWarped")
        # EveJS defaults a missing achievement record to True. eve.rs derives this from the new character's
        # authoritative achievement state, False, which lets V24 enter through its first-space camera.
        assert ever_warped is (True if REFERENCE else False)
        token = walker.call("userSvc", "GetUserToken")
        if REFERENCE:
            assert token == f"evejs-local-token-{user_id}-{character_id}".encode("utf-8")
        else:
            header, payload, signature = token.decode("ascii").split(".")
            claims = json.loads(base64.urlsafe_b64decode(payload + "=" * (-len(payload) % 4)))
            assert header == "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9"
            assert claims["sub"] == f"USER:EVE:{user_id}"
            assert claims["character_id"] == character_id
            assert int(claims["exp"]) > int(datetime.now(timezone.utc).timestamp())
            assert signature

        # Selection notices depend on the configured AIR experience mode. The reference starts the
        # NodeGraph tutorial; the local live Host defaults it to disabled. Compare the full ordered
        # stream for each mode and compare the shared session/skills payloads below.
        walker.ping()
        seen = [describe(n.raw) for n in walker.notifications if n.command in (p.SESSIONCHANGENOTIFICATION, p.NOTIFICATION)]
        notification_names = [event["name"] for event in notifications_of(walker)]
        assert notification_names[0] == "OnSubscriptionTimeUpdateServer"
        assert "OnCharacterSelected" not in notification_names
        recorded_changes = WALK["afterSelect"]
        recorded_names = [packet_event_name(event) for event in recorded_changes]
        actual_names = [packet_event_name(event) for event in seen]
        if REFERENCE:
            expected_names = recorded_names
        else:
            # Character ships are persisted before selection and fetched from the station inventory;
            # the disabled Host does not start NodeGraph's AIR tutorial for this character.
            absent_when_air_npe_disabled = {
                "OnItemChange",
                "OnStartClientNodeGraph",
                "OnClientNodeGraphMessage",
                "OnClientNodeGraphUpdate",
            }
            expected_names = [name for name in recorded_names if name not in absent_when_air_npe_disabled]
        assert actual_names == expected_names, f"selection notifications {actual_names} in place of {expected_names}"
        expected_skills = next(event for event, event_name in zip(recorded_changes, recorded_names) if event_name == "OnServerSkillsChanged")
        actual_skills = next(event for event, event_name in zip(seen, actual_names) if event_name == "OnServerSkillsChanged")
        problems += _compare_skills_notification(expected_skills, actual_skills)

        # These are two distinct selection modes. EveJS puts the character directly into the
        # tutorial system and starts its AIR NodeGraph; the local Host defaults AIR NPE off and
        # places a new character in its starter station. Check the session the V24 client sees
        # in each mode instead of treating one mode's session delta as the other's contract.
        session_state = walker.session.session_init
        assert session_state.get("solarsystemid2") == 30100032
        assert isinstance(session_state.get("shipid"), int) and session_state["shipid"] > 0
        if REFERENCE:
            assert session_state.get("stationid") is None
            assert session_state.get("locationid") == 30100032
            assert session_state.get("solarsystemid") == 30100032
        else:
            assert session_state.get("stationid") == 60015249
            assert session_state.get("stationid2") == 60015249
            assert session_state.get("locationid") == 60015249
            assert session_state.get("solarsystemid") is None
    finally:
        walker.close()
        if other is not None:
            other.close()
    assert problems == [], "\n".join(problems)

def test_new_account_enters_a_new_character_with_named_arguments():
    run = run_token()
    with connect(f"walk-named-{run}") as client:
        assert m.plain(client.call("charUnboundMgr", "GetNumCharacters")) == 0

        creation = next(
            step
            for step in WALK["steps"]
            if step["call"][1] == "CreateCharacterWithDoll" and "ok" in step
        )
        service, method, described = creation["call"]
        args = [revive(value, run) for value in described]
        character_id = m.plain(client.call(service, method, *args))
        assert isinstance(character_id, int)
        assert m.plain(client.call("charUnboundMgr", "GetNumCharacters")) == 1

        selection_data = m.plain(client.call("charUnboundMgr", "GetCharacterSelectionData"))
        character_rows = selection_data[2]
        assert len(character_rows) == 1
        assert character_rows[0][1]["unreadMailCount"] == 1

        client.notifications.clear()
        result = client.call(
            "charUnboundMgr",
            "SelectCharacterID",
            charID=character_id,
            secondChoiceID=None,
            skipTutorial=False,
        )

        assert m.plain(result) is None
        assert client.session.session_init.get("charid") == character_id

        contract_login = dict(m.plain(client.call("contractProxy", "GetLoginInfo"))[1])
        for name in ("needsAttention", "inProgress", "assignedToMe"):
            assert m.plain(contract_login[name])[1]["lines"] == []
        contract_escrow = dict(m.plain(client.call("contractProxy", "GetMyContractEscrow"))[1])
        assert m.plain(contract_escrow["iskEscrow"]) == 0.0
        assert m.plain(contract_escrow["itemsEscrow"]) == 0

        selection_names = [event["name"] for event in notifications_of(client)]
        assert selection_names[0] == "OnSubscriptionTimeUpdateServer"
        assert "OnCharacterSelected" not in selection_names
        mail = m.plain(client.call("mailMgr", "SyncMail", None, None))
        expected_title = "Welcome to EveJS Elysian" if REFERENCE else "Welcome to New Eden"
        assert any(header[1]["title"] == expected_title for header in mail[1]["newMail"])
        notifications = client.call("notificationMgr", "GetAllNotifications")
        notification_types = notification_type_ids(notifications)
        if REFERENCE:
            # EveJS retains its CRowset shape but has no welcome-mail notification for this new character.
            assert isinstance(notifications, m.ObjectEx)
            assert notification_types == []
        else:
            assert isinstance(notifications, m.List)
            assert notification_types == [1004]

OMEGA_END_FILETIME = 157469184000000000  # 2100-01-01T00:00:00Z: the end time every new account is written with

def test_a_new_account_is_omega_until_2100_on_every_answer():
    """The Omega state is one stored value per account (its subscription end time). A new account is Omega until 2100:
    the clone grade, the subscription time, the lapsed-Omega check and the selection screen's account row must agree."""
    run = run_token()
    with connect(f"walk-omega-{run}") as client:
        assert m.plain(client.call("subscriptionMgr", "GetCloneGrade")) == 1
        assert m.plain(client.call("subscriptionMgr", "GetSubscriptionTime")) == OMEGA_END_FILETIME
        assert m.plain(client.call("charUnboundMgr", "GetCharOmegaDowngradeStatus")) is False
        account_row = m.plain(client.call("charUnboundMgr", "GetCharacterSelectionData"))[0][0][1]
        assert account_row["subscriptionEndTime"] == OMEGA_END_FILETIME
        token = m.plain(client.call("userSvc", "GetUserToken"))
        header, payload, signature = token.split(".")
        claims = json.loads(base64.urlsafe_b64decode(payload + "=" * (-len(payload) % 4)))
        assert header == "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9"
        assert claims["sub"].startswith("USER:EVE:")
        assert int(claims["exp"]) > int(datetime.now(timezone.utc).timestamp())
        assert signature
        accrued = m.plain(client.call("authentication", "AccruedTime"))
        assert len(accrued) == 2
        assert [int(row[0]) for row in accrued] == sorted(int(row[0]) for row in accrued)
        for session_id, started, ended in accrued:
            assert int(session_id) > 0
            assert int(started) <= int(ended)

def test_a_character_logs_off_to_the_selection_screen_and_selects_again():
    """The client's "log off character": userSvc.UserLogOffCharacter answers True, the connection stays open, the
    selection screen lists the character again, and selecting it enters the game again (the session's charid)."""
    run = run_token()
    with connect(f"walk-logoff-{run}") as client:
        creation = next(
            step
            for step in WALK["steps"]
            if step["call"][1] == "CreateCharacterWithDoll" and "ok" in step
        )
        service, method, described = creation["call"]
        character_id = m.plain(client.call(service, method, *[revive(value, run) for value in described]))
        assert client.call("charUnboundMgr", "SelectCharacterID", charID=character_id, secondChoiceID=None, skipTutorial=False) is None
        assert client.session.session_init.get("charid") == character_id

        assert m.plain(client.call("userSvc", "UserLogOffCharacter")) is True

        rows = m.plain(client.call("charUnboundMgr", "GetCharacterSelectionData"))[2]
        assert [row[1]["characterID"] for row in rows] == [character_id]
        assert m.plain(client.call("charUnboundMgr", "SelectCharacterID", charID=character_id, secondChoiceID=None, skipTutorial=False)) is None
        assert client.session.session_init.get("charid") == character_id


def test_a_selected_character_can_be_selected_again_after_an_abrupt_client_exit():
    run = run_token()
    account = f"walk-relogin-{run}"
    creation = next(step for step in WALK["steps"] if step["call"][1] == "CreateCharacterWithDoll" and "ok" in step)
    service, method, recorded_args = creation["call"]
    with connect(account) as client:
        character_id = int(m.plain(client.call(service, method, *[revive(value, run) for value in recorded_args])))

    host, _, port = LIVE.partition(":")
    code = (
        "import os, sys; "
        "from evecli.client import Client; "
        "client = Client(sys.argv[1], int(sys.argv[2]), timeout=20); "
        "client.login(sys.argv[3], sys.argv[4]); "
        "client.call('charUnboundMgr', 'SelectCharacterID', int(sys.argv[5]), None, True); "
        "os._exit(0)"
    )
    exited = subprocess.run(
        [sys.executable, "-c", code, host or "127.0.0.1", port or "26000", account, "walk-password", str(character_id)],
        capture_output=True,
        timeout=30,
        cwd=os.path.dirname(os.path.dirname(__file__)),
    )
    assert exited.returncode == 0, exited.stderr.decode(errors="replace")

    with Client(host or "127.0.0.1", int(port or 26000), timeout=30) as reconnected:
        reconnected.login(account, "walk-password")
        assert reconnected.session.session_init.get("charid") is None
        rows = m.plain(reconnected.call("charUnboundMgr", "GetCharacterSelectionData"))[2]
        assert [row[1]["characterID"] for row in rows] == [character_id]
        assert reconnected.call("charUnboundMgr", "SelectCharacterID", charID=character_id, secondChoiceID=None, skipTutorial=True) is None
        assert reconnected.session.session_init.get("charid") == character_id
        assert reconnected.session.session_init.get("stationid") == 60015249
        assert reconnected.session.session_init.get("solarsystemid") is None

def test_search_query_and_quickquery_use_the_registered_host_route():
    """The client's item type lookup reads Query's category map and QuickQuery's flat ID list."""
    run = run_token()
    with connect(f"walk-search-{run}") as client:
        query = m.plain(client.call("search", "Query", "Rifter", [10], exact=2))
        quick = m.plain(client.call("search", "QuickQuery", "Rifter", [10], exact=2, machoVersion=496))

        assert isinstance(query, dict)
        assert isinstance(query[10], list) and query[10]
        assert isinstance(quick, list) and quick
        assert set(query[10]).issubset(set(quick))


def test_login_configuration_and_loyalty_rpc_routes_keep_their_wire_contracts():
    """The selection UI and wallet services use the registered Host methods, not local stubs."""
    run = run_token()
    with connect(f"walk-rpc-coverage-{run}") as client:
        creation = next(
            step
            for step in WALK["steps"]
            if step["call"][1] == "CreateCharacterWithDoll" and "ok" in step
        )
        service, method, described = creation["call"]
        character_id = m.plain(client.call(service, method, *[revive(value, run) for value in described]))
        assert isinstance(character_id, int)
        assert client.call("charUnboundMgr", "SelectCharacterID", charID=character_id, secondChoiceID=None, skipTutorial=False) is None

        assert isinstance(m.plain(client.call("kiringMgr", "IsMonochromeStyleEnabled", machoVersion=496)), bool)
        assert isinstance(m.plain(client.call("machoNet", "GetGlobalConfig", machoVersion=496)), dict)
        assert isinstance(m.plain(client.call("multiLoginBlocker", "Login", f"walk-{run}", machoVersion=496)), bool)

        assert client.call("LPStoreMgr", "GetAvailableOffersFromCorp", 1000125, machoVersion=496) is not None
        assert client.call("LPSvc", "GetAllMyCharacterWalletLPBalances", machoVersion=496) is not None
        assert client.call("LPSvc", "GetAllMyCorporationWalletLPBalances", machoVersion=496) is not None
        with pytest.raises(p.MachoError):
            client.call("LPStoreMgr", "TakeOfferForCharacter", 1000125, 0, 1, machoVersion=496)
        with pytest.raises(p.MachoError):
            client.call("LPStoreMgr", "TakeOfferForCorporation", 1000125, 0, 1, machoVersion=496)
        for method, args in (
            ("ExchangeConcordLP", (1000125, 10)),
            ("TransferLPFromMyCorpWalletToOtherCorp", (1000125, 1000125, 10)),
            ("TransferLPFromMyWalletToOtherCorp", (1000125, 1000125, 10)),
        ):
            with pytest.raises(p.MachoError):
                client.call("LPSvc", method, *args, machoVersion=496)


def test_the_loyalty_point_wallet_answers_and_the_concord_exchange_is_refused():
    """LPSvc (the loyalty point wallet): a new character's balance rowset is answered (no source credits points yet), and
    ExchangeConcordLP is refused with the reason rather than answered as done (AG-1, DEVIATIONS.md)."""
    run = run_token()
    with connect(f"walk-lp-{run}") as client:
        creation = next(
            step
            for step in WALK["steps"]
            if step["call"][1] == "CreateCharacterWithDoll" and "ok" in step
        )
        service, method, described = creation["call"]
        character_id = m.plain(client.call(service, method, *[revive(value, run) for value in described]))
        assert client.call("charUnboundMgr", "SelectCharacterID", charID=character_id, secondChoiceID=None, skipTutorial=False) is None

        assert client.call("LPSvc", "GetAllMyCharacterWalletLPBalances") is not None
        with pytest.raises(p.MachoError):
            client.call("LPSvc", "ExchangeConcordLP", 1000125, 10)

GROUP_CHARACTER = 1  # appLogConst.groupCharacter

def test_the_population_cap_answers_through_its_moniker():
    """The selection screen's populationCap moniker: bind with (charID, groupCharacter) and a nested GetCharacterLoadSlot,
    then call the bound object. A new character's system has room, so one entry; a move to a system that was not offered is
    refused, and so is a move with a wrong slot key."""
    run = run_token()
    with connect(f"walk-cap-{run}") as client:
        creation = next(
            step
            for step in WALK["steps"]
            if step["call"][1] == "CreateCharacterWithDoll" and "ok" in step
        )
        service, method, described = creation["call"]
        character_id = m.plain(client.call(service, method, *[revive(value, run) for value in described]))
        assert isinstance(character_id, int)

        bound, answer = client.call(
            "populationCap",
            "MachoBindObject",
            (character_id, GROUP_CHARACTER),
            ("GetCharacterLoadSlot", (character_id,), None),
        )
        slots = m.plain(answer)
        assert len(slots) == 1, slots
        ((system_id, slot_key),) = slots.items()
        assert isinstance(system_id, int) and len(slot_key) == 36, slots
        assert m.plain(client.call_bound(bound, "GetCharacterLoadSlot", character_id)) == slots
        assert m.plain(client.call_bound(bound, "MoveCharacterToNewSystem", character_id, system_id, slot_key)) is None

        for system, key in ((system_id + 1, slot_key), (system_id, "not-a-slot")):
            with pytest.raises(p.MachoError):
                client.call_bound(bound, "MoveCharacterToNewSystem", character_id, system, key)

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

def _blank_ship_value(described):
    copy = json.loads(json.dumps(described))
    for entry in copy["$tuple"][4]["$tuple"][1]["$tuple"][1]["$dict"]:
        if entry[0] == "shipid":
            entry[1] = "$blanked"
    return copy

def _compare_session_change(recorded, ours) -> list[str]:
    if ours is None:
        return ["no SessionChangeNotification after SelectCharacterID"]

    recorded, ours = _blank_ship_value(recorded), _blank_ship_value(ours)
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
    run = run_token()
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

# Keep EveJS captures raw; compare fresh-character skill replies using the V24.01 client rules in DEVIATIONS.md.
def _skill_entry_values(described):
    if not isinstance(described, dict):
        return None
    objectex = described.get("$objectex")
    structure = objectex.get("$tuple") if isinstance(objectex, dict) else None
    if not isinstance(structure, list) or len(structure) < 2:
        return None
    token, values = structure[0], structure[1]
    if not isinstance(token, dict) or not token.get("$token", "").endswith(".CharacterSkillEntry"):
        return None
    if not isinstance(values, dict) or not isinstance(values.get("$tuple"), list) or len(values["$tuple"]) < 4:
        return None
    return values["$tuple"]

def _client_skill_oracle(described, recorded_total=None, client_total=None):
    if isinstance(described, list):
        return [_client_skill_oracle(value, recorded_total, client_total) for value in described]
    if not isinstance(described, dict):
        return described
    result = {key: _client_skill_oracle(value, recorded_total, client_total) for key, value in described.items()}
    values = _skill_entry_values(result)
    if values is not None:
        level, rank = min(int(values[1]), 5), int(values[3])
        values[2] = 0 if level <= 0 else math.ceil(250 * rank * 2 ** (2.5 * (level - 1)))
    if recorded_total is not None and result.get("skillPoints") == recorded_total:
        result["skillPoints"] = client_total
    pairs = result.get("$dict")
    if recorded_total is not None and isinstance(pairs, list):
        for pair in pairs:
            if isinstance(pair, list) and len(pair) == 2 and pair[0] == "skillPoints" and pair[1] == recorded_total:
                pair[1] = client_total
    return result

def _character_skill_points(described):
    values = _skill_entry_values(described)
    if values is not None:
        return int(values[2])
    if isinstance(described, list):
        return sum(_character_skill_points(value) for value in described)
    if isinstance(described, dict):
        return sum(_character_skill_points(value) for value in described.values())
    return 0

def _client_skill_totals(race):
    recorded_total = next(step["ok"] for step in race["calls"] if step["call"][1] == "GetSkillPoints")
    all_skills = next(step["ok"] for step in race["calls"] if step["call"][1] == "GetAllSkills")
    if isinstance(all_skills, dict) and all_skills.get("$same") == "GetSkills":
        all_skills = next(step["ok"] for step in race["calls"] if step["call"][1] == "GetSkills")
    client_total = _character_skill_points(_client_skill_oracle(all_skills))
    return int(recorded_total), client_total

@pytest.mark.reference_sde
@pytest.mark.parametrize("race", SKILLS["races"], ids=lambda r: f"race-{r['raceID']}")
def test_a_new_character_has_the_skills_the_reference_gives(race):
    run = run_token()
    ids: dict[int, int] = {}
    if not REFERENCE:
        recorded_total, client_total = _client_skill_totals(race)
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
        notification_names = [event["name"] for event in notifications_of(client)]
        assert notification_names[0] == "OnSubscriptionTimeUpdateServer"
        assert "OnCharacterSelected" not in notification_names
        seen = seen[1:]
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
            if not REFERENCE:
                expected = _client_skill_oracle(expected, recorded_total, client_total)
                if method == "GetSkillPoints" and expected == recorded_total:
                    expected = client_total
                if method == "MachoBindObject":
                    bound_args = described[1] if isinstance(described, list) and len(described) > 1 else None
                    bound_call = bound_args.get("$tuple") if isinstance(bound_args, dict) else None
                    if bound_call and bound_call[0] == "GetSkillPoints" and expected["$tuple"][1] == recorded_total:
                        expected["$tuple"][1] = client_total
                if method == "GetAttributes":
                    set_attribute(expected["$dict"], 164, 19)
                if method == "GetRespecInfo":
                    set_attribute(expected["args"]["$dict"], "freeRespecs", 2)
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
TIMES = {"createDateTime", "startDateTime", "corporationDateTime"}
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
        if isinstance(record, dict) and record.get("$object") == "util.KeyVal":
            for pair in record["args"]["$dict"]:
                if pair[0] == "description":
                    pair[1] = text
        if isinstance(record, dict):
            for row in record.get("list", []):
                if isinstance(row, dict) and isinstance(row.get("$row"), dict):
                    if "description" in row["$row"]:
                        row["$row"]["description"] = text
    return copy

@pytest.mark.parametrize("race", INFO["races"], ids=lambda r: f"race-{r['raceID']}")
def test_a_new_character_is_described_as_the_reference_describes_it(race):
    run = run_token()
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

def lines_of(rowset: dict) -> list:
    """The lines of a described `Rowset`."""
    return next(v for k, v in rowset["args"]["$dict"] if k == "lines")

def lines_dropped(rowset: dict) -> dict:
    """The same described `Rowset` without its lines."""
    return {**rowset, "args": {"$dict": [[k, [] if k == "lines" else v] for k, v in rowset["args"]["$dict"]]}}

@pytest.mark.parametrize("race", INVENTORY["races"], ids=lambda r: f"race-{r['raceID']}")
def test_a_new_characters_inventories_are_those_of_the_reference(race):
    run = run_token()
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
            try:
                handle = client.call("invbroker", opener, argument)
            except p.MachoError as error:
                raise AssertionError(f"race {race['raceID']} {label}.{opener}({argument}): {brief(describe(error.payload))}") from error
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
                if not REFERENCE and label == "ship" and method == "GetContainerContents":
                    # DEVIATION (ship contents): the reference lists the ship itself among the contents of its own inventory; eve.rs lists what is in the ship (the
                    # starter kit of the race: its three modules). The rowset has the shape the reference's has (DEVIATIONS.md #10).
                    lines = lines_of(outcome)
                    if len(lines) != 3:
                        problems.append(f"{where}: {len(lines)} lines in place of the three fitted modules")
                    expected, outcome = lines_dropped(expected), lines_dropped(outcome)
                if not REFERENCE and label in ("hangar", "station") and method == "GetCapacity":
                    # DEVIATION (station capacity): the V24.01 client reports groupStation capacity as 9e15/0.0; EveJS uses 1e6 and contents volume.
                    expected = {**expected, "args": {"$dict": [["capacity", 9_000_000_000_000_000.0], ["used", 0.0]]}}
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

def events_form(events: list[dict]) -> list:
    """Recorded events (plain dictionaries: name, id type, arguments) in the form `differences` compares."""
    return [as_dict({"name": e["name"], "idType": e["idType"], "args": e["args"]}) for e in events]

def _current_evejs_event_payload(value):
    """Apply the current EveJS wire types to old event captures without relaxing value checks."""
    if isinstance(value, list):
        result = [_current_evejs_event_payload(item) for item in value]
        if len(result) >= 7 and result[0] == "OnModuleAttributeChange":
            attribute = number(result[3])
            if attribute not in (2, 805, 164, 165, 166, 167, 168):
                for index in (5, 6):
                    if isinstance(result[index], (int, float)) and not isinstance(result[index], bool):
                        result[index] = float(result[index])
        return result
    if isinstance(value, dict):
        return {key: _current_evejs_event_payload(item) for key, item in value.items()}
    return value

def _current_evejs_notifications(events: list[dict]) -> list[dict]:
    """The current EveJS sends character brain updates by charid and dogma changes with retail numeric wire kinds."""
    normalized = copy.deepcopy(events)
    for event in normalized:
        if event["name"] == "OnServerBrainUpdated":
            event["idType"] = "charid"
        event["args"] = _current_evejs_event_payload(event["args"])
    return normalized

def _current_evejs_character_status(info, character_id: int):
    """Current EveJS leaves the character's instance status row at its default offline values."""
    normalized = copy.deepcopy(info)
    ship_state = keyval(normalized)["shipState"]["$tuple"][0]
    for item_id, row in pairs(ship_state):
        if number(item_id) == character_id:
            row["$row"]["online"] = False
            break
    return normalized

def where_form(state: dict) -> dict:
    """Where things are (plain dictionaries of lists) in the form `differences` compares."""
    return as_dict({k: json_list(rows) for k, rows in state.items()})

def json_list(rows: list) -> list:
    return [as_dict(row) for row in rows]

def set_attribute(attributes: list, attribute: int, value) -> None:
    for pair in attributes:
        if pair[0] == attribute:
            pair[1] = value
            return
    raise AssertionError(f"attribute {attribute} is not in the recording")

def _client_ship_state_amounts(described):
    """A GetAllInfo shipState row carries the same absolute amounts as shipInfo's condition attributes."""
    fields = keyval(described)
    ship_key, ship_info = pairs(fields["shipInfo"])[0]
    ship_id = number(ship_key)
    attributes = dict(pairs(keyval(ship_info)["attributes"]))
    ship_state = fields["shipState"]["$tuple"][0]
    state_row = next(row["$row"] for key, row in pairs(ship_state) if number(key) == ship_id)
    for field, attribute in (("damage", 3), ("charge", 18), ("armorDamage", 266), ("shieldCharge", 264)):
        if attribute in attributes:
            state_row[field] = float(attributes[attribute])

def _client_character_attributes(described):
    if isinstance(described, list):
        result = [_client_character_attributes(value) for value in described]
        if len(result) == 2 and result[0] == "attributes" and isinstance(result[1], dict):
            attributes = result[1].get("$dict")
            if isinstance(attributes, list) and any(pair[0] == 164 for pair in attributes):
                set_attribute(attributes, 164, 19)
        return result
    if not isinstance(described, dict):
        return described
    result = {key: _client_character_attributes(value) for key, value in described.items()}
    attributes = result.get("attributes")
    if isinstance(attributes, dict):
        values = attributes.get("$dict")
        if isinstance(values, list) and any(pair[0] == 164 for pair in values):
            set_attribute(values, 164, 19)
    return result

def _client_charisma_events(described, character_id):
    if isinstance(described, list):
        result = [_client_charisma_events(value, character_id) for value in described]
        if (len(result) >= 6 and result[0] == "OnModuleAttributeChange" and result[1] == character_id
                and result[3] == 164):
            result[5] = 19
        return result
    if isinstance(described, dict):
        return {key: _client_charisma_events(value, character_id) for key, value in described.items()}
    return described

@pytest.mark.reference_sde
@pytest.mark.parametrize("race", DOGMA["races"], ids=lambda r: f"race-{r['raceID']}")
def test_a_new_characters_dogma_is_that_of_the_reference(race):
    run = run_token()
    ids: dict[int, int] = {}
    with connect(f"walk-dogma-{run}") as client:
        service, method, *args = [race["create"]["call"][0], race["create"]["call"][1], *[revive(a, run, ids, RECORDED_DOGMA_RUN) for a in race["create"]["call"][2]]]
        kind, made = attempt(client, service, method, *args)
        assert kind == "ok", brief(made)
        assert attempt(client, "charUnboundMgr", "SelectCharacterID", int(made), None, False) == ("ok", None)

        notifications_of(client)  # what choosing the character sent is not what is asked about here
        service, method, call_args = race["bind"]["call"]
        outcome = describe(client.call(service, method, *[revive(a, run, ids, RECORDED_DOGMA_RUN) for a in call_args]))
        sent = notifications_of(client)

    handle_shape = ("$substruct", "$substream", "$tuple")
    expected = race["bind"]["ok"]
    problems = same_way(blank(expected["$tuple"][0], *handle_shape), blank(outcome["$tuple"][0], *handle_shape), "MachoBindObject", volatile=set())
    expected_info, actual_info = expected["$tuple"][1], outcome["$tuple"][1]

    recorded_ids, given_ids = dogma_ids(expected_info), dogma_ids(actual_info)
    assert len(recorded_ids) == len(given_ids), f"ids {recorded_ids} against {given_ids}"
    expected_info = substitute(expected_info, dict(zip(recorded_ids, given_ids)))
    expected_info = _current_evejs_character_status(expected_info, given_ids[0])
    if not REFERENCE:
        skills_race = next(skill_race for skill_race in SKILLS["races"] if skill_race["raceID"] == race["raceID"])
        recorded_total, client_total = _client_skill_totals(skills_race)
        expected_info = _client_skill_oracle(expected_info, recorded_total, client_total)
        expected_info = _client_character_attributes(expected_info)
        # DEVIATION (drone limits on the ship): the reference also gives the ship the character's two drone limits, which the client

        ship_attributes = pairs(keyval(pairs(keyval(expected_info)["shipInfo"])[0][1])["attributes"])
        ship_attributes[:] = [pair for pair in ship_attributes if pair[0] not in (352, 458)]
        # DEVIATION (the character as the ship changes it): eve.rs applies every modifier that reaches the character: the trade
        # premium is 0.9 with Trade trained (the reference says 0, which its own brain contradicts), and the Gallente rookie ship's
        # mining-drone bonus is there (DEVIATIONS.md #11)
        character_attributes = pairs(keyval(keyval(expected_info)["shipModifiedCharAttribs"])["attributes"])
        set_attribute(character_attributes, 443, 0.9)
        if race["raceID"] == 8:
            set_attribute(character_attributes, 428, 120)
    # The original EveJS capture recorded ratios in shipState, while V24.01
    # consumes its rows beside the absolute dogma amounts in shipInfo:
    # baseDogmaLocation.GetDamageStateEx divides shieldCharge by shieldCapacity,
    # armorDamage by armorHP, and damage by hp. EveJS v0.12.9 now derives these
    # rows from the same attributes in shipConditionAmounts.readShipConditionAmounts.
    _client_ship_state_amounts(expected_info)
    problems += differences(
        blank_times(expected_info), blank_times(actual_info), "GetAllInfo", volatile_keys=set(), float_tolerance=DOGMA_TOLERANCE)

    # the events that come with the answer: the character's attributes, then its brain (the client's copy of the character is made from them)
    mapping = dict(zip(recorded_ids, given_ids))
    expected_events = events_form(_current_evejs_notifications(substitute(race["notifications"], mapping)))
    if not REFERENCE:
        skills_race = next(skill_race for skill_race in SKILLS["races"] if skill_race["raceID"] == race["raceID"])
        recorded_total, client_total = _client_skill_totals(skills_race)
        expected_events = _client_skill_oracle(expected_events, recorded_total, client_total)
        expected_events = _client_charisma_events(expected_events, given_ids[0])
    expected_events = blank_times(expected_events)
    if race["notificationsBeforeAnswer"] != 0:
        problems.append("the recording has events before the answer, which this test does not place")
    problems += differences(
        expected_events, blank_times(events_form(sent)), "GetAllInfo events", volatile_keys=set(), float_tolerance=DOGMA_TOLERANCE)
    assert problems == [], "\n".join(problems[:30])

# ── fitting: switching modules and moving them out of their slots ───────────

FITTING = load("fitting.json")
RECORDED_FITTING_RUN = re.search(r"(\d{6,})$", FITTING["races"][0]["create"]["call"][2][0]).group(1)

@pytest.mark.parametrize("race", FITTING["races"], ids=lambda r: f"race-{r['raceID']}")
def test_switching_and_moving_modules_is_what_the_reference_does(race):
    run = run_token()
    ids: dict[int, int] = {}
    problems: list[str] = []
    with connect(f"walk-fitting-{run}") as client:
        service, method, *args = [race["create"]["call"][0], race["create"]["call"][1], *[revive(a, run, ids, RECORDED_FITTING_RUN) for a in race["create"]["call"][2]]]
        kind, made = attempt(client, service, method, *args)
        assert kind == "ok", brief(made)
        assert attempt(client, "charUnboundMgr", "SelectCharacterID", int(made), None, False) == ("ok", None)
        mgr = client.call("invbroker", "MachoBindObject", (60015249, 15), None)[0]
        hangar = client.call_bound(mgr, "GetInventory", 10004)
        ship_id = int(client.call_bound(hangar, "List")[0].fields()["itemID"])
        before = hangar_and_ship(client, mgr, ship_id)
        given = [number(x["itemID"]) for x in before["shipInfo"]]
        recorded = [number(x["itemID"]) for x in race["before"]["shipInfo"]]
        mapping = {int(race["create"]["ok"]): int(made), **dict(zip(recorded, given))}
        problems += differences(where_form(blank_times(substitute(race["before"], mapping))), where_form(blank_times(before)), "before")
        dogma = client.call("dogmaIM", "MachoBindObject", (60015249, 15), ("GetAllInfo", (True, True, None), None))[0]
        notifications_of(client)

        handles = {"hangar": hangar, "dogma": dogma}
        for step in race["steps"]:
            method, recorded_args, kwargs = step["call"]
            target = "hangar" if method == "Add" else "dogma"
            args = [mapping.get(a, a) for a in recorded_args]
            where = f"race {race['raceID']} {step['label']}"
            try:
                outcome = describe(client.call_bound(handles[target], method, *args, **kwargs))
            except p.MachoError as error:
                problems.append(f"{where}: error {brief(describe(error.payload))}")
                continue
            sent = notifications_of(client)
            after = hangar_and_ship(client, mgr, ship_id)
            notifications_of(client)
            problems += differences(substitute(step["ok"], mapping), outcome, f"{where}: answer")
            problems += differences(
                blank_times(events_form(_current_evejs_notifications(substitute(step["notifications"], mapping)))),
                blank_times(events_form(sent)), f"{where}: events",
                float_tolerance=DOGMA_TOLERANCE)
            problems += differences(where_form(blank_times(substitute(step["after"], mapping))), where_form(blank_times(after)), f"{where}: after")
    assert problems == [], "\n".join(problems[:30])

# ── skill training: the queue, and levels that finish ───────────────────────

TRAINING = load("training.json")
RECORDED_TRAINING_RUN = re.search(r"(\d{6,})$", TRAINING["races"][0]["create"]["call"][2][0]).group(1)
TRAINING_FAST = load("training_fast.json")
RECORDED_FAST_RUN = re.search(r"(\d{6,})$", TRAINING_FAST["create"]["call"][2][0]).group(1)
# a skill in training gains a point every couple of seconds: the points it has are the recording's within what a slow test adds
TRAINING_POINTS_SLACK = 30

def queue_form(queue: list[dict]) -> list[dict]:
    """A queue in a form that does not depend on the clock: a line's start as its distance from the first line's start, and how long it takes."""
    first = None
    lines = []
    for line in queue:
        start = number(line["trainingStartTime"]) if line["trainingStartTime"] is not None else None
        end = number(line["trainingEndTime"]) if line["trainingEndTime"] is not None else None
        if first is None and start is not None:
            first = start
        lines.append({
            "position": line["queuePosition"], "skill": line["trainingTypeID"], "toLevel": line["trainingToLevel"],
            "startSP": line["trainingStartSP"], "destinationSP": line["trainingDestinationSP"],
            "startsAfter": None if start is None else start - first, "takes": None if start is None else end - start,
        })
    return lines

def view_problems(expected: dict, actual: dict, where: str) -> list[str]:
    """What the skill window shows, compared: the queue by its shape in time, the skills by level and points (a skill that is training may
    have gained a few points more or less), the totals."""
    problems = []
    if queue_form(expected["queue"]) != queue_form(actual["queue"]):
        problems.append(f"{where}: queue {queue_form(actual['queue'])} instead of {queue_form(expected['queue'])}")
    if expected["freeSkillPoints"] != actual["freeSkillPoints"]:
        problems.append(f"{where}: free skill points {actual['freeSkillPoints']} instead of {expected['freeSkillPoints']}")
    recorded = {int(k): v for k, v in expected["skills"].items()}
    if not REFERENCE and not any(line["trainingStartTime"] for line in expected["queue"]):
        # DEVIATION (a paused queue): the reference shows the first skill of a queue that does not run as in training; eve.rs shows what

        recorded = {t: {**v, "inTraining": False} for t, v in recorded.items()}
    if set(recorded) != set(actual["skills"]):
        problems.append(f"{where}: skills {sorted(actual['skills'])} instead of {sorted(recorded)}")
    training = False
    for type_id, skill in recorded.items():
        ours = actual["skills"].get(type_id)
        if ours is None:
            continue
        training = training or bool(skill["inTraining"])
        slack = TRAINING_POINTS_SLACK if skill["inTraining"] else 0
        if (ours["level"], ours["inTraining"]) != (skill["level"], skill["inTraining"]) or abs(ours["points"] - skill["points"]) > slack:
            problems.append(f"{where}: skill {type_id} is {ours} instead of {skill}")
    if abs(actual["skillPoints"] - expected["skillPoints"]) > (TRAINING_POINTS_SLACK if training else 0):
        problems.append(f"{where}: {actual['skillPoints']} skill points instead of {expected['skillPoints']}")
    return problems

def training_client(name: str, recording: dict, recorded_run: str, run: str, ids: dict) -> tuple[Client, int]:
    client = connect(name)
    service, method, *args = [recording["create"]["call"][0], recording["create"]["call"][1], *[revive(a, run, ids, recorded_run) for a in recording["create"]["call"][2]]]
    kind, made = attempt(client, service, method, *args)
    assert kind == "ok", brief(made)
    # This walk exercises skills, not first-login onboarding. EveJS otherwise
    # enters the tutorial in space; the V24 creation path also skips it when
    # AIR NPE is disabled (characterCreationContainer.CheckSkipTutorial).
    assert attempt(client, "charUnboundMgr", "SelectCharacterID", int(made), None, REFERENCE) == ("ok", None)
    return client, int(made)

def test_project_discovery_availability_is_enabled_after_character_selection():
    run = run_token()
    race = TRAINING["races"][0]
    client, _character = training_client(f"walk-project-discovery-{run}", race, RECORDED_TRAINING_RUN, run, {})
    try:
        assert m.plain(client.call("ProjectDiscovery", "is_enabled")) is True
    finally:
        client.close()


def test_global_assets_moniker_resolves_and_lists_the_active_ship():
    run = run_token()
    race = TRAINING["races"][0]
    client, character = training_client(f"walk-global-assets-{run}", race, RECORDED_TRAINING_RUN, run, {})
    try:
        node_id = m.plain(client.call("charMgr", "MachoResolveObject", (character, 10002)))
        assert node_id == 0xFFAA
        bound = client.call("charMgr", "MachoBindObject", (character, 10002), ("List", (), None))[0]
        asset_ids = {int(row.fields()["itemID"]) for row in client.call_bound(bound, "List")}
        assert ship_of(client) in asset_ids
    finally:
        client.close()


def test_personal_calendar_event_notifies_an_online_invitee_after_commit():
    run = run_token()
    creator_run, invitee_run = f"{run}a", f"{run}b"
    race = TRAINING["races"][0]
    creator, creator_id = training_client(f"walk-calendar-owner-{run}", race, RECORDED_TRAINING_RUN, creator_run, {})
    invitee, invitee_id = training_client(f"walk-calendar-invitee-{run}", race, RECORDED_TRAINING_RUN, invitee_run, {})
    try:
        notifications_of(invitee)
        event_time = int((datetime.now(timezone.utc) + timedelta(days=2)).timestamp() * 10_000_000) + 116_444_736_000_000_000
        event_id = m.plain(creator.call(
            "calendarMgr", "CreatePersonalEvent", event_time, 60, "Calendar integration", "Invite notification", 0, [invitee_id]
        ))
        assert event_id > 0
        event_name, event_args = m.plain(creator.call("calendarProxy", "GetEventDetails", event_id, creator_id))
        assert event_name == "util.KeyVal"
        assert event_args["eventText"] == "Invite notification"
        assert event_args["creatorID"] == creator_id
        received = notifications_of(invitee)
        created = [notice for notice in received if notice["name"] == "OnNewCalendarEvent"]
        assert len(created) == 1, received
        event_payload = created[0]["args"]["$tuple"][1]["$tuple"][1]["$tuple"]
        assert event_payload[0] == {"$long": event_id}
        assert event_payload[4] == "Calendar integration"
    finally:
        creator.close()
        invitee.close()


def id_mapping(recorded_character: int, character: int, skills) -> dict[int, int]:
    """The ids of a recording that belong to the run: the character, and the item id of each of its skills (the character's id and the type)."""
    return {recorded_character: character, **{recorded_character * 100000 + int(t): character * 100000 + int(t) for t in skills}}

def speed_of(client: Client) -> float:
    """How fast this server trains: Intelligence starts at 20, so its value isolates the server multiplier."""
    attributes = dict(describe(client.call("skillHandler", "GetAttributes"))["$dict"])
    return attributes[165] / 20

@pytest.mark.reference_sde
@pytest.mark.parametrize("race", TRAINING["races"], ids=lambda r: f"race-{r['raceID']}")
def test_the_skill_queue_is_what_the_reference_does(race):
    run = run_token()
    ids: dict[int, int] = {}
    problems: list[str] = []
    client, character = training_client(f"walk-training-{run}", race, RECORDED_TRAINING_RUN, run, ids)
    with client:
        if speed_of(client) != race["trainingSpeed"]:
            pytest.skip(f"the server does not train at the recorded speed ({race['trainingSpeed']})")
        ids[int(race["create"]["ok"])] = character
        mapping = {**id_mapping(int(race["characterID"]), character, race["before"]["skills"]), int(race["shipID"]): ship_of(client)}
        problems += view_problems(race["before"], training_view(client), "before")
        notifications_of(client)

        recorded_steps: dict[str, dict] = {}
        for step in race["steps"]:
            service, method, recorded_args, kwargs = step["call"]
            args = [revive(a, run, mapping, RECORDED_TRAINING_RUN) for a in recorded_args]
            where = f"race {race['raceID']} {step['label']}"
            expected_events = step["notifications"]
            expected_after = step.get("after")
            if step["label"] == "start by item" and not REFERENCE:
                # DEVIATION (starting a skill by its item): the reference cannot find a skill's item and does nothing; eve.rs starts the skill,
                # which here is the same one-skill queue told again (DEVIATIONS.md #13)
                expected_events = recorded_steps["save the same one again"]["notifications"]
                expected_after = recorded_steps["save the same one again"]["after"]
            try:
                outcome = describe(client.call(service, method, *args, **kwargs))
                error = None
            except p.MachoError as failure:
                outcome, error = None, describe(failure.payload)
            sent = notifications_of(client)
            if "error" in step:
                problems += differences(step["error"]["payload"], error, f"{where}: error") if error is not None else [f"{where}: no error"]
            elif error is not None:
                problems.append(f"{where}: error {brief(error)}")
            else:
                problems += differences(blank_times(step["ok"]), blank_times(outcome), f"{where}: answer") if method != "GetSkillHistory" else _history_problems(step["ok"], outcome, where)
            expected_events = events_form(substitute(expected_events, mapping))
            if not REFERENCE:
                expected_events = _client_charisma_events(expected_events, character)
            problems += differences(
                blank_times(expected_events), blank_times(events_form(sent)), f"{where}: events", float_tolerance=DOGMA_TOLERANCE)
            if expected_after is not None:
                problems += view_problems(expected_after, training_view(client), f"{where}: after")
                notifications_of(client)
            recorded_steps[step["label"]] = step
    assert problems == [], "\n".join(problems[:30])

def _history_problems(expected: list, actual: list, where: str) -> list[str]:
    """What the history lists, without its dates."""
    def form(entries):
        if isinstance(entries, dict) and "$objectex" in entries:
            # The reference returns a CRowset whose rows are packed-row
            # members; eve.rs currently returns the same rows as KeyVals.
            entries = entries.get("list", [])
        rows = []
        for entry in entries:
            if isinstance(entry, dict) and "$row" in entry:
                values = entry["$row"]
            elif isinstance(entry, dict) and entry.get("$object") == "util.KeyVal":
                values = dict(entry["args"]["$dict"])
            else:
                raise AssertionError(f"unexpected skill history row in {where}: {entry!r}")
            rows.append({key: value for key, value in values.items() if key != "logDate"})
        return rows
    return [] if form(expected) == form(actual) else [f"{where}: history {form(actual)} instead of {form(expected)}"]

def _training_notification_payload(event: dict) -> list:
    return event["args"]["$tuple"][1]["$tuple"][1]["$tuple"]

def _training_tuple(value):
    if isinstance(value, dict) and isinstance(value.get("$tuple"), list):
        return value["$tuple"]
    return value

def _training_skill_update(event: dict) -> dict[int, tuple[int, int]]:
    entries = _training_notification_payload(event)[0]["$dict"]
    result = {}
    for type_id, value in entries:
        header = value["$objectex"]["$tuple"][1]["$tuple"]
        result[int(type_id)] = (int(header[1]), int(header[2]))
    return result

def _training_queue_update(event: dict) -> list[tuple[int, int]]:
    result = []
    for value in _training_notification_payload(event)[0]:
        fields = dict(value["args"]["$dict"])
        result.append((int(fields["trainingTypeID"]), int(fields["trainingToLevel"])))
    return result

def _training_attribute_update(event: dict) -> list[tuple[int, int, int, int | float]]:
    return [
        (int(row[1]), int(row[2]), int(row[3]), row[5])
        for row in (_training_tuple(value) for value in _training_notification_payload(event)[0])
    ]

def _training_rank(type_id: int) -> int:
    path = os.path.join(os.environ["EVE_SDE_DIR"], "typeDogma.jsonl")
    with open(path, encoding="utf-8") as source:
        for line in source:
            type_data = json.loads(line)
            if type_data.get("_key") == type_id:
                return int(next(attribute["value"] for attribute in type_data["dogmaAttributes"] if attribute["attributeID"] == 275))
    raise AssertionError(f"skill type {type_id} is absent from the pinned SDE")

def _fast_training_oracle(events: list[dict], character: int, speed: float) -> list[dict]:
    """EVE.js's test speed scales dogma learning attributes; the client receives raw attributes from eve.rs."""
    normalized = json.loads(json.dumps(events))
    for event in normalized:
        if event["name"] == "OnModuleAttributeChanges":
            for encoded_row in _training_notification_payload(event)[0]:
                row = _training_tuple(encoded_row)
                if row[0] == "OnModuleAttributeChange" and row[1] == character and row[2] == character and row[3] in (164, 165, 166, 167, 168):
                    value = row[5] / speed
                    row[5] = int(value) if float(value).is_integer() else value
        elif event["name"] == "OnServerBrainUpdated":
            _training_notification_payload(event)[0] = "$brain-version"
    return normalized

def _blank_brain_versions(events: list[dict]) -> list[dict]:
    normalized = json.loads(json.dumps(events))
    for event in normalized:
        if event["name"] == "OnServerBrainUpdated":
            _training_notification_payload(event)[0] = "$brain-version"
    return normalized

def test_a_queue_that_finishes_while_the_client_watches_is_told_level_by_level():
    run = run_token()
    ids: dict[int, int] = {}
    problems: list[str] = []
    client, character = training_client(f"walk-swift-{run}", TRAINING_FAST, RECORDED_FAST_RUN, run, ids)
    with client:
        if speed_of(client) != TRAINING_FAST["trainingSpeed"]:
            pytest.skip(f"the server does not train at the recorded speed ({TRAINING_FAST['trainingSpeed']}): start it with EVE(JS)_SKILL_TRAINING_SPEED")
        mapping = {**id_mapping(int(TRAINING_FAST["characterID"]), character, TRAINING_FAST["before"]["skills"]), int(TRAINING_FAST["shipID"]): ship_of(client)}
        problems += view_problems(TRAINING_FAST["before"], training_view(client), "before")
        notifications_of(client)
        time.sleep(1)
        problems += view_problems(TRAINING_FAST["before"], training_view(client), "before after character selection settled")
        notifications_of(client)

        for step in TRAINING_FAST["steps"][:-1]:
            service, method, recorded_args, kwargs = step["call"]
            args = [revive(a, run, mapping, RECORDED_FAST_RUN) for a in recorded_args]
            where = step["label"]
            problems += differences(step["ok"], describe(client.call(service, method, *args, **kwargs)), f"{where}: answer")
            sent = wait_for_events(client, lambda seen: any(e["name"] == "OnSkillQueuePausedServer" for e in seen))
            if not REFERENCE and step["label"] == "two levels in a queue":
                # eve.rs publishes each completed level before the next queue row starts. The reference settles both overdue levels
                # together; V24 skillsvc.py and skillQueueSvc.py consume both forms and expose the same level sequence locally.
                expected_names = [
                    "OnNewSkillQueueSaved", "OnServerSkillsChanged", "OnModuleAttributeChanges", "OnModuleAttributeChanges",
                    "OnServerBrainUpdated", "OnNewSkillQueueSaved", "OnServerSkillsChanged", "OnModuleAttributeChanges",
                    "OnModuleAttributeChanges", "OnServerBrainUpdated", "OnNewSkillQueueSaved", "OnSkillQueuePausedServer",
                ]
                assert [event["name"] for event in sent] == expected_names
                assert [event["idType"] for event in sent] == [
                    "charid", "charid", "clientID", "clientID", "charid", "charid",
                    "charid", "clientID", "clientID", "charid", "charid", "clientID",
                ]

                skill_rank = _training_rank(3301)
                level_two_points = math.ceil(250 * skill_rank * 2 ** (2.5 * (2 - 1)))
                assert [_training_skill_update(event) for event in sent if event["name"] == "OnServerSkillsChanged"] == [
                    {3301: (2, level_two_points)}, {3301: (3, 8000)},
                ]
                queues = [_training_queue_update(event) for event in sent if event["name"] == "OnNewSkillQueueSaved"]
                assert queues == [[(3301, 2), (3301, 3)], [(3301, 3)], []]

                expected = _fast_training_oracle(substitute(step["notifications"], mapping), character, TRAINING_FAST["trainingSpeed"])
                expected = _client_charisma_events(expected, character)
                final_level_expected = [expected[0], *expected[6:]]
                actual = [sent[index] for index in (0, 6, 7, 8, 9, 10, 11)]
                problems += differences(
                    blank_times(events_form(final_level_expected)), blank_times(events_form(_blank_brain_versions(actual))),
                    f"{where}: final-level wire events", float_tolerance=DOGMA_TOLERANCE)

                skill_event = substitute([next(event for event in step["notifications"] if event["name"] == "OnServerSkillsChanged")], mapping)[0]
                header = _training_notification_payload(skill_event)[0]["$dict"][0][1]["$objectex"]["$tuple"][1]["$tuple"]
                header[1], header[2] = 2, level_two_points
                problems += differences(
                    blank_times(events_form([skill_event])), blank_times(events_form([sent[1]])),
                    f"{where}: first-level skill update", float_tolerance=DOGMA_TOLERANCE)

                expected_attributes = [event for event in step["notifications"] if event["name"] == "OnModuleAttributeChanges"]
                expected_attributes = _fast_training_oracle(substitute(expected_attributes, mapping), character, TRAINING_FAST["trainingSpeed"])
                expected_attributes = _client_charisma_events(expected_attributes, character)
                expected_groups = [_training_attribute_update(event) for event in expected_attributes]
                actual_groups = [_training_attribute_update(event) for event in sent[2:4] + sent[7:9]]
                assert actual_groups == expected_groups
                assert _training_queue_update(sent[5]) == [(3301, 3)]
                brains = [event for event in sent if event["name"] == "OnServerBrainUpdated"]
                versions = [_training_tuple(_training_notification_payload(event)[0])[0] for event in brains]
                assert len(versions) == 2 and versions[0] > 0 and versions[1] == versions[0] + 1
            else:
                expected_events = substitute(step["notifications"], mapping)
                actual_events = sent
                if not REFERENCE:
                    expected_events = _fast_training_oracle(expected_events, character, TRAINING_FAST["trainingSpeed"])
                    expected_events = _client_charisma_events(expected_events, character)
                    actual_events = _blank_brain_versions(actual_events)
                problems += differences(
                    blank_times(events_form(expected_events)), blank_times(events_form(actual_events)), f"{where}: events", float_tolerance=DOGMA_TOLERANCE)
            problems += view_problems(step["after"], training_view(client), f"{where}: after")
            # V24.01 applies these skill and queue notifications locally after each level; it does not fetch a full snapshot here.
            notifications_of(client)

        # a level that finishes while nobody is logged in is found when the character comes back
        away = TRAINING_FAST["steps"][-1]
        client.call("skillHandler", "SaveNewQueue", m.Dict([(0, (3319, 2))]))
        notifications_of(client)
    time.sleep(4)
    with connect(f"walk-swift-{run}") as back:
        notifications_of(back)
        assert attempt(back, "charUnboundMgr", "SelectCharacterID", character, None, False) == ("ok", None)
        received = notifications_of(back)
        assert received[0]["name"] == "OnSubscriptionTimeUpdateServer"
        assert "OnCharacterSelected" not in [event["name"] for event in received]
        received = received[1:]
        expected_events = substitute(away["notifications"], mapping)
        actual_events = received
        if not REFERENCE:
            expected_events = _fast_training_oracle(expected_events, character, TRAINING_FAST["trainingSpeed"])
            expected_events = _client_charisma_events(expected_events, character)
            actual_events = _blank_brain_versions(actual_events)
        problems += differences(
            blank_times(events_form(expected_events)), blank_times(events_form(actual_events)), "finished while away: events", float_tolerance=DOGMA_TOLERANCE)
        problems += view_problems(away["after"], training_view(back), "finished while away: after")
    assert problems == [], "\n".join(problems[:30])


def test_authentication_age_gate_uses_verified_account_state():
    from tests import dbseed

    run = run_token()
    user_name = f"underage-{run}"
    # Password-login headless sessions have no SSO JWT; exercise the RPC with its accepted Unicode token type.
    login_jwt = m.WString("test.header.signature")
    with connect(user_name) as client:
        dbseed.set_underage(user_name, False)
        kind, outcome = attempt(client, "authentication", "AmUnderage", login_jwt)
        assert kind == "ok", brief(outcome)
        assert outcome is False
        dbseed.set_underage(user_name, True)
        kind, outcome = attempt(client, "authentication", "AmUnderage", login_jwt)
        assert kind == "ok", brief(outcome)
        assert outcome is True


def test_active_survey_id_uses_reference_default():
    run = run_token()
    with connect(f"survey-default-{run}") as client:
        kind, outcome = attempt(client, "survey", "GetActiveSurveyID")
        assert kind == "ok", brief(outcome)
        assert outcome is None, f"expected no active survey, got {outcome!r}"


def test_localization_text_changes_keep_the_reference_empty_shape():
    run = run_token()
    expected = {"$tuple": [{"$dict": []}, {"$dict": []}, {"$dict": []}]}
    with connect(f"localization-{run}") as client:
        kind, outcome = attempt(client, "localizationServer", "GetAllTextChanges", m.Dict([]))
        assert kind == "ok", brief(outcome)
        assert outcome == expected, f"expected empty localization changes, got {outcome!r}"

"""Regression checks for replaying the mitm JSONL records produced by tools/mitm/mitm."""

import os
from pathlib import Path

import pytest

from evecli import packet as p
from evecli.trace_replay import (
    DEFAULT_ALLOWLIST,
    DEFAULT_FIXTURE,
    _BoundHandle,
    _ReplayBindings,
    _notification_body,
    _notification_values,
    ReplayIssue,
    ReplayReport,
    analyze_reply,
    format_report,
    load_allowlist,
    load_capture,
    request_steps,
    replay_live_capture,
    replay_records,
)


def test_request_steps_pair_client_calls_and_keep_one_way_notifications():
    records = [
        {"conn": 4, "dir": "c2s", "stage": "macho", "type": "CallReq", "call_id": 7,
         "service": "config", "method": "GetOldStationData", "args": {"$tuple": []}},
        {"conn": 4, "dir": "s2c", "stage": "macho", "type": "CallRsp", "call_id": 7,
         "result": {"$tuple": []}},
        {"conn": 4, "dir": "c2s", "stage": "macho", "type": "Notification", "call_id": None,
         "service": None, "method": "ClientHasReleasedTheseObjects", "args": {"$tuple": []}},
        {"conn": 5, "dir": "c2s", "stage": "macho", "type": "CallReq", "call_id": 8,
         "service": "config", "method": "OtherConnection", "args": {"$tuple": []}},
    ]

    steps = request_steps(records, conn=4)

    assert [(step.record["type"], step.record["method"]) for step in steps] == [
        ("CallReq", "GetOldStationData"),
        ("Notification", "ClientHasReleasedTheseObjects"),
    ]
    assert steps[0].reply["type"] == "CallRsp"
    assert steps[1].reply is None


def test_reply_analysis_detects_error_timeout_and_recorded_success_shape_change():
    request = {"type": "CallReq", "service": "config", "method": "GetOldStationData"}
    recorded_success = {"type": "CallRsp", "result": {"$tuple": [1, "station"]}}

    assert analyze_reply(request, recorded_success, {"type": "CallRsp", "result": {"$tuple": [2, "station"]}}) is None
    assert analyze_reply(request, recorded_success, {"type": "CallRsp", "result": {"$tuple": ["2", "station"]}}).kind == "shape"
    assert analyze_reply(request, recorded_success, {"type": "ErrorResponse", "error": {"code": 2}}).kind == "error"
    assert analyze_reply(request, recorded_success, None).kind == "missing"


def test_report_renders_every_error_missing_reply_and_shape_difference():
    report = ReplayReport(requests=3, notifications=1, issues=[
        ReplayIssue("error", {"seq": 10, "call_id": 4, "service": "alert", "method": "First"}, {"code": 2}),
        ReplayIssue("error", {"seq": 12, "call_id": 5, "service": "eventLog", "method": "Second"}, {"code": 3}),
        ReplayIssue("missing", {"seq": 14, "call_id": 6, "service": "map", "method": "Third"}, "timeout"),
        ReplayIssue("shape", {"seq": 16, "call_id": 7, "service": "config", "method": "Fourth"}, ["root type"]),
    ])

    rendered = format_report(report)

    assert rendered.count("ErrorResponse [unexpected]") == 2
    assert "no reply within timeout seq=14 call=6 map.Third" in rendered
    assert "reply shape differs seq=16 call=7 config.Fourth" in rendered


def test_service_calls_do_not_use_an_unrelated_bound_object_and_dict_key_order_is_ignored():
    handle = object()
    unrelated = object()
    skill_handle = object()
    bindings = _ReplayBindings()
    bindings.handles.append(_BoundHandle("inventory", None, "GetInventory", handle, None))
    bindings.handles.append(_BoundHandle("warRegistry", None, None, unrelated, None))
    bindings.handles.append(_BoundHandle("skillHandler", None, None, skill_handle, None))

    assert bindings.for_request({"service": "inventory", "method": "GetInventory"}) is None
    assert bindings.for_request({"service": None, "method": "GetInventory"}) is handle
    assert bindings.for_request({"service": None, "method": "GetSkills"}) is skill_handle
    request = {"type": "CallReq", "service": "config", "method": "GetOldStationData"}
    recorded = {"type": "CallRsp", "result": {"$dict": [["first", 1], ["second", 2]]}}
    actual = {"type": "CallRsp", "result": {"$dict": [["second", 2], ["first", 1]]}}
    assert analyze_reply(request, recorded, actual) is None


def test_unmapped_bound_call_is_reported_without_sending_a_fake_service_call():
    class Client:
        called = False
        session = None

        def call(self, *args, **kwargs):
            self.called = True
            raise AssertionError("an unmapped bound call must not be rewritten as a service call")

    client = Client()
    report = replay_records(client, [{
        "conn": 4, "dir": "c2s", "type": "CallReq", "call_id": 7, "service": None,
        "method": "List", "args": {"$tuple": []}, "bound_object": {"$tuple": ["N=65450:999", 1]},
    }], conn=4)

    assert report.requests == 1
    assert [(issue.kind, issue.details) for issue in report.issues] == [
        ("send", "service-less CallReq has no captured or inferred bound-object handle")]
    assert not client.called


def test_committed_owner_trace_is_the_first_400_records_of_connection_four():
    records = load_capture(DEFAULT_FIXTURE, conn=4, limit=400)
    assert len(records) == 400
    assert all(record["conn"] == 4 for record in records)
    assert Path(DEFAULT_FIXTURE).stat().st_size < 300_000
    notifications = [record for record in records if record.get("dir") == "c2s" and record.get("type") == "Notification"]
    assert len(notifications) == 1
    assert _notification_body(notifications[0])["method"] == "ClientHasReleasedTheseObjects"


def test_owner_trace_fixture_redacts_password_proofs_without_changing_their_shape():
    def named_values(value, names):
        found = []
        if isinstance(value, dict):
            for key, item in value.get("$dict", []):
                if isinstance(key, dict):
                    name = key.get("$unicode", key.get("$token"))
                else:
                    name = key
                if name in names:
                    found.append((name, item))
                found.extend(named_values(item, names))
            for key, item in value.items():
                if key != "$dict":
                    found.extend(named_values(item, names))
        elif isinstance(value, list):
            for item in value:
                found.extend(named_values(item, names))
        return found

    records = load_capture(DEFAULT_FIXTURE, conn=4, limit=400)
    sensitive = dict((name, value) for record in records for name, value in named_values(
        record.get("value"), {"user_password_hash"}))

    assert set(sensitive) == {"user_password_hash"}
    assert all(isinstance(value, str) and value.startswith("$bytes:") for value in sensitive.values())
    assert all(set(value[7:]) <= {"0"} for value in sensitive.values())


def test_legacy_notification_replay_uses_its_recorded_broadcast_address():
    records = load_capture(DEFAULT_FIXTURE, conn=4, limit=400)
    (notification,) = [record for record in records
                       if record.get("dir") == "c2s" and record.get("type") == "Notification"]
    notification["method"] = 62001

    address, _, _ = _notification_values(notification, {}, _ReplayBindings())

    assert address == p.broadcast_address(62001)


def test_known_error_allowlist_is_exact_and_tracks_the_live_fix_jobs():
    allowlist = load_allowlist(DEFAULT_ALLOWLIST)
    signatures = {(entry["service"], entry["method"], entry["reason"]) for entry in allowlist}
    assert signatures == {
        ("alert", "SendClientStackTraceAlert", "ALERT_ARGUMENTS_INVALID"),
        ("eventLog", "LogClientStats", "CLIENT_STATS_SESSION_INVALID"),
        ("corpFittingMgr", "GetFittings", "SessionRpcBudgetExceeded"),
        ("corpFittingMgr", "GetCommunityFittings", "SessionRpcBudgetExceeded"),
    }
    assert all(entry["owner_job"] == "60-fix-live-*" for entry in allowlist)


LIVE = os.environ.get("EVE_LIVE_HOST", "")


@pytest.mark.skipif(not LIVE, reason="set EVE_LIVE_HOST=host:port to replay against a running Host and simulation")
def test_owner_trace_replays_against_the_live_host_and_simulation():
    report = replay_live_capture(DEFAULT_FIXTURE, conn=4, host=LIVE, allowlist_path=DEFAULT_ALLOWLIST)
    assert report.requests == 184
    assert report.notifications == 1
    assert report.unexpected_errors == []
    assert report.missing_replies == []
    assert not [issue for issue in report.issues if issue.kind == "send"], format_report(report)
    configured = {(entry["service"], entry["method"], entry["reason"])
                  for entry in load_allowlist(DEFAULT_ALLOWLIST)}
    observed = {(issue.allowed_by["service"], issue.allowed_by["method"], issue.allowed_by["reason"])
                for issue in report.errors if issue.allowed_by}
    assert observed == configured, f"stale or unobserved allow-list entries: {configured - observed}"

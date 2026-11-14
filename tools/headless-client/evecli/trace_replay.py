"""Replay decoded MachoNet requests from tools/mitm JSONL captures against a live server."""

from __future__ import annotations

import ast
import gzip
import json
import os
import re
import time
import uuid
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Iterable

from . import marshal as m
from . import packet as p
from .client import Client
from .describe import describe, differences, shape

ROOT = Path(__file__).resolve().parents[3]
DEFAULT_FIXTURE = ROOT / "tools" / "headless-client" / "fixtures" / "live-trace-conn4-first400.jsonl.gz"
DEFAULT_ALLOWLIST = ROOT / "tools" / "headless-client" / "fixtures" / "live-trace-replay-allowlist.json"
WALK_PATH = ROOT / "oracle" / "fixtures" / "scenarios" / "login_to_character.json"
LEGACY_BOUND_SERVICES = {
    "CheckAndSendNotifications": "skillHandler",
    "GetAggressionSettings": "corpRegistry",
    "GetAllSkills": "skillHandler",
    "GetAvailableTurretSlots": "invbroker",
    "GetEveOwners": "corpRegistry",
    "GetInventoryFromId": "invbroker",
    "GetMyApplications": "corpRegistry",
    "GetSelfInvItem": "invbroker",
    "GetSkillHistory": "skillHandler",
    "GetSkillQueueAndFreePoints": "skillHandler",
    "GetSkills": "skillHandler",
    "GetWars": "warRegistry",
    "List": "invbroker",
}


@dataclass(frozen=True)
class RequestStep:
    record: dict[str, Any]
    reply: dict[str, Any] | None


@dataclass
class ReplayIssue:
    kind: str
    record: dict[str, Any]
    details: Any = None
    allowed_by: dict[str, Any] | None = None

    @property
    def service(self) -> str | None:
        value = self.record.get("service")
        return str(value) if value is not None else None

    @property
    def method(self) -> str | None:
        value = self.record.get("method")
        return str(value) if value is not None else None


@dataclass
class ReplayReport:
    requests: int = 0
    notifications: int = 0
    issues: list[ReplayIssue] = field(default_factory=list)

    @property
    def errors(self) -> list[ReplayIssue]:
        return [issue for issue in self.issues if issue.kind == "error"]

    @property
    def unexpected_errors(self) -> list[ReplayIssue]:
        return [issue for issue in self.errors if issue.allowed_by is None]

    @property
    def missing_replies(self) -> list[ReplayIssue]:
        return [issue for issue in self.issues if issue.kind == "missing"]

    @property
    def shape_differences(self) -> list[ReplayIssue]:
        return [issue for issue in self.issues if issue.kind == "shape"]

    @property
    def failed(self) -> bool:
        return any(issue.kind != "error" for issue in self.issues) or bool(self.unexpected_errors)


def open_capture(path: str | os.PathLike[str]):
    path = Path(path)
    return gzip.open(path, "rt", encoding="utf-8") if path.suffix == ".gz" else path.open(encoding="utf-8")


def load_capture(path: str | os.PathLike[str], *, conn: int | None = None, limit: int | None = None) -> list[dict[str, Any]]:
    records = []
    with open_capture(path) as source:
        for line in source:
            if not line.strip():
                continue
            record = json.loads(line)
            if conn is not None and record.get("conn") != conn:
                continue
            records.append(record)
            if limit is not None and len(records) >= limit:
                break
    return records


def request_steps(records: Iterable[dict[str, Any]], *, conn: int | None = None) -> list[RequestStep]:
    """Pair c2s CallReq responses by call id and retain one-way c2s Notification packets."""
    rows = list(records)
    replies: dict[tuple[Any, Any], dict[str, Any]] = {}
    for record in rows:
        if record.get("dir") == "s2c" and record.get("type") in ("CallRsp", "ErrorResponse"):
            replies[(record.get("conn"), record.get("call_id"))] = record
    steps = []
    for record in rows:
        if conn is not None and record.get("conn") != conn:
            continue
        if record.get("dir") != "c2s" or record.get("type") not in ("CallReq", "Notification"):
            continue
        reply = replies.get((record.get("conn"), record.get("call_id"))) if record.get("type") == "CallReq" else None
        steps.append(RequestStep(record, reply))
    return steps


def analyze_reply(request: dict[str, Any], recorded_reply: dict[str, Any] | None,
                  actual_reply: dict[str, Any] | None) -> ReplayIssue | None:
    """Report missing replies, every ErrorResponse, and shape changes for recorded successes."""
    if actual_reply is None:
        return ReplayIssue("missing", request, "no reply before the per-call timeout")
    if actual_reply.get("type") == "ErrorResponse":
        return ReplayIssue("error", request, actual_reply.get("error"))
    if actual_reply.get("type") != "CallRsp":
        return ReplayIssue("missing", request, f"unexpected reply type {actual_reply.get('type')!r}")
    if recorded_reply and recorded_reply.get("type") == "CallRsp":
        expected_shape = shape(recorded_reply.get("result"))
        actual_shape = shape(actual_reply.get("result"))
        shape_differences = differences(expected_shape, actual_shape)
        if shape_differences:
            return ReplayIssue("shape", request, shape_differences[:12])
    return None


def _description_error_reason(error: Any) -> str | None:
    """Return the named UserError reason from a mitm-described ErrorResponse, when present."""
    if not isinstance(error, dict):
        return None
    payload = error.get("payload")
    if not isinstance(payload, dict) or "$objectex" not in payload:
        return None
    header = payload["$objectex"].get("$tuple")
    if not isinstance(header, list) or len(header) < 2:
        return None
    arguments = header[1]
    if not isinstance(arguments, dict) or not isinstance(arguments.get("$tuple"), list):
        return None
    values = arguments["$tuple"]
    return str(values[0]) if values and values[0] is not None else None


def load_allowlist(path: str | os.PathLike[str] = DEFAULT_ALLOWLIST) -> list[dict[str, Any]]:
    data = json.loads(Path(path).read_text(encoding="utf-8"))
    entries = data.get("known_failures") if isinstance(data, dict) else None
    if not isinstance(entries, list):
        raise ValueError(f"{path}: expected a known_failures array")
    for entry in entries:
        if not all(isinstance(entry.get(key), str) and entry[key] for key in ("service", "method", "reason", "owner_job")):
            raise ValueError(f"{path}: every known failure needs service, method, reason, and owner_job")
    return entries


def _allowed_error(request: dict[str, Any], error: Any, allowlist: list[dict[str, Any]]) -> dict[str, Any] | None:
    reason = _description_error_reason(error)
    return next((entry for entry in allowlist
                 if entry["service"] == request.get("service")
                 and entry["method"] == request.get("method")
                 and entry["reason"] == reason), None)


def _sequence(value: Any) -> list[Any] | None:
    if isinstance(value, dict) and isinstance(value.get("$tuple"), list):
        return value["$tuple"]
    if isinstance(value, list):
        return value
    return None


def _dict_pairs(value: Any) -> list[list[Any]] | None:
    return value.get("$dict") if isinstance(value, dict) and isinstance(value.get("$dict"), list) else None


def _key_text(value: Any) -> str:
    if isinstance(value, dict):
        if "$unicode" in value:
            return str(value["$unicode"])
        if "$token" in value:
            return str(value["$token"])
    return str(value)


def _revive(value: Any) -> Any:
    """Rebuild a marshal value from mitm's lossless-enough JSON description."""
    if value is None or isinstance(value, (bool, int, float, str)):
        if isinstance(value, str) and value.startswith("$bytes:"):
            return bytes.fromhex(value[7:])
        return value
    if isinstance(value, list):
        return m.List([_revive(item) for item in value])
    if not isinstance(value, dict):
        raise ValueError(f"unsupported description type: {type(value).__name__}")
    if "$long" in value:
        return m.Long(value["$long"])
    if "$bigunsigned" in value:
        return m.BigUnsigned(value["$bigunsigned"])
    if "$bigsigned" in value:
        return m.BigSigned(value["$bigsigned"])
    if "$buffer" in value:
        return m.Buffer.fromhex(value["$buffer"])
    if "$unicode" in value:
        return m.WString(value["$unicode"])
    if "$token" in value:
        return m.Token(value["$token"])
    if "$pickle" in value:
        return m.CPickled.fromhex(value["$pickle"])
    if "$tuple" in value:
        return tuple(_revive(item) for item in value["$tuple"])
    if "$dict" in value:
        return m.Dict([(_revive(key), _revive(item)) for key, item in value["$dict"]])
    if "$object" in value:
        return m.Object(value["$object"], _revive(value.get("args")))
    if "$objectex" in value:
        header = _revive(value["$objectex"])
        return m.ObjectEx(2, header, [_revive(item) for item in value.get("list", [])],
                          [(_revive(key), _revive(item)) for key, item in value.get("dict", [])])
    if "$substruct" in value:
        return m.SubStruct(_revive(value["$substruct"]))
    if "$substream" in value:
        nested = value["$substream"]
        return m.SubStream(raw=bytes.fromhex(nested["$raw"]) if isinstance(nested, dict) and "$raw" in nested else None,
                           value=None if isinstance(nested, dict) and "$raw" in nested else _revive(nested))
    if "$checksummed" in value:
        return m.Checksummed(0, _revive(value["$checksummed"]))
    if "$row" in value:
        raise ValueError("a packed-row description cannot be replayed without its row descriptor")
    if "$unknown" in value:
        raise ValueError("an unknown marshal value cannot be replayed")
    raise ValueError(f"unsupported description: {list(value)}")


def _remap_description(value: Any, identities: dict[int, int]) -> Any:
    """Substitute per-session ids learned from the captured session changes."""
    if isinstance(value, bool):
        return value
    if isinstance(value, int):
        return identities.get(value, value)
    if isinstance(value, list):
        return [_remap_description(item, identities) for item in value]
    if isinstance(value, dict):
        for tag in ("$long", "$bigunsigned", "$bigsigned"):
            if tag in value:
                old = value[tag]
                return {tag: identities.get(old, old)}
        return {key: _remap_description(item, identities) for key, item in value.items()}
    return value


def _kwargs(record: dict[str, Any], identities: dict[int, int]) -> dict[str, Any]:
    pairs = _dict_pairs(record.get("kwargs"))
    if pairs is None:
        return {}
    output = {}
    for key, value in pairs:
        name = _key_text(key)
        revived = _revive(_remap_description(value, identities))
        output[name] = revived
    return output


def _args(record: dict[str, Any], identities: dict[int, int]) -> tuple[Any, ...]:
    described = _remap_description(record.get("args", {"$tuple": []}), identities)
    revived = _revive(described)
    if isinstance(revived, tuple):
        return revived
    if isinstance(revived, list):
        return tuple(revived)
    raise ValueError(f"CallReq args were not a tuple: {type(revived).__name__}")


def _legacy_notification(record: dict[str, Any]) -> dict[str, Any] | None:
    """Recover a client release notification from older mitm lines with a decodeError $unknown."""
    value = record.get("args")
    if not isinstance(value, dict) or not isinstance(value.get("$unknown"), str):
        return None
    try:
        error = ast.literal_eval(value["$unknown"])
    except (ValueError, SyntaxError):
        return None
    described = error.get("payload") if isinstance(error, dict) else None
    outer = _sequence(described)
    wrapper = _sequence(outer[0]) if outer else None
    substream = wrapper[1].get("$substream") if wrapper and len(wrapper) > 1 and isinstance(wrapper[1], dict) else None
    body = _sequence(substream)
    if not body or len(body) < 3 or not isinstance(body[1], str):
        return None
    return {
        "method": body[1],
        "args": body[2],
        "kwargs": body[3] if len(body) > 3 else None,
        "notification_objects": body[0],
    }


def _notification_body(record: dict[str, Any]) -> dict[str, Any]:
    if isinstance(record.get("notification_objects"), list):
        return record
    return _legacy_notification(record) or record


def _session_descriptions(records: Iterable[dict[str, Any]]) -> list[tuple[str, Any]]:
    values: list[tuple[str, Any]] = []
    for record in records:
        payload = record.get("payload")
        parts = _sequence(payload)
        if record.get("type") == "SessionInitialStateNotification" and parts and len(parts) > 2:
            pairs = _dict_pairs(parts[2]) or []
            values.extend((_key_text(key), value) for key, value in pairs)
        elif record.get("type") == "SessionChangeNotification" and parts and len(parts) > 1:
            change = _sequence(parts[1])
            pairs = _dict_pairs(change[1]) if change and len(change) > 1 else None
            for key, old_new in pairs or []:
                pair = _sequence(old_new)
                if pair and len(pair) > 1:
                    values.append((_key_text(key), pair[1]))
    return values


def _session_identities(records: Iterable[dict[str, Any]], client: Client) -> dict[int, int]:
    if client.session is None:
        return {}
    current = client.session.session_init
    identities: dict[int, int] = {}
    for key, old in _session_descriptions(records):
        if key in ("userid", "charid", "shipid", "locationid", "solarsystemid", "stationid", "regionid",
                   "constellationid", "corpid", "allianceid", "fleetid", "warFactionID", "warfactionid"):
            new = current.get(key)
            if isinstance(old, dict):
                old = next((old[tag] for tag in ("$long", "$bigunsigned", "$bigsigned") if tag in old), old)
            if isinstance(new, int) and isinstance(old, int) and old > 1_000_000:
                identities[old] = new
    return identities


def _add_session_identities(records: Iterable[dict[str, Any]], client: Client, identities: dict[int, int]) -> None:
    identities.update(_session_identities(records, client))
    if client.session is None:
        return
    for record in records:
        if record.get("stage") != "handshake" or record.get("dir") != "s2c":
            continue
        for key, value in _dict_pairs(record.get("value")) or []:
            if _key_text(key) == "sessionID":
                old = next((value[tag] for tag in ("$long", "$bigunsigned") if isinstance(value, dict) and tag in value), value)
                if isinstance(old, int) and isinstance(client.session.session_id, int):
                    identities[old] = int(client.session.session_id)
    for key, value in client.session.handshake.items():
        if key in ("sessionID", "session_id"):
            old = next((value[tag] for tag in ("$long", "$bigunsigned") if isinstance(value, dict) and tag in value), value)
            if isinstance(old, int) and isinstance(client.session.session_id, int):
                identities[old] = int(client.session.session_id)


def _handle_key(value: Any) -> str | None:
    while hasattr(value, "value"):
        value = value.value
    if isinstance(value, (tuple, list)) and value:
        return str(value[0])
    if isinstance(value, str):
        return value
    if isinstance(value, (bytes, bytearray)):
        return bytes(value).decode("utf-8", "replace")
    return None


def _described_handle_key(value: Any) -> str | None:
    if isinstance(value, dict):
        for tag in ("$substruct", "$substream"):
            if tag in value:
                return _described_handle_key(value[tag])
        if "$tuple" in value:
            values = value["$tuple"]
            return _described_handle_key(values[0]) if values else None
    if isinstance(value, list) and value:
        return _described_handle_key(value[0])
    if isinstance(value, str):
        return value
    return None


def _described_handles(value: Any) -> list[tuple[str, Any]]:
    """Find `(oid, refID)` bound handles in the mitm description of a result."""
    if isinstance(value, dict):
        for tag in ("$substruct", "$substream"):
            if tag in value:
                return _described_handles(value[tag])
        if "$tuple" in value:
            values = value["$tuple"]
            if len(values) >= 2 and isinstance(values[0], str) and values[0].startswith("N="):
                return [(values[0], value)]
            return [pair for item in values for pair in _described_handles(item)]
        if "$dict" in value:
            return [pair for item in value["$dict"] for pair in _described_handles(item)]
        if "$object" in value:
            return _described_handles(value.get("args"))
        if "$objectex" in value:
            return _described_handles(value["$objectex"])
    if isinstance(value, list):
        return [pair for item in value for pair in _described_handles(item)]
    return []


def _live_handles(value: Any) -> list[tuple[str, Any]]:
    """Find live `(oid, refID)` values in a decoded marshal result."""
    while hasattr(value, "value"):
        value = value.value
    if isinstance(value, (tuple, list)):
        if len(value) >= 2 and _handle_key(value[0]) and _handle_key(value[0]).startswith("N="):
            return [(_handle_key(value[0]), value)]
        return [pair for item in value for pair in _live_handles(item)]
    if isinstance(value, m.Dict):
        return [pair for key, item in value for pair in (_live_handles(key) + _live_handles(item))]
    if isinstance(value, m.Object):
        return _live_handles(value.args)
    if isinstance(value, m.ObjectEx):
        return _live_handles(value.header) + _live_handles(value.items) + _live_handles(value.pairs)
    return []


@dataclass
class _BoundHandle:
    service: str
    object_key: str | None
    method: str | None
    handle: Any
    old_handle_key: str | None


class _ReplayBindings:
    def __init__(self):
        self.handles: list[_BoundHandle] = []
        self.old_to_new: dict[str, Any] = {}

    def service_for(self, handle: Any) -> str | None:
        key = _handle_key(handle)
        if key is None:
            return None
        return next((entry.service for entry in reversed(self.handles)
                     if _handle_key(entry.handle) == key and entry.service), None)

    def note(self, record: dict[str, Any], result: Any, recorded_reply: dict[str, Any] | None,
             *, inferred_service: str | None = None) -> None:
        method = record.get("method")
        service = record.get("service") or inferred_service
        if result is None:
            return
        args = _sequence(record.get("args")) or []
        object_key = _described_handle_key(args[0]) if args else None
        method_key = None
        if isinstance(service, str) and method == "MachoBindObject" and len(args) > 1:
            bind = _sequence(args[1])
            if bind and bind and isinstance(bind[0], str):
                method_key = bind[0]
        old_result = recorded_reply.get("result") if recorded_reply and recorded_reply.get("type") == "CallRsp" else None
        old_handles = _described_handles(old_result)
        live_handles = _live_handles(result)
        for index, (live_key, live_handle) in enumerate(live_handles):
            old_key = old_handles[index][0] if index < len(old_handles) else None
            if old_key:
                self.old_to_new[old_key] = live_handle
            self.handles.append(_BoundHandle(
                str(service) if isinstance(service, str) else "",
                object_key,
                method_key if method in ("MachoResolveObject", "MachoBindObject") else None,
                live_handle,
                old_key,
            ))

    def for_request(self, record: dict[str, Any]) -> Any | None:
        old_key = _described_handle_key(record.get("bound_object"))
        if old_key:
            return self.old_to_new.get(old_key)
        # Service-addressed calls use the service route even when an earlier bind happened to expose
        # the same method name (or a generic moniker). Only a service-less request needs inference
        # from the legacy captures that do not retain the call body's remoteObject.
        if record.get("service"):
            return None
        method = record.get("method")
        hinted_service = LEGACY_BOUND_SERVICES.get(method)
        if hinted_service:
            candidates = [entry for entry in self.handles if entry.service == hinted_service]
            exact = [entry for entry in candidates if entry.method == method]
            if exact:
                return exact[-1].handle
            if candidates:
                return candidates[-1].handle
        exact = [entry for entry in self.handles if entry.method == method]
        if exact:
            return exact[-1].handle
        broad = [entry for entry in self.handles if entry.method is None]
        if broad:
            return broad[-1].handle
        return None

    def remap_oid_description(self, described: Any) -> Any:
        if isinstance(described, str) and described in self.old_to_new:
            return _handle_key(self.old_to_new[described]) or described
        if isinstance(described, list):
            return [self.remap_oid_description(value) for value in described]
        if isinstance(described, dict):
            return {key: self.remap_oid_description(value) for key, value in described.items()}
        return described


def _notification_values(record: dict[str, Any], identities: dict[int, int], bindings: _ReplayBindings):
    body = _notification_body(record)
    method = body.get("method")
    if not isinstance(method, str):
        raise ValueError(f"cannot decode client Notification method from seq {record.get('seq')}")
    args_description = _remap_description(body.get("args", {"$tuple": []}), identities)
    args = _revive(args_description)
    if not isinstance(args, tuple):
        args = (args,)
    objects_description = bindings.remap_oid_description(body.get("notification_objects", []))
    objects = _revive(objects_description)
    kwargs_description = body.get("kwargs")
    if kwargs_description is None:
        kwargs = None
    else:
        kwargs = _revive(kwargs_description)
    named_payload = record.get("named_payload")
    if named_payload is None:
        oid_pairs = []
        if isinstance(objects_description, list):
            for item in objects_description:
                pair = _sequence(item)
                if pair and len(pair) == 2:
                    oid_pairs.append((pair[0], _revive(pair[1])))
        named_payload = m.Dict([("OID-", m.Dict(oid_pairs))]) if oid_pairs else None
    else:
        named_payload = _revive(named_payload)
    address_description = record.get("address")
    if address_description is None:
        service = record.get("service")
        if isinstance(service, str):
            address = p.any_address(service)
        elif type(record.get("method")) is int:
            # The existing mitm schema stores a broadcast destination ID in `method` when
            # the notification body could not be decoded. `_legacy_notification` recovers
            # the actual method from its `$unknown` payload description.
            address = p.broadcast_address(record["method"])
        else:
            raise ValueError("Notification destination is not present in the mitm record")
    else:
        address = _revive(address_description)
    payload = ((1, m.SubStream(value=(objects, method, args, kwargs))),)
    return address, payload, named_payload


def send_notification(client: Client, record: dict[str, Any], identities: dict[int, int], bindings: _ReplayBindings) -> None:
    if client.conn is None or client.session is None:
        raise RuntimeError("cannot replay Notification without an authenticated client")
    address, payload, named_payload = _notification_values(record, identities, bindings)
    client.conn.send(p.build_packet(
        p.NOTIFICATION,
        p.client_address(client.session.client_id, None),
        address,
        client.session.user_id,
        payload,
        named_payload,
    ))


def _actual_error(error: p.MachoError) -> dict[str, Any]:
    return {"type": "ErrorResponse", "error": {"code": describe(error.code), "payload": describe(error.payload)}}


def replay_records(client: Client, records: list[dict[str, Any]], *, conn: int | None = None,
                   allowlist: list[dict[str, Any]] | None = None,
                   identities: dict[int, int] | None = None, time_scale: float = 1.0) -> ReplayReport:
    """Replay the c2s CallReq and Notification records through a live headless-client connection."""
    allowlist = allowlist or []
    steps = request_steps(records, conn=conn)
    report = ReplayReport()
    bindings = _ReplayBindings()
    identities = dict(identities or {})
    timed_steps = [step for step in steps if isinstance(step.record.get("t"), (int, float))]
    first_timestamp = timed_steps[0].record["t"] if timed_steps else None
    replay_started = time.monotonic()
    for step in steps:
        request = step.record
        if first_timestamp is not None and isinstance(request.get("t"), (int, float)) and time_scale > 0:
            target = replay_started + (request["t"] - first_timestamp) / 1000.0 * time_scale
            delay = target - time.monotonic()
            if delay > 0:
                time.sleep(delay)
        if request.get("type") == "Notification":
            report.notifications += 1
            try:
                send_notification(client, request, identities, bindings)
            except Exception as error:
                report.issues.append(ReplayIssue("send", request, f"{type(error).__name__}: {error}"))
            continue

        report.requests += 1
        actual = None
        result = None
        try:
            _add_session_identities(records, client, identities)
            args = _args(request, identities)
            kwargs = _kwargs(request, identities)
            bound_handle = bindings.for_request(request)
            if not request.get("service") and bound_handle is None:
                report.issues.append(ReplayIssue(
                    "send", request, "service-less CallReq has no captured or inferred bound-object handle"))
                continue
            if bound_handle is not None:
                result = client.call_bound(bound_handle, request["method"], *args, **kwargs)
            else:
                result = client.call(request.get("service"), request["method"], *args, **kwargs)
            actual = {"type": "CallRsp", "result": describe(result)}
        except p.MachoError as error:
            actual = _actual_error(error)
        except TimeoutError as error:
            issue = ReplayIssue("missing", request, str(error))
            report.issues.append(issue)
        except (OSError, EOFError, ConnectionError) as error:
            report.issues.append(ReplayIssue("missing", request, f"{type(error).__name__}: {error}"))
        except Exception as error:
            report.issues.append(ReplayIssue("send", request, f"{type(error).__name__}: {error}"))
        if actual is not None:
            issue = analyze_reply(request, step.reply, actual)
            if issue is not None:
                if issue.kind == "error":
                    issue.allowed_by = _allowed_error(request, issue.details, allowlist)
                report.issues.append(issue)
            bindings.note(request, result, step.reply,
                          inferred_service=bindings.service_for(bound_handle) if bound_handle is not None else None)
        _add_session_identities(records, client, identities)
    return report


def _scenario_character_call() -> tuple[str, str, list[Any]]:
    walk = json.loads(WALK_PATH.read_text(encoding="utf-8"))
    creation = next(step for step in walk["steps"] if step["call"][1] == "CreateCharacterWithDoll" and "ok" in step)
    service, method, described = creation["call"]
    recorded_name = next(step["call"][2][0] for step in walk["steps"] if step["call"][1] == "CreateCharacterWithDoll")
    match = re.search(r"(\d{6,})$", recorded_name)
    recorded_run = match.group(1) if match else ""
    run = uuid.uuid4().hex[:10]

    def replace_run(value: Any) -> Any:
        if isinstance(value, str):
            return value.replace(recorded_run, run) if recorded_run else value
        if isinstance(value, list):
            return [replace_run(item) for item in value]
        if isinstance(value, dict) and "$tuple" in value:
            return tuple(replace_run(item) for item in value["$tuple"])
        if isinstance(value, dict) and "$dict" in value:
            return m.Dict([(replace_run(key), replace_run(item)) for key, item in value["$dict"]])
        if isinstance(value, dict) and "$long" in value:
            return m.Long(value["$long"])
        if isinstance(value, dict) and "$unicode" in value:
            return m.WString(value["$unicode"])
        if isinstance(value, dict) and "$token" in value:
            return m.Token(value["$token"])
        if isinstance(value, dict) and "$buffer" in value:
            return m.Buffer.fromhex(value["$buffer"])
        if isinstance(value, dict):
            raise ValueError(f"unsupported character creation argument: {value}")
        return value

    return service, method, [replace_run(value) for value in described]


def _trace_character_id(records: list[dict[str, Any]]) -> int | None:
    for record in records:
        if record.get("dir") == "c2s" and record.get("type") == "CallReq" and record.get("method") == "SelectCharacterID":
            args = _sequence(record.get("args"))
            if args and isinstance(args[0], int):
                return args[0]
    return None


def replay_live_capture(capture: str | os.PathLike[str], *, conn: int, host: str, timeout: float = 20.0,
                        allowlist_path: str | os.PathLike[str] = DEFAULT_ALLOWLIST,
                        user: str | None = None, password: str | None = None,
                        time_scale: float = 1.0) -> ReplayReport:
    """Login to a fresh local account, create the trace's pre-selection pilot, and replay its requests."""
    records = load_capture(capture, conn=conn)
    if not records:
        raise ValueError(f"capture has no records for conn {conn}")
    service, method, creation_args = _scenario_character_call()
    username = user or f"trace-replay-{uuid.uuid4().hex[:12]}"
    secret = password or uuid.uuid4().hex
    address, separator, port = host.rpartition(":")
    if not separator:
        address, port = host, "26000"
    selected_id = _trace_character_id(records)
    identities: dict[int, int] = {}
    has_captured_creation = any(
        record.get("dir") == "c2s" and record.get("type") == "CallReq" and record.get("method") == "CreateCharacterWithDoll"
        for record in records
    )
    if selected_id is not None and not has_captured_creation:
        # The trace starts with character selection, but the local simulation must first create
        # a real pilot. Do that on a setup connection so its one-time RPC is not charged to the
        # session whose captured calls are being replayed.
        with Client(address or "127.0.0.1", int(port), timeout=timeout) as setup_client:
            setup_client.login(username, secret)
            created_id = setup_client.call(service, method, *creation_args)
        if not isinstance(created_id, int):
            raise RuntimeError(f"CreateCharacterWithDoll returned {created_id!r}, expected a character id")
        identities[selected_id] = int(created_id)

    client = Client(address or "127.0.0.1", int(port), timeout=timeout)
    with client:
        client.login(username, secret)
        report = replay_records(client, records, conn=conn, allowlist=load_allowlist(allowlist_path),
                                identities=identities, time_scale=time_scale)
    return report


def format_report(report: ReplayReport) -> str:
    lines = [f"replayed {report.requests} CallReq and {report.notifications} Notification request(s)"]
    if not report.issues:
        lines.append("no errors, missing replies, or reply-shape differences")
        return "\n".join(lines)
    for issue in report.issues:
        record = issue.record
        call = f"{record.get('service')}.{record.get('method')}" if record.get("service") else str(record.get("method"))
        location = f"seq={record.get('seq', '?')} call={record.get('call_id')} {call}"
        if issue.kind == "error":
            status = "allowed" if issue.allowed_by else "unexpected"
            lines.append(f"ErrorResponse [{status}] {location}: {json.dumps(issue.details, ensure_ascii=False, separators=(',', ':'))}")
            if issue.allowed_by:
                lines.append(f"  tracked by {issue.allowed_by['owner_job']}: {issue.allowed_by.get('note', '')}")
        elif issue.kind == "shape":
            lines.append(f"reply shape differs {location}: {json.dumps(issue.details, ensure_ascii=False, separators=(',', ':'))}")
        elif issue.kind == "missing":
            lines.append(f"no reply within timeout {location}: {issue.details}")
        else:
            lines.append(f"request could not be replayed {location}: {issue.details}")
    lines.append(f"totals: {len(report.errors)} ErrorResponse(s), {len(report.missing_replies)} missing reply(s), "
                 f"{len(report.shape_differences)} reply-shape difference(s)")
    return "\n".join(lines)

#!/usr/bin/env python3
"""Run the service audit with cached imports and whole-client pattern supplements."""

from __future__ import annotations

import functools
import importlib.util
import pathlib
import sys
from collections import defaultdict
from typing import Any

ROOT = pathlib.Path(__file__).resolve().parents[2]
GENERATOR = ROOT / "tools/spec-extract/generate_service_audit.py"
SPEC = importlib.util.spec_from_file_location("service_audit_generator", GENERATOR)
if SPEC is None or SPEC.loader is None:
    raise RuntimeError(f"cannot load {GENERATOR}")
audit = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = audit
SPEC.loader.exec_module(audit)
audit.load_contract = functools.lru_cache(maxsize=1)(audit.load_contract)
_contract = audit.load_contract()
audit.CLIENT_METHOD = _contract.CLIENT_METHOD
audit.MONIKER_BIND = _contract.MONIKER_CONSTRUCTOR
_unfiltered_extended_calls = audit.extended_calls
_original_test_evidence = audit.test_evidence
_REMOTE_PARK_REF = audit.re.compile(r"\bGetRemotePark\s*\(\s*\)\s*\.\s*([A-Za-z_][A-Za-z0-9_]*)\b")
_CONNECTOR = audit.re.compile(
    r"\b(?:sm|session|self\.session)\.(ConnectToRemoteService|ConnectToSolarSystemService|"
    r"ConnectToSolServerService|ConnectToAnyService|ConnectToProxyServerService|"
    r"ConnectToService|RemoteSvc|ProxySvc)\s*\("
)
_BINDOBJECT_CALL = audit.re.compile(r"\bMachoBindObject\s*\(")


def family(service: str) -> str:
    name = service.lower()
    if name in {"pingservice", "authentication", "subscriptionmgr", "air_npe", "usersvc"}:
        return "login"
    if any(part in name for part in ("char", "skill", "clone")):
        return "character"
    if any(part in name for part in ("station", "dock", "undock")):
        return "station"
    if any(part in name for part in ("invbroker", "inventory", "item", "asset")):
        return "inventory"
    if any(part in name for part in ("beyonce", "michelle", "dogma", "space", "ship", "target")):
        return "space"
    if "fleet" in name:
        return "fleet"
    if any(part in name for part in ("corp", "alliance")):
        return "corporation"
    if any(part in name for part in ("agent", "mission")):
        return "agents"
    if "contract" in name:
        return "contracts"
    if any(part in name for part in ("industry", "manufactur", "research")):
        return "industry"
    if any(part in name for part in ("market", "trade", "loyalty", "insurance", "wallet")):
        return "economy"
    if any(part in name for part in ("view", "map", "ui")):
        return "ui"
    if any(part in name for part in ("structure", "sovereignty", "planet", "shipcaster")):
        return "structures"
    if any(part in name for part in ("mail", "chat", "calendar", "social")):
        return "social"
    if name == "config":
        return "config"
    return "other"


audit.family = family


def argument_shape(contract: Any, source: str, opening: int) -> list[str] | None:
    raw = contract.argument_text(source, opening)
    return contract.split_arguments(raw) if raw is not None else None


def remote_park_method_references(contract: Any, path: pathlib.Path, source: str) -> list[dict[str, Any]]:
    calls = []
    for match in _REMOTE_PARK_REF.finditer(source):
        method = match.group(1)
        if source[match.end(1):].lstrip().startswith("("):
            continue
        enclosing = list(audit.re.finditer(r"\bPerformSessionChange\s*\(", source[:match.start()]))
        arguments = None
        for outer in reversed(enclosing):
            opening = outer.end() - 1
            end = contract.end_of_call(source, opening)
            if end is None or end < match.end():
                continue
            values = contract.split_arguments(source[opening + 1:end - 1])
            callback = next((index for index, value in enumerate(values)
                             if "GetRemotePark" in value and method in value), None)
            if callback is not None:
                arguments = values[callback + 1:]
                break
        if arguments is None:
            continue
        line = contract.line_at(source, match.start())
        calls.append({
            "service": "beyonce", "method": method, "arguments": arguments,
            "file": path.as_posix(), "line": line, "kind": "bound-object-method-reference",
            "via": "GetRemotePark moniker passed to PerformSessionChange",
            "consumer": audit.rpc_context(contract, source, line),
        })
    return calls


def guarded_extended_calls(contract: Any, path: pathlib.Path, source: str) -> list[dict[str, Any]]:
    if not any(marker in source for marker in (
        "RemoteSvc", "ProxySvc", "ConnectTo", "GetRemotePark", "Recordset"
    )):
        return []
    calls = _unfiltered_extended_calls(contract, path, source)
    known = {(call["service"], call["method"], call["line"]) for call in calls}
    for call in remote_park_method_references(contract, path, source):
        key = (call["service"], call["method"], call["line"])
        if key not in known:
            calls.append(call)
    return calls


def filtered_test_evidence(repo_root: pathlib.Path, pairs: set[tuple[str, str]],
                           type_by_service: dict[str, str]) -> dict[tuple[str, str], list[str]]:
    source_matches = _original_test_evidence(repo_root, pairs, type_by_service)
    # Budget and registration-name tests do not exercise a successful method reply.
    excluded = ("budget", "registered_under_client_names")
    return {
        pair: [name for name in names if not any(marker in name.lower() for marker in excluded)]
        for pair, names in source_matches.items()
        if any(not any(marker in name.lower() for marker in excluded) for name in names)
    }


audit.extended_calls = guarded_extended_calls
audit.test_evidence = filtered_test_evidence
_original_queue_jobs = audit.queue_jobs


def typed_queue_jobs(rows: list[dict[str, Any]], queue_dir: pathlib.Path) -> list[pathlib.Path]:
    for row in rows:
        row["row_type"] = "rpc"
        row["status"] = row["eve_rs_status"]
        row["service_family"] = family(row["service"])
    return _original_queue_jobs(rows, queue_dir)


audit.queue_jobs = typed_queue_jobs


def add_binding(rows: list[dict[str, str]], contract: Any, source: str, path: pathlib.Path,
                service: str, kind: str, method: str, arguments: list[str] | None, line: int,
                consumer: str, row_type: str = "service-bind") -> None:
    rows.append({
        "row_type": row_type, "service": service, "bound_object_kind": kind, "method": method,
        "argument_shape": audit.shape(arguments), "client_file_line": f"{path.as_posix()}:{line}",
        "reply_consumer": consumer,
        "core_play_priority": "P0" if row_type == "bound-object-dispatch" else audit.priority(path.as_posix(), service, method),
        "service_family": family(service), "evidence": "client-side remote service or moniker binding path",
    })


def optimized_client_inventory(contract: Any, client_root: pathlib.Path, server_root: pathlib.Path) -> list[dict[str, str]]:
    data = contract.collect(client_root, server_root)
    call_files: dict[str, list[dict[str, Any]]] = defaultdict(list)
    for pair in data["service_method_pairs"]:
        for signature in pair["signatures"]:
            args = None if signature["arguments"] is None else contract.split_arguments(signature["arguments"])
            for site in signature["sites"]:
                call_files[site["file"]].append({
                    "service": pair["service"], "method": pair["method"], "arguments": args,
                    "file": site["file"], "line": site["line"],
                    "kind": "bound-object" if site["kind"] == "bound-object-call" else "remote-service",
                    "via": site["via"],
                })
    rows: list[dict[str, str]] = []
    session_rows: dict[str, dict[str, set[str]]] = {}
    notification_signatures: dict[tuple[str, int], str] = {}
    bind_dispatch = client_root / "carbon/common/script/net/moniker.py"
    bind_source = bind_dispatch.read_text(errors="replace") if bind_dispatch.exists() else ""
    dispatch_match = _BINDOBJECT_CALL.search(bind_source)
    if dispatch_match:
        line = contract.line_at(bind_source, dispatch_match.start())
        add_binding(rows, contract, bind_source, bind_dispatch.relative_to(client_root), "dynamic moniker",
                    "bound-object dispatch", "MachoBindObject", ["bind parameters", "remote call"], line,
                    "MachoMoniker.bind returns the bound object's remote method reply", "bound-object-dispatch")
    for path in sorted(client_root.rglob("*.py")):
        try:
            source = path.read_text(errors="replace")
        except OSError:
            continue
        relative = path.relative_to(client_root).as_posix()
        for call in audit.extended_calls(contract, path.relative_to(client_root), source):
            audit.add_call(rows, call["service"], call["method"], call["arguments"], call["kind"],
                           call["via"], relative, call["line"], call["consumer"])
        lines = source.splitlines()
        for call in call_files.get(relative, []):
            line = call["line"]
            text = lines[line - 1] if line <= len(lines) else ""
            prefix = text.split(call["method"], 1)[0]
            assignment = audit.re.search(r"(?:(?:self\.)?([A-Za-z_][A-Za-z0-9_]*)\s*=\s*)[^=]*$", prefix)
            variable = assignment.group(1) if assignment else None
            consumer = audit.rpc_context(contract, source, line, variable)
            audit.add_call(rows, call["service"], call["method"], call["arguments"], call["kind"],
                           call["via"], relative, line, consumer)
        for match in _CONNECTOR.finditer(source):
            args = argument_shape(contract, source, match.end() - 1)
            service = "<dynamic service>"
            if args:
                literal = audit.re.fullmatch(r"(['\"])([A-Za-z0-9_]+)\1", args[0].strip())
                if literal:
                    service = literal.group(2)
            connector = match.group(1)
            kind = "proxy service" if connector in {"ProxySvc", "ConnectToProxyServerService"} else (
                "solar-system service" if "SolarSystem" in connector or "SolServer" in connector else "remote service"
            )
            line = contract.line_at(source, match.start())
            add_binding(rows, contract, source, path.relative_to(client_root), service, kind, connector,
                        args, line, audit.rpc_context(contract, source, line))
        for match in audit.MONIKER_BIND.finditer(source):
            args = argument_shape(contract, source, match.end() - 1)
            service = "<dynamic moniker>"
            if args:
                literal = audit.re.fullmatch(r"(['\"])([A-Za-z0-9_]+)\1", args[0].strip())
                if literal:
                    service = literal.group(2)
            line = contract.line_at(source, match.start())
            add_binding(rows, contract, source, path.relative_to(client_root), service, "moniker",
                        "Moniker", args, line, audit.rpc_context(contract, source, line))
        for match in contract.CLIENT_METHOD.finditer(source):
            method = match.group(1)
            if method.startswith("On"):
                opening = source.find("(", match.start())
                raw = contract.argument_text(source, opening)
                notification_signatures[(relative, contract.line_at(source, match.start()))] = (
                    method + "(" + (contract.normalize(raw) if raw is not None else "") + ")"
                )
        if "session" not in source:
            continue
        for pattern, access in ((audit.SESSION_GET, "Get"), (audit.SESSION_INDEX, "index"),
                                (audit.SESSION_ATTR, "attribute")):
            for match in pattern.finditer(source):
                field = match.group(2) if access != "attribute" else match.group(1)
                if access == "attribute" and field in {"Get", "Set", "HasKey", "GetByCharID"}:
                    continue
                line = contract.line_at(source, match.start())
                if access == "attribute" and audit.re.match(r"\s*\(", source[match.end():]):
                    continue
                item = session_rows.setdefault(field, {"sites": set(), "consumers": set(), "accesses": set()})
                item["sites"].add(f"{relative}:{line}")
                item["consumers"].add(audit.rpc_context(contract, source, line))
                item["accesses"].add(access)
    cfg = client_root / "carbon/common/script/sys/cfg.py"
    if cfg.exists():
        cfg_source = cfg.read_text(errors="replace")
        runtime = [
            f"{cfg.relative_to(client_root).as_posix()}:{contract.line_at(cfg_source, match.start())}"
            for match in audit.re.finditer(r"getattr\(conf, self\.(?:dbfetcher|dbMultiFetcher)\)", cfg_source)
        ]
        for row in rows:
            if row["service"] == "config" and row["bound_object_kind"] == "dynamic-config-recordset":
                row["reply_consumer"] += "; runtime invocation " + ",".join(runtime)
    grouped: dict[tuple[str, str, str, str, str], dict[str, Any]] = {}
    for row in rows:
        key = (row["row_type"], row["service"], row["bound_object_kind"], row["method"], row["argument_shape"])
        item = grouped.setdefault(key, {**row, "sites": set(), "consumers": set(), "evidence_set": set()})
        item["sites"].add(row["client_file_line"])
        item["consumers"].add(row["reply_consumer"])
        item["evidence_set"].add(row["evidence"])
    result: list[dict[str, str]] = []
    for _, item in sorted(grouped.items()):
        item["client_file_line"] = "; ".join(sorted(item.pop("sites")))
        item["reply_consumer"] = "; ".join(sorted(item.pop("consumers")))
        item["evidence"] = "; ".join(sorted(item.pop("evidence_set")))
        if item["row_type"] == "rpc":
            item["core_play_priority"] = min(
                (audit.priority(site.rsplit(":", 1)[0], item["service"], item["method"])
                 for site in item["client_file_line"].split("; ")),
                key=lambda value: int(value[1:]),
            )
        result.append(item)
    handlers = data["client_notification_handlers"]
    for event in data["client_notifyevents"]:
        sites = [f"{site['file']}:{site['line']}" for site in event["sites"]]
        handler_sites = handlers.get(event["name"], [])
        consumer = "; ".join(f"{site['file']}:{site['line']}" for site in handler_sites) or "no same-name handler definition found"
        signatures = sorted({
            notification_signatures[(site["file"], site["line"])]
            for site in handler_sites if (site["file"], site["line"]) in notification_signatures
        })
        rows.append({
            "row_type": "notification", "service": "notification", "bound_object_kind": "notification subscription",
            "method": event["name"],
            "argument_shape": "handler signatures: " + "; ".join(signatures) if signatures else "notification payload; handler signature not found",
            "client_file_line": "; ".join(sorted(sites)), "reply_consumer": consumer,
            "core_play_priority": audit.priority(sites[0].split(":")[0], "notification", event["name"]),
            "evidence": "client __notifyevents__ declaration and same-name handler scan",
            "service_family": "notification",
        })
    for field, item in sorted(session_rows.items()):
        sites = sorted(item["sites"])
        rank = "P2" if any("/devtools/" in f"/{site.lower()}" for site in sites) else (
            "P0" if field in audit.CORE_SESSION_FIELDS else "P1"
        )
        rows.append({
            "row_type": "session-attribute", "service": "session", "bound_object_kind": "session state",
            "method": field, "argument_shape": "read/write: " + ",".join(sorted(item["accesses"])),
            "client_file_line": "; ".join(sites), "reply_consumer": "; ".join(sorted(item["consumers"])),
            "core_play_priority": rank, "evidence": "session attribute/Get/index access", "service_family": "session",
        })
    rows.sort(key=lambda row: (row["row_type"], row["service"], row["method"], row["argument_shape"], row["client_file_line"]))
    for index, row in enumerate(rows, start=1):
        prefix = {"rpc": "RPC", "notification": "NTF", "session-attribute": "SES",
                  "service-bind": "BND", "bound-object-dispatch": "BND"}.get(row["row_type"], "ROW")
        row["row_id"] = f"{prefix}-{index:05d}"
    return rows


audit.client_inventory = optimized_client_inventory


def main() -> int:
    return audit.main()


if __name__ == "__main__":
    raise SystemExit(main())

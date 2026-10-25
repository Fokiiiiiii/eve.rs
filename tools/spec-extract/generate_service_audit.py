#!/usr/bin/env python3
"""Generate a V24.01 client call inventory and its eve.rs route coverage matrix."""

from __future__ import annotations

import argparse
import collections
import csv
import importlib.util
import pathlib
import re
import sys
from typing import Any

ROOT = pathlib.Path(__file__).resolve().parents[2]
CONTRACT_PATH = ROOT / "tools/spec-extract/client_contract.py"
CLIENT_ROOT = pathlib.Path("/home/user/eve-logic")
REGISTRATION_FILES = (
    "dotnet/src/Eve.Host/Hosting/HostComposition.cs",
    "dotnet/src/Eve.Host/Rpc",
    "dotnet/src/Eve.Host/Composition",
    "dotnet/src/Eve.Host/OperationalRpcServices.cs",
    "dotnet/src/Eve.Host/MachoNetService.cs",
    "dotnet/src/Eve.Host/EventLogRpcService.cs",
    "dotnet/src/Eve.Host/Xmpp/XmppChatMgrService.cs",
    "dotnet/src/Eve.Services.Core/CoreServices.cs",
)
DIRECT_BIND = re.compile(
    r"\b(?:sm|session|self\.session)\.(RemoteSvc|ProxySvc|ConnectToRemoteService|"
    r"ConnectToAnyService|ConnectToSolServerService|ConnectToProxyServerService|ConnectToService)"
    r"\s*\(\s*(['\"])([A-Za-z0-9_]+)\2\s*\)"
    r"\s*(?:\.\s*NoBlock)?\s*\.\s*([A-Za-z_][A-Za-z0-9_]*)\s*\("
)
REMOTE_ASSIGN = re.compile(
    r"(?P<lhs>(?:self\.)?[A-Za-z_][A-Za-z0-9_]*)\s*=\s*"
    r"(?:sm|session|self\.session)\.(?:RemoteSvc|ProxySvc|ConnectToRemoteService|"
    r"ConnectToAnyService|ConnectToSolServerService|ConnectToProxyServerService|ConnectToService)"
    r"\s*\(\s*(['\"])(?P<service>[A-Za-z0-9_]+)\2\s*\)"
)
ALIAS_METHOD = re.compile(
    r"(?P<lhs>(?:self\.)?[A-Za-z_][A-Za-z0-9_]*)\s*\.\s*"
    r"(?:NoBlock\s*\.\s*)?(?P<method>[A-Za-z_][A-Za-z0-9_]*)\s*\("
)
REMOTE_ACCESSOR = re.compile(r"\bGetRemotePark\s*\(\s*\)\s*\.\s*([A-Za-z_][A-Za-z0-9_]*)\s*\(")
SESSION_GET = re.compile(r"\bsession\.Get\s*\(\s*(['\"])([A-Za-z_][A-Za-z0-9_]*)\1")
SESSION_INDEX = re.compile(r"\bsession\s*\[\s*(['\"])([A-Za-z_][A-Za-z0-9_]*)\1\s*\]")
SESSION_ATTR = re.compile(r"\bsession\.([A-Za-z_][A-Za-z0-9_]*)\b")
RECORDSET = re.compile(r"\bRecordset\s*\(")
CONNECTOR = re.compile(
    r"\b(?:sm|session|self\.session)\.(ConnectToRemoteService|ConnectToAnyService|"
    r"ConnectToSolServerService|ConnectToProxyServerService|ConnectToService|RemoteSvc|ProxySvc)"
    r"\s*\(\s*(['\"])([A-Za-z0-9_]+)\2"
)
MONIKER_BIND = re.compile(r"\bMoniker\s*\(")
GETTER_BIND = re.compile(r"\bGet([A-Z][A-Za-z0-9]*Handler)\s*\(")
TEST_DECORATED = re.compile(
    r"\[(?:Fact|Theory)\]\s*(?:\[[^\]]+\]\s*)*"
    r"(?:public|private|internal)\s+(?:static\s+)?(?:async\s+)?"
    r"(?:Task(?:\s*<[^>]+>)?|ValueTask(?:\s*<[^>]+>)?|void)\s+"
    r"([A-Za-z_][A-Za-z0-9_]*)\s*\([^)]*\)\s*\{"
)
OWNER_REJECTIONS = {
    ("pingService", "Ping"): "owner log: 2026-10-24 core breakage, normal Ping rejected with transport metadata",
    ("air_npe", "is_air_npe_enabled"): "owner log: 2026-10-24 core breakage, normal AIR NPE read rejected with transport metadata",
    ("config", "GetMultiOwnersEx"): "owner log: 2026-10-24 core breakage, valid identifier batch rejected",
    ("subscriptionMgr", "GetCloneGrade"): "owner log: 2026-10-24 core breakage, normal clone-grade read rejected",
    ("fleetMgr", "RegisterForDamageUpdates"): "owner log: 2026-10-24 core breakage, non-fleet subscription raised FleetError",
    ("beyonce", "CmdStargateJump"): "owner log: 2026-10-24 core breakage, in-warp gate jump rejected after normal warp",
}
CLIENT_LOCAL_APIS = {
    ("charMgr", "InvalidateStationItemsCache"):
        "client-local cache eviction at eve/client/script/environment/invCache.py:855-859; no service or moniker call",
}
CORE_SERVICES = {
    "pingService", "air_npe", "config", "subscriptionMgr", "charMgr", "charUnboundMgr",
    "station", "stationSvc", "invbroker", "dogmaIM", "beyonce", "viewStateSvc",
    "mapViewSvc", "search", "searchQuery",
}
CORE_SESSION_FIELDS = {
    "userid", "charid", "corpid", "stationid", "solarsystemid", "locationid",
    "shipid", "fleetid", "roles", "languageID", "worldspaceid",
}
FAMILY_OVERRIDES = {
    "beyonce": "space", "michelle": "space", "dogmaIM": "space", "fleetMgr": "fleet",
    "invbroker": "inventory", "inventory": "inventory", "station": "station",
    "stationSvc": "station", "charMgr": "character", "charUnboundMgr": "character",
    "subscriptionMgr": "login", "pingService": "login", "authentication": "login",
    "air_npe": "login", "config": "config", "viewStateSvc": "ui", "mapViewSvc": "ui",
    "corporation": "corporation", "corpRegistry": "corporation",
}


def load_contract() -> Any:
    spec = importlib.util.spec_from_file_location("client_contract", CONTRACT_PATH)
    if spec is None or spec.loader is None:
        raise RuntimeError(f"cannot load {CONTRACT_PATH}")
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


def shape(arguments: list[str] | None) -> str:
    if arguments is None:
        return "method reference; arguments not present at this callsite"
    positional = []
    keyword = []
    for value in arguments:
        match = re.match(r"([A-Za-z_][A-Za-z0-9_]*)\s*=", value)
        if match:
            keyword.append(f"{match.group(1)}={value.split('=', 1)[1].strip()}")
        else:
            positional.append(value)
    return f"positional=[{'; '.join(positional)}]; keyword=[{'; '.join(keyword)}]"


def family(service: str) -> str:
    if service in FAMILY_OVERRIDES:
        return FAMILY_OVERRIDES[service]
    name = re.sub(r"(?:Svc|Mgr|Service|Handler)$", "", service)
    return re.sub(r"[^a-z0-9]+", "-", name.lower()).strip("-") or "other"


def priority(file: str, service: str, method: str) -> str:
    lower = file.lower()
    if "/devtools/" in f"/{lower}" or "tournament" in lower or "referee" in lower:
        return "P2"
    if any(service == item for item in CORE_SERVICES):
        return "P0"
    if lower.startswith(("eve/client/script/ui/login/", "eve/client/script/ui/charactercreation/")):
        return "P0"
    if lower.startswith(("eve/client/", "eve/common/", "carbon/client/")):
        return "P1"
    return "P2"


def arg_at(contract: Any, source: str, offset: int) -> list[str] | None:
    open_paren = source.find("(", offset)
    if open_paren < 0:
        return []
    raw = contract.argument_text(source, open_paren)
    return contract.split_arguments(raw) if raw is not None else None


def rpc_context(contract: Any, source: str, line_no: int, variable: str | None = None) -> str:
    lines = source.splitlines()
    if not lines or line_no < 1 or line_no > len(lines):
        return ""
    scopes = contract.scopes_by_line(source)
    class_name, function_name = scopes[line_no - 1]
    scope = ".".join(part for part in (class_name, function_name) if part) or "module"
    start = line_no - 1
    end = line_no
    if function_name:
        while start > 0 and not re.match(r"^\s*def\s+", lines[start]):
            start -= 1
        if start < line_no - 1 and re.match(r"^\s*def\s+", lines[start]):
            indent = len(lines[start]) - len(lines[start].lstrip())
            end = len(lines)
            for index in range(start + 1, len(lines)):
                stripped = lines[index].lstrip()
                current_indent = len(lines[index]) - len(stripped)
                if stripped and current_indent <= indent and re.match(r"(?:def|class)\s+", stripped):
                    end = index
                    break
    current_line = lines[line_no - 1].strip()
    prefix = current_line.split("(", 1)[0]
    if re.search(r"\breturn\s*$", prefix):
        use = "reply returned directly"
    elif variable:
        refs = [
            index + 1 for index in range(line_no, end)
            if re.search(rf"\b{re.escape(variable)}\b", lines[index])
        ]
        use = f"reply assigned to {variable}; later use lines {','.join(map(str, refs[:12])) or 'none in caller'}"
    elif re.search(r"\bawait\b", current_line):
        use = "reply awaited inline"
    else:
        use = "reply consumed inline by caller expression"
    return f"{scope}; {use}"


def add_call(rows: list[dict[str, str]], service: str, method: str, args: list[str] | None,
             kind: str, via: str, file: str, line: int, consumer: str, priority_file: str | None = None) -> None:
    rows.append({
        "row_type": "rpc",
        "service": service,
        "bound_object_kind": kind,
        "method": method,
        "argument_shape": shape(args),
        "client_file_line": f"{file}:{line}",
        "reply_consumer": consumer,
        "core_play_priority": priority(priority_file or file, service, method),
        "evidence": f"{via}; standard Macho service transport may add machoVersion",
        "service_family": family(service),
    })


def extended_calls(contract: Any, path: pathlib.Path, source: str) -> list[dict[str, Any]]:
    calls: list[dict[str, Any]] = []
    relative = path.as_posix()
    scopes = contract.scopes_by_line(source)
    aliases: dict[tuple[str, str, str], str] = {}
    attrs: dict[tuple[str, str], str] = {}
    for match in REMOTE_ASSIGN.finditer(source):
        class_name, function_name = scopes[contract.line_at(source, match.start()) - 1]
        lhs = match.group("lhs")
        if lhs.startswith("self."):
            attrs[(class_name, lhs[5:])] = match.group("service")
        elif function_name:
            aliases[(class_name, function_name, lhs)] = match.group("service")
    for match in DIRECT_BIND.finditer(source):
        args = arg_at(contract, source, match.start(4))
        line = contract.line_at(source, match.start())
        cname, fname = scopes[line - 1]
        calls.append({
            "service": match.group(3), "method": match.group(4), "arguments": args,
            "file": relative, "line": line, "kind": "proxy-service" if match.group(1) == "ProxySvc" else "remote-service",
            "via": match.group(1), "consumer": rpc_context(contract, source, line),
        })
    for match in ALIAS_METHOD.finditer(source):
        lhs = match.group("lhs")
        line = contract.line_at(source, match.start())
        class_name, function_name = scopes[line - 1]
        service = attrs.get((class_name, lhs[5:])) if lhs.startswith("self.") else aliases.get((class_name, function_name, lhs))
        if service is None:
            continue
        method_open = source.find("(", match.start("method"))
        calls.append({
            "service": service, "method": match.group("method"),
            "arguments": arg_at(contract, source, match.start("method")),
            "file": relative, "line": line, "kind": "remote-service-alias", "via": "assigned remote object",
            "consumer": rpc_context(contract, source, line),
        })
    for match in REMOTE_ACCESSOR.finditer(source):
        line = contract.line_at(source, match.start())
        calls.append({
            "service": "beyonce", "method": match.group(1),
            "arguments": arg_at(contract, source, match.start(1)),
            "file": relative, "line": line, "kind": "bound-object", "via": "GetRemotePark moniker",
            "consumer": rpc_context(contract, source, line),
        })
    for match in RECORDSET.finditer(source):
        raw = contract.argument_text(source, match.end() - 1)
        args = contract.split_arguments(raw) if raw is not None else []
        for config_method in args[2:]:
            literal = re.fullmatch(r"(['\"])([A-Za-z_][A-Za-z0-9_]*)\1", config_method.strip())
            if literal is None:
                continue
            line = contract.line_at(source, match.start())
            calls.append({
                "service": "config", "method": literal.group(2),
                "arguments": ["recordset key or key batch"], "file": relative, "line": line,
                "kind": "dynamic-config-recordset", "via": "Recordset fetcher configuration",
                "consumer": f"cfg.Recordset.__LoadData / _Prime; declaration at {relative}:{line}",
            })
    return calls


def client_inventory(contract: Any, client_root: pathlib.Path, server_root: pathlib.Path) -> list[dict[str, str]]:
    data = contract.collect(client_root, server_root)
    records: dict[tuple[str, str, str, str], dict[str, Any]] = {}
    call_files: dict[str, list[dict[str, Any]]] = collections.defaultdict(list)
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
    result: list[dict[str, str]] = []
    for relative, call_list in call_files.items():
        source = (client_root / relative).read_text(errors="replace")
        for call in call_list:
            line = call["line"]
            text = source.splitlines()[line - 1] if line <= len(source.splitlines()) else ""
            prefix = text.split(call["method"], 1)[0]
            assignment = re.search(r"(?:(?:self\.)?([A-Za-z_][A-Za-z0-9_]*)\s*=\s*)[^=]*$", prefix)
            variable = assignment.group(1) if assignment else None
            context = rpc_context(contract, source, line, variable)
            add_call(result, call["service"], call["method"], call["arguments"], call["kind"], call["via"],
                     relative, line, context)
    for path in sorted(client_root.rglob("*.py")):
        try:
            source = path.read_text(errors="replace")
        except OSError:
            continue
        relative = path.relative_to(client_root).as_posix()
        for call in extended_calls(contract, path.relative_to(client_root), source):
            add_call(result, call["service"], call["method"], call["arguments"], call["kind"], call["via"],
                     relative, call["line"], call["consumer"])
    # Config Recordset fetchers are dynamic RPCs; retain the exact declaration and its adapter path.
    cfg = client_root / "carbon/common/script/sys/cfg.py"
    if cfg.exists():
        cfg_source = cfg.read_text(errors="replace")
        for match in re.finditer(r"getattr\(conf, self\.(?:dbfetcher|dbMultiFetcher)\)", cfg_source):
            line = contract.line_at(cfg_source, match.start())
            for record in result:
                if record["service"] == "config" and record["bound_object_kind"] == "dynamic-config-recordset":
                    record["reply_consumer"] += f"; runtime invocation {cfg.relative_to(client_root).as_posix()}:{line}"
    # Aggregate equivalent signatures while retaining every caller and consumer.
    grouped: dict[tuple[str, str, str, str], dict[str, Any]] = {}
    for row in result:
        key = (row["service"], row["bound_object_kind"], row["method"], row["argument_shape"])
        item = grouped.setdefault(key, {**row, "sites": set(), "consumers": set(), "evidence_set": set()})
        item["sites"].add(row["client_file_line"])
        item["consumers"].add(row["reply_consumer"])
        item["evidence_set"].add(row["evidence"])
    rows = []
    for _, item in sorted(grouped.items()):
        item["client_file_line"] = "; ".join(sorted(item["sites"]))
        item["reply_consumer"] = "; ".join(sorted(item["consumers"]))
        item["evidence"] = "; ".join(sorted(item["evidence_set"]))
        item["core_play_priority"] = min(
            (priority(site.rsplit(":", 1)[0], item["service"], item["method"]) for site in item["sites"]),
            key=lambda p: int(p[1:]),
        )
        item["service_family"] = family(item["service"])
        for key in ("sites", "consumers", "evidence_set"):
            del item[key]
        rows.append(item)
    # Notifications: each row records the subscription, all matching handlers, and their client consumers.
    handlers = data["client_notification_handlers"]
    for event in data["client_notifyevents"]:
        sites = [f"{site['file']}:{site['line']}" for site in event["sites"]]
        handler_sites = handlers.get(event["name"], [])
        consumer = "; ".join(f"{site['file']}:{site['line']}" for site in handler_sites) or "no same-name handler definition found"
        rows.append({
            "row_type": "notification", "service": "notification", "bound_object_kind": "notification subscription",
            "method": event["name"], "argument_shape": "notification payload; see matching handler signature",
            "client_file_line": "; ".join(sorted(sites)), "reply_consumer": consumer,
            "core_play_priority": priority(sites[0].split(":")[0], "notification", event["name"]),
            "evidence": "client __notifyevents__ declaration and same-name handler scan",
            "service_family": "notification",
        })
    # Session reads/writes are part of the session contract consumed by client logic.
    session_rows: dict[str, dict[str, Any]] = {}
    for path in sorted(client_root.rglob("*.py")):
        try:
            source = path.read_text(errors="replace")
        except OSError:
            continue
        relative = path.relative_to(client_root).as_posix()
        for pattern, access in ((SESSION_GET, "Get"), (SESSION_INDEX, "index"), (SESSION_ATTR, "attribute")):
            for match in pattern.finditer(source):
                field = match.group(2) if access != "attribute" else match.group(1)
                if access == "attribute" and field in {"Get", "Set", "HasKey", "GetByCharID"}:
                    continue
                line = contract.line_at(source, match.start())
                if access == "attribute" and re.match(r"\s*\(", source[match.end():]):
                    continue
                site = f"{relative}:{line}"
                consumer = rpc_context(contract, source, line)
                item = session_rows.setdefault(field, {"sites": set(), "consumers": set(), "accesses": set()})
                item["sites"].add(site)
                item["consumers"].add(consumer)
                item["accesses"].add(access)
    for field, item in sorted(session_rows.items()):
        sites = sorted(item["sites"])
        rank = "P2" if any("/devtools/" in f"/{site.lower()}" for site in sites) else ("P0" if field in CORE_SESSION_FIELDS else "P1")
        rows.append({
            "row_type": "session-attribute", "service": "session", "bound_object_kind": "session state",
            "method": field, "argument_shape": "read/write: " + ",".join(sorted(item["accesses"])),
            "client_file_line": "; ".join(sites), "reply_consumer": "; ".join(sorted(item["consumers"])),
            "core_play_priority": rank, "evidence": "session attribute/Get/index access",
            "service_family": "session",
        })
    # Stable ids are shared by the client and coverage matrices.
    rows.sort(key=lambda row: (row["row_type"], row["service"], row["method"], row["argument_shape"], row["client_file_line"]))
    for index, row in enumerate(rows, start=1):
        prefix = {"rpc": "RPC", "notification": "NTF", "session-attribute": "SES"}.get(row["row_type"], "ROW")
        row["row_id"] = f"{prefix}-{index:05d}"
    return rows


def host_routes(contract: Any, repo_root: pathlib.Path) -> tuple[set[tuple[str, str]], dict[str, str], dict[str, str]]:
    source_root = repo_root / "dotnet/src"
    all_routes = contract.host_registered_methods(source_root)
    type_by_service: dict[str, str] = {}
    class_sources: dict[str, str] = {}
    for path in sorted(source_root.rglob("*.cs")):
        try:
            source = path.read_text(errors="replace")
        except OSError:
            continue
        classes = list(contract.CS_CLASS_DECLARATION.finditer(source))
        attrs = list(contract.SERVICE_ATTRIBUTE.finditer(source))
        for index, attr in enumerate(attrs):
            following = attrs[index + 1].start() if index + 1 < len(attrs) else len(source)
            block = source[attr.start():following]
            class_match = contract.SERVICE_CLASS.search(source, attr.end())
            if class_match and class_match.start() - attr.end() < 1000:
                type_by_service[attr.group(2)] = class_match.group(1)
                class_sources[class_match.group(1)] = source
        for match in contract.MANUAL_NAME.finditer(source):
            owner = next((item for item in reversed(classes) if item.start() < match.start()), None)
            if owner:
                type_by_service[match.group(2)] = owner.group(1)
                class_sources[owner.group(1)] = source
    registration_sources: list[str] = []
    for relative in REGISTRATION_FILES:
        path = repo_root / relative
        if path.is_dir():
            candidates = sorted(path.glob("*.cs"))
        elif path.exists():
            candidates = [path]
        elif relative.endswith("CharacterServicesComposition"):
            candidates = sorted((repo_root / "dotnet/src/Eve.Host/Composition").glob("CharacterServicesComposition*.cs"))
        else:
            candidates = []
        for candidate in candidates:
            try:
                registration_sources.append(candidate.read_text(errors="replace"))
            except OSError:
                pass
    core_path = repo_root / "dotnet/src/Eve.Services.Core/CoreServices.cs"
    if core_path.exists() and all("CoreServices.Create" not in source for source in registration_sources):
        registration_sources.append(core_path.read_text(errors="replace"))
    registered_types: set[str] = set()
    aliases: dict[str, str] = {}
    for source in registration_sources:
        registered_types.update(re.findall(r"\bnew\s+(?:[A-Za-z_][\w]*\.)*([A-Za-z_][A-Za-z0-9_]*)\s*(?:\(|\{)", source))
        aliases.update((match.group(2), match.group(4)) for match in contract.REGISTER_ALIAS.finditer(source))
    active_services = {
        service for service, service_type in type_by_service.items()
        if service_type in registered_types
    }
    active_routes = {(service, method) for service, method in all_routes if service in active_services}
    for alias, target in aliases.items():
        active_routes.update((alias, method) for service, method in tuple(active_routes) if service == target)
        if target in active_services:
            active_services.add(alias)
            type_by_service[alias] = type_by_service.get(target, target)
    return active_routes, type_by_service, aliases


def test_evidence(repo_root: pathlib.Path, pairs: set[tuple[str, str]], type_by_service: dict[str, str]) -> dict[tuple[str, str], list[str]]:
    evidence: dict[tuple[str, str], set[str]] = collections.defaultdict(set)
    test_root = repo_root / "dotnet/tests"
    if not test_root.exists():
        return {}
    for path in sorted(test_root.rglob("*.cs")):
        try:
            source = path.read_text(errors="replace")
        except OSError:
            continue
        relative = path.relative_to(repo_root).as_posix()
        for match in TEST_DECORATED.finditer(source):
            opening = source.find("{", match.end() - 1)
            if opening < 0:
                continue
            end = load_contract().csharp_block_end(source, opening)
            if end is None:
                continue
            body = source[opening:end]
            test_name = match.group(1)
            if not re.search(r"\b(?:HostComposition|HostServer|ServiceRegistry|PacketDispatcher|Macho|DispatchAsync|InvokeAsync|RpcHarness|HostCodec)\b", body):
                continue
            for service, method in pairs:
                service_type = type_by_service.get(service, "")
                if re.search(rf"\b{re.escape(method)}\b", body) and (
                    re.search(rf"\b{re.escape(service)}\b", body)
                    or (service_type and re.search(rf"\b{re.escape(service_type)}\b", body))
                ):
                    evidence[(service, method)].add(f"{relative}:{test_name}")
    return {key: sorted(values) for key, values in evidence.items()}


def status_for(pair: tuple[str, str], routes: set[tuple[str, str]], tests: dict[tuple[str, str], list[str]],
               contract: Any) -> tuple[str, str]:
    if pair in CLIENT_LOCAL_APIS:
        return "CLIENT-LOCAL", CLIENT_LOCAL_APIS[pair]
    if pair in OWNER_REJECTIONS:
        return "REJECTS-VALID-INPUT", OWNER_REJECTIONS[pair]
    if pair in contract.REGISTERED_BEHAVIOR_GAPS:
        cause = contract.REGISTERED_BEHAVIOR_GAPS[pair]
        return "HOLLOW", f"registered behavior gap catalog: {cause}"
    if pair not in routes:
        return "MISSING", "no active Host registration route matched this service/method"
    if tests.get(pair):
        return f"IMPLEMENTED-TESTED ({tests[pair][0]})", "matching Host wire-test source found; execution is checked separately"
    return "IMPLEMENTED-UNTESTED", "active Host route found; no matching Host wire-test source found"


def write_csv(path: pathlib.Path, rows: list[dict[str, Any]], fields: list[str]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", encoding="utf-8", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=fields, extrasaction="ignore")
        writer.writeheader()
        writer.writerows(rows)


def queue_jobs(rows: list[dict[str, Any]], queue_dir: pathlib.Path) -> list[pathlib.Path]:
    pending = [
        row for row in rows
        if row["row_type"] == "rpc" and row["core_play_priority"] in {"P0", "P1"}
        and row["status"].split(" (", 1)[0] != "IMPLEMENTED-TESTED"
    ]
    grouped: dict[str, list[dict[str, Any]]] = collections.defaultdict(list)
    for row in pending:
        grouped[row["service_family"]].append(row)
    created = []
    batch = 0
    priority_order = {"P0": 0, "P1": 1}
    for family_name, family_rows in sorted(grouped.items()):
        family_rows.sort(key=lambda row: (priority_order[row["core_play_priority"]], row["service"], row["method"], row["row_id"]))
        for offset in range(0, len(family_rows), 25):
            batch += 1
            chunk = family_rows[offset:offset + 25]
            ids = ", ".join(row["row_id"] for row in chunk)
            lines = [
                "EFFORT: high",
                "LANE: none",
                f"Job: Resolve eve.rs Host coverage for rows {ids}.",
                "",
                "Use these fix classes:",
                "A. MISSING: register and implement the client-called service/method through the real Host with authoritative state, correct wire reply, errors, persistence, and notifications.",
                "B. REJECTS-VALID-INPUT: accept the observed valid V24.01 arguments, including transport-added machoVersion and normal burst limits; keep malformed-input validation.",
                "C. HOLLOW or IMPLEMENTED-UNTESTED: return the client-consumed shape/state or add a focused end-to-end wire regression for the existing behavior.",
                "",
                "For every row, trace all client callers and reply consumers in the whole /home/user/eve-logic tree, then compare the matching EveJS behavior and record evidence with client file:line references.",
                "Write wire tests through the real Host registration. Do not mock a handler as a substitute for route registration.",
                "",
                "Rows:",
            ]
            lines.extend(
                f"- {row['row_id']} {row['service']}.{row['method']} ({row['status']}; {row['core_play_priority']}; {row['client_file_line']})"
                for row in chunk
            )
            lines.extend([
                "",
                "Commit each fix with an English message as Fokiiiiiii <229531129+Fokiiiiiii@users.noreply.github.com>, then run git fetch origin main, git merge origin/main, and git push origin HEAD:main.",
                "Verify on Actions with python3 tools/dev/ci-status.py dispatch-agent-test --ref <sha> --projects <TestProject[,..]> --filter 'FullyQualifiedName~Name' --live '' --rust none, then tools/dev/ci-status.py wait <run-id> and tools/dev/ci-status.py failures <sha>.",
                "Keep the handoff note current after every push. No database lane is required for this batch.",
            ])
            path = queue_dir / f"03-allsvc-batch-{batch:02d}-{family_name}.txt"
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text("\n".join(lines) + "\n", encoding="utf-8")
            created.append(path)
    return created


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--client-root", type=pathlib.Path, default=CLIENT_ROOT)
    parser.add_argument("--repo-root", type=pathlib.Path, default=ROOT)
    parser.add_argument("--queue-dir", type=pathlib.Path)
    args = parser.parse_args()
    contract = load_contract()
    client_rows = client_inventory(contract, args.client_root, args.repo_root / "dotnet/src")
    routes, type_by_service, _ = host_routes(contract, args.repo_root)
    pairs = {(row["service"], row["method"]) for row in client_rows if row["row_type"] == "rpc"}
    tests = test_evidence(args.repo_root, pairs, type_by_service)
    coverage = []
    for row in client_rows:
        if row["row_type"] != "rpc":
            continue
        pair = (row["service"], row["method"])
        status, evidence = status_for(pair, routes, tests, contract)
        coverage.append({
            "row_id": row["row_id"], "service": row["service"], "bound_object_kind": row["bound_object_kind"],
            "method": row["method"], "argument_shape": row["argument_shape"],
            "client_file_line": row["client_file_line"], "core_play_priority": row["core_play_priority"],
            "eve_rs_status": status, "status_evidence": evidence,
        })
    audit_dir = args.repo_root / "docs/project/service-audit"
    write_csv(audit_dir / "CLIENT_CALL_MATRIX.csv", client_rows, [
        "row_id", "row_type", "service", "bound_object_kind", "method", "argument_shape",
        "client_file_line", "reply_consumer", "core_play_priority", "service_family", "evidence",
    ])
    write_csv(audit_dir / "EVE_RS_COVERAGE.csv", coverage, [
        "row_id", "service", "bound_object_kind", "method", "argument_shape", "client_file_line",
        "core_play_priority", "eve_rs_status", "status_evidence",
    ])
    queue_paths = queue_jobs(coverage, args.queue_dir) if args.queue_dir else []
    counts = collections.Counter(row["eve_rs_status"].split(" (", 1)[0] for row in coverage)
    print(f"client rows={len(client_rows)} rpc rows={len(coverage)} active Host routes={len(routes)} queue files={len(queue_paths)}")
    print("coverage=" + ", ".join(f"{key}:{value}" for key, value in sorted(counts.items())))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

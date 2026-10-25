#!/usr/bin/env python3
"""Generate the current service audit, including getter/property backed RPCs."""

from __future__ import annotations

import importlib.util
import pathlib
import re
import sys
from collections import defaultdict
from typing import Any

ROOT = pathlib.Path(__file__).resolve().parents[2]
RUNNER_PATH = ROOT / "tools/spec-extract/run_service_audit.py"
SPEC = importlib.util.spec_from_file_location("service_audit_runner", RUNNER_PATH)
if SPEC is None or SPEC.loader is None:
    raise RuntimeError(f"cannot load {RUNNER_PATH}")
runner = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = runner
SPEC.loader.exec_module(runner)
audit = runner.audit

_contract = audit.load_contract()
_original_split_arguments = _contract.split_arguments


def split_arguments_without_trailing_space(source: str) -> list[str]:
    return [
        "\n".join(line.rstrip() for line in argument.splitlines()).strip()
        for argument in _original_split_arguments(source)
    ]


_contract.split_arguments = split_arguments_without_trailing_space
GLOBAL_ACCESSORS: dict[str, set[str]] = {}
GLOBAL_ACCESSOR_PATTERN: re.Pattern[str] | None = None
SERVICE_MARKERS = ("RemoteSvc", "ProxySvc", "ConnectTo", "GetRemotePark", "MachoBindObject")
RETURN_DIRECT = audit.re.compile(
    r"\breturn\s+(?:sm|session|self\.session)\.(?:RemoteSvc|ProxySvc|ConnectToRemoteService|"
    r"ConnectToSolarSystemService|ConnectToAnyService|ConnectToSolServerService|"
    r"ConnectToProxyServerService|ConnectToService)\s*\(\s*(['\"])([A-Za-z0-9_]+)\1\s*\)"
)
RETURN_SELF = audit.re.compile(r"\breturn\s+self\.([A-Za-z_][A-Za-z0-9_]*)\b")
RETURN_LOCAL = audit.re.compile(r"\breturn\s+([A-Za-z_][A-Za-z0-9_]*)\s*$")


def accessor_definitions(contract: Any, source: str) -> dict[tuple[str, str], str]:
    if not any(marker in source for marker in SERVICE_MARKERS):
        return {}
    scopes = contract.scopes_by_line(source)
    attrs: dict[tuple[str, str], str] = {}
    locals_: dict[tuple[str, str, str], str] = {}
    accessors: dict[tuple[str, str], str] = {}

    for match in audit.REMOTE_ASSIGN.finditer(source):
        line = contract.line_at(source, match.start())
        class_name, function_name = scopes[line - 1]
        lhs = match.group("lhs")
        if lhs.startswith("self."):
            attrs[(class_name, lhs[5:])] = match.group("service")
        elif function_name:
            locals_[(class_name, function_name, lhs)] = match.group("service")

    for match in RETURN_DIRECT.finditer(source):
        line = contract.line_at(source, match.start())
        class_name, function_name = scopes[line - 1]
        if function_name:
            accessors[(class_name, function_name)] = match.group(2)

    for match in RETURN_SELF.finditer(source):
        line = contract.line_at(source, match.start())
        class_name, function_name = scopes[line - 1]
        service = attrs.get((class_name, match.group(1)))
        if service is not None and function_name:
            accessors[(class_name, function_name)] = service

    for match in RETURN_LOCAL.finditer(source):
        line = contract.line_at(source, match.start())
        class_name, function_name = scopes[line - 1]
        service = locals_.get((class_name, function_name, match.group(1)))
        if service is not None and function_name:
            accessors[(class_name, function_name)] = service
    return accessors


def build_global_accessors(client_root: pathlib.Path, contract: Any) -> dict[str, set[str]]:
    accessors: dict[str, set[str]] = defaultdict(set)
    for path in sorted(client_root.rglob("*.py")):
        try:
            source = path.read_text(errors="replace")
        except OSError:
            continue
        if not any(marker in source for marker in SERVICE_MARKERS):
            continue
        for (_, name), service in accessor_definitions(contract, source).items():
            accessors[name].add(service)
    return dict(accessors)


def accessor_calls(contract: Any, path: pathlib.Path, source: str) -> list[dict[str, Any]]:
    if not any(marker in source for marker in SERVICE_MARKERS):
        if GLOBAL_ACCESSOR_PATTERN is None or not GLOBAL_ACCESSOR_PATTERN.search(source):
            return []
    scopes = contract.scopes_by_line(source)
    attrs: dict[tuple[str, str], str] = {}
    locals_: dict[tuple[str, str, str], str] = {}
    accessors = accessor_definitions(contract, source)

    for match in audit.REMOTE_ASSIGN.finditer(source):
        line = contract.line_at(source, match.start())
        class_name, function_name = scopes[line - 1]
        lhs = match.group("lhs")
        if lhs.startswith("self."):
            attrs[(class_name, lhs[5:])] = match.group("service")
        elif function_name:
            locals_[(class_name, function_name, lhs)] = match.group("service")

    def resolve(class_name: str, function_name: str, name: str) -> str | None:
        service = (accessors.get((class_name, name))
                   or attrs.get((class_name, name))
                   or locals_.get((class_name, function_name, name)))
        if service:
            return service
        candidates = GLOBAL_ACCESSORS.get(name, set())
        return next(iter(candidates)) if len(candidates) == 1 else None

    def add_call(result: list[dict[str, Any]], service: str, method: str,
                 method_start: int, receiver: str) -> None:
        line = contract.line_at(source, method_start)
        args = audit.arg_at(contract, source, method_start)
        lines = source.splitlines()
        line_text = lines[line - 1] if line <= len(lines) else ""
        prefix = line_text.split(method, 1)[0]
        assignment = audit.re.search(r"(?:(?:self\.)?([A-Za-z_][A-Za-z0-9_]*)\s*=\s*)[^=]*$", prefix)
        variable = assignment.group(1) if assignment else None
        result.append({
            "service": service, "method": method, "arguments": args, "file": path.as_posix(),
            "line": line, "kind": "remote-service-accessor", "via": f"service object returned by {receiver}",
            "consumer": audit.rpc_context(contract, source, line, variable),
        })

    result: list[dict[str, Any]] = []
    patterns = (
        audit.re.compile(r"\bself\.([A-Za-z_][A-Za-z0-9_]*)\s*\(\s*\)\s*\.\s*([A-Za-z_][A-Za-z0-9_]*)\s*\("),
        audit.re.compile(r"\bself\.([A-Za-z_][A-Za-z0-9_]*)\s*\.\s*([A-Za-z_][A-Za-z0-9_]*)\s*\("),
        audit.re.compile(r"\b([A-Za-z_][A-Za-z0-9_]*)\s*\(\s*\)\s*\.\s*([A-Za-z_][A-Za-z0-9_]*)\s*\("),
    )
    for pattern in patterns:
        for match in pattern.finditer(source):
            line = contract.line_at(source, match.start())
            class_name, function_name = scopes[line - 1]
            service = resolve(class_name, function_name, match.group(1))
            if service:
                add_call(result, service, match.group(2), match.start(2), match.group(1))

    for match in audit.re.finditer(r"\b([A-Za-z_][A-Za-z0-9_]*)\s*\(\s*\)\s*\.\s*([A-Za-z_][A-Za-z0-9_]*)\s*\.\s*([A-Za-z_][A-Za-z0-9_]*)\s*\(", source):
        accessor, property_name, method = match.groups()
        candidates = GLOBAL_ACCESSORS.get(property_name, set())
        if len(candidates) == 1:
            add_call(result, next(iter(candidates)), method, match.start(3), f"{accessor}().{property_name}")

    for match in audit.re.finditer(r"(?P<lhs>(?:self\.)?[A-Za-z_][A-Za-z0-9_]*)\s*=\s*self\.(?P<accessor>[A-Za-z_][A-Za-z0-9_]*)\s*\(\s*\)", source):
        line = contract.line_at(source, match.start())
        class_name, function_name = scopes[line - 1]
        service = resolve(class_name, function_name, match.group("accessor"))
        if service:
            lhs = match.group("lhs")
            if lhs.startswith("self."):
                attrs[(class_name, lhs[5:])] = service
            else:
                locals_[(class_name, function_name, lhs)] = service

    for match in audit.ALIAS_METHOD.finditer(source):
        lhs = match.group("lhs")
        line = contract.line_at(source, match.start())
        class_name, function_name = scopes[line - 1]
        service = attrs.get((class_name, lhs[5:])) if lhs.startswith("self.") else locals_.get((class_name, function_name, lhs))
        if service:
            add_call(result, service, match.group("method"), match.start("method"), lhs)
    return result


_original_extended_calls = audit.extended_calls


def complete_extended_calls(contract: Any, path: pathlib.Path, source: str) -> list[dict[str, Any]]:
    calls = _original_extended_calls(contract, path, source)
    seen = {(call["service"], call["method"], call["line"]) for call in calls}
    for call in accessor_calls(contract, path, source):
        key = (call["service"], call["method"], call["line"])
        if key not in seen:
            calls.append(call)
            seen.add(key)
    for call in calls:
        if call["kind"] == "bound-object-method-reference":
            if path.as_posix().endswith("autopilot.py"):
                call["consumer"] = (
                    "AutoPilot.Update; passed as callback to SessionMgr.PerformSessionChange; "
                    "the wrapper returns the callback reply but AutoPilot.Update ignores it, then sends "
                    "the jump prompt and OnAutoPilotJump (autopilot.py:359-360)"
                )
            else:
                call["consumer"] = (
                    f"{call['consumer']}; callback reply is returned through SessionMgr.PerformSessionChange"
                )
    return calls


audit.extended_calls = complete_extended_calls
_original_client_inventory = audit.client_inventory


def complete_client_inventory(contract: Any, client_root: pathlib.Path, server_root: pathlib.Path) -> list[dict[str, str]]:
    global GLOBAL_ACCESSORS, GLOBAL_ACCESSOR_PATTERN
    GLOBAL_ACCESSORS = build_global_accessors(client_root, contract)
    names = sorted(name for name, services in GLOBAL_ACCESSORS.items() if len(services) == 1)
    GLOBAL_ACCESSOR_PATTERN = audit.re.compile(r"\b(?:" + "|".join(map(re.escape, names)) + r")\s*(?:\(\s*\)|\.)") if names else None
    return _original_client_inventory(contract, client_root, server_root)


audit.client_inventory = complete_client_inventory

_original_host_routes = audit.host_routes


def complete_host_routes(contract: Any, repo_root: pathlib.Path):
    routes, type_by_service, aliases = _original_host_routes(contract, repo_root)
    registration_sources: list[str] = []
    for relative in audit.REGISTRATION_FILES:
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
                continue
    core_path = repo_root / "dotnet/src/Eve.Services.Core/CoreServices.cs"
    if core_path.exists() and all("CoreServices.Create" not in source for source in registration_sources):
        registration_sources.append(core_path.read_text(errors="replace"))
    registered_types: set[str] = set()
    for source in registration_sources:
        registered_types.update(re.findall(
            r"\bnew\s+(?:[A-Za-z_][\w]*\.)*([A-Za-z_][A-Za-z0-9_]*)\s*(?:\(|\{)",
            source,
        ))

    if "StructureTravelRpcService" in registered_types:
        route_pairs = contract.host_registered_methods(repo_root / "dotnet/src")
        for registration in registration_sources:
            for match in re.finditer(r"\bnew\s+StructureTravelRpcService\s*\(\s*(['\"])([^'\"]+)\1", registration):
                service = match.group(2)
                type_by_service[service] = "StructureTravelRpcService"
                routes.update((name, method) for name, method in route_pairs if name == service)

    source_root = repo_root / "dotnet/src"
    for path in sorted(source_root.rglob("*.cs")):
        try:
            source = path.read_text(errors="replace")
        except OSError:
            continue
        classes = list(contract.CS_CLASS_DECLARATION.finditer(source))
        for manual_name in contract.MANUAL_NAME.finditer(source):
            owner = next((item for item in reversed(classes) if item.start() < manual_name.start()), None)
            if owner is None or owner.group(1) not in registered_types:
                continue
            following = next((item for item in classes if item.start() > manual_name.start()), None)
            block = source[owner.start() : following.start() if following is not None else len(source)]
            service = manual_name.group(2)
            for expression in contract.HAS_METHOD_EXPRESSION.finditer(block):
                value = expression.group(1)
                routes.update((service, literal.group(2)) for literal in contract.STRING_LITERAL.finditer(value))
                for name in re.findall(r"(?:==|\bis\b)\s*([A-Za-z_][A-Za-z0-9_]*)", value):
                    constant = re.compile(
                        rf"\b(?:const\s+)?string\s+{re.escape(name)}\s*=\s*(['\"])([^'\"]+)\1"
                    )
                    routes.update((service, match.group(2)) for match in constant.finditer(block))
    return routes, type_by_service, aliases


audit.host_routes = complete_host_routes


def host_harness_test_evidence(
    repo_root: pathlib.Path,
    pairs: set[tuple[str, str]],
    type_by_service: dict[str, str],
) -> dict[tuple[str, str], list[str]]:
    evidence: dict[tuple[str, str], set[str]] = defaultdict(set)
    test_root = repo_root / "dotnet/tests"
    if not test_root.exists():
        return {}
    contract = audit.load_contract()
    real_host_registration = re.compile(
        r"\bStart\s*\(\s*configure\s*:\s*registry\s*=>\s*registry\.Register\s*\(",
        re.S,
    )
    for path in sorted(test_root.rglob("*.cs")):
        try:
            source = path.read_text(errors="replace")
        except OSError:
            continue
        for match in audit.TEST_DECORATED.finditer(source):
            opening = source.find("{", match.end() - 1)
            if opening < 0:
                continue
            end = contract.csharp_block_end(source, opening)
            if end is None:
                continue
            body = source[opening:end]
            if not real_host_registration.search(body) or not re.search(r"\bConnectAsync\s*\(", body):
                continue
            relative = path.relative_to(repo_root).as_posix()
            test_name = match.group(1)
            for service, method in pairs:
                service_type = type_by_service.get(service, "")
                if re.search(rf"\b{re.escape(method)}\b", body) and (
                    re.search(rf"\b{re.escape(service)}\b", body)
                    or (service_type and re.search(rf"\b{re.escape(service_type)}\b", body))
                ):
                    evidence[(service, method)].add(f"{relative}:{test_name}")
    return {key: sorted(names) for key, names in evidence.items()}


def filtered_test_evidence(repo_root: pathlib.Path, pairs: set[tuple[str, str]],
                           type_by_service: dict[str, str]) -> dict[tuple[str, str], list[str]]:
    source_matches = runner._original_test_evidence(repo_root, pairs, type_by_service)
    for pair, names in host_harness_test_evidence(repo_root, pairs, type_by_service).items():
        source_matches[pair] = sorted(set(source_matches.get(pair, [])).union(names))
    excluded = ("budget", "registered_under_the_client_names")
    return {
        pair: [name for name in names if not any(marker in name.lower() for marker in excluded)]
        for pair, names in source_matches.items()
        if any(not any(marker in name.lower() for marker in excluded) for name in names)
    }


_original_status_for = audit.status_for


def current_status_for(pair: tuple[str, str], routes: set[tuple[str, str]],
                       tests: dict[tuple[str, str], list[str]], contract: Any) -> tuple[str, str]:
    if pair == ("beyonce", "CmdStargateJump") and pair in routes and tests.get(pair):
        return (f"IMPLEMENTED-TESTED ({tests[pair][0]})",
                "current Host dispatcher warp-then-jump wire regression supersedes the owner log recorded before the fix")
    return _original_status_for(pair, routes, tests, contract)


audit.test_evidence = filtered_test_evidence
audit.status_for = current_status_for


def main() -> int:
    return runner.main()


if __name__ == "__main__":
    raise SystemExit(main())

#!/usr/bin/env python3
"""Extract literal V24.01 client RPC callsites and event subscriptions.

The client is a read-only reference. This scanner reports service/method
expressions, their source argument text, caller locations, and names declared
in ``__notifyevents__``. It does not copy client code into the server.

    python3 tools/spec-extract/client_contract.py /home/user/eve-logic --format summary
    python3 tools/spec-extract/client_contract.py /home/user/eve-logic --format json
"""

from __future__ import annotations

import argparse
import collections
import json
import pathlib
import re
import sys
from typing import Any


REMOTE_CALL = re.compile(
    r"RemoteSvc\(\s*(['\"])([A-Za-z0-9_]+)\1\s*\)\s*"
    r"(?:\.\s*NoBlock)?\.\s*([A-Za-z_][A-Za-z0-9_]*)\s*\("
)
REMOTE_REFERENCE = re.compile(
    r"RemoteSvc\(\s*(['\"])([A-Za-z0-9_]+)\1\s*\)\s*"
    r"(?:\.\s*([A-Za-z_][A-Za-z0-9_]*))?"
)
CONNECTED_CALL = re.compile(
    r"(?:ConnectToRemoteService|ConnectToAnyService|ConnectToSolServerService|"
    r"ConnectToProxyServerService|ConnectToService)\(\s*(['\"])([A-Za-z0-9_]+)\1\s*\)\s*"
    r"(?:\.\s*NoBlock)?\.\s*([A-Za-z_][A-Za-z0-9_]*)\s*\("
)
CONNECT_SERVICE = re.compile(
    r"(?:ConnectToRemoteService|ConnectToAnyService|ConnectToSolServerService|"
    r"ConnectToProxyServerService|ConnectToService)\(\s*(['\"])([A-Za-z0-9_]+)\1"
)
MONIKER_GETTER = re.compile(
    r"(?:ConnectToRemoteService|ConnectToAnyService|ConnectToSolServerService|"
    r"ConnectToProxyServerService|ConnectToService)\(\s*(['\"])[A-Za-z0-9_]+\1\s*\)\s*"
    r"\.\s*GetMy([A-Z][A-Za-z0-9]*Handler)\s*\("
)
BOUND_GETTER_CALL = re.compile(
    r"(?:self\.)?Get([A-Z][A-Za-z0-9]*Handler)\s*\(\s*\)\s*"
    r"\.\s*([A-Za-z_][A-Za-z0-9_]*)\s*\("
)
BOUND_ATTRIBUTE_CALL = re.compile(
    r"(?:self\.)?([A-Za-z_][A-Za-z0-9_]*)\s*\.\s*"
    r"([A-Za-z_][A-Za-z0-9_]*)\s*\("
)
MONIKER_CONSTRUCTOR = re.compile(r"\bMoniker\s*\(")
NOTIFY_DECLARATION = re.compile(r"\b__notifyevents__\s*(?:=|\+=)\s*([\[(])")
SERVER_NOTIFY = re.compile(r"\bSendNotification\s*\(")
STRING_LITERAL = re.compile(r"(?i)(?:u|r|ur|ru)?(['\"])(.*?)(?<!\\)\1", re.S)
CLIENT_METHOD = re.compile(r"^\s*def\s+([A-Za-z_][A-Za-z0-9_]*)\s*\(", re.M)
SERVICE_ATTRIBUTE = re.compile(r'\[(?:[A-Za-z_][A-Za-z0-9_]*\.)*Service\(\s*(["\'])([^"\']+)\1[^)]*\)\]')
SERVICE_CLASS = re.compile(r"\b(?:class|record)\s+([A-Za-z_][A-Za-z0-9_]*)")
METHOD_ATTRIBUTE = re.compile(r'\[(?:[A-Za-z_][A-Za-z0-9_]*\.)*Method\(\s*(["\'])([^"\']+)\1\s*\)\]')
MANUAL_NAME = re.compile(r'\bName\s*=>\s*(["\'])([^"\']+)\1')
HAS_METHOD_EXPRESSION = re.compile(r"\bHasMethod\s*\([^)]*\)\s*=>\s*([^;]+);", re.S)
REGISTER_ALIAS = re.compile(r'\bRegisterAlias\(\s*(["\'])([^"\']+)\1\s*,\s*(["\'])([^"\']+)\3\s*\)')
ROUTE_TABLE_ENTRY = re.compile(r'\[\s*(["\'])([^"\']+)\1\s*\]\s*=\s*\[([^\]]*)\]')
METHOD_SWITCH = re.compile(r"\bmethod\s+switch\s*\{([\s\S]*?)\}")
SERVICE_CASE_METHODS = re.compile(r"\b([A-Za-z_][A-Za-z0-9_]*)\s*=>\s*method\s+is\s+([^,}]+)")
SERVICE_STRING_CASE_METHODS = re.compile(r"(['\"])([A-Za-z0-9_]+)\1\s*=>\s*method\s+is\s+([^,}]+)")
STRING_CONSTANT = re.compile(r'\bconst\s+string\s+([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(["\'])([^"\']+)\2')
CS_CLASS_DECLARATION = re.compile(r"\b(?:class|record)\s+([A-Za-z_][A-Za-z0-9_]*)")
CS_METHOD_DECLARATION = re.compile(
    r"\b(?:public|private|internal|protected)\s+(?:static\s+)?(?:async\s+)?"
    r"(?:Task(?:<[^>]+>)?|ValueTask(?:<[^>]+>)?|void)\s+([A-Za-z_][A-Za-z0-9_]*)\s*\("
)


def end_of_call(source: str, open_paren: int) -> int | None:
    """Return the index just after a balanced Python-like call expression."""
    depth = 0
    quote: str | None = None
    triple = False
    escaped = False
    comment = False
    for index in range(open_paren, len(source)):
        char = source[index]
        if comment:
            if char == "\n":
                comment = False
            continue
        if quote is not None:
            if escaped:
                escaped = False
            elif char == "\\":
                escaped = True
            elif triple and source.startswith(quote * 3, index):
                quote = None
                triple = False
            elif not triple and char == quote:
                quote = None
            continue
        if char == "#":
            comment = True
        elif char in "'\"":
            quote = char
            triple = source.startswith(char * 3, index)
        elif char == "(":
            depth += 1
        elif char == ")":
            depth -= 1
            if depth == 0:
                return index + 1
    return None


def split_arguments(source: str) -> list[str]:
    """Split a call's argument text on top-level commas."""
    parts: list[str] = []
    start = 0
    stack: list[str] = []
    quote: str | None = None
    triple = False
    escaped = False
    comment = False
    pairs = {")": "(", "]": "[", "}": "{"}
    for index, char in enumerate(source):
        if comment:
            if char == "\n":
                comment = False
            continue
        if quote is not None:
            if escaped:
                escaped = False
            elif char == "\\":
                escaped = True
            elif triple and source.startswith(quote * 3, index):
                quote = None
                triple = False
            elif not triple and char == quote:
                quote = None
            continue
        if char == "#":
            comment = True
        elif char in "'\"":
            quote = char
            triple = source.startswith(char * 3, index)
        elif char in "([{":
            stack.append(char)
        elif char in ")]}":
            if stack and stack[-1] == pairs[char]:
                stack.pop()
        elif char == "," and not stack:
            value = source[start:index].strip()
            if value:
                parts.append(value)
            start = index + 1
    value = source[start:].strip()
    if value:
        parts.append(value)
    return parts


def normalize(text: str) -> str:
    return re.sub(r"\s+", " ", text).strip()


def line_at(source: str, offset: int) -> int:
    return source.count("\n", 0, offset) + 1


def argument_text(source: str, open_paren: int) -> str | None:
    end = end_of_call(source, open_paren)
    if end is None:
        return None
    return source[open_paren + 1 : end - 1]


def csharp_block_end(source: str, opening: int) -> int | None:
    """Return the end of a C# brace block while skipping comments and literals."""
    depth = 0
    index = opening
    quote: str | None = None
    verbatim = False
    raw_quote_count = 0
    escaped = False
    line_comment = False
    block_comment = False
    while index < len(source):
        char = source[index]
        if line_comment:
            if char == "\n":
                line_comment = False
            index += 1
            continue
        if block_comment:
            if source.startswith("*/", index):
                block_comment = False
                index += 2
            else:
                index += 1
            continue
        if quote is not None:
            if raw_quote_count:
                closing = quote * raw_quote_count
                if source.startswith(closing, index):
                    quote = None
                    raw_quote_count = 0
                    index += len(closing)
                else:
                    index += 1
                continue
            if verbatim:
                if char == quote:
                    if index + 1 < len(source) and source[index + 1] == quote:
                        index += 2
                    else:
                        quote = None
                        verbatim = False
                        index += 1
                else:
                    index += 1
                continue
            if escaped:
                escaped = False
            elif char == "\\":
                escaped = True
            elif char == quote:
                quote = None
            index += 1
            continue
        if source.startswith("//", index):
            line_comment = True
            index += 2
        elif source.startswith("/*", index):
            block_comment = True
            index += 2
        elif char == "@" and index + 1 < len(source) and source[index + 1] == '"':
            quote = '"'
            verbatim = True
            index += 2
        elif char == '"':
            raw_quote_count = 0
            while index + raw_quote_count < len(source) and source[index + raw_quote_count] == '"':
                raw_quote_count += 1
            if raw_quote_count >= 3:
                quote = '"'
                index += raw_quote_count
            else:
                quote = '"'
                index += 1
        elif char in "'\"":
            quote = char
            index += 1
        elif char == "{":
            depth += 1
            index += 1
        elif char == "}":
            depth -= 1
            index += 1
            if depth == 0:
                return index
        else:
            index += 1
    return None


def host_registered_methods(root: pathlib.Path) -> set[tuple[str, str]]:
    """Collect declared service routes and manual IService routes from Host sources."""
    if not root.exists():
        return set()
    pairs: set[tuple[str, str]] = set()
    sources: list[str] = []
    service_types: dict[str, str] = {}
    aliases: list[tuple[str, str]] = []
    for path in sorted(root.rglob("*.cs")):
        try:
            source = path.read_text(errors="replace")
        except OSError:
            continue
        sources.append(source)
        aliases.extend((match.group(2), match.group(4)) for match in REGISTER_ALIAS.finditer(source))
        for route in ROUTE_TABLE_ENTRY.finditer(source):
            pairs.update((route.group(2), literal.group(2)) for literal in STRING_LITERAL.finditer(route.group(3)))
        attributes = list(SERVICE_ATTRIBUTE.finditer(source))
        for index, attribute in enumerate(attributes):
            end = attributes[index + 1].start() if index + 1 < len(attributes) else len(source)
            service = attribute.group(2)
            block = source[attribute.start() : end]
            pairs.update((service, method.group(2)) for method in METHOD_ATTRIBUTE.finditer(block))
            declaration = SERVICE_CLASS.search(source, attribute.end())
            if declaration is not None and declaration.start() - attribute.end() < 1_000:
                service_types[declaration.group(1)] = attribute.group(2)

    for source in sources:
        class_declarations = list(CS_CLASS_DECLARATION.finditer(source))
        for method in METHOD_ATTRIBUTE.finditer(source):
            owner = next((item for item in reversed(class_declarations) if item.start() < method.start()), None)
            if owner is not None and owner.group(1) in service_types:
                pairs.add((service_types[owner.group(1)], method.group(2)))

        for manual_name in MANUAL_NAME.finditer(source):
            owner = next((item for item in reversed(class_declarations) if item.start() < manual_name.start()), None)
            if owner is None:
                continue
            following = next((item for item in class_declarations if item.start() > manual_name.start()), None)
            block = source[owner.start() : following.start() if following is not None else len(source)]
            service = manual_name.group(2)
            for expression in HAS_METHOD_EXPRESSION.finditer(block):
                value = expression.group(1)
                pairs.update((service, literal.group(2)) for literal in STRING_LITERAL.finditer(value))
                fields = re.findall(r"\b([A-Za-z_][A-Za-z0-9_]*)\s*\.Contains\s*\(", value)
                for field in fields:
                    declaration_pattern = re.compile(
                        rf"\b{re.escape(field)}\s*=\s*(?:new(?:\([^)]*\))?\s*)?[\[\(\{{]([\s\S]*?)[\]\)\}}]"
                    )
                    array = declaration_pattern.search(block)
                    if array is not None:
                        pairs.update((service, literal.group(2)) for literal in STRING_LITERAL.finditer(array.group(1)))
            for switch in METHOD_SWITCH.finditer(block):
                pairs.update((service, literal.group(2)) for literal in STRING_LITERAL.finditer(switch.group(1)))

        for owner in class_declarations:
            following = next((item for item in class_declarations if item.start() > owner.start()), None)
            block = source[owner.start() : following.start() if following is not None else len(source)]
            constants = {match.group(1): match.group(3) for match in STRING_CONSTANT.finditer(block)}
            for route in SERVICE_CASE_METHODS.finditer(block):
                service = constants.get(route.group(1))
                if service is not None:
                    pairs.update((service, literal.group(2)) for literal in STRING_LITERAL.finditer(route.group(2)))
            for route in SERVICE_STRING_CASE_METHODS.finditer(block):
                pairs.update((route.group(2), literal.group(2)) for literal in STRING_LITERAL.finditer(route.group(3)))
    for alias, target in aliases:
        pairs.update((alias, method) for service, method in tuple(pairs) if service == target)
    return pairs


def scopes_by_line(source: str) -> list[tuple[str, str]]:
    scopes: list[tuple[int, str, str]] = []
    result: list[tuple[str, str]] = []
    for line in source.splitlines():
        stripped = line.lstrip()
        indent = len(line) - len(stripped)
        declaration = re.match(r"(?:class|def)\s+([A-Za-z_][A-Za-z0-9_]*)", stripped)
        if declaration is not None:
            while scopes and indent <= scopes[-1][0]:
                scopes.pop()
            kind = "class" if stripped.startswith("class ") else "def"
            scopes.append((indent, kind, declaration.group(1)))
        elif stripped and not stripped.startswith("#"):
            while scopes and indent <= scopes[-1][0]:
                scopes.pop()
        class_name = ".".join(name for _, kind, name in scopes if kind == "class")
        function_name = next((name for _, kind, name in reversed(scopes) if kind == "def"), "")
        result.append((class_name, function_name))
    return result


def assigned_name(source: str, match_start: int) -> tuple[str, bool] | None:
    line_start = source.rfind("\n", 0, match_start) + 1
    before = source[line_start:match_start]
    match = re.search(r"(?:(self)\.)?([A-Za-z_][A-Za-z0-9_]*)\s*=\s*[^#]*$", before)
    if match is None:
        return None
    return match.group(2), match.group(1) == "self"


def direct_member_call(source: str, expression_end: int) -> tuple[str, int, str] | None:
    cursor = expression_end
    while cursor < len(source) and source[cursor].isspace():
        cursor += 1
    match = re.match(r"\.\s*([A-Za-z_][A-Za-z0-9_]*)\s*\(", source[cursor:])
    if match is None:
        return None
    return match.group(1), cursor + match.end() - 1, source[cursor : cursor + match.end()]


def moniker_return_map(path: pathlib.Path, source: str) -> dict[str, set[str]]:
    scopes = scopes_by_line(source)
    local: dict[tuple[str, str, str], str] = {}
    class_attrs: dict[tuple[str, str], str] = {}
    returned: dict[str, set[str]] = collections.defaultdict(set)
    lines = source.splitlines()

    def record_return(scope: tuple[str, str], service: str) -> None:
        class_name, function_name = scope
        if function_name:
            key = f"{class_name}.{function_name}" if class_name else function_name
            returned[key].add(service)
            returned[function_name].add(service)

    for match in MONIKER_GETTER.finditer(source):
        suffix = match.group(2)
        service = suffix[0].lower() + suffix[1:]
        record_return(scopes[line_at(source, match.start()) - 1], service)

    for match in MONIKER_CONSTRUCTOR.finditer(source):
        raw_args = argument_text(source, match.end() - 1)
        if raw_args is None:
            continue
        args = split_arguments(raw_args)
        literal = re.fullmatch(r"(['\"])([A-Za-z0-9_]+)\1", args[0].strip()) if args else None
        if literal is None:
            continue
        service = literal.group(2)
        scope = scopes[line_at(source, match.start()) - 1]
        binding = assigned_name(source, match.start())
        if binding is not None:
            name, is_self = binding
            if is_self and scope[0]:
                class_attrs[(scope[0], name)] = service
            else:
                local[(scope[0], scope[1], name)] = service
        line_prefix = lines[line_at(source, match.start()) - 1][: (match.start() - (source.rfind("\n", 0, match.start()) + 1))]
        if re.search(r"\breturn\s*$", line_prefix):
            record_return(scope, service)

    for line_number, line in enumerate(lines, start=1):
        scope = scopes[line_number - 1]
        returned_name = re.match(r"\s*return\s+((?:self\.)?[A-Za-z_][A-Za-z0-9_]*)\s*(?:#.*)?$", line)
        if returned_name is None:
            continue
        name = returned_name.group(1)
        if name.startswith("self."):
            service = class_attrs.get((scope[0], name[5:]))
        else:
            service = local.get((scope[0], scope[1], name))
        if service is not None:
            record_return(scope, service)
    return returned


def source_calls(path: pathlib.Path, source: str, module_getters: dict[str, set[str]]) -> list[dict[str, Any]]:
    found: dict[tuple[int, str, str], dict[str, Any]] = {}
    scopes = scopes_by_line(source)

    def add(offset: int, service: str, method: str, args: list[str] | None, via: str, kind: str) -> None:
        key = (offset, service, method)
        found[key] = {
            "service": service,
            "method": method,
            "arguments": args,
            "via": via,
            "file": path.as_posix(),
            "line": line_at(source, offset),
            "kind": kind,
        }

    for match in REMOTE_REFERENCE.finditer(source):
        method = match.group(3)
        if not method:
            continue
        following = match.end()
        while following < len(source) and source[following].isspace():
            following += 1
        raw_args = argument_text(source, following) if following < len(source) and source[following] == "(" else None
        add(match.start(), match.group(2), method, split_arguments(raw_args) if raw_args is not None else None,
            "RemoteSvc", "call" if raw_args is not None else "method-reference")

    for pattern, via in ((REMOTE_CALL, "RemoteSvc"), (CONNECTED_CALL, "Connect")):
        for match in pattern.finditer(source):
            raw_args = argument_text(source, match.end() - 1)
            if raw_args is not None:
                add(match.start(), match.group(2), match.group(3), split_arguments(raw_args), via, "call")

    local_services: dict[tuple[str, str, str], str] = {}
    class_attrs: dict[tuple[str, str], str] = {}
    getter_services: dict[tuple[str, str], set[str]] = collections.defaultdict(set)
    for name, services in module_getters.items():
        if "." not in name:
            getter_services[("", name)].update(services)

    for match in MONIKER_GETTER.finditer(source):
        suffix = match.group(2)
        service = suffix[0].lower() + suffix[1:]
        class_name, function_name = scopes[line_at(source, match.start()) - 1]
        getter_services[(class_name, function_name)].add(service)
        binding = assigned_name(source, match.start())
        if binding is not None:
            attr, is_self = binding
            if is_self and class_name:
                class_attrs[(class_name, attr)] = service
            elif function_name:
                local_services[(class_name, function_name, attr)] = service

    for match in MONIKER_CONSTRUCTOR.finditer(source):
        raw_args = argument_text(source, match.end() - 1)
        if raw_args is None:
            continue
        args = split_arguments(raw_args)
        literal = re.fullmatch(r"(['\"])([A-Za-z0-9_]+)\1", args[0].strip()) if args else None
        if literal is None:
            continue
        service = literal.group(2)
        class_name, function_name = scopes[line_at(source, match.start()) - 1]
        binding = assigned_name(source, match.start())
        if binding is not None:
            name, is_self = binding
            if is_self and class_name:
                class_attrs[(class_name, name)] = service
            elif function_name:
                local_services[(class_name, function_name, name)] = service
        call_end = end_of_call(source, match.end() - 1)
        if call_end is not None:
            member = direct_member_call(source, call_end)
            if member is not None:
                method, method_open, _ = member
                if method != "SetSessionCheck":
                    method_args = argument_text(source, method_open)
                    if method_args is not None:
                        add(match.start(), service, method, split_arguments(method_args), "Moniker", "bound-object-call")

    for match in BOUND_GETTER_CALL.finditer(source):
        class_name, _ = scopes[line_at(source, match.start()) - 1]
        service_names = getter_services.get((class_name, "Get" + match.group(1))) or getter_services.get(("", "Get" + match.group(1)))
        if not service_names:
            continue
        raw_args = argument_text(source, match.end() - 1)
        if raw_args is None:
            continue
        for service in service_names:
            add(match.start(), service, match.group(2), split_arguments(raw_args), "Moniker", "bound-object-call")

    if class_attrs or local_services:
        for match in BOUND_ATTRIBUTE_CALL.finditer(source):
            class_name, function_name = scopes[line_at(source, match.start()) - 1]
            service = class_attrs.get((class_name, match.group(1)))
            if service is None:
                service = local_services.get((class_name, function_name, match.group(1)))
            if service is None or match.group(2) == "SetSessionCheck":
                continue
            raw_args = argument_text(source, match.end() - 1)
            if raw_args is not None:
                add(match.start(), service, match.group(2), split_arguments(raw_args), "Moniker", "bound-object-call")

    helper_names = {name.rsplit(".", 1)[-1] for name in module_getters}
    helper_names.update(name for _, name in getter_services)
    if helper_names:
        helper_pattern = re.compile(
            r"(?:self\.)?("
            + "|".join(re.escape(name) for name in sorted(helper_names, key=lambda item: (-len(item), item)))
            + r")\s*\("
        )
        for match in helper_pattern.finditer(source):
            getter = match.group(1)
            getter_end = end_of_call(source, match.end() - 1)
            if getter_end is None:
                continue
            member = direct_member_call(source, getter_end)
            if member is None:
                continue
            method, method_open, _ = member
            class_name, _ = scopes[line_at(source, match.start()) - 1]
            service_names = getter_services.get((class_name, getter)) or getter_services.get(("", getter)) or module_getters.get(getter)
            if not service_names:
                continue
            method_args = argument_text(source, method_open)
            if method_args is not None:
                for service in service_names:
                    add(match.start(), service, method, split_arguments(method_args), "Moniker", "bound-object-call")
    return list(found.values())


def declared_notifications(path: pathlib.Path, source: str) -> list[dict[str, Any]]:
    found: list[dict[str, Any]] = []
    for match in NOTIFY_DECLARATION.finditer(source):
        opening = match.start(1)
        opening_char = source[opening]
        closing_char = "]" if opening_char == "[" else ")"
        depth = 0
        quote: str | None = None
        escaped = False
        end = None
        for index in range(opening, len(source)):
            char = source[index]
            if quote is not None:
                if escaped:
                    escaped = False
                elif char == "\\":
                    escaped = True
                elif char == quote:
                    quote = None
                continue
            if char in "'\"":
                quote = char
            elif char == opening_char:
                depth += 1
            elif char == closing_char:
                depth -= 1
                if depth == 0:
                    end = index + 1
                    break
        if end is None:
            continue
        body = source[opening + 1 : end - 1]
        for literal in STRING_LITERAL.finditer(body):
            name = literal.group(2)
            if re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]*", name):
                found.append(
                    {
                        "name": name,
                        "file": path.as_posix(),
                        "line": line_at(source, opening + 1 + literal.start()),
                    }
                )
    return found


def server_notifications(root: pathlib.Path) -> list[dict[str, Any]]:
    if not root.exists():
        return []
    found: list[dict[str, Any]] = []
    for path in sorted(root.rglob("*.cs")):
        try:
            source = path.read_text(errors="replace")
        except OSError:
            continue
        for match in SERVER_NOTIFY.finditer(source):
            raw_args = argument_text(source, match.end() - 1)
            if raw_args is None:
                continue
            args = split_arguments(raw_args)
            if len(args) < 2:
                continue
            name_match = re.fullmatch(r"\"([^\"]+)\"", args[0].strip())
            if name_match is None:
                continue
            found.append(
                {
                    "name": name_match.group(1),
                    "idType": normalize(args[1]),
                    "arguments": args[2:],
                    "file": path.relative_to(root).as_posix(),
                    "line": line_at(source, match.start()),
                }
            )
    return found


def collect(client_root: pathlib.Path, server_root: pathlib.Path) -> dict[str, Any]:
    calls: list[dict[str, Any]] = []
    notifications: list[dict[str, Any]] = []
    connect_services: set[str] = set()
    client_methods: dict[str, list[dict[str, Any]]] = collections.defaultdict(list)
    module_getters: dict[str, set[str]] = collections.defaultdict(set)
    files = 0
    for path in sorted(client_root.rglob("*.py")):
        try:
            source = path.read_text(errors="replace")
        except OSError:
            continue
        files += 1
        notifications.extend(declared_notifications(path.relative_to(client_root), source))
        connect_services.update(match.group(2) for match in CONNECT_SERVICE.finditer(source))
        for getter, services in moniker_return_map(path.relative_to(client_root), source).items():
            module_getters[getter].update(services)
        for match in CLIENT_METHOD.finditer(source):
            client_methods[match.group(1)].append(
                {"file": path.relative_to(client_root).as_posix(), "line": line_at(source, match.start())}
            )

    for path in sorted(client_root.rglob("*.py")):
        try:
            source = path.read_text(errors="replace")
        except OSError:
            continue
        calls.extend(source_calls(path.relative_to(client_root), source, module_getters))

    pairs: dict[tuple[str, str], dict[str, Any]] = {}
    for call in calls:
        pair = (call["service"], call["method"])
        item = pairs.setdefault(pair, {"service": pair[0], "method": pair[1], "count": 0, "signatures": {}, "sites": []})
        item["count"] += 1
        item["sites"].append({"file": call["file"], "line": call["line"], "kind": call["kind"], "via": call["via"]})
        signature = None if call["arguments"] is None else ", ".join(call["arguments"])
        details = item["signatures"].setdefault(signature, {"count": 0, "sites": []})
        details["count"] += 1
        details["sites"].append({"file": call["file"], "line": call["line"], "via": call["via"], "kind": call["kind"]})

    for item in pairs.values():
        item["signatures"] = [
            {"arguments": signature, "count": details["count"], "sites": details["sites"]}
            for signature, details in sorted(item["signatures"].items(), key=lambda pair: "" if pair[0] is None else pair[0])
        ]

    unique_events: dict[str, list[dict[str, Any]]] = collections.defaultdict(list)
    for notification in notifications:
        unique_events[notification["name"]].append({"file": notification["file"], "line": notification["line"]})
    emits = server_notifications(server_root)
    host_pairs = host_registered_methods(server_root)
    emit_names = {event["name"] for event in emits}
    handler_names = set(client_methods)
    declared_names = set(unique_events)
    return {
        "client_root": str(client_root),
        "client_python_files": files,
        "service_method_pairs": [pairs[key] for key in sorted(pairs)],
        "callsite_count": sum(call["kind"] == "call" for call in calls),
        "method_reference_count": sum(call["kind"] == "method-reference" for call in calls),
        "bound_object_callsite_count": sum(call["kind"] == "bound-object-call" for call in calls),
        "connected_service_names": sorted(connect_services),
        "client_notifyevents_count": len(notifications),
        "client_notifyevents": [{"name": name, "sites": unique_events[name]} for name in sorted(unique_events)],
        "client_notification_handlers": {
            name: client_methods.get(name, []) for name in sorted(unique_events)
        },
        "host_registered_methods": [
            {"service": service, "method": method} for service, method in sorted(host_pairs)
        ],
        "client_calls_without_host_route": [
            {"service": service, "method": method}
            for service, method in sorted(set(pairs) - host_pairs)
        ],
        "host_literal_notifications": emits,
        "host_notifications_without_client_method": sorted(emit_names - handler_names),
        "host_notifications_without_client_subscription": sorted(emit_names - declared_names),
        "connected_calls": [call for call in calls if call["via"] == "Connect"],
        "client_connected_calls": [
            call for call in calls if call["via"] == "Connect" and call["file"].startswith(("eve/client/", "carbon/client/"))
        ],
    }


def read_manifest(path: pathlib.Path) -> set[tuple[str, str]]:
    import csv

    pairs: set[tuple[str, str]] = set()
    with path.open(newline="", encoding="utf-8") as stream:
        for row in csv.DictReader(stream, delimiter="\t"):
            service, method = row.get("service", ""), row.get("method", "")
            if service and method:
                pairs.add((service, method))
    return pairs


def summary(data: dict[str, Any], manifest: set[tuple[str, str]] | None) -> str:
    client_pairs = {(item["service"], item["method"]) for item in data["service_method_pairs"]}
    host_names = {item["name"] for item in data["host_literal_notifications"]}
    client_names = {item["name"] for item in data["client_notifyevents"]}
    lines = [
        f"Client Python files: {data['client_python_files']}",
        f"Direct RPC callsites: {data['callsite_count']}",
        f"Remote method references without direct arguments: {data['method_reference_count']}",
        f"Distinct source service/method pairs: {len(client_pairs)}",
        f"Connected service names: {len(data['connected_service_names'])}",
        f"Direct ConnectTo* calls in client-specific source trees: {len(data['client_connected_calls'])}",
        f"Bound-object callsites found through *Handler monikers: {data['bound_object_callsite_count']}",
        f"Declared client notification subscriptions: {data['client_notifyevents_count']} occurrences / {len(client_names)} names",
        f"Host literal notification calls: {len(data['host_literal_notifications'])} occurrences / {len(host_names)} names",
        f"Host registered methods found in source: {len(data['host_registered_methods'])}",
        f"Client service/method pairs without a parsed Host route: {len(data['client_calls_without_host_route'])}",
        f"Names shared by client __notifyevents__ and Host SendNotification: {len(client_names & host_names)}",
        f"Host notification names without a matching client method definition: {len(data['host_notifications_without_client_method'])}",
        f"Host notification names without a __notifyevents__ declaration: {len(data['host_notifications_without_client_subscription'])}",
    ]
    if manifest is not None:
        lines.extend(
            [
                f"Manifest service/method pairs: {len(manifest)}",
                f"Client pairs absent from manifest: {len(client_pairs - manifest)}",
                f"Manifest pairs absent from direct callsites: {len(manifest - client_pairs)}",
                f"Manifest pairs absent from parsed Host methods: {len(manifest - {(item['service'], item['method']) for item in data['host_registered_methods']})}",
            ]
        )
        if client_pairs - manifest:
            lines.append("Client-only: " + ", ".join(f"{s}.{m}" for s, m in sorted(client_pairs - manifest)))
        if manifest - client_pairs:
            lines.append("Manifest-only: " + ", ".join(f"{s}.{m}" for s, m in sorted(manifest - client_pairs)))
        client_sites = {(item["service"], item["method"]): item["sites"] for item in data["service_method_pairs"]}
        if client_pairs - manifest:
            for service, method in sorted(client_pairs - manifest):
                sites = ", ".join(f"{site['file']}:{site['line']}" for site in client_sites[(service, method)][0:4])
                lines.append(f"  {service}.{method}: {sites}")
        if data["host_notifications_without_client_method"]:
            lines.append("Host notifications without client method:")
            for event in data["host_literal_notifications"]:
                if event["name"] in data["host_notifications_without_client_method"]:
                    lines.append(f"  {event['name']}: {event['file']}:{event['line']}")
        if data["client_connected_calls"]:
            lines.append("Client-specific ConnectTo* methods:")
            for call in data["client_connected_calls"]:
                lines.append(f"  {call['service']}.{call['method']}({', '.join(call['arguments'] or [])}): {call['file']}:{call['line']}")
    return "\n".join(lines) + "\n"


def markdown_cell(value: str) -> str:
    return value.replace("|", "\\|").replace("\n", "<br>")


ROOT_CAUSES: dict[str, tuple[str, str]] = {
    "infrastructure": (
        "I1",
        "Carbon or cluster infrastructure call. Host implements the player-facing login/session and MachoNet surface it needs, but does not expose arbitrary DB2 procedures, cache rowsets, external queue publication, node ACL/address/code-reload operations, or the legacy i2 resolver as game RPCs. The callsites are in carbon/common or devtools source, outside eve/client gameplay code.",
    ),
    "character_inventory": (
        "G1",
        "CharMgrService exposes character metadata, asset reads, notes, contacts, and character settings; it has no Add, MultiAdd, or station-item-cache invalidation handler. These calls require a server-side inventory mutation/cache-invalidation path with item change notifications. That path is not wired to this bound charMgr service.",
    ),
    "crimewatch_tags": (
        "G2",
        "CrimewatchService covers bound state, safety, duels, and security-status reads. It has no tag exchange operation. Applying tags needs one transaction spanning tag inventory consumption and the authoritative security-status relation; no such callback is wired to this service.",
    ),
    "planet_pin": (
        "G3",
        "PlanetMgrService stores and projects a known planet's pin network, but exposes no single-pin lookup by pin item ID. The current service has no reverse pin-ID-to-planet query used by GetPin.",
    ),
    "pos_state": (
        "G4",
        "PosMgrService exposes only GetControlTowers and UnanchorStructure. The tower settings, moon-process, silo, and orbital operations in these calls have no authoritative POS runtime/state provider wired to the service, so a reply or mutation cannot be derived from current Host state.",
    ),
    "ship_state": (
        "G5",
        "ShipService has no handlers for these calls. It has no persisted ship-configuration or dirt-timestamp record and no provider implementing the requested container launch/drop transaction. Returning default values would lose ship settings or report a mutation that did not occur.",
    ),
    "shipcaster_state": (
        "G6",
        "Host registers all four Shipcaster services, but production construction supplies no landing-pad, faction, or starter-eligibility state provider. The service now reports a CustomNotify error when one of those reads has no provider instead of presenting an empty catalogue or a false eligibility answer. EveJS also deliberately rejects travel while no destination is available; an authoritative Shipcaster catalogue and starter eligibility policy remain absent from Host wiring.",
    ),
    "structure_travel": (
        "G7",
        "Host wires single-character CmdJumpToStructureBeacon through StructureBeaconTravel and the SpaceSessions jump callback, and wires jump-bridge destination/link reads through the persisted structure directory. It explicitly refuses group jumps and portal bridging to cynosural beacons, and production registers structureJumpBridgeMgr without a CmdJumpThroughStructureStargate travel provider. EveJS implements those group, portal-bridge, and Ansiblex travel transitions; these routes need fleet notifications, portal activation/fuel, access/range checks, and authoritative session settlement.",
    ),
    "structure_fuel": (
        "G8",
        "StructureRpcServices does not expose JettisonStructureFuel, and the structure inventory/fuel transaction is not wired to this route. A success reply without an inventory mutation and item notifications would be false success.",
    ),
    "developer_tools": (
        "D1",
        "These calls come only from eve/devtools authoring or tournament-referee tools. Host has no corresponding privileged content-authoring or tournament-referee service/state; they are outside the player login, station, and space flows audited here.",
    ),
    "unclassified": (
        "R0",
        "The source comparison found no registered Host route, but this call did not match a documented root-cause category. It requires a source-specific investigation before the contract can be closed.",
    ),
}


def missing_root_cause(service: str, method: str) -> str:
    if service in {"DB2", "authentication", "cache", "externalQueueMgr", "i2", "machoNet", "userSvc"}:
        return "infrastructure"
    if service == "charMgr":
        return "character_inventory"
    if service == "crimewatch":
        return "crimewatch_tags"
    if service == "planetMgr" and method == "GetPin":
        return "planet_pin"
    if service == "posMgr":
        return "pos_state"
    if service == "ship":
        return "ship_state"
    if service in {"shipCasterLandingPadMgr", "shipCasterLauncherMgr", "shipcasterTravelMgr", "starterShipcasterTravelMgr"}:
        return "shipcaster_state"
    if service in {"structureCynoBeaconMgr", "structureJumpBridgeMgr"}:
        return "structure_travel"
    if service == "structure" and method == "JettisonStructureFuel":
        return "structure_fuel"
    if service in {"abyssal_content_manager", "tourneyMgr"}:
        return "developer_tools"
    return "unclassified"


REGISTERED_BEHAVIOR_GAPS: dict[tuple[str, str], str] = {
    ("shipCasterLandingPadMgr", "GetAllLandingPads"): "shipcaster_state",
    ("shipCasterLandingPadMgr", "GetFactionLandingPads"): "shipcaster_state",
    ("shipCasterLauncherMgr", "GetFactionsWithShipcaster"): "shipcaster_state",
    ("starterShipcasterTravelMgr", "CanCharacterJump"): "shipcaster_state",
    ("structureCynoBeaconMgr", "CmdBridgeToStructureBeacon"): "structure_travel",
    ("structureCynoBeaconMgr", "CmdGroupJumpToStructureBeacon"): "structure_travel",
    ("structureJumpBridgeMgr", "CmdJumpThroughStructureStargate"): "structure_travel",
}

REFERENCE_UNAVAILABLE_ROUTES: set[tuple[str, str]] = {
    ("shipcasterTravelMgr", "CmdJumpThroughShipcaster"),
    ("starterShipcasterTravelMgr", "CmdJumpThroughStarterShipcaster"),
}


def csharp_service_type(service: str) -> str:
    return "".join(piece[:1].upper() + piece[1:] for piece in re.split(r"[^A-Za-z0-9]+", service) if piece) + "Service"


def test_references(root: pathlib.Path, pairs: set[tuple[str, str]], events: set[str]) -> tuple[dict[tuple[str, str], list[str]], dict[str, list[str]]]:
    test_roots = [root / "dotnet" / "tests", root / "tools" / "headless-client" / "tests"]
    pair_refs: dict[tuple[str, str], list[str]] = collections.defaultdict(list)
    event_refs: dict[str, list[str]] = collections.defaultdict(list)
    pairs_by_method: dict[str, list[tuple[str, str]]] = collections.defaultdict(list)
    for pair in pairs:
        pairs_by_method[pair[1]].append(pair)
    service_aliases = {service: csharp_service_type(service) for service, _ in pairs}
    vocabulary = {method for _, method in pairs} | events | set(service_aliases) | set(service_aliases.values())
    identifier = re.compile(r"[A-Za-z_][A-Za-z0-9_]*")
    for test_root in test_roots:
        if not test_root.exists():
            continue
        for path in sorted(test_root.rglob("*")):
            if path.suffix not in {".cs", ".py"} or any(part in {"bin", "obj"} for part in path.parts):
                continue
            try:
                source = path.read_text(errors="replace")
            except OSError:
                continue
            relative = path.relative_to(root).as_posix()
            found_lines: dict[str, list[int]] = collections.defaultdict(list)
            for line_number, line in enumerate(source.splitlines(), start=1):
                seen: set[str] = set()
                for match in identifier.finditer(line):
                    name = match.group(0)
                    if name in vocabulary:
                        seen.add(name)
                for name in seen:
                    found_lines[name].append(line_number)
            for method, locations in found_lines.items():
                for service, pair_method in pairs_by_method.get(method, []):
                    if service not in found_lines and service_aliases[service] not in found_lines:
                        continue
                    pair_refs[(service, pair_method)].append(
                        f"{relative}:{','.join(map(str, locations[:3]))}"
                    )
            for name in events.intersection(found_lines):
                event_refs[name].append(f"{relative}:{found_lines[name][0]}")
    return pair_refs, event_refs


def markdown(data: dict[str, Any], manifest: set[tuple[str, str]] | None, root: pathlib.Path) -> str:
    client_pairs = {(item["service"], item["method"]): item for item in data["service_method_pairs"]}
    host_pairs = {(item["service"], item["method"]) for item in data["host_registered_methods"]}
    event_names = {item["name"] for item in data["client_notifyevents"]}
    pair_tests, event_tests = test_references(root, set(client_pairs), event_names)
    emitted: dict[str, list[dict[str, Any]]] = collections.defaultdict(list)
    for notification in data["host_literal_notifications"]:
        emitted[notification["name"]].append(notification)

    lines = [
        "# V24.01 Client Contract Audit",
        "",
        "Generated from the V24.01 client source tree and the current Host service sources by `tools/spec-extract/client_contract.py`.",
        "The client-call rows retain each distinct argument signature, occurrence count, and source location; identical calls are aggregated only when service, method, and argument text match.",
        "Host route presence is compared against service attributes, aliases, explicit route tables, manually dispatched `IService` method sets, and service-name switch routes. Test references are source matches and are not a claim that those tests passed.",
        "",
        "## Extraction totals",
        "",
        f"- Client Python files scanned: {data['client_python_files']}",
        f"- Invoked RPC and bound-object callsites: {data['callsite_count'] + data['bound_object_callsite_count']}",
        f"- Method references without a direct invocation: {data['method_reference_count']}",
        f"- Distinct service/method pairs: {len(client_pairs)}",
        f"- Client notification subscription occurrences/names: {data['client_notifyevents_count']}/{len(event_names)}",
        f"- Host literal notification sends: {len(data['host_literal_notifications'])} occurrences / {len(emitted)} names",
        f"- Client pairs without a parsed Host route: {len(data['client_calls_without_host_route'])}",
        "",
        "## Calls",
        "",
        "`Status` reports route presence or a root-cause record. Route presence alone does not prove the reply shape; the handler/test source is linked in the final column when the static source scan found a likely test reference.",
        "",
        "| Call (arguments and client sites) | Status | Test source |",
        "|---|---|---|",
    ]
    for key, item in sorted(client_pairs.items()):
        service, method = key
        call_forms = []
        for signature in item["signatures"]:
            args = signature["arguments"]
            label = f"{service}.{method} (method reference; arguments not captured)" if args is None else f"{service}.{method}({args})"
            sites = ", ".join(f"{site['file']}:{site['line']}" for site in signature["sites"])
            call_forms.append(f"`{label}` ×{signature['count']} ({sites})")
        if key in REGISTERED_BEHAVIOR_GAPS:
            cause = REGISTERED_BEHAVIOR_GAPS[key]
            status = f"Host route registered; behavior gap, see {ROOT_CAUSES[cause][0]}"
        elif key in REFERENCE_UNAVAILABLE_ROUTES:
            status = "Host unavailable CustomNotify matches EveJS"
        elif key in host_pairs:
            status = "Host route registered"
        else:
            cause = missing_root_cause(service, method)
            status = f"No Host route; see {ROOT_CAUSES[cause][0]}"
        tests = pair_tests.get(key, [])
        test_value = "<br>".join(tests) if tests else "No source test match found"
        lines.append(f"| {markdown_cell('<br>'.join(call_forms))} | {status} | {markdown_cell(test_value)} |")

    lines.extend([
        "",
        "## Client notifications",
        "",
        "Each row is a unique name declared in a client `__notifyevents__` list. The handler column identifies same-name Python method definitions. Host sends list literal `SendNotification` names found in C#; a blank result means no literal send was found by the source scan.",
        "",
        "| Event | Client subscription sites | Handler definitions | Host literal sends | Test source |",
        "|---|---|---:|---|---|",
    ])
    for event in data["client_notifyevents"]:
        name = event["name"]
        sites = ", ".join(f"{site['file']}:{site['line']}" for site in event["sites"])
        host_sends = emitted.get(name, [])
        host_value = "<br>".join(
            f"{item['idType']} ({', '.join(item['arguments'])}) at {item['file']}:{item['line']}"
            for item in host_sends
        ) if host_sends else "No literal send found"
        handler_sites = data["client_notification_handlers"].get(name, [])
        handler_value = ", ".join(f"{site['file']}:{site['line']}" for site in handler_sites) if handler_sites else "No same-name def found"
        tests = event_tests.get(name, [])
        lines.append(
            f"| `{name}` | {markdown_cell(sites)} | {len(handler_sites)}: {markdown_cell(handler_value)} | {markdown_cell(host_value)} | {markdown_cell('<br>'.join(tests) if tests else 'No source test match found')} |"
        )

    host_only = sorted(set(emitted) - event_names)
    if host_only:
        lines.extend([
            "",
            "## Host literal sends without a client subscription name",
            "",
            "| Event | Send sites and argument expressions |",
            "|---|---|",
        ])
        for name in host_only:
            calls = emitted[name]
            details = "<br>".join(
                f"{item['file']}:{item['line']} id={item['idType']} args=({', '.join(item['arguments'])})"
                for item in calls
            )
            lines.append(f"| `{name}` | {markdown_cell(details)} |")

    missing = data["client_calls_without_host_route"]
    if missing or REGISTERED_BEHAVIOR_GAPS:
        lines.extend([
            "",
            "## Root causes for missing routes and registered behavior gaps",
            "",
            "| ID | Calls | Root cause |",
            "|---|---|---|",
        ])
        missing_by_cause: dict[str, list[str]] = collections.defaultdict(list)
        for item in missing:
            cause = missing_root_cause(item["service"], item["method"])
            missing_by_cause[cause].append(f"`{item['service']}.{item['method']}`")
        for (service, method), cause in REGISTERED_BEHAVIOR_GAPS.items():
            missing_by_cause[cause].append(f"`{service}.{method}` (registered behavior gap)")
        for cause in sorted(missing_by_cause, key=lambda key: ROOT_CAUSES[key][0]):
            cause_id, reason = ROOT_CAUSES[cause]
            lines.append(f"| {cause_id} | {', '.join(missing_by_cause[cause])} | {reason} |")

    lines.extend([
        "",
        "## Response-shape review",
        "",
        "The route table is a source-level registration comparison, not a packet-level result comparison. Focused source tests cover the populationCap character-entry map, the warsInfoMgr rowset/object-caching forms, lookupSvc location key-value rows, Shipcaster landing-pad ObjectEx/list rows, and jump-bridge lookup shapes. This audit records each route's test source in the Calls table; only tests listed in the validation section below were executed here. Remaining routes without a shape-specific test reference still need per-handler review or an executable client trace.",
        "",
        "## Validation recorded by this audit",
        "",
        "- The extractor unit tests exercise qualified `[Eve.Services.Service]`/`[Eve.Services.Method]` routes and manual `IService` method sets.",
        "- A passing extractor unit test does not constitute Host dispatch or graphical-client verification.",
        "- The `Test source` column lists matches in test files; it does not report execution state.",
        "",
    ])
    return "\n".join(lines)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("client_root", type=pathlib.Path)
    parser.add_argument("--server-root", type=pathlib.Path, default=pathlib.Path("dotnet/src"))
    parser.add_argument("--manifest", type=pathlib.Path)
    parser.add_argument("--format", choices=("json", "summary", "markdown"), default="summary")
    args = parser.parse_args()
    if not args.client_root.is_dir():
        parser.error(f"client source tree does not exist: {args.client_root}")
    data = collect(args.client_root.resolve(), args.server_root.resolve())
    if args.format == "json":
        json.dump(data, sys.stdout, indent=2, sort_keys=True)
        sys.stdout.write("\n")
    elif args.format == "markdown":
        manifest = read_manifest(args.manifest) if args.manifest else None
        sys.stdout.write(markdown(data, manifest, args.server_root.resolve().parent.parent))
    else:
        manifest = read_manifest(args.manifest) if args.manifest else None
        sys.stdout.write(summary(data, manifest))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

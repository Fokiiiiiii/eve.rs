#!/usr/bin/env python3
"""Run the selected agent test set on a prepared CI runner."""
from __future__ import annotations

import os
from pathlib import Path
import re
import shlex
import shutil
import subprocess
import sys
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
LOGS = ROOT / ".agent-test-logs"
FAST_LIVE_FILES = [
    "test_live.py", "test_login_to_character.py", "test_undock.py", "test_movement.py",
    "test_warp.py", "test_jump.py", "test_others.py", "test_target.py", "test_module.py",
    "test_health.py", "test_market.py", "test_structures.py",
]
SAFE_TEST_FILE = re.compile(r"test_[A-Za-z0-9_]+\.py\Z")


def projects_for(value: str) -> list[Path]:
    available = {path.stem: path for path in sorted((ROOT / "dotnet/tests").glob("*/*.csproj"))}
    selected = [part.strip() for part in value.split(",") if part.strip()]
    if not selected or selected == ["none"]:
        return []
    if selected == ["all"]:
        return list(available.values())
    paths = []
    for item in selected:
        name = Path(item).name
        if name.endswith(".csproj"):
            name = name[:-7]
        if name not in available:
            raise ValueError(f"unknown .NET test project: {item}")
        if available[name] not in paths:
            paths.append(available[name])
    return paths


def live_files_for(value: str) -> tuple[list[str], bool]:
    normalized = value.strip()
    if not normalized:
        return [], False
    if normalized == "fast":
        return FAST_LIVE_FILES, False
    if normalized in ("slow", "all"):
        names = sorted(path.name for path in (ROOT / "tools/headless-client/tests").glob("test_*.py"))
        return names, True

    names = []
    for item in normalized.split(","):
        item = item.strip().replace("\\", "/")
        for prefix in ("tools/headless-client/", "tests/"):
            if item.startswith(prefix):
                item = item[len(prefix):]
        if "/" in item or not SAFE_TEST_FILE.fullmatch(item):
            raise ValueError(f"live test must be a test_*.py file: {item}")
        if not (ROOT / "tools/headless-client/tests" / item).is_file():
            raise ValueError(f"headless-client test file does not exist: {item}")
        if item not in names:
            names.append(item)
    if not names:
        raise ValueError("live test selection is empty")
    return names, False


def local_name(tag: str) -> str:
    return tag.rsplit("}", 1)[-1]


def node_text(node: ET.Element, names: set[str]) -> str:
    return "\n".join(
        (child.text or "").strip()
        for child in node.iter()
        if local_name(child.tag) in names and (child.text or "").strip()
    )


def trx_results(path: Path) -> tuple[int, int, int, list[tuple[str, str]]]:
    root = ET.parse(path).getroot()
    counters = next((node for node in root.iter() if local_name(node.tag) == "Counters"), None)
    executed = int(counters.attrib.get("executed", "0")) if counters is not None else 0
    passed = int(counters.attrib.get("passed", "0")) if counters is not None else 0
    skipped = int(counters.attrib.get("notExecuted", "0")) if counters is not None else 0
    failures = []
    for node in root.iter():
        if local_name(node.tag) != "UnitTestResult" or node.attrib.get("outcome", "").lower() != "failed":
            continue
        name = node.attrib.get("testName", "unnamed .NET test")
        details = node_text(node, {"Message", "StackTrace", "StdOut"})
        failures.append((name, details))
    return executed, passed, skipped, failures


def junit_results(path: Path) -> tuple[int, int, int, list[tuple[str, str]]]:
    root = ET.parse(path).getroot()
    suites = [node for node in root.iter() if local_name(node.tag) == "testsuite"]
    if not suites and local_name(root.tag) == "testsuite":
        suites = [root]
    tests = sum(int(node.attrib.get("tests", "0")) for node in suites)
    skipped = sum(int(node.attrib.get("skipped", "0")) for node in suites)
    failures = []
    for node in root.iter():
        if local_name(node.tag) != "testcase":
            continue
        failed = [child for child in node if local_name(child.tag) in {"failure", "error"}]
        if failed:
            label = "::".join(part for part in (node.attrib.get("classname", ""), node.attrib.get("name", "")) if part)
            failures.append((label or "unnamed live test", "\n".join((child.text or "").strip() for child in failed)))
    return tests - skipped, tests - skipped - len(failures), skipped, failures


def rust_results(log: str) -> tuple[int, int, int, list[tuple[str, str]]]:
    lines = log.splitlines()
    failures = []
    seen = set()
    for index, line in enumerate(lines):
        match = re.match(r"\s*test\s+(.+?)\s+\.\.\.\s+FAILED\s*$", line)
        if not match:
            continue
        name = match.group(1)
        if name in seen:
            continue
        seen.add(name)
        start = next((at for at in range(index + 1, len(lines)) if lines[at].strip() == f"---- {name} stdout ----"), None)
        if start is None:
            detail = "\n".join(lines[max(0, index - 5):index + 35])
        else:
            end = next((at for at in range(start + 1, len(lines)) if lines[at].startswith("---- ") or lines[at].startswith("failures:")), len(lines))
            detail = "\n".join(lines[start:end])
        failures.append((name, detail))
    summaries = [re.search(r"test result: (?:ok|FAILED)\. (\d+) passed; (\d+) failed; (\d+) ignored", line)
                 for line in lines]
    counts = [match for match in summaries if match]
    executed = sum(int(match.group(1)) + int(match.group(2)) for match in counts)
    passed = sum(int(match.group(1)) for match in counts)
    skipped = sum(int(match.group(3)) for match in counts)
    return executed, passed, skipped, failures


def run(label: str, command: list[str], cwd: Path, environment: dict[str, str], log_path: Path) -> tuple[int, str]:
    log_path.parent.mkdir(parents=True, exist_ok=True)
    print(f"Running {label}; full log: {log_path.relative_to(ROOT)}", flush=True)
    with log_path.open("w", encoding="utf-8") as output:
        try:
            result = subprocess.run(command, cwd=cwd, env=environment, stdout=output, stderr=subprocess.STDOUT, check=False)
        except OSError as error:
            output.write(f"Could not start command: {error}\n")
            return 127, output_path_tail(log_path)
    return result.returncode, output_path_tail(log_path)


def output_path_tail(path: Path) -> str:
    try:
        return "\n".join(path.read_text(encoding="utf-8", errors="replace").splitlines()[-40:])
    except OSError:
        return ""


def add_failure(failures: list[tuple[str, str]], label: str, details: str) -> None:
    failures.append((label, details))


def write_summary(lines: list[str]) -> None:
    summary = "\n".join(lines) + "\n"
    print(summary, end="", flush=True)
    LOGS.mkdir(parents=True, exist_ok=True)
    (LOGS / "summary.txt").write_text(summary, encoding="utf-8")
    path = os.environ.get("GITHUB_STEP_SUMMARY")
    if path:
        with open(path, "a", encoding="utf-8") as output:
            output.write(summary)


def main() -> int:
    try:
        selected_projects = projects_for(os.environ.get("DOTNET_PROJECTS", "all"))
        live_files, live_slow = live_files_for(os.environ.get("LIVE_TESTS", "fast"))
        rust_mode = os.environ.get("RUST_TESTS", "none").strip()
        if rust_mode not in {"none", "tests", "clippy", "all"}:
            raise ValueError("rust selection must be none, tests, clippy, or all")
        if not selected_projects and not live_files and rust_mode == "none":
            raise ValueError("at least one test selection is required")
    except ValueError as error:
        print(error, file=sys.stderr)
        return 2

    LOGS.mkdir(parents=True, exist_ok=True)
    failures: list[tuple[str, str]] = []
    counts: list[str] = []
    env = os.environ.copy()
    env.setdefault("DOTNET_CLI_USE_MSBUILD_SERVER", "0")
    env.setdefault("MSBUILDDISABLENODEREUSE", "1")

    dotnet_built = False
    if selected_projects:
        code, tail = run("one Release build of the .NET solution",
                         ["dotnet", "build", "EveRs.slnx", "-c", "Release", "-nologo", "--verbosity", "minimal", "-nodeReuse:false", "-p:UseSharedCompilation=false"],
                         ROOT / "dotnet", env, LOGS / "dotnet-build.log")
        dotnet_built = code == 0
        if not dotnet_built:
            add_failure(failures, ".NET solution build", tail)
        for project in selected_projects:
            name = project.stem
            if not dotnet_built:
                break
            report = LOGS / "dotnet-results" / f"{name}.trx"
            report.parent.mkdir(parents=True, exist_ok=True)
            report.unlink(missing_ok=True)
            command = ["dotnet", "test", str(project.relative_to(ROOT / "dotnet")), "-c", "Release", "--no-build", "--no-restore",
                       "-nologo", "--verbosity", "minimal", "-nodeReuse:false", "-p:UseSharedCompilation=false", "--results-directory", str(report.parent),
                       "--logger", f"trx;LogFileName={name}.trx"]
            filter_value = os.environ.get("DOTNET_FILTER", "").strip()
            if filter_value:
                command.extend(["--filter", filter_value])
            code, tail = run(f".NET tests {name}", command, ROOT / "dotnet", env, LOGS / f"dotnet-{name}.log")
            if not report.is_file():
                add_failure(failures, f"{name} (no TRX report)", tail)
                continue
            try:
                executed, passed, skipped, found = trx_results(report)
            except (ET.ParseError, OSError) as error:
                add_failure(failures, f"{name} (unreadable TRX report)", f"{error}\n{tail}")
                continue
            counts.append(f"{name}: {passed} passed, {len(found)} failed, {skipped} skipped")
            failures.extend(found)
            if executed == 0:
                add_failure(failures, f"{name} (zero tests executed)", tail)
            elif code != 0 and not found:
                add_failure(failures, name, tail)

    cargo = ["cargo"]
    if rust_mode in {"tests", "all"}:
        code, tail = run("Rust workspace tests", cargo + ["test", "--workspace"], ROOT, env, LOGS / "rust-test.log")
        rust_log = (LOGS / "rust-test.log").read_text(encoding="utf-8", errors="replace")
        executed, passed, skipped, found = rust_results(rust_log)
        counts.append(f"Rust tests: {passed} passed, {len(found)} failed, {skipped} ignored")
        failures.extend(found)
        if code != 0 and not found:
            add_failure(failures, "Rust workspace tests", tail)
    if rust_mode in {"clippy", "all"}:
        if rust_mode == "all":
            code, tail = run("Rust format check", cargo + ["fmt", "--all", "--", "--check"], ROOT, env, LOGS / "rust-fmt.log")
            if code != 0:
                add_failure(failures, "Rust format check", tail)
        code, tail = run("Rust clippy", cargo + ["clippy", "--workspace", "--all-targets", "--", "-D", "warnings"], ROOT, env, LOGS / "rust-clippy.log")
        if code != 0:
            add_failure(failures, "Rust clippy", tail)

    if live_files:
        client = ROOT / "tools/headless-client"
        junit = LOGS / "live-results.xml"
        junit.unlink(missing_ok=True)
        test_args = [".venv/bin/python", "-m", "pytest", "-q", "-vv", "--tb=long", f"--junitxml={junit}",
                     *(f"tests/{name}" for name in live_files)]
        live_command = f"cd {shlex.quote(str(client))} && EVE_LIVE_SERVER=eve.rs EVE_LIVE_STRICT_PASSWORDS=1 {shlex.join(test_args)}"
        live_env = env.copy()
        live_env["EVE_RUN"] = live_command
        live_env["EVE_LIVE_LOG_DIR"] = str(LOGS / "live-stack")
        live_env["KEEP_LOGS"] = "1"
        if dotnet_built:
            live_env["EVE_LIVE_SKIP_DOTNET_BUILD"] = "1"
        if live_slow:
            live_env["EVE_LIVE_SLOW"] = "1"
        else:
            live_env.pop("EVE_LIVE_SLOW", None)
        if "test_walk_xmpp_live.py" in live_files:
            live_env.update({"EVE_XMPP_ENABLED": "true", "EVE_XMPP_PORT": "5222", "EVE_LIVE_XMPP_PORT": "5222"})
        code, tail = run(f"live tests ({len(live_files)} files)", ["bash", "tools/dev/run-eve-live.sh"], ROOT, live_env, LOGS / "live-run.log")
        if junit.is_file():
            try:
                executed, passed, skipped, found = junit_results(junit)
                counts.append(f"Live tests: {passed} passed, {len(found)} failed, {skipped} skipped")
                failures.extend(found)
                if executed == 0:
                    add_failure(failures, "live tests (zero tests executed)", tail)
                elif code != 0 and not found:
                    add_failure(failures, "live tests", tail)
            except (ET.ParseError, OSError) as error:
                add_failure(failures, "live tests (unreadable JUnit report)", f"{error}\n{tail}")
        else:
            add_failure(failures, "live tests (no JUnit report)", tail)

    summary = ["Agent test summary", "===================", *(f"- {item}" for item in counts)]
    if failures:
        summary.append(f"Failures: {len(failures)}")
        for name, details in failures:
            summary.append("")
            summary.append(f"[agent-test failure] {name}")
            summary.extend((details or "No per-test output was recorded.").splitlines()[-40:])
        write_summary(summary)
        return 1
    summary.append("All selected test commands completed successfully.")
    write_summary(summary)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

#!/usr/bin/env python3
"""Generate the evidence-based eve.rs unit map in docs/project/MAP.md."""
from __future__ import annotations

import argparse
import csv
from dataclasses import dataclass
import hashlib
from pathlib import Path
import re
import sys


ROOT = Path(__file__).resolve().parents[2]
MAP_REL = Path("docs/project/MAP.md")
START = "<!-- MAP:START -->"
END = "<!-- MAP:END -->"

ROUTE_COVERAGE = Path("docs/project/service-audit/EVE_RS_COVERAGE.csv")
CLIENT_MATRIX = Path("docs/project/service-audit/CLIENT_CALL_MATRIX.csv")
CLIENT_VERIFIED = Path("docs/project/service-audit/CLIENT_VERIFIED.md")
CONTEXT_PATHS = (
    Path("docs/project/coverage-sweep-1.md"),
    Path("docs/project/coverage-sweep-1.csv"),
    Path("docs/project/audit-summary-short.md"),
    Path("docs/project/TODO.md"),
    Path("docs/reference/compat/DEVIATIONS.md"),
    Path("docs/project/live-skipped-scenarios.md"),
    Path("docs/project/pos-force-field-and-modules.md"),
    Path("docs/project/ship-rpcs-implementation.md"),
)

STATUS_NAMES = {
    "client-dead",
    "unverified",
    "partial-tested",
    "verified-against-EveJS",
    "verified-against-client-trace",
    "unknown",
}
KIND_ORDER = {
    "Server project": 0,
    "RPC family": 1,
    "Scene subsystem": 2,
    "Rust crate": 3,
    "Tool": 4,
}


@dataclass(frozen=True)
class Unit:
    kind: str
    name: str
    state: str
    evidence: str
    owner_doc: str
    gaps: str


def link_from_map(path: Path, label: str, root: Path = ROOT) -> str:
    import os

    relative = Path(os.path.relpath(root / path, root / MAP_REL.parent))
    return f"[{label}]({relative.as_posix()})"


def markdown_cell(value: str) -> str:
    return value.replace("|", "&#124;").replace("\n", " ").strip()


def read_csv(root: Path, relative: Path) -> list[dict[str, str]]:
    path = root / relative
    if not path.is_file():
        return []
    with path.open(newline="", encoding="utf-8-sig") as handle:
        return list(csv.DictReader(handle))


def csv_status(value: str) -> str:
    return value.split("(", 1)[0].strip().upper()


def parse_client_verification(text: str) -> tuple[set[str], set[str], set[str]]:
    """Return unambiguous MATCH rows and their client/EveJS evidence subsets."""
    verdicts: dict[str, list[tuple[bool, bool, bool]]] = {}
    for line in text.splitlines():
        if not line.lstrip().startswith("| RPC-"):
            continue
        cells = [cell.strip() for cell in line.strip().strip("|").split("|")]
        if len(cells) < 3 or not re.fullmatch(r"RPC-\d{5}", cells[0]):
            continue
        rpc_id = cells[0]
        verdict = cells[-1].strip().upper()
        matched = bool(re.match(r"^MATCH(?:ES)?(?:\s|\(|$)", verdict))
        folded_line = line.casefold()
        cites_client = matched and any(token in folded_line for token in ("eve-logic", "eve/client/", "v24 client", "client call", "client consumer", "client caller", "whole-client"))
        cites_evejs = matched and "evejs" in folded_line
        verdicts.setdefault(rpc_id, []).append((matched, cites_client, cites_evejs))

    matched_ids: set[str] = set()
    client_ids: set[str] = set()
    evejs_ids: set[str] = set()
    for rpc_id, records in verdicts.items():
        # A positive row is not enough if the same source has a conflicting or open verdict.
        if records and all(matched for matched, _, _ in records):
            matched_ids.add(rpc_id)
            if all(cites_client for _, cites_client, _ in records):
                client_ids.add(rpc_id)
            if all(cites_evejs for _, _, cites_evejs in records):
                evejs_ids.add(rpc_id)
    return matched_ids, client_ids, evejs_ids


def extract_repository_paths(text: str, root: Path) -> list[Path]:
    candidates = re.findall(r"(?<![A-Za-z0-9_./-])((?:dotnet|crates|tools)/[A-Za-z0-9_./ -]+?\.(?:cs|rs|py|sh|csproj|toml))(?=[:)`'\s,]|$)", text)
    paths: list[Path] = []
    for candidate in candidates:
        path = Path(candidate.rstrip(" ."))
        if (root / path).is_file() and path not in paths:
            paths.append(path)
    return paths


def referenced_docs(text: str, root: Path) -> list[Path]:
    found: list[Path] = []
    for candidate in re.findall(r"(?<![A-Za-z0-9_./-])([A-Za-z0-9_./-]+\.md)(?::L?\d+)?", text):
        raw = Path(candidate)
        choices = [raw]
        if not raw.is_absolute():
            choices.extend((Path("docs/project") / raw, Path("docs/project/service-audit") / raw, Path("docs/reference/compat") / raw))
        for choice in choices:
            try:
                resolved = (root / choice).resolve()
                resolved.relative_to(root.resolve())
            except (OSError, ValueError):
                continue
            if resolved.is_file():
                relative = resolved.relative_to(root.resolve())
                if relative not in found:
                    found.append(relative)
                break
    return found


def normalized_name(value: str) -> str:
    return re.sub(r"[^a-z0-9]", "", value.casefold())


def exact_deepcheck(service: str, root: Path) -> Path | None:
    audit_dir = root / "docs/project/service-audit"
    wanted = normalized_name(service)
    for path in sorted(audit_dir.glob("DEEPCHECK_*.md")):
        if normalized_name(path.stem.removeprefix("DEEPCHECK_")) == wanted:
            return path.relative_to(root)
    return None


def rpc_owner_doc(service: str, rows: list[dict[str, str]], root: Path) -> Path | None:
    direct = exact_deepcheck(service, root)
    if direct:
        return direct
    for row in rows:
        for path in referenced_docs(row.get("status_evidence", ""), root):
            if path.name.casefold().startswith(("deepcheck_", "audit-fix", "implement-", "posmgr-", "space-world-gaps")):
                return path
    return None


def rpc_units(root: Path) -> list[Unit]:
    coverage_rows = read_csv(root, ROUTE_COVERAGE)
    matrix_rows = read_csv(root, CLIENT_MATRIX)
    verification_text = (root / CLIENT_VERIFIED).read_text(encoding="utf-8", errors="replace") if (root / CLIENT_VERIFIED).is_file() else ""
    matched_ids, client_ids, evejs_ids = parse_client_verification(verification_text)

    by_service: dict[str, list[dict[str, str]]] = {}
    for row in coverage_rows:
        service = row.get("service", "").strip()
        if service:
            by_service.setdefault(service, []).append(row)

    rpc_services = {row.get("service", "").strip() for row in matrix_rows if row.get("row_type") == "rpc"}
    bind_services = {row.get("service", "").strip() for row in matrix_rows if row.get("row_type") == "service-bind"}
    client_services = {service for service in rpc_services | bind_services if service and not (service.startswith("<") or service.casefold() == "dynamic moniker")}
    services = set(by_service) | client_services

    units: list[Unit] = []
    for service in sorted(services, key=str.casefold):
        rows = by_service.get(service, [])
        matrix_present = service in rpc_services
        evidence_links: list[str] = []
        if matrix_present:
            evidence_links.append(link_from_map(CLIENT_MATRIX, "client RPC inventory", root))
        if rows:
            evidence_links.append(link_from_map(ROUTE_COVERAGE, "route coverage", root))
        if any(row.get("row_id", "") in matched_ids for row in rows):
            evidence_links.append(link_from_map(CLIENT_VERIFIED, "client verification records", root))
        for row in rows:
            for path in extract_repository_paths(row.get("eve_rs_status", "") + " " + row.get("status_evidence", ""), root):
                link = link_from_map(path, path.name, root)
                if link not in evidence_links:
                    evidence_links.append(link)
                if len(evidence_links) >= 6:
                    break
            if len(evidence_links) >= 6:
                break

        statuses = [csv_status(row.get("eve_rs_status", "")) for row in rows]
        non_remote = {"CLIENT-DEAD", "CLIENT-LOCAL"}
        active = [row for row in rows if csv_status(row.get("eve_rs_status", "")) not in non_remote]
        partial_rows = [row for row in rows if csv_status(row.get("eve_rs_status", "")) == "PARTIAL-TESTED"]

        if rows and not active:
            state = "client-dead"
            gaps = "—"
        elif not rows:
            state = "unverified"
            gaps = "Client service-bind evidence exists, but no RPC route row is present in the route coverage audit."
        elif partial_rows:
            state = "partial-tested"
            ids = ", ".join(row.get("row_id", "") for row in partial_rows[:8])
            more = f" and {len(partial_rows) - 8} more" if len(partial_rows) > 8 else ""
            gaps = f"Explicit PARTIAL-TESTED gaps are recorded for {ids}{more}; see the linked route coverage rows."
        elif any(csv_status(row.get("eve_rs_status", "")) != "IMPLEMENTED-TESTED" for row in active):
            state = "unverified"
            gaps = "The route status contains a value this generator does not map to a verification state; review the source rows."
        elif all(row.get("row_id", "") in matched_ids and row.get("row_id", "") in client_ids for row in active):
            if all(row.get("row_id", "") in evejs_ids for row in active):
                state = "verified-against-EveJS"
            else:
                state = "verified-against-client-trace"
            gaps = "The recorded source/test match is method-level; graphical-client acceptance is not established by this record."
        else:
            state = "partial-tested"
            verified = sum(row.get("row_id", "") in matched_ids and row.get("row_id", "") in client_ids for row in active)
            gaps = f"{len(active) - verified} of {len(active)} active route rows lack a matching client-trace record; the coverage CSV's test association is not a family-wide parity check."

        if any(csv_status(row.get("eve_rs_status", "")) in non_remote for row in rows) and active:
            non_remote_count = len(rows) - len(active)
            gaps += f" {non_remote_count} additional row(s) are explicitly CLIENT-DEAD or CLIENT-LOCAL."

        owners = rpc_owner_doc(service, rows, root) if rows else None
        owner_text = link_from_map(owners, owners.name, root) if owners else "— (not recorded)"
        if not owners:
            gaps += " No dedicated owning document is identified by the source records."
        if not evidence_links:
            evidence_links.append(link_from_map(CLIENT_MATRIX, "client service inventory", root))
        units.append(Unit("RPC family", service, state, "; ".join(evidence_links), owner_text, gaps))

    return units


def structural_units(root: Path) -> list[Unit]:
    units: list[Unit] = []
    project_root = root / "dotnet/src"
    for project in sorted(project_root.rglob("*.csproj")):
        if any(part in {"bin", "obj"} for part in project.parts):
            continue
        relative = project.relative_to(root)
        test_project = Path("dotnet/tests") / f"{project.parent.name}.Tests" / f"{project.parent.name}.Tests.csproj"
        evidence = [link_from_map(relative, "source project", root)]
        if (root / test_project).is_file():
            evidence.append(link_from_map(test_project, "test project", root))
        units.append(Unit(
            "Server project",
            relative.as_posix(),
            "unknown",
            "; ".join(evidence),
            "— (not recorded)",
            "The source set identifies the project and any test project, but does not record a project-wide compatibility state or a dedicated owning doc.",
        ))

    for manifest in sorted((root / "crates").glob("*/Cargo.toml")):
        relative = manifest.relative_to(root)
        crate = manifest.parent.name
        tests = sorted((manifest.parent / "tests").rglob("*.rs")) if (manifest.parent / "tests").is_dir() else []
        evidence = [link_from_map(relative, "crate manifest", root)]
        evidence.extend(link_from_map(path.relative_to(root), path.name, root) for path in tests[:2])
        units.append(Unit(
            "Rust crate",
            relative.as_posix(),
            "unknown",
            "; ".join(evidence),
            "— (not recorded)",
            f"{len(tests)} external Rust test file(s) are present; no crate-wide client/EveJS verification state is recorded in the map inputs.",
        ))

    space_src = root / "crates/eve-space/src"
    public_modules: list[tuple[str, Path]] = []
    lib_rs = space_src / "lib.rs"
    if lib_rs.is_file():
        for module in re.findall(r"^\s*pub\s+mod\s+([A-Za-z_][A-Za-z0-9_]*)\s*;", lib_rs.read_text(encoding="utf-8", errors="replace"), re.MULTILINE):
            candidate = space_src / f"{module}.rs"
            if not candidate.is_file():
                candidate = space_src / module / "mod.rs"
            if candidate.is_file():
                public_modules.append((module, candidate))
    for module, source in public_modules:
        rel = source.relative_to(root)
        test_files = sorted(source.parent.glob("*tests.rs"))
        evidence = [link_from_map(rel, "module source", root)]
        evidence.extend(link_from_map(path.relative_to(root), path.name, root) for path in test_files[:2])
        units.append(Unit(
            "Scene subsystem",
            f"crates/eve-space::{module}",
            "unknown",
            "; ".join(evidence),
            "— (not recorded)",
            "Public module inventory is present; no module-level compatibility state is inferred from source or test-file presence.",
        ))
        if module == "scene":
            scene_mod = space_src / "scene/mod.rs"
            scene_text = scene_mod.read_text(encoding="utf-8", errors="replace") if scene_mod.is_file() else ""
            for child in re.findall(r"^\s*pub\s+mod\s+([A-Za-z_][A-Za-z0-9_]*)\s*;", scene_text, re.MULTILINE):
                child_source = space_src / "scene" / f"{child}.rs"
                if not child_source.is_file() or child in {"tests", "mod"}:
                    continue
                child_tests = sorted(child_source.parent.glob(f"{child}*_tests.rs"))
                child_tests.extend(sorted(child_source.parent.glob("tests.rs")))
                evidence = [link_from_map(child_source.relative_to(root), "scene module source", root)]
                evidence.extend(link_from_map(path.relative_to(root), path.name, root) for path in child_tests[:2])
                units.append(Unit(
                    "Scene subsystem",
                    f"crates/eve-space::scene::{child}",
                    "unknown",
                    "; ".join(evidence),
                    "— (not recorded)",
                    "Public scene module inventory is present; no module-level compatibility state is inferred from source or test-file presence.",
                ))
    return units


def tool_paths(root: Path) -> list[Path]:
    paths: set[Path] = set()
    tools_root = root / "tools"
    excluded_parts = {"tests", "__pycache__", ".venv", "site-packages"}
    for path in tools_root.rglob("*"):
        if not path.is_file() or path.suffix not in {".py", ".sh"}:
            continue
        if any(part in excluded_parts for part in path.parts) or path.name == "__init__.py" or path.name.startswith("test_") or path.stem.endswith("_tests"):
            continue
        if "tools/headless-client/evecli" in path.as_posix():
            if path.name == "__main__.py":
                paths.add(Path("tools/headless-client/evecli"))
            continue
        paths.add(path.relative_to(root))
    for extra in (Path("sde/fetch.sh"), Path("oracle/scripts/sync-oracle.sh")):
        if (root / extra).is_file():
            paths.add(extra)
    for path in (root / "crates/eve-sde/src/main.rs",):
        if path.is_file():
            paths.add(path.relative_to(root))
    for path in sorted((root / "crates/eve-sim/src/bin").glob("*.rs")) if (root / "crates/eve-sim/src/bin").is_dir() else []:
        paths.add(path.relative_to(root))
    return sorted(paths, key=lambda p: p.as_posix().casefold())


def tool_test_path(path: Path, root: Path) -> Path | None:
    if path.as_posix() == "tools/headless-client/evecli":
        return None
    if path.suffix == ".py":
        candidate = path.with_name(f"test_{path.stem}.py")
        if (root / candidate).is_file():
            return candidate
        candidate = path.with_name(f"{path.stem}_tests.py")
        if (root / candidate).is_file():
            return candidate
    return None


def tool_units(root: Path) -> list[Unit]:
    units: list[Unit] = []
    for path in tool_paths(root):
        evidence = [link_from_map(path, path.name, root)]
        test = tool_test_path(path, root)
        if test:
            evidence.append(link_from_map(test, "focused test", root))
        gaps = "No unit-level compatibility status is recorded in the supplied evidence."
        if not test:
            gaps += " No matching focused test file was found by the filename convention."
        units.append(Unit("Tool", path.as_posix(), "unknown", "; ".join(evidence), "— (not recorded)", gaps))
    return units


def parse_claims(map_text: str) -> dict[tuple[str, str], str]:
    if START not in map_text or END not in map_text:
        return {}
    section = map_text.split(START, 1)[1].split(END, 1)[0]
    claims: dict[tuple[str, str], str] = {}
    for line in section.splitlines():
        if not line.lstrip().startswith("|"):
            continue
        cells = [cell.strip() for cell in line.strip().strip("|").split("|")]
        if len(cells) < 7 or cells[0] == "Kind" or set(cells[0]) <= {"-", ":"}:
            continue
        claim = "" if cells[6] in {"", "—", "-"} else cells[6]
        if claim:
            key = (cells[0], cells[1])
            previous = claims.get(key)
            if previous and previous != claim:
                raise ValueError(f"conflicting claims for {key[0]} / {key[1]}: {previous!r} and {claim!r}")
            claims[key] = claim
    return claims


def input_docs(root: Path) -> list[Path]:
    paths = [CLIENT_MATRIX, ROUTE_COVERAGE, CLIENT_VERIFIED, *CONTEXT_PATHS]
    paths.extend(path.relative_to(root) for path in sorted((root / "docs/project").glob("evejs-diff-*.md")))
    return list(dict.fromkeys(path for path in paths if (root / path).is_file()))


def source_fingerprint(root: Path, units: list[Unit]) -> str:
    digest = hashlib.sha256()
    for path in input_docs(root):
        digest.update(path.as_posix().encode("utf-8"))
        digest.update(b"\0")
        digest.update((root / path).read_bytes())
        digest.update(b"\0")
    inventory_names = sorted((unit.kind + "\0" + unit.name for unit in units), key=str.casefold)
    for item in inventory_names:
        digest.update(item.encode("utf-8"))
        digest.update(b"\0")
    return digest.hexdigest()


def inventory(root: Path) -> list[Unit]:
    units = [*structural_units(root), *rpc_units(root), *tool_units(root)]
    return sorted(units, key=lambda unit: (KIND_ORDER.get(unit.kind, 99), unit.name.casefold(), unit.name))


def render_map(root: Path = ROOT) -> str:
    map_path = root / MAP_REL
    existing = map_path.read_text(encoding="utf-8", errors="replace") if map_path.is_file() else ""
    claims = parse_claims(existing)
    units = inventory(root)
    unit_keys = {(unit.kind, unit.name) for unit in units}
    orphaned = sorted(set(claims) - unit_keys)
    if orphaned:
        joined = ", ".join(f"{kind} / {name}" for kind, name in orphaned)
        raise ValueError(f"claimed unit no longer exists in the source inventory; clear or correct the claim in MAP.md: {joined}")

    status_counts: dict[str, int] = {status: 0 for status in sorted(STATUS_NAMES)}
    kind_counts: dict[str, int] = {}
    for unit in units:
        if unit.state not in STATUS_NAMES:
            raise ValueError(f"unsupported status {unit.state!r} on {unit.kind} / {unit.name}")
        status_counts[unit.state] += 1
        kind_counts[unit.kind] = kind_counts.get(unit.kind, 0) + 1
    summary = ", ".join(f"{kind}: {count}" for kind, count in sorted(kind_counts.items(), key=lambda item: KIND_ORDER.get(item[0], 99)))
    state_summary = ", ".join(f"{state}: {count}" for state, count in status_counts.items() if count)
    fingerprint = source_fingerprint(root, units)

    lines = [
        "# eve.rs unit map",
        "",
        "Generated by [`tools/dev/gen_project_map.py`](../../tools/dev/gen_project_map.py). Regenerate with `python3 tools/dev/gen_project_map.py`; CI checks freshness with `--check`.",
        "",
        "Claim a unit by putting your name in the Claim column and committing `MAP.md` before starting work. The commit preserves the claim across container restarts. Clear the claim with another commit when the work is finished. The generator carries claims forward by the exact `(Kind, Unit)` key and fails rather than dropping a claim for a unit that disappeared.",
        "",
        f"Inventory: {len(units)} units ({summary}). States: {state_summary}. Source fingerprint: `{fingerprint}`.",
        "",
        "## Scope and status rules",
        "",
        "The inventory includes .NET projects, RPC families from the V24 client call matrix and route-coverage audit, public `eve-space` modules (including nested scene modules), Rust command-line tools, and executable Python/shell tools. RPC families present only as client service binds are included as `unverified` when no route-coverage row exists.",
        "",
        "RPC states are conservative aggregates. `client-dead` is used only when every audited row is `CLIENT-DEAD` or `CLIENT-LOCAL`; those client-local methods do not make a server RPC. `partial-tested` means the audit associates tests with some behavior or only some routes have an explicit match. `verified-against-EveJS` and `verified-against-client-trace` require every active route row in that family to have an unambiguous `MATCH` in `CLIENT_VERIFIED.md` and to cite a V24 client callsite or consumer; the former also requires each row to cite EveJS. Here client-trace verification means the recorded V24 callsite/consumer trace plus its registered Host regression. It does not imply a graphical client session or a packet-capture comparison. `unverified` means a client service binding is known but no route verification is recorded. `unknown` means the available sources do not establish a unit-level verification state.",
        "",
        "Project, crate, scene-module, and tool rows remain `unknown` unless a source explicitly records a unit-level verification state. A test file, route registration, or source file by itself does not promote a state. `Owning doc` is populated only when a unit-specific document is directly identified by the source records; `not recorded` is kept visible when none is identified.",
        "",
        "Status inputs: [client call matrix](service-audit/CLIENT_CALL_MATRIX.csv), [route coverage](service-audit/EVE_RS_COVERAGE.csv), and [client verification records](service-audit/CLIENT_VERIFIED.md). Context inputs: [audit summary](audit-summary-short.md), [TODO](TODO.md), [compatibility deviations](../reference/compat/DEVIATIONS.md), [live skipped scenarios](live-skipped-scenarios.md), [POS audit](pos-force-field-and-modules.md), [ship RPC record](ship-rpcs-implementation.md), and the linked [EveJS diff records](evejs-diff-core-flows.md), [economy/social](evejs-diff-economy-social.md), [space/world](evejs-diff-space-world.md). Context documents are not treated as blanket proof for every unit.",
        "",
        "The requested `coverage-sweep-1.md` and `coverage-sweep-1.csv` are absent from this checkout; they are not used to infer states. The available route-level source is `service-audit/EVE_RS_COVERAGE.csv`.",
        "",
        "<!-- MAP:START -->",
        "| Kind | Unit | State | Evidence | Owning doc | Open gaps | Claim |",
        "| --- | --- | --- | --- | --- | --- | --- |",
    ]
    for unit in units:
        claim = claims.get((unit.kind, unit.name), "—")
        cells = (unit.kind, unit.name, unit.state, unit.evidence, unit.owner_doc, unit.gaps, claim)
        lines.append("| " + " | ".join(markdown_cell(cell) for cell in cells) + " |")
    lines.extend(("<!-- MAP:END -->", ""))
    return "\n".join(lines)


def is_current(root: Path = ROOT) -> bool:
    target = root / MAP_REL
    return target.is_file() and target.read_text(encoding="utf-8") == render_map(root)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true", help="fail if MAP.md differs from generated output")
    args = parser.parse_args(argv)
    target = ROOT / MAP_REL
    try:
        generated = render_map(ROOT)
    except (OSError, ValueError, csv.Error) as error:
        print(f"project map generation failed: {error}", file=sys.stderr)
        return 2
    if args.check:
        if not is_current(ROOT):
            print("docs/project/MAP.md is stale; run python3 tools/dev/gen_project_map.py and commit the result.", file=sys.stderr)
            return 1
        print("docs/project/MAP.md is current.")
        return 0
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(generated, encoding="utf-8", newline="\n")
    print(f"Generated docs/project/MAP.md ({len(inventory(ROOT))} units).")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

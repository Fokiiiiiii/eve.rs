#!/usr/bin/env python3
"""Print a VSTest filter that assigns test classes or methods to one shard."""

from __future__ import annotations

import argparse
from collections import Counter
from pathlib import Path
import sys


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("test_list", type=Path)
    parser.add_argument("index", type=int)
    parser.add_argument("count", type=int)
    parser.add_argument("--split-methods", action="store_true")
    args = parser.parse_args()
    if args.count < 1 or not 0 <= args.index < args.count:
        parser.error("index must be within a positive shard count")

    tests: Counter[str] = Counter()
    listing_started = False
    for line in args.test_list.read_text(encoding="utf-8").splitlines():
        if line.strip() == "The following Tests are available:":
            listing_started = True
            continue
        if not listing_started:
            continue
        test_name = line.strip()
        if not test_name:
            continue
        # xUnit includes theory arguments in listed names; keep every case of a method together.
        method_name = test_name.split("(", 1)[0]
        if "." not in method_name:
            raise SystemExit(f"Cannot determine test method from: {test_name}")
        class_name, _, _ = method_name.rpartition(".")
        key = method_name if args.split_methods else class_name
        tests[key] += 1

    if not tests:
        raise SystemExit("VSTest did not list any tests")

    bins: list[list[str]] = [[] for _ in range(args.count)]
    loads = [0] * args.count
    for name, case_count in sorted(tests.items(), key=lambda item: (-item[1], item[0])):
        target = min(range(args.count), key=lambda shard: (loads[shard], shard))
        bins[target].append(name)
        loads[target] += case_count

    selected = bins[args.index]
    if not selected:
        raise SystemExit(f"Shard {args.index} has no test groups")
    print(", ".join(f"{name} ({tests[name]} tests)" for name in sorted(selected)), file=sys.stderr)
    if args.split_methods:
        filters = [f"FullyQualifiedName={name}" for name in sorted(selected)]
    else:
        filters = [f"FullyQualifiedName~{name}." for name in sorted(selected)]
    print("|".join(filters))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

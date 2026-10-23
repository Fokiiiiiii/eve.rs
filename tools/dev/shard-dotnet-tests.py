#!/usr/bin/env python3
"""Print a VSTest filter that assigns whole test classes to one shard."""

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
        # xUnit includes theory arguments in listed names. Their values may contain
        # fully qualified type names, so find the class before the argument list.
        case_name = test_name.split("(", 1)[0]
        class_name, separator, _ = case_name.rpartition(".")
        if not separator:
            raise SystemExit(f"Cannot determine test class from: {test_name}")
        tests[class_name] += 1

    if not tests:
        raise SystemExit("VSTest did not list any tests")

    bins: list[list[str]] = [[] for _ in range(args.count)]
    loads = [0] * args.count
    for class_name, test_count in sorted(tests.items(), key=lambda item: (-item[1], item[0])):
        target = min(range(args.count), key=lambda shard: (loads[shard], shard))
        bins[target].append(class_name)
        loads[target] += test_count

    selected = bins[args.index]
    if not selected:
        raise SystemExit(f"Shard {args.index} has no test classes")
    print(", ".join(f"{name} ({tests[name]} tests)" for name in sorted(selected)), file=sys.stderr)
    print("|".join(f"FullyQualifiedName~{name}." for name in sorted(selected)))
    return 0

if __name__ == "__main__":
    raise SystemExit(main())

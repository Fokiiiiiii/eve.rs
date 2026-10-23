#!/usr/bin/env python3
"""Assign test projects to shards by recent VSTest durations."""

from __future__ import annotations

import argparse
from pathlib import Path

# New projects receive a small default weight and are included automatically.
DURATIONS = {
    "Eve.Corporations.Tests": 20,
    "Eve.Dogma.Tests": 21,
    "Eve.FactionWarfare.Tests": 22,
    "Eve.Market.Tests": 37,
    "Eve.Services.Dogma.Tests": 66,
    "Eve.Services.Inventory.Tests": 29,
    "Eve.Services.Skills.Tests": 22,
    "Eve.Ships.Tests": 110,
}

def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("index", type=int)
    parser.add_argument("count", type=int)
    parser.add_argument("projects", nargs="+", type=Path)
    args = parser.parse_args()
    if args.count < 1 or not 0 <= args.index < args.count:
        parser.error("index must be within a positive shard count")

    names = sorted(path.stem for path in args.projects)
    bins: list[list[str]] = [[] for _ in range(args.count)]
    loads = [0] * args.count
    for name in sorted(names, key=lambda item: (-DURATIONS.get(item, 5), item)):
        target = min(range(args.count), key=lambda shard: (loads[shard], shard))
        bins[target].append(name)
        loads[target] += DURATIONS.get(name, 5)
    print(" ".join(sorted(bins[args.index])))
    return 0

if __name__ == "__main__":
    raise SystemExit(main())

#!/usr/bin/env python3
"""Build the embedded copies of the V24.01 client static data that the server reads.

    EVE_CLIENT_SDE_DIR=<client export dir> python3 -I tools/dev/extract-client-sde.py
    python3 -I tools/dev/extract-client-sde.py <client export dir> [<repo root>]

Reads native/solarsystemcontent, native/spacecomponentsbytype, native/systems and native/missions (gzipped JSONL) and
metadata/summary.json (clientBuild). Writes four gzipped JSONL files. Each has a first line {"_header": {...}} that records
the build, the source file and this generator; then one row per line, sorted by key. A row keeps only the fields listed in
its header, in the export's own shape, so the server's loaders read the embedded copy and the export the same way:

  dotnet/src/Eve.Universe/Data/client-solarsystemcontent.jsonl.gz     disallowCyno, disallowScanning, stargates (allowedShipsTypeListID)
  dotnet/src/Eve.Universe/Data/client-spacecomponentsbytype.jsonl.gz  alignmentBasedToll, deployBlocker (the two block ranges)
  dotnet/src/Eve.Universe/Data/client-systems.jsonl.gz                solarSystemID, wormholeClassID
  dotnet/src/Eve.Services.Core/Data/client-missions.jsonl.gz          contentTemplate, messages (localization IDs only)

No localized text is copied: the messages carry the IDs only. The output is deterministic (sorted keys and rows, gzip
mtime 0): the same export always gives the same bytes. The server reads EVE_CLIENT_SDE_DIR, when it is set and holds a
file, instead of the embedded copy; this script is how the copies are refreshed for a new build.
"""
import gzip
import io
import json
import os
import sys

GENERATOR = "tools/dev/extract-client-sde.py"
WORMHOLE_SPACE = range(31_000_000, 32_000_000)
TOLL_FIELDS = ("pricePerKilo", "exponent", "fixedAddition", "minCost", "alignedDiscountMultiplier", "alignmentRestrictionList")
BLOCK_FIELDS = ("blockAnchorWarpBubbles", "blockStructures")


def rows(path):
    with gzip.open(path, "rt", encoding="utf-8") as handle:
        for line in handle:
            if line.strip():
                yield json.loads(line)


def solarsystemcontent(path):
    out = []
    for row in rows(path):
        kept = {"_key": row["_key"]}
        for flag in ("disallowCyno", "disallowScanning"):
            if row.get(flag) is True:
                kept[flag] = True
        gates = {gate: {"allowedShipsTypeListID": value["allowedShipsTypeListID"]}
                 for gate, value in (row.get("stargates") or {}).items() if "allowedShipsTypeListID" in value}
        if gates:
            kept["stargates"] = gates
        if len(kept) > 1:
            out.append(kept)
    return sorted(out, key=lambda r: r["_key"])


def spacecomponentsbytype(path):
    out = []
    for row in rows(path):
        kept = {"_key": row["_key"]}
        toll = row.get("alignmentBasedToll")
        if isinstance(toll, dict):
            kept["alignmentBasedToll"] = {field: toll[field] for field in TOLL_FIELDS if field in toll}
        blocker = row.get("deployBlocker")
        if isinstance(blocker, dict):
            kept["deployBlocker"] = {name: {"range": blocker[name]["range"]} for name in BLOCK_FIELDS
                                     if isinstance(blocker.get(name), dict) and "range" in blocker[name]}
        if len(kept) > 1:
            out.append(kept)
    return sorted(out, key=lambda r: r["_key"])


def systems(path):
    out = []
    for row in rows(path):
        if "wormholeClassID" in row:
            out.append({"solarSystemID": row["solarSystemID"], "wormholeClassID": row["wormholeClassID"]})
    # the server refuses a J-space system without a class; the copy must hold one for every J-space system
    jspace = {row["solarSystemID"] for row in rows(path) if row["solarSystemID"] in WORMHOLE_SPACE}
    classed = {row["solarSystemID"] for row in out if row["solarSystemID"] in WORMHOLE_SPACE}
    if jspace != classed:
        sys.exit(f"{len(jspace - classed)} J-space systems have no wormholeClassID in {path}")
    return sorted(out, key=lambda r: r["solarSystemID"])


def missions(path):
    out = []
    for row in rows(path):
        kept = {"_key": row["_key"]}
        if isinstance(row.get("contentTemplate"), str):
            kept["contentTemplate"] = row["contentTemplate"]
        if isinstance(row.get("messages"), dict):
            kept["messages"] = row["messages"]
        out.append(kept)
    return sorted(out, key=lambda r: r["_key"])


def write(target, build, source, description, fields, lines):
    header = {"_header": {"description": description, "build": build, "source": f"V24.01 client static data, {source}",
                          "generator": GENERATOR, "fields": fields, "rows": len(lines)}}
    text = "\n".join(json.dumps(line, sort_keys=True, separators=(",", ":")) for line in [header, *lines]) + "\n"
    buffer = io.BytesIO()
    with gzip.GzipFile(filename="", mode="wb", fileobj=buffer, mtime=0, compresslevel=9) as handle:
        handle.write(text.encode("utf-8"))
    os.makedirs(os.path.dirname(target), exist_ok=True)
    with open(target, "wb") as handle:
        handle.write(buffer.getvalue())
    print(f"{target}: {len(lines)} rows, {len(buffer.getvalue())} bytes")


def main():
    client = sys.argv[1] if len(sys.argv) > 1 else os.environ.get("EVE_CLIENT_SDE_DIR", "")
    if not client:
        sys.exit("usage: EVE_CLIENT_SDE_DIR=<client export dir> python3 -I tools/dev/extract-client-sde.py [<client export dir>] [<repo root>]")
    root = sys.argv[2] if len(sys.argv) > 2 else os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
    with open(os.path.join(client, "metadata", "summary.json"), encoding="utf-8") as handle:
        build = int(json.load(handle)["clientBuild"])
    native = os.path.join(client, "native")
    universe = os.path.join(root, "dotnet", "src", "Eve.Universe", "Data")
    core = os.path.join(root, "dotnet", "src", "Eve.Services.Core", "Data")

    solar = solarsystemcontent(os.path.join(native, "solarsystemcontent.jsonl.gz"))
    write(os.path.join(universe, "client-solarsystemcontent.jsonl.gz"), build, "native/solarsystemcontent.jsonl.gz",
          "Per-system flags disallowCyno and disallowScanning (only the systems that set one), and the allowed-ship type list of each stargate that has one.",
          ["_key", "disallowCyno", "disallowScanning", "stargates[].allowedShipsTypeListID"], solar)

    components = spacecomponentsbytype(os.path.join(native, "spacecomponentsbytype.jsonl.gz"))
    write(os.path.join(universe, "client-spacecomponentsbytype.jsonl.gz"), build, "native/spacecomponentsbytype.jsonl.gz",
          "Stargate type alignment tolls (alignmentBasedToll) and deploy blockers (the range of blockAnchorWarpBubbles and blockStructures), by type ID.",
          ["_key", "alignmentBasedToll.*", "deployBlocker.blockAnchorWarpBubbles.range", "deployBlocker.blockStructures.range"], components)

    system_classes = systems(os.path.join(native, "systems.jsonl.gz"))
    write(os.path.join(universe, "client-systems.jsonl.gz"), build, "native/systems.jsonl.gz",
          "The wormhole class of each solar system that has one: J-space (31000000 to 31999999) and K-space classes 7 to 9 and the others.",
          ["solarSystemID", "wormholeClassID"], system_classes)

    mission_rows = missions(os.path.join(native, "missions.jsonl.gz"))
    write(os.path.join(core, "client-missions.jsonl.gz"), build, "native/missions.jsonl.gz",
          "Per mission: contentTemplate (the trade and mining kinds) and messages (the localization IDs of its texts; no text is copied).",
          ["_key", "contentTemplate", "messages"], mission_rows)


if __name__ == "__main__":
    main()

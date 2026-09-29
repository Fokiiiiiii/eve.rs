#!/usr/bin/env python3
"""Writes crates/eve-space/data/undock_points.json: where a ship leaves each type of station.

    tools/dev/extract-undock-points.py [/path/to/evejs-v0.12.9]

The SDE says where a station is but not where its ships come out; the reference server carries a table of its own for it
(`tools/DatabaseCreator/staticTables/stationGraphicLocators`, made from the client's graphics: each station type's directional locators, in
the station model's own frame, in metres). This keeps the undock points of it (`undockPoint`, `undockPointSmall`, `undockPointLarge`,
`undockPointCapital`, `undockPointSupercapital`), in the reference's order, because the first that suits the ship is the one that is used.
The table is game data and is used as data; none of the reference's code is copied (docs/adr/0001, oracle/PROVENANCE.md).
"""

import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "..", "..", "crates", "eve-space", "data", "undock_points.json")
EVEJS = sys.argv[1] if len(sys.argv) > 1 else "/home/user/evejs-v0.12.9"
SOURCE = os.path.join(EVEJS, "tools", "DatabaseCreator", "staticTables", "stationGraphicLocators", "data.json")

with open(SOURCE, encoding="utf-8") as handle:
    table = json.load(handle)

stations = {}
for row in table["locators"]:
    points = [
        [d["category"], [d["position"]["x"], d["position"]["y"], d["position"]["z"]], [d["direction"]["x"], d["direction"]["y"], d["direction"]["z"]]]
        for d in row["directionalLocators"]
        if d["category"].startswith("undockPoint")
    ]
    if points:
        stations[str(row["stationTypeID"])] = points

out = {
    "source": "EVE.js v0.12.9 tools/DatabaseCreator/staticTables/stationGraphicLocators/data.json (client graphics, reverse engineered)",
    "note": "per station type: [category, position (x, y, z) in the model's frame in metres, direction], in the reference's order",
    "stations": dict(sorted(stations.items(), key=lambda kv: int(kv[0]))),
}
with open(OUT, "w", encoding="utf-8") as handle:
    json.dump(out, handle, separators=(",", ":"))
    handle.write("\n")
print(f"{len(stations)} station types, {sum(len(v) for v in stations.values())} undock points -> {os.path.normpath(OUT)}")

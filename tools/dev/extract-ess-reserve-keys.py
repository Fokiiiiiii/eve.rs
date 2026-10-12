#!/usr/bin/env python3
"""Extract the ESS Reserve Security Keys from the pinned SDE (essreservekeys.jsonl.gz) into the embedded server data file.

    python3 -I tools/dev/extract-ess-reserve-keys.py <sde native dir> dotnet/src/Eve.Services.Space/Ess/Data/ess-reserve-keys.json

Each key opens one Reserve Bank for totalPulses pulses (one pulse a minute). The quadrant of a key is the one in its SDE name
("ESS-NW-15 Reserve Security Key", read from sde.types in the eve database); the SDE rows carry only localized name ids.
"""
import gzip
import json
import sys

# the English names of the eight keys, as sde.types holds them (doc -> name -> en)
KEY_NAMES = {
    57714: "ESS-NW-15 Reserve Security Key",
    57715: "ESS-NW-45 Reserve Security Key",
    57716: "ESS-NE-15 Reserve Security Key",
    57717: "ESS-SE-15 Reserve Security Key",
    57718: "ESS-SE-45 Reserve Security Key",
    57719: "ESS-NE-45 Reserve Security Key",
    57720: "ESS-SW-15 Reserve Security Key",
    57721: "ESS-SW-45 Reserve Security Key",
}


def main() -> None:
    source_dir, target = sys.argv[1], sys.argv[2]
    rows = []
    with gzip.open(f"{source_dir.rstrip('/')}/essreservekeys.jsonl.gz", "rt", encoding="utf-8") as handle:
        for line in handle:
            row = json.loads(line)
            key_type = int(row["keyTypeID"])
            name = KEY_NAMES.get(key_type)
            if name is None:
                sys.exit(f"ESS reserve key {key_type} has no name in this extractor")
            rows.append({
                "keyTypeId": key_type,
                "name": name,
                "quadrant": name.split("-")[1],
                "totalPulses": int(row["totalPulses"]),
                "locationListId": int(row["locationListID"]),
            })
    rows.sort(key=lambda row: row["keyTypeId"])
    if len(rows) != 8:
        sys.exit(f"expected 8 ESS reserve keys in the SDE, found {len(rows)}")
    document = {
        "source": "SDE essreservekeys (native/essreservekeys.jsonl.gz), build 3396210; names from sde.types",
        "keys": rows,
    }
    with open(target, "w", encoding="utf-8") as handle:
        json.dump(document, handle, indent=2)
        handle.write("\n")


if __name__ == "__main__":
    main()

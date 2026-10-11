#!/usr/bin/env python3
"""Extract the ESS Reserve Security Keys from the pinned SDE (essreservekeys.jsonl.gz) into the embedded server data file.

    python3 -I tools/dev/extract-ess-reserve-keys.py <sde native dir> dotnet/src/Eve.Services.Space/Ess/Data/ess-reserve-keys.json

Each key opens one Reserve Bank for totalPulses pulses (one pulse a minute). The output keeps the SDE ids unchanged.
"""
import gzip
import json
import sys


def main() -> None:
    source_dir, target = sys.argv[1], sys.argv[2]
    rows = []
    with gzip.open(f"{source_dir.rstrip('/')}/essreservekeys.jsonl.gz", "rt", encoding="utf-8") as handle:
        for line in handle:
            row = json.loads(line)
            rows.append({
                "keyTypeId": int(row["keyTypeID"]),
                "totalPulses": int(row["totalPulses"]),
                "locationListId": int(row["locationListID"]),
            })
    rows.sort(key=lambda row: row["keyTypeId"])
    if len(rows) != 8:
        sys.exit(f"expected 8 ESS reserve keys in the SDE, found {len(rows)}")
    document = {
        "source": "SDE essreservekeys (native/essreservekeys.jsonl.gz), build 3396210",
        "keys": rows,
    }
    with open(target, "w", encoding="utf-8") as handle:
        json.dump(document, handle, indent=2)
        handle.write("\n")


if __name__ == "__main__":
    main()

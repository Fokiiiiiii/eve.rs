#!/usr/bin/env bash
# Fetch an EVE static data export (JSONL) into .cache/sde/ and record it in sde/lock.json.
#
#   sde/fetch.sh            the pinned build (3396210, the client build this project targets)
#   sde/fetch.sh latest     whatever CCP currently publishes
#   sde/fetch.sh 3400000    a specific build
#
# The data is CCP's; it is downloaded for development and never committed
# (.cache/ is git-ignored). SDE_BASE_URL overrides the host, for tests.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BASE="${SDE_BASE_URL:-https://developers.eveonline.com/static-data}"
PINNED_BUILD=3396210
CACHE="$HERE/.cache/sde"
LOCK="$HERE/sde/lock.json"

want="${1:-$PINNED_BUILD}"
if [ "$want" = "latest" ]; then
  # latest.jsonl has a record {"_key":"sde","buildNumber":N,...}
  build="$(curl -fsSL --retry 3 "$BASE/tranquility/latest.jsonl" \
    | python3 -c 'import json,sys
for line in sys.stdin:
    line = line.strip()
    if line:
        record = json.loads(line)
        if record.get("_key") == "sde":
            print(record["buildNumber"]); break
else:
    sys.exit("no sde record in latest.jsonl")')"
else
  build="$want"
fi
case "$build" in ''|*[!0-9]*) echo "bad build number: $build" >&2; exit 2;; esac

name="eve-online-static-data-$build-jsonl"
url="$BASE/tranquility/$name.zip"
archive="$CACHE/$name.zip"
dir="$CACHE/$name"
mkdir -p "$CACHE"

if [ -f "$dir/.complete" ]; then
  echo "build $build is already extracted: $dir"
else
  echo "downloading $url"
  curl -fL --retry 3 -o "$archive.partial" "$url"
  python3 - "$archive.partial" <<'PY'
import sys, zipfile
with zipfile.ZipFile(sys.argv[1]) as z:
    bad = z.testzip()
    if bad:
        sys.exit(f"corrupt zip member: {bad}")
    if not any(n.endswith("_sde.jsonl") for n in z.namelist()):
        sys.exit("not an SDE archive: no _sde.jsonl")
PY
  mv "$archive.partial" "$archive"
  rm -rf "$dir.partial"; mkdir -p "$dir.partial"
  python3 - "$archive" "$dir.partial" <<'PY'
import os, sys, zipfile
archive, out = sys.argv[1:3]
with zipfile.ZipFile(archive) as z:
    for member in z.infolist():
        if member.is_dir():
            continue
        # flatten: the JSONL files live at the top of the archive (or one directory down)
        target = os.path.join(out, os.path.basename(member.filename))
        with z.open(member) as src, open(target, "wb") as dst:
            while chunk := src.read(1 << 20):
                dst.write(chunk)
PY
  touch "$dir.partial/.complete"
  rm -rf "$dir"; mv "$dir.partial" "$dir"
fi

# Record what we have: hash of the archive (if still present), sizes and line counts of each table.
python3 - "$LOCK" "$build" "$url" "$archive" "$dir" <<'PY'
import datetime, hashlib, json, os, sys
lock_path, build, url, archive, directory = sys.argv[1:6]
try:
    lock = json.load(open(lock_path))
except FileNotFoundError:
    lock = {"note": "SDE builds fetched for development. The data itself is never committed.", "builds": {}}

def line_count(path):
    with open(path, "rb") as f:
        return sum(1 for line in f if line.strip())

tables = {n[:-6]: line_count(os.path.join(directory, n)) for n in sorted(os.listdir(directory)) if n.endswith(".jsonl")}
entry = lock["builds"].get(build, {})
entry.update({"url": url, "tables": tables})
if os.path.exists(archive):
    digest = hashlib.sha256(open(archive, "rb").read()).hexdigest()
    if entry.get("sha256") and entry["sha256"] != digest:
        sys.exit(f"sha256 of build {build} changed: was {entry['sha256']}, now {digest}")
    entry.update({"sha256": digest, "bytes": os.path.getsize(archive)})
entry["fetchedAt"] = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
lock["builds"][build] = entry
json.dump(lock, open(lock_path, "w"), indent=2, sort_keys=True)
open(lock_path, "a").write("\n")
print(f"build {build}: {len(tables)} tables, {sum(tables.values())} records -> {directory}")
PY
echo "load it with: cargo run -p eve-sde -- load $dir --pg \$(tools/dev/pg18.sh url)"

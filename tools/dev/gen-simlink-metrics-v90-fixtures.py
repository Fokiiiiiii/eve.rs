#!/usr/bin/env python3
"""Independent v90 metrics member-count vectors, including framed malformed payloads."""
import json
import struct
from pathlib import Path

OUT = Path(__file__).resolve().parents[2] / "oracle/fixtures/simlink/tournament-metrics-v90.json"
def p(fmt, *values): return struct.pack("<" + fmt, *values)
def frame(kind, body):
    payload = bytes([kind]) + body
    return p("I", len(payload)) + payload
def identity():
    return p("QiqiqqqHqqB", 73, 30000001, 0, 0, 81, 2, 133900000000000000, 1, 140000001, 998800001, 0)
def sample(companion):
    pilot = p("qqBBHHH", 140000001, 998800001, 0, 0, 0, 0, 1) + companion + p("B", 0)
    return identity() + p("BHqBH", 0, 0, 133900000010000000, 0, 1) + pilot
def companion(kind, presence, members=b""):
    return p("qiqiBB", 6001, 123, 998800001, 1, kind, presence) + members
valid, invalid = [], []
def add(dst, name, kind, body):
    dst.append(dict(name=name, kind=kind, hex=frame(kind, body).hex()))
add(valid, "request", 154, identity())
for name, kind, tag, count in [("drone-unknown",0,0,None),("fighter-unknown",1,0,None),("fighter-zero",1,1,0),("fighter-maximum",1,1,64)]:
    add(valid, name, 155, sample(companion(kind, tag, b"" if count is None else p("i",count))))
for name, kind, tag, value in [("fighter-negative",1,1,p("i",-1)),("fighter-over-limit",1,1,p("i",65)),("invalid-presence",1,2,b""),("drone-count",0,1,p("i",1))]:
    add(invalid, name, 155, sample(companion(kind,tag,value)))
raw = frame(155, sample(companion(1,1,p("i",12))))
# Retain a complete frame length while truncating the optional i32 itself.
needle = p("qiqiBBi",6001,123,998800001,1,1,1,12)
pos = raw.index(needle) + len(needle) - 4
broken = raw[:pos] + raw[pos:pos+2]
invalid.append(dict(name="truncated-members",kind=155,hex=broken.hex()))
OUT.write_text(json.dumps(dict(note="Independent v90 framed member-count vectors",valid=valid,invalid=invalid),indent=1)+"\n",encoding="utf-8")
print(f"{len(valid)} valid, {len(invalid)} invalid -> {OUT}")

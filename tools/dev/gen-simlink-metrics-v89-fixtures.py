#!/usr/bin/env python3
"""Independent v89 tournament metrics wire vectors; frames include the u32 length."""
import json
import struct
from pathlib import Path

OUT = Path(__file__).resolve().parents[2] / "oracle/fixtures/simlink/tournament-metrics-v89.json"
def p(fmt, *values): return struct.pack("<" + fmt, *values)
def text(value): return p("H", len(value)) + value
def roster(n=2): return [(140000001+i, 998800001+i, i % 2) for i in range(n)]
def ident(rows): return p("QiqiqqqH", 73, 30000001, 0, 0, 81, 2, 133900000000000000, len(rows)) + b"".join(p("qqB", *row) for row in rows)
def frame(kind, body):
    payload = bytes([kind]) + body
    return p("I", len(payload)) + payload

def pilot(row, fitted=(), running=(), companions=(), ehp=0, ehp_value=0.0, state=0):
    return (p("qqBB", *row, state) + p("H", len(fitted)) + b"".join(fitted)
        + p("H", len(running)) + b"".join(running)
        + p("H", len(companions)) + b"".join(companions)
        + p("B", ehp) + (p("d", ehp_value) if ehp == 1 else b""))
def result(rows, pilots, applied=None, status=0, error=b"", at=133900000010000000):
    body = ident(rows) + p("B", status) + text(error)
    if status == 0:
        body += p("qB", at, int(applied is not None))
        if applied is not None: body += p("dd", *applied)
        body += p("H", len(pilots)) + b"".join(pilots)
    return body

valid, invalid = [], []
def add(dst, name, kind, body, **extra):
    raw = frame(kind, body)
    assert struct.unpack_from("<I", raw)[0] == len(raw)-4
    dst.append(dict(name=name, kind=kind, hex=raw.hex(), **extra))

r = roster(); one = roster(1); maximum = roster(256)
for kind in (148,150,152): add(valid, "request-"+str(kind), kind, ident(r))
for kind in (149,153):
    add(valid, "reply-ok-"+str(kind), kind, ident(r)+p("B",0)+text(b""))
    add(valid, "reply-unavailable-"+str(kind), kind, ident(r)+p("B",1)+text(b""))
    add(valid, "reply-error-"+str(kind), kind, ident(r)+p("B",2)+text("é".encode()*512))
add(valid, "roster-minimum", 148, ident(one))
add(valid, "roster-maximum", 148, ident(maximum))
fitted = [p("qiiB", 5001, 458, 27, 2)]
running = [p("qiiB", 5001, 10, 998800002, 1) if False else p("qiqB", 5001, 10, 998800002, 1) + p("iBq", 246, 1, 7)]
companions = [p("qiqiB", 6001, 123, 998800001, 1, 0)]
sample = result(r,[pilot(r[0],fitted,running,companions,1,123.5),pilot(r[1],ehp=2)],(12.5,0.0))
add(valid,"sample-all-fields",151,sample)
add(valid,"sample-unknown",151,result(one,[pilot(one[0])]))
add(valid,"sample-known-destroyed",151,result(one,[pilot(one[0],ehp=1,ehp_value=0,state=1)]))
add(valid,"sample-unavailable",151,result(one,[],status=1))
add(valid,"sample-error",151,result(one,[],status=2,error=b"unavailable"))
add(valid,"nested-limit-256",151,result(one,[pilot(one[0],[p("qiiB",5000+i,458,27,1) for i in range(256)])]))
large = roster(33)
def many(i, count): return [p("qiiB",500000+i*256+j,458,27,1) for j in range(count)]
add(valid,"nested-total-8192",151,result(large,[pilot(row,many(i,256 if i < 32 else 0)) for i,row in enumerate(large)]))
add(invalid,"nested-total-8193",151,result(large,[pilot(row,many(i,256 if i < 32 else 1)) for i,row in enumerate(large)]))
for kind in (148,150,152):
    add(invalid,"roster-empty-"+str(kind),kind,ident([]))
    add(invalid,"roster-257-"+str(kind),kind,ident(roster(257)))
add(invalid,"roster-duplicate",148,ident([r[0],r[0]]))
add(invalid,"roster-reversed",148,ident(list(reversed(r))))
add(invalid,"invalid-team",148,ident([(r[0][0],r[0][1],2)]))
add(invalid,"reply-status",149,ident(one)+p("B",3)+text(b""))
add(invalid,"error-nonempty-on-success",149,ident(one)+p("B",0)+text(b"bad"))
add(invalid,"error-empty-on-error",149,ident(one)+p("B",2)+text(b""))
add(invalid,"error-too-long",149,ident(one)+p("B",2)+text(b"x"*1025))
add(invalid,"error-invalid-utf8",149,ident(one)+p("B",2)+text(b"\xff"))
add(invalid,"sample-roster-mismatch",151,result(r,[pilot(r[1]),pilot(r[0])]))
add(invalid,"sample-missing-pilot",151,result(r,[pilot(r[0])]))
add(invalid,"sample-invalid-heat",151,result(one,[pilot(one[0],[p("qiiB",5001,458,27,3)])]))
add(invalid,"sample-invalid-pilot-state",151,result(one,[pilot(one[0],state=2)]))
add(invalid,"destroyed-unknown-ehp",151,result(one,[pilot(one[0],state=1)]))
add(invalid,"destroyed-infinite-ehp",151,result(one,[pilot(one[0],ehp=2,state=1)]))
add(invalid,"destroyed-nonzero-ehp",151,result(one,[pilot(one[0],ehp=1,ehp_value=1,state=1)]))
add(invalid,"destroyed-fitted",151,result(one,[pilot(one[0],fitted=[p("qiiB",5001,458,27,1)],ehp=1,state=1)]))
add(invalid,"destroyed-running",151,result(one,[pilot(one[0],running=[p("qiqBB",5001,10,998800002,0,0)],ehp=1,state=1)]))
add(invalid,"destroyed-companion",151,result(one,[pilot(one[0],companions=[p("qiqiB",6001,123,998800001,1,0)],ehp=1,state=1)]))
add(invalid,"sample-invalid-ehp-enum",151,result(one,[pilot(one[0],ehp=3)]))
add(invalid,"sample-negative-ehp",151,result(one,[pilot(one[0],ehp=1,ehp_value=-1)]))
add(invalid,"sample-nan-ehp",151,result(one,[pilot(one[0],ehp=1,ehp_value=float("nan"))]))
add(invalid,"sample-infinite-applied",151,result(one,[pilot(one[0])],(float("inf"),0)))
add(invalid,"sample-negative-applied",151,result(one,[pilot(one[0])],(-1,0)))
add(invalid,"nested-257",151,result(one,[pilot(one[0],[p("qiiB",5000+i,458,27,1) for i in range(257)])]))
add(invalid,"bad-charge-option",151,result(one,[pilot(one[0],running=[p("qiqB",5001,10,998800002,2)])]))
add(invalid,"bad-companion-kind",151,result(one,[pilot(one[0],companions=[p("qiqiB",6001,123,998800001,1,2)])]))
raw=frame(151,sample)
invalid.append(dict(name="truncated-last-byte",kind=151,hex=(raw[:-1]).hex()))
# The frame payload limit includes kind, and excludes the four-byte length prefix.
invalid.append(dict(name="payload-over-1mib",kind=148,hex=frame(148,b"\x00"*(1024*1024)).hex()))
OUT.parent.mkdir(parents=True,exist_ok=True)
OUT.write_text(json.dumps(dict(note="Generated by tools/dev/gen-simlink-metrics-v89-fixtures.py; framed little-endian bytes",valid=valid,invalid=invalid),indent=1)+"\n",encoding="utf-8")
print(f"{len(valid)} valid, {len(invalid)} invalid -> {OUT}")

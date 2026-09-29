#!/usr/bin/env python3
"""Rebuild a protobuf FileDescriptorSet from the generated `*_pb2.py` files of a client source tree.

The client ships only generated Python for its gateway schemas (no .proto files),
but each generated module embeds its serialized file descriptor. This collects
them into one FileDescriptorSet so a schema-aware tool can read them.

The descriptor set reproduces CCP's API schema, so it is written under
tools/spec-extract/out/ (git-ignored) and is not meant to be committed.

    python3 tools/spec-extract/protos.py /path/to/eve-logic
Needs the `protobuf` package (pip install protobuf).
"""
import ast
import collections
import pathlib
import re
import sys

from google.protobuf import descriptor_pb2

SERIALIZED = re.compile(r"serialized_pb=((?:b?'(?:[^'\\]|\\.)*')|(?:b?\"(?:[^\"\\]|\\.)*\"))", re.S)


def load_descriptor(path: pathlib.Path):
    match = SERIALIZED.search(path.read_text(errors="replace"))
    if not match:
        return None
    literal = ast.literal_eval(match.group(1))
    data = literal if isinstance(literal, bytes) else literal.encode("latin-1")
    proto = descriptor_pb2.FileDescriptorProto()
    proto.ParseFromString(data)
    return proto


def main(root: pathlib.Path) -> None:
    generated = root / "eveProto" / "generated"
    if not generated.is_dir():
        sys.exit(f"{generated} does not exist")
    fds = descriptor_pb2.FileDescriptorSet()
    failed = []
    for path in sorted(generated.rglob("*_pb2.py")):
        try:
            proto = load_descriptor(path)
        except Exception as error:  # a malformed literal: report, keep going
            failed.append((path, str(error)))
            continue
        if proto is None:
            failed.append((path, "no serialized_pb"))
        else:
            fds.file.append(proto)

    out = pathlib.Path(__file__).resolve().parent / "out"
    out.mkdir(exist_ok=True)
    (out / "eve_proto.desc").write_bytes(fds.SerializeToString())

    def count_messages(messages):
        return sum(1 + count_messages(m.nested_type) for m in messages)

    messages = sum(count_messages(f.message_type) for f in fds.file)
    services = [(f.package, s.name, len(s.method)) for f in fds.file for s in f.service]
    tops = collections.Counter(f.package.split(".")[0] for f in fds.file)
    print(f"{len(fds.file)} descriptors ({len(failed)} failed), {messages} messages, {len(services)} services")
    print("top-level packages:", dict(tops))
    for package, name, methods in services[:10]:
        print(f"  service {package}.{name}: {methods} methods")
    for path, reason in failed[:5]:
        print(f"  FAILED {path.relative_to(root)}: {reason}", file=sys.stderr)
    print(f"wrote {out / 'eve_proto.desc'}")


if __name__ == "__main__":
    if len(sys.argv) != 2:
        sys.exit(__doc__)
    main(pathlib.Path(sys.argv[1]))

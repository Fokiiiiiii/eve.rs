#!/usr/bin/env python3
"""List the remote service names and methods a client source tree calls.

Reads a local copy of the decompiled client as a read-only reference and writes
only facts: service names, method names and how often they are called. No client
code is copied.

    python3 tools/spec-extract/services.py /path/to/eve-logic > docs/reference/spec/services.tsv
"""
import collections
import pathlib
import re
import sys

# Calls that name a service the client reaches over the wire.
REMOTE_SVC = re.compile(r"""RemoteSvc\(\s*['"]([A-Za-z0-9_]+)['"]\s*\)(?:\s*\.\s*([A-Za-z_][A-Za-z0-9_]*))?""")
CONNECT = re.compile(
    r"""(?:ConnectToRemoteService|ConnectToAnyService|ConnectToSolServerService|"""
    r"""ConnectToProxyServerService|ConnectToService)\(\s*['"]([A-Za-z0-9_]+)['"]""")


def main(root: pathlib.Path) -> None:
    pairs = collections.Counter()
    names = collections.Counter()
    via = collections.defaultdict(set)
    files = 0
    for path in root.rglob("*.py"):
        try:
            text = path.read_text(errors="replace")
        except OSError:
            continue
        files += 1
        for service, method in REMOTE_SVC.findall(text):
            names[service] += 1
            via[service].add("RemoteSvc")
            if method:
                pairs[(service, method)] += 1
        for service in CONNECT.findall(text):
            names[service] += 1
            via[service].add("Connect")

    print("service\tmethod\tcalls\tvia")
    for service in sorted(names):
        print(f"{service}\t\t{names[service]}\t{'+'.join(sorted(via[service]))}")
        for (svc, method), count in sorted(pairs.items()):
            if svc == service:
                print(f"{service}\t{method}\t{count}\t")
    remote = sum(1 for s in names if "RemoteSvc" in via[s])
    print(f"# scanned {files} files: {len(names)} service names ({remote} via RemoteSvc), "
          f"{len(pairs)} service.method pairs", file=sys.stderr)


if __name__ == "__main__":
    if len(sys.argv) != 2:
        sys.exit(__doc__)
    main(pathlib.Path(sys.argv[1]))

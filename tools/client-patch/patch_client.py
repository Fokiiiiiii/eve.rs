#!/usr/bin/env python3
"""Point a local, unmodified EVE Online client (build 3396210) at an eve.rs server.

The tool changes exactly one file: <client-dir>/start.ini, the client's own
settings file, where it sets the server address and the crypto pack. It never
writes anything under bin64/ or ResFiles. Before the first change the original
bytes are saved to start.ini.eve-rs-backup; --restore copies them back and
removes that backup.

It refuses to act unless start.ini says build 3396210 and bin64/exefile.exe is
the unmodified build-3396210 executable (size and SHA-256 below). Python 3
standard library only; runs on Windows and Linux.
"""

import argparse
import hashlib
import ipaddress
import os
import re
import sys

SUPPORTED_BUILD = "3396210"
EXE_RELPATH = os.path.join("bin64", "exefile.exe")
EXE_SIZE = 1003152
EXE_SHA256 = "2aaf7a9a8dfcde85e4adb50c1eccd3756a4d29aeb854dfe69629846ba56ee979"
BACKUP_NAME = "start.ini.eve-rs-backup"
DEFAULT_SERVER = "127.0.0.1:26000"
DEFAULT_PORT = 26000
CRYPTO_PACK = "Placebo"
SERVER_KEYS = ("server", "serverip")
CRYPTO_KEYS = ("cryptopack",)

_KEY_LINE = re.compile(r"^(?:\xef\xbb\xbf)?\s*([A-Za-z_]+)\s*=\s*(.*)$")
_PREFIX = re.compile(r"^((?:\xef\xbb\xbf)?\s*[A-Za-z_]+\s*=\s*)")


class ClientError(Exception):
    """A refusal: the client is not the expected build, or a value is unusable."""


def parse_server(value):
    """Return (ipv4, port) from HOST or HOST:PORT. Host names are never resolved."""
    host, _, port_text = value.partition(":")
    if port_text and not port_text.isdigit():
        raise ClientError("--server port must be a number: %r" % value)
    port = int(port_text) if port_text else DEFAULT_PORT
    if not 1 <= port <= 65535:
        raise ClientError("--server port must be 1-65535: %r" % value)
    try:
        address = ipaddress.IPv4Address(host)
    except ValueError:
        raise ClientError("--server host must be an IPv4 literal such as 127.0.0.1 (names are not resolved)")
    if address.is_unspecified or address.is_multicast or address == ipaddress.IPv4Address("255.255.255.255"):
        raise ClientError("--server host %s is not a server address" % address)
    return str(address), port


def load_ini(path):
    """Return (raw bytes, lines). Lines keep their own endings, so untouched bytes survive."""
    with open(path, "rb") as f:
        raw = f.read()
    parts = raw.decode("latin-1").split("\n")
    lines = [part + "\n" for part in parts[:-1]]
    if parts[-1]:
        lines.append(parts[-1])
    return raw, lines


def ini_values(lines, keys):
    """Return (index, value) for every line whose key is one of keys (case-insensitive)."""
    found = []
    for i, line in enumerate(lines):
        match = _KEY_LINE.match(line.rstrip("\r\n"))
        if match and match.group(1).lower() in keys:
            found.append((i, match.group(2).strip()))
    return found


def set_ini_key(lines, keys, value, default_key):
    """Set every matching key to value; append default_key if none exists."""
    hits = ini_values(lines, keys)
    out = list(lines)
    if not hits:
        eol = "\r\n" if any(line.endswith("\r\n") for line in lines) else "\n"
        if out and not out[-1].endswith("\n"):
            out[-1] += eol
        out.append("%s = %s%s" % (default_key, value, eol))
        return out
    for i, _old in hits:
        body = out[i].rstrip("\r\n")
        eol = out[i][len(body):]
        out[i] = _PREFIX.match(body).group(1) + value + eol
    return out


def is_configured(lines, host):
    servers = ini_values(lines, SERVER_KEYS)
    crypto = ini_values(lines, CRYPTO_KEYS)
    return (bool(servers) and all(v == host for _, v in servers)
            and bool(crypto) and all(v == CRYPTO_PACK for _, v in crypto))


def sha256_file(path):
    digest = hashlib.sha256()
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            digest.update(block)
    return digest.hexdigest()


def check_client(client_dir, lines):
    """Refuse anything other than the unmodified build-3396210 client."""
    builds = [value for _, value in ini_values(lines, ("build",))]
    if builds != [SUPPORTED_BUILD]:
        raise ClientError("start.ini must contain exactly one 'build = %s' line (found: %s)."
                          % (SUPPORTED_BUILD, builds or "none"))
    exe = os.path.join(client_dir, EXE_RELPATH)
    if not os.path.isfile(exe):
        raise ClientError("missing %s" % exe)
    if os.path.getsize(exe) != EXE_SIZE or sha256_file(exe) != EXE_SHA256:
        raise ClientError("%s is not the unmodified build %s executable (size or SHA-256 differs). "
                          "A patched or different client is refused." % (exe, SUPPORTED_BUILD))


def write_atomic(path, data):
    tmp = path + ".eve-rs-tmp"
    with open(tmp, "wb") as f:
        f.write(data)
        f.flush()
        os.fsync(f.fileno())
    os.replace(tmp, path)


def _ini_path(client_dir):
    if not os.path.isdir(client_dir):
        raise ClientError("--client-dir is not a folder: %s" % client_dir)
    ini = os.path.join(client_dir, "start.ini")
    if not os.path.isfile(ini):
        raise ClientError("missing %s" % ini)
    return ini


def apply(client_dir, host, port, dry_run=False):
    ini = _ini_path(client_dir)
    raw, lines = load_ini(ini)
    check_client(client_dir, lines)
    new_lines = set_ini_key(lines, SERVER_KEYS, host, "server")
    new_lines = set_ini_key(new_lines, CRYPTO_KEYS, CRYPTO_PACK, "cryptoPack")
    new_raw = "".join(new_lines).encode("latin-1")
    if new_raw == raw:
        print("[ok] start.ini already points at %s:%d with cryptoPack = %s. Nothing to change."
              % (host, port, CRYPTO_PACK))
        return 0
    before = [v for _, v in ini_values(lines, SERVER_KEYS)] or ["(absent)"]
    print("[plan] start.ini server: %s -> %s" % (", ".join(before), host))
    print("[plan] start.ini cryptoPack: %s -> %s" % (
        ", ".join(v for _, v in ini_values(lines, CRYPTO_KEYS)) or "(absent)", CRYPTO_PACK))
    print("[plan] launch with port %d (the port is not stored in start.ini)" % port)
    if dry_run:
        print("[dry-run] no file was changed.")
        return 0
    backup = os.path.join(client_dir, BACKUP_NAME)
    if not os.path.exists(backup):
        write_atomic(backup, raw)
        print("[backup] %s" % backup)
    write_atomic(ini, new_raw)
    _, after = load_ini(ini)
    if not is_configured(after, host):
        raise ClientError("start.ini did not verify after writing. Run --restore and check the file.")
    print("[ok] start.ini configured for %s:%d. Verify with --dry-run." % (host, port))
    return 0


def restore(client_dir, dry_run=False):
    ini = _ini_path(client_dir)
    backup = os.path.join(client_dir, BACKUP_NAME)
    if not os.path.isfile(backup):
        raise ClientError("no %s; nothing to restore" % backup)
    if dry_run:
        print("[dry-run] would replace %s with %s and remove the backup." % (ini, backup))
        return 0
    with open(backup, "rb") as f:
        original = f.read()
    write_atomic(ini, original)
    os.remove(backup)
    print("[ok] start.ini restored from the backup taken before the first change.")
    return 0


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("--client-dir", required=True,
                        help="client folder that holds start.ini and bin64 (the EVE 'tq' folder). Use a copy.")
    parser.add_argument("--server", default=DEFAULT_SERVER, metavar="HOST[:PORT]",
                        help="IPv4 address of the eve.rs front (default %(default)s)")
    parser.add_argument("--dry-run", action="store_true", help="show the plan and verify, change nothing")
    parser.add_argument("--restore", action="store_true", help="restore start.ini from the backup")
    args = parser.parse_args(argv)
    try:
        if args.restore:
            return restore(args.client_dir, args.dry_run)
        host, port = parse_server(args.server)
        return apply(args.client_dir, host, port, args.dry_run)
    except ClientError as error:
        print("[error] %s" % error, file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())

"""python -m evecli --host 127.0.0.1 --port 26000 --user U --password P"""

import argparse
import importlib
import os
import subprocess
import sys
import time
from pathlib import Path

from . import handshake, packet
from .client import Client, filetime_to_unix


def _show(value, limit=200) -> str:
    text = repr(value)
    return text if len(text) <= limit else text[:limit] + "..."


def _find_official_exefile(launcher_root):
    if not launcher_root:
        raise FileNotFoundError(
            "Pass --launcher-root or set EVE_LAUNCHER_ROOT to the EVE Launcher installation directory."
        )

    root = Path(launcher_root).expanduser()
    candidates = (
        root / "SharedCache" / "tq" / "bin64" / "exefile.exe",
        root / "SharedCache" / "tq" / "bin" / "exefile.exe",
        root / "bin64" / "exefile.exe",
        root / "bin" / "exefile.exe",
        root / "exefile.exe",
    )
    for executable in candidates:
        if executable.is_file():
            return executable.resolve()
    raise FileNotFoundError(f"No Launcher-managed exefile.exe found under {root}")


def _prepare_headless_client():
    try:
        thinclients = importlib.import_module("thinclients")
        thinclients.HEADLESS = True
        clientsetup = importlib.import_module("thinclients.clientsetup")
        patch_all = clientsetup.patch_all
    except (ImportError, AttributeError) as error:
        raise RuntimeError("thinclients headless bootstrap is unavailable in this Python environment.") from error
    patch_all()


def _launch_official_client(launcher_root):
    executable = _find_official_exefile(launcher_root)
    _prepare_headless_client()
    environment = os.environ.copy()
    environment["EO_REMOTEFILECACHEFOLDER"] = str(executable.parent.parent.parent / "ResFiles")
    return subprocess.Popen([str(executable), "/thinclient"], cwd=str(executable.parent.parent), env=environment)


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(prog="evecli", description="A headless EVE client.")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=26000)
    parser.add_argument("--user", default="evecli")
    parser.add_argument("--password", default="evecli")
    parser.add_argument("--timeout", type=float, default=10.0)
    parser.add_argument(
        "--official-client",
        action="store_true",
        help="launch the Launcher-managed official client headlessly",
    )
    parser.add_argument(
        "--launcher-root",
        default=os.environ.get("EVE_LAUNCHER_ROOT"),
        help="EVE Launcher installation root (or EVE_LAUNCHER_ROOT)",
    )
    parser.add_argument("--call", action="append", default=[], metavar="SERVICE.METHOD",
                        help="also call a service method without arguments (repeatable)")
    args = parser.parse_args(argv)
    if args.official_client:
        try:
            process = _launch_official_client(args.launcher_root)
        except (OSError, RuntimeError) as error:
            print(f"official client launch failed: {error}", file=sys.stderr)
            return 4
        print(f"official client pid: {process.pid}")
        return 0

    with Client(args.host, args.port, args.timeout) as client:
        try:
            session = client.login(args.user, args.password)
        except handshake.LoginRejected as error:
            print(f"login rejected: {error.reason}", file=sys.stderr)
            return 2
        except (OSError, handshake.HandshakeError) as error:
            print(f"handshake failed: {error}", file=sys.stderr)
            return 1

        server = session.server
        print(f"server: macho {server.macho_version}, build {server.boot_build}, "
              f"version {server.boot_version}, users {server.user_count}")
        print(f"session: user {session.user_id}, client {session.client_id}, "
              f"session id {session.session_id}, role {session.role}")

        try:
            now = filetime_to_unix(client.call("machoNet", "GetTime"))
            print(f"machoNet.GetTime: {time.strftime('%Y-%m-%d %H:%M:%S', time.gmtime(now))} UTC "
                  f"(local skew {time.time() - now:+.2f}s)")
            print(f"ping: {client.ping() * 1000:.1f} ms")
            for spec in args.call:
                service, _, method = spec.partition(".")
                print(f"{spec}: {_show(client.call(service, method))}")
        except (packet.MachoError, TimeoutError) as error:
            print(f"call failed: {error}", file=sys.stderr)
            return 3
    return 0


if __name__ == "__main__":
    sys.exit(main())

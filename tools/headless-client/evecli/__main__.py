"""python -m evecli --host 127.0.0.1 --port 26000 --user U --password P"""

import argparse
import sys
import time

from . import handshake, packet
from .client import Client, filetime_to_unix


def _show(value, limit=200) -> str:
    text = repr(value)
    return text if len(text) <= limit else text[:limit] + "..."


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(prog="evecli", description="A headless EVE client.")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=26000)
    parser.add_argument("--user", default="evecli")
    parser.add_argument("--password", default="evecli")
    parser.add_argument("--timeout", type=float, default=10.0)
    parser.add_argument("--call", action="append", default=[], metavar="SERVICE.METHOD",
                        help="also call a service method without arguments (repeatable)")
    args = parser.parse_args(argv)

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

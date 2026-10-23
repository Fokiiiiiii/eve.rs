"""The client against a real server (the EVE.js reference, later eve.rs).

Skipped unless EVE_LIVE_HOST=host:port is set. tools/dev/run-evejs-live.sh
builds the reference server's data from an SDE, starts it, and runs this file.

The reference server is a development server: it creates accounts on first
login and skips password validation, so a wrong password is only rejected when
EVE_LIVE_STRICT_PASSWORDS=1 (which a hardened server such as eve.rs would set).
"""

import os
import subprocess
import sys
import time
from pathlib import Path

import pytest

from evecli import handshake, packet
from evecli import marshal as m
from evecli.client import Client, filetime_to_unix

LIVE = os.environ.get("EVE_LIVE_HOST", "")
pytestmark = pytest.mark.skipif(not LIVE, reason="set EVE_LIVE_HOST=host:port to test against a running server")

def _client() -> Client:
    host, _, port = LIVE.partition(":")
    return Client(host or "127.0.0.1", int(port or 26000), timeout=20.0)

def test_login_reports_the_release_and_a_session():
    with _client() as client:
        session = client.login("evecli-live", "evecli-live")
        server = session.server
        assert server.birthday == 170472
        assert server.macho_version == 496
        assert session.user_id > 0
        assert int(session.client_id) >= 1_000_000
        assert int(session.session_id) > 0
        assert m.plain(session.handshake["macho_version"]) == 496
        assert "config_vals" in session.handshake

def test_server_time_is_current_and_ping_answers():
    with _client() as client:
        client.login("evecli-live", "evecli-live")
        unix = filetime_to_unix(client.call("machoNet", "GetTime"))
        assert abs(unix - time.time()) < 30
        assert 0 < client.ping() < 5

def test_get_init_vals_lists_services_and_config():
    with _client() as client:
        client.login("evecli-live", "evecli-live")
        service_info, global_config = m.plain(client.call("machoNet", "GetInitVals"))
        assert isinstance(service_info, dict) and "machoNet" in service_info
        assert isinstance(global_config, dict) and global_config

def test_unknown_service_and_method_are_answered_not_a_hang():
    # The reference answers a call it cannot dispatch with a None result (only a
    # wrapped exception becomes an error response), so the client must get an
    # answer either way rather than time out.
    with _client() as client:
        client.login("evecli-live", "evecli-live")
        for service, method in (("noSuchService", "Nothing"), ("machoNet", "NoSuchMethod")):
            try:
                result = client.call(service, method)
            except packet.MachoError:
                continue
            assert m.plain(result) is None

@pytest.mark.skipif(not os.environ.get("EVE_LIVE_STRICT_PASSWORDS"), reason="the server does not validate passwords")
def test_wrong_password_is_rejected():
    with _client() as client:
        client.login("evecli-strict", "right")
    with _client() as client:
        with pytest.raises(handshake.LoginRejected):
            client.login("evecli-strict", "wrong")


def test_login_rate_limit_rejects_the_correct_password_after_ten_failures():
    account = f"evecli-rl-{os.urandom(6).hex()}"
    with _client() as client:
        client.login(account, "right")

    for _ in range(10):
        with _client() as client:
            with pytest.raises(handshake.LoginRejected):
                client.login(account, "wrong")

    with _client() as client:
        with pytest.raises(handshake.LoginRejected):
            client.login(account, "right")


def test_reconnect_after_client_process_exits_abruptly():
    host, separator, port = LIVE.partition(":")
    assert separator and host and port
    account = f"evecli-re-{os.urandom(6).hex()}"
    code = (
        "import os, sys; "
        "from evecli.client import Client; "
        "Client(sys.argv[1], int(sys.argv[2]), timeout=20).login(sys.argv[3], sys.argv[4]); "
        "os._exit(0)"
    )
    result = subprocess.run(
        [sys.executable, "-c", code, host, port, account, "right"],
        capture_output=True,
        timeout=30,
        cwd=Path(__file__).resolve().parents[1],
    )
    assert result.returncode == 0, result.stderr.decode(errors="replace")

    with _client() as client:
        client.login(account, "right")

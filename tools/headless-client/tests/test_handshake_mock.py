"""The client against a scripted server (no game data needed)."""

import pytest

from evecli import handshake, packet
from evecli import marshal as m
from evecli.client import Client, filetime_to_unix

from .mock_server import MockServer


def test_password_hash_matches_the_clients_recipe():
    # An independent restatement of machobase.PasswordHash
    import hashlib
    salt = "  Pilot ".strip().encode("utf-16-le").lower()
    digest = hashlib.sha1("hunter2".encode("utf-16-le") + salt).digest()
    for _ in range(1000):
        digest = hashlib.sha1(digest + salt).digest()
    assert handshake.password_hash("  Pilot ", "hunter2") == digest
    assert len(digest) == 20
    # The salt is the *lower-cased* user name, so the case of the name does not matter...
    assert handshake.password_hash("PILOT", "hunter2") == handshake.password_hash("pilot", "hunter2")
    # ...but the password's case does.
    assert handshake.password_hash("pilot", "Hunter2") != handshake.password_hash("pilot", "hunter2")


def test_login_and_calls():
    with MockServer() as server:
        server.add_account("pilot", "hunter2")
        with Client("127.0.0.1", server.port) as client:
            session = client.login("pilot", "hunter2")

            assert session.server.macho_version == 496
            assert session.server.boot_build == 3396210
            assert session.user_id == 7
            assert int(session.client_id) == 7_000_001
            assert session.handshake["macho_version"] == 496
            assert m.plain(session.handshake["config_vals"]) == {"imageserverurl": "http://127.0.0.1:26001/"}

            unix = filetime_to_unix(client.call("machoNet", "GetTime"))
            import time
            assert abs(unix - time.time()) < 5

            assert m.plain(client.call("echo", "Echo", 1, "two", flag=True)) == ((1, "two"), {"flag": True})
            assert 0 < client.ping() < 5

    kinds = [kind for kind, _ in server.received]
    assert kinds[:5] == ["version", "vk", "crypto", "login", "handshake-result"]
    assert not server.errors

    login = dict(server.received[3][1])
    assert m.plain(login["user_name"]) == "pilot"
    assert bytes(login["user_password_hash"]) == handshake.password_hash("pilot", "hunter2")
    assert login["user_password"] is None
    # The crypto request carries no keys: nothing is encrypted
    assert m.plain(server.received[2][1][0]) == "placebo"


def test_wrong_password_is_rejected():
    with MockServer() as server:
        server.add_account("pilot", "hunter2")
        with Client("127.0.0.1", server.port) as client:
            with pytest.raises(handshake.LoginRejected) as info:
                client.login("pilot", "wrong")
    assert info.value.reason == "LoginAuthFailed"


def test_unknown_account_can_be_auto_created():
    with MockServer(auto_create=True) as server:
        with Client("127.0.0.1", server.port) as client:
            assert client.login("newcomer", "pw").user_id == 7
        # The account now exists: the same password works again, another does not.
        with Client("127.0.0.1", server.port) as client:
            client.login("newcomer", "pw")
        with Client("127.0.0.1", server.port) as client:
            with pytest.raises(handshake.LoginRejected):
                client.login("newcomer", "different")


def test_error_response_becomes_an_exception():
    with MockServer() as server:
        server.add_account("pilot", "hunter2")
        with Client("127.0.0.1", server.port) as client:
            client.login("pilot", "hunter2")
            with pytest.raises(packet.MachoError) as info:
                client.call("noSuchService", "Nothing")
    assert info.value.code == 2
    assert "noSuchService.Nothing" in str(m.plain(info.value.payload))


def test_call_request_wire_shape():
    """What the server receives is the reference's CallReq shape."""
    with MockServer() as server:
        server.add_account("pilot", "hunter2")
        with Client("127.0.0.1", server.port) as client:
            client.login("pilot", "hunter2")
            client.call("machoNet", "GetTime")
    request = next(v for k, v in server.received if k == "packet" and v.command == packet.CALL_REQ)
    assert request.destination.kind == packet.ADDR_ANY and request.destination.service == "machoNet"
    assert request.source.kind == packet.ADDR_CLIENT and request.source.call_id == 1
    assert int(request.source.client_id) == 7_000_001
    assert request.user_id == 7
    is_bound, stream = request.payload[0]
    assert is_bound == 0 and isinstance(stream, m.SubStream)
    remote_object, method, args, kwargs = stream.value
    assert (remote_object, args) == (1, ()) and bytes(method).decode() == "GetTime"

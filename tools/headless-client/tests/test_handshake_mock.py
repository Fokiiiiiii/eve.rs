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
            assert client.service_info["machoNet"] is None
            assert client.service_info["stationSvc"] == "station"

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

def test_service_info_routes_routed_services_through_a_cached_node():
    client = Client()
    client.service_info = {
        "machoNet": None,
        "stationSvc": "station",
        "invbroker": "station",
        "beyonce": "solarsystem2",
    }
    client._service_nodes["stationSvc"] = 65_450

    assert client._address_for_service("machoNet") == packet.any_address("machoNet")
    assert client._address_for_service("invbroker") == (packet.ADDR_NODE, 65_450, "invbroker", None)
    assert client._address_for_service("beyonce") == packet.any_address("beyonce")

def test_session_change_is_applied_while_waiting_for_call_response():
    session = handshake.SessionInfo(
        server=handshake.ServerVersion(1, 496, 0, 1, 3_396_210, "EVE"),
        user_name="pilot",
        user_id=7,
        client_id=7_000_001,
        session_id=m.Long(12),
        role=0,
        session_init={"userid": 7, "role": 0},
    )
    client = Client()
    client.session = session

    change = packet.build_packet(
        packet.SESSIONCHANGENOTIFICATION,
        packet.any_address(),
        packet.client_address(session.client_id),
        session.user_id,
        (
            m.Long(13),
            (0, m.Dict([
                ("charid", (None, m.Long(42))),
                ("solarsystemid", (None, m.Long(30_000_142))),
            ])),
            m.List([65_450]),
        ),
        m.Dict(),
    )
    response = packet.build_packet(
        packet.CALL_RSP,
        packet.any_address("echo"),
        packet.client_address(session.client_id, 9),
        session.user_id,
        (m.SubStream(value=m.PyString(b"ok")),),
        m.Dict(),
    )

    class PacketQueue:
        def __init__(self, *items):
            self.items = list(items)

        def recv(self):
            return self.items.pop(0)

    client.conn = PacketQueue(change, response)
    reply = client._await(packet.CALL_RSP, 9)

    assert m.plain(packet.result_of(reply)) == "ok"
    assert m.plain(session.session_id) == 13
    assert m.plain(session.session_init["charid"]) == 42
    assert m.plain(session.session_init["solarsystemid"]) == 30_000_142
    assert client.notifications[0].command == packet.SESSIONCHANGENOTIFICATION

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
    request = next(v for k, v in server.received if k == "packet" and v.command == packet.CALL_REQ and m.plain(v.payload[0][1].value[1]) == "GetTime")
    assert request.destination.kind == packet.ADDR_ANY and request.destination.service == "machoNet"
    assert request.source.kind == packet.ADDR_CLIENT and request.source.call_id == 2
    assert int(request.source.client_id) == 7_000_001
    assert request.user_id == 7
    is_bound, stream = request.payload[0]
    assert is_bound == 1 and isinstance(stream, m.SubStream)  # a direct call has its official isBound flag set (4d289a7)
    remote_object, method, args, kwargs = stream.value
    assert (remote_object, args) == (1, ()) and bytes(method).decode() == "GetTime"

def test_a_bound_call_names_its_object_in_the_body_and_no_service_in_the_address():
    """The call to a bound object: the destination has no service and the body's first element is the object."""
    oid = ("N=65450:1", m.Long(134351313110300000))
    with MockServer() as server:
        server.add_account("pilot", "hunter2")
        with Client("127.0.0.1", server.port) as client:
            client.login("pilot", "hunter2")
            with pytest.raises(packet.MachoError):
                client.call_bound(m.SubStruct(value=m.SubStream(value=oid)), "List", 4, flags=1)
    request = next(v for k, v in server.received if k == "packet" and v.command == packet.CALL_REQ and m.plain(v.payload[0][1].value[1]) == "List")
    assert request.destination.kind == packet.ADDR_ANY and request.destination.service is None
    is_bound, stream = request.payload[0]
    assert is_bound == 1 and isinstance(stream, m.SubStream)
    remote_object, method, args, kwargs = stream.value
    assert m.plain(remote_object) == ("N=65450:1", 134351313110300000)
    assert bytes(method).decode() == "List" and args == (4,)
    assert m.plain(kwargs) == {"flags": 1}

def test_the_object_of_a_bind_answer_is_found_through_its_wrappers():
    oid = ("N=65450:1", 5)
    assert Client.bound_object(m.SubStruct(value=m.SubStream(value=oid))) == oid
    assert Client.bound_object(oid) == oid

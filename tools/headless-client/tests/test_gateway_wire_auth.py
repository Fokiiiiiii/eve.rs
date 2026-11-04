from tests.gateway_wire import as_user


class TokenClient:
    def __init__(self):
        self.calls = []

    def call(self, service, method):
        self.calls.append((service, method))
        return "signed-by-host"


def test_gateway_channel_uses_the_host_issued_user_token():
    client = TokenClient()
    channel = object()

    authorized = as_user(channel, client)

    assert client.calls == [("userSvc", "GetUserToken")]
    assert authorized._channel is channel
    assert authorized._metadata == (("authorization", "signed-by-host"),)

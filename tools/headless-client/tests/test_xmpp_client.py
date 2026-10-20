from evecli.xmpp import _StreamReader


def test_stream_reader_assembles_fragmented_features_and_groupchat_elements():
    reader = _StreamReader()
    reader.feed(b"<stream:stream xmlns='jabber:client' xmlns:stream='http://etherx.jabber.org/streams'><stream:features><mechanisms>")
    assert not reader.ready
    reader.feed(b"<mechanism>PLAIN</mechanism></mechanisms></stream:features><message type='groupchat'><body>Local &amp; private</body></message>")

    features, message = reader.ready
    assert features.name == "features"
    assert features.child("mechanisms").child("mechanism").text == "PLAIN"
    assert message.name == "message"
    assert message.child("body").text == "Local & private"

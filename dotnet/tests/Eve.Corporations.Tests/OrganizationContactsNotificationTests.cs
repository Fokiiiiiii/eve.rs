using Eve.Marshal;
using Eve.Services;
using Eve.Services.Corporations;

namespace Eve.Corporations.Tests;

public sealed class OrganizationContactsNotificationTests
{
    private sealed class PacketSink : IPacketSink
    {
        public List<PyValue> Packets { get; } = [];
        public void Send(PyValue packet) => Packets.Add(packet);
    }

    [Fact]
    public void Contact_updates_are_sent_after_the_response_to_only_matching_corporate_and_alliance_sessions()
    {
        var sessions = new CharacterSessions();
        var corporationActor = NewSession(sessions, 101, 501, corporationId: 9001, allianceId: 8001);
        var corporationPeer = NewSession(sessions, 102, 502, corporationId: 9001, allianceId: 8001);
        var corporationOutsider = NewSession(sessions, 103, 503, corporationId: 9002, allianceId: 8002);
        var alliancePeer = NewSession(sessions, 104, 504, corporationId: 9003, allianceId: 8001);
        var call = new ServiceCall { Session = corporationActor.Session, Method = "AddCorporateContact", Args = [] };
        var updates = new (long ContactId, long? RelationshipId, long? LabelMask)[]
        {
            (7001, 5, null),
            (7002, null, null),
        };

        CorporationNotificationAdapter.OrganizationContactsUpdated(sessions, call, 9001, false, updates);
        Assert.Empty(corporationActor.Sink.Packets);
        Assert.Empty(corporationPeer.Sink.Packets);
        corporationActor.Session.RunAfterResponse();

        Assert.Equal("OnOrganizationContactsUpdated", Name(Assert.Single(corporationActor.Sink.Packets)));
        Assert.Equal("OnOrganizationContactsUpdated", Name(Assert.Single(corporationPeer.Sink.Packets)));
        Assert.Empty(corporationOutsider.Sink.Packets);
        Assert.Empty(alliancePeer.Sink.Packets);
        Assert.Equal("corpid", Address(Assert.Single(corporationActor.Sink.Packets)).IdType);
        Assert.Equal("corpid", Address(Assert.Single(corporationPeer.Sink.Packets)).IdType);
        var payload = Arguments(Assert.Single(corporationActor.Sink.Packets));
        Assert.Equal(new PyInt(9001), payload.Items[0]);
        Assert.Equal(new PyList([
            new PyTuple([new PyInt(7001), new PyInt(5), PyNone.Instance]),
            new PyTuple([new PyInt(7002), PyNone.Instance, PyNone.Instance]),
        ]), payload.Items[1]);

        var allianceActor = NewSession(sessions, 105, 505, corporationId: 9003, allianceId: 8001);
        var allianceCall = new ServiceCall { Session = allianceActor.Session, Method = "EditContactsRelationshipID", Args = [] };
        CorporationNotificationAdapter.OrganizationContactsUpdated(sessions, allianceCall, 8001, true,
            [(7003, (long?)8, (long?)null)]);
        allianceActor.Session.RunAfterResponse();

        Assert.Equal(2, corporationActor.Sink.Packets.Count);
        Assert.Equal(2, corporationPeer.Sink.Packets.Count);
        Assert.Equal("OnOrganizationContactsUpdated", Name(Assert.Single(allianceActor.Sink.Packets)));
        Assert.Equal("OnOrganizationContactsUpdated", Name(Assert.Single(alliancePeer.Sink.Packets)));
        Assert.Empty(corporationOutsider.Sink.Packets);
        Assert.Equal("OnOrganizationContactsUpdated", Name(corporationActor.Sink.Packets[1]));
        Assert.Equal("OnOrganizationContactsUpdated", Name(corporationPeer.Sink.Packets[1]));
        Assert.Equal("allianceid", Address(corporationActor.Sink.Packets[1]).IdType);
        Assert.Equal("allianceid", Address(corporationPeer.Sink.Packets[1]).IdType);
        Assert.Equal("allianceid", Address(Assert.Single(allianceActor.Sink.Packets)).IdType);
        Assert.Equal("allianceid", Address(Assert.Single(alliancePeer.Sink.Packets)).IdType);
        var alliancePayload = Arguments(Assert.Single(allianceActor.Sink.Packets));
        Assert.Equal(new PyInt(8001), alliancePayload.Items[0]);
        Assert.Equal(new PyList([
            new PyTuple([new PyInt(7003), new PyInt(8), PyNone.Instance]),
        ]), alliancePayload.Items[1]);
    }

    private static (ClientSession Session, PacketSink Sink) NewSession(
        CharacterSessions sessions, long characterId, long clientId, long corporationId, long? allianceId = null)
    {
        var sink = new PacketSink();
        var session = new ClientSession(sink) { CharacterId = characterId, ClientId = clientId };
        session.Set("corpid", new PyInt(corporationId));
        if (allianceId is { } id) session.Set("allianceid", new PyInt(id));
        sessions.Enter(characterId, session);
        return (session, sink);
    }

    private static string? Name(PyValue packet)
    {
        if (packet is not PyObject { Name: PyStr name, Args: PyTuple args } ||
            !name.Value.EndsWith("Notification", StringComparison.Ordinal)) return null;
        return Eve.Macho.MachoAddress.Decode(args.Items[2]).BroadcastId;
    }

    private static Eve.Macho.MachoAddress Address(PyValue packet) =>
        Eve.Macho.MachoAddress.Decode(Assert.IsType<PyTuple>(Assert.IsType<PyObject>(packet).Args).Items[2]);

    private static PyTuple Arguments(PyValue packet)
    {
        var state = Assert.IsType<PyTuple>(Assert.IsType<PyObject>(packet).Args);
        var payload = Assert.IsType<PyTuple>(state.Items[4]);
        var envelope = Assert.IsType<PyTuple>(payload.Items[0]);
        var buffer = Assert.IsType<PyBuffer>(envelope.Items[1]);
        var decoded = Assert.IsType<PyTuple>(MarshalCodec.DecodeExact(buffer.Value));
        var call = Assert.IsType<PyTuple>(decoded.Items[1]);
        return Assert.IsType<PyTuple>(call.Items[1]);
    }
}

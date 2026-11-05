using Eve.Host;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using Xunit;

namespace Eve.Host.Tests;

public sealed class IndustryJobNotificationWireTests
{
    private sealed class Capture : IPacketSink
    {
        public List<PyValue> Packets { get; } = [];
        public void Send(PyValue packet) => Packets.Add(packet);
    }

    [Fact]
    public void Industry_job_change_broadcasts_six_client_arguments_to_installer_and_authorized_corporation_sessions()
    {
        var sessions = new CharacterSessions();
        var installer = Session(1002, 5001, 0);
        var factoryManager = Session(1003, 5001, 1024);
        var unrelatedMember = Session(1004, 5001, 1);
        sessions.Enter(1002, installer.Session);
        sessions.Enter(1003, factoryManager.Session);
        sessions.Enter(1004, unrelatedMember.Session);

        IndustryJobClientNotifications.Send(
            sessions,
            new IndustryJobChangedEvent(42001, 5001, 65001, 1002, 3, null, "industry-op-1"));

        AssertIndustryJobNotification(installer.Capture, 42001, 5001, 65001, 1002, 3, PyNone.Instance);
        AssertIndustryJobNotification(factoryManager.Capture, 42001, 5001, 65001, 1002, 3, PyNone.Instance);
        Assert.Empty(unrelatedMember.Capture.Packets);
    }

    private static (ClientSession Session, Capture Capture) Session(long characterId, long corporationId, long role)
    {
        var capture = new Capture();
        var session = new ClientSession(capture)
        {
            UserId = 7,
            ClientId = characterId,
            CharacterId = characterId,
        };
        session.Set("corpid", new PyLong(corporationId));
        session.Set("corprole", new PyLong(role));
        return (session, capture);
    }

    private static void AssertIndustryJobNotification(
        Capture capture, long jobId, long ownerId, long blueprintId, long installerId, int status, PyValue successfulRuns)
    {
        var packet = MachoPacket.Decode(Assert.Single(capture.Packets))!;
        Assert.Equal(MachoMessage.Notification, packet.Type);
        Assert.Equal("OnIndustryJob", packet.Dest.BroadcastId);
        Assert.Equal("charid", packet.Dest.IdType);

        var call = Assert.IsType<PyTuple>(Assert.IsType<PyTuple>(packet.Payload).Items.Single());
        var inner = Assert.IsType<PyTuple>(
            MarshalCodec.Decode(Assert.IsType<PyBuffer>(call.Items[1]).Value));
        var eventCall = Assert.IsType<PyTuple>(inner.Items[1]);
        Assert.Equal(new PyInt(1), eventCall.Items[0]);
        Assert.Equal(new PyTuple([
            new PyInt(jobId), new PyInt(ownerId), new PyInt(blueprintId),
            new PyInt(installerId), new PyInt(status), successfulRuns,
        ]), eventCall.Items[1]);
    }
}

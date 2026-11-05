using Eve.Characters;
using Eve.Host;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Nes_intro_reads_and_skip_match_client_state_and_notification_flow_over_host_wire()
    {
        const long pilot = 90000100;
        var storedState = NesIntroStore.Active;
        var reads = new List<long>();
        var skips = new List<long>();
        ClientSession? openedSession = null;
        var characterSessions = new CharacterSessions();
        var service = new NesIntroService(
            (characterId, _) =>
            {
                reads.Add(characterId);
                return Task.FromResult(storedState);
            },
            (characterId, _) =>
            {
                skips.Add(characterId);
                if (storedState != NesIntroStore.Active) return Task.FromResult(false);
                storedState = NesIntroStore.Skipped;
                return Task.FromResult(true);
            },
            (characterId, _) => Task.FromResult<AirNpeCharacterState?>(
                new AirNpeCharacterState("tutorial", 1)),
            () => "tutorial",
            characterSessions);
        Start(
            configure: registry => registry.Register(service),
            sessionOpened: session => openedSession = session,
            strictRpc: true);

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();
        Assert.NotNull(openedSession);
        openedSession!.CharacterId = pilot;
        characterSessions.Enter(pilot, openedSession);

        async Task<PyValue?> Call(uint callId, string method)
        {
            var request = Packets.CallWithKwargs(
                open, callId, "nes_intro", method, new PyTuple([]),
                Py.Dict(("machoVersion", new PyInt(1))));
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(request)));
            return Result(MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload));
        }

        Assert.Equal(new PyInt(NesIntroStore.Active), await Call(1, "get_nes_intro_state"));
        Assert.Equal([pilot], reads);

        var request = Packets.CallWithKwargs(
            open, 2, "nes_intro", "skip_nes_intro", new PyTuple([]),
            Py.Dict(("machoVersion", new PyInt(1))));
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(request)));

        var notificationSend = Assert.IsType<Send>(await front.ReceiveAsync());
        Assert.Equal(open.SessionId, notificationSend.SessionId);
        var notification = Assert.IsType<PyObject>(MarshalCodec.Decode(notificationSend.Payload));
        Assert.Equal(ClientNotification.ClassName, T(notification.Name));
        var packet = Assert.IsType<PyTuple>(notification.Args).Items;
        Assert.Equal(new PyInt(MachoMessage.Notification), packet[0]);
        var destination = Assert.IsType<PyObject>(packet[2]);
        var address = Assert.IsType<PyTuple>(destination.Args).Items;
        Assert.Equal(new PyInt(4), address[0]);
        Assert.Equal("OnNesIntroStateChanged", T(address[1]));
        Assert.IsType<PyList>(address[2]);
        Assert.Equal("clientID", T(address[3]));
        Assert.Equal(new PyDict([new(new PyStr("sn"), new PyInt(0))]), packet[5]);
        var envelope = Assert.IsType<PyTuple>(packet[4]).Items[0];
        var bytes = Assert.IsType<PyBuffer>(Assert.IsType<PyTuple>(envelope).Items[1]);
        var marshalled = Assert.IsType<PyTuple>(MarshalCodec.Decode(bytes.Value.ToArray()));
        Assert.Equal(new PyInt(0), marshalled.Items[0]);
        var notificationCall = Assert.IsType<PyTuple>(marshalled.Items[1]);
        Assert.Equal(new PyInt(1), notificationCall.Items[0]);
        var notificationArgs = Assert.IsType<PyTuple>(notificationCall.Items[1]).Items;
        Assert.Equal(new PyInt(pilot), notificationArgs[0]);
        Assert.Equal(new PyInt(NesIntroStore.Skipped), notificationArgs[1]);

        var skipReply = Assert.IsType<Send>(await front.ReceiveAsync());
        Assert.Equal(PyBool.True, Result(MarshalCodec.Decode(skipReply.Payload)));
        Assert.Equal([pilot], skips);
        Assert.Equal(new PyInt(NesIntroStore.Skipped), await Call(3, "get_nes_intro_state"));
    }
}

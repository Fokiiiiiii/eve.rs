using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.CharacterInfo;

namespace Eve.Services.CharacterInfo.Tests;

public sealed class CharMgrSettingsWireTests
{
    [Fact]
    public async Task GetSettingsInfo_is_dispatched_from_the_registered_charMgr_route_with_the_client_pair_shape()
    {
        var charMgr = new CharMgrService(
            characters: null!,
            homeStations: null!,
            readRecentShipKillmailPage: (_, _, _, _) => ValueTask.FromResult<IReadOnlyList<PyValue>>([]),
            readCharacterDescription: (_, _) => ValueTask.FromResult<string?>(null),
            writeCharacterDescription: (_, _, _) => ValueTask.CompletedTask,
            readCharacterSecurityStatus: (_, _) => ValueTask.FromResult(0d),
            readMedal1GraphicId: (_, _) => ValueTask.FromResult<long?>(null));
        var services = new ServiceRegistry();
        services.Register(charMgr);
        var dispatcher = new PacketDispatcher(services, () => 1_790_646_377_000);
        var sink = new RecordingSink();
        var session = new ClientSession(sink) { SessionId = 15, ClientId = 100, UserId = 42 };

        var request = MarshalCodec.Decode(MarshalCodec.Encode(CallRequest()));
        Assert.True(await dispatcher.DispatchAsync(request, session));

        var response = MarshalCodec.Decode(MarshalCodec.Encode(Assert.Single(sink.Packets)));
        var responseState = Assert.IsType<PyTuple>(Assert.IsType<PyObject>(response).Args).Items;
        Assert.Equal((int)MachoMessage.CallRsp, Assert.IsType<PyInt>(responseState[0]).Value);
        var responsePayload = Assert.IsType<PyTuple>(responseState[4]).Items;
        var result = Assert.IsType<PyTuple>(Assert.IsType<PySubStream>(responsePayload[0]).Decoded);
        Assert.IsType<PyBuffer>(result.Items[0]);
        Assert.Equal(new PyInt(0), result.Items[1]);
    }

    private static PyValue CallRequest()
    {
        var callBody = new PyTuple([new PyInt(1), new PyStr("GetSettingsInfo"), new PyTuple([]), PyNone.Instance]);
        var payload = new PyTuple([new PyTuple([new PyInt(0), new PySubStream(callBody)])]);
        var source = new PyTuple([new PyInt(2), new PyLong(100), new PyInt(1), PyNone.Instance]);
        var destination = new PyTuple([new PyInt(8), new PyStr("charMgr"), PyNone.Instance]);
        var header = new PyTuple([new PyInt((int)MachoMessage.CallReq), source, destination, new PyInt(42), payload, PyNone.Instance]);
        var args = new PyTuple([.. header.Items, .. Enumerable.Repeat<PyValue>(PyNone.Instance, 14 - header.Items.Count)]);
        return new PyObject(new PyStr("carbon.common.script.net.machoNetPacket.CallReq"), args);
    }

    private sealed class RecordingSink : IPacketSink
    {
        public List<PyValue> Packets { get; } = [];

        public void Send(PyValue packet) => Packets.Add(packet);
    }
}

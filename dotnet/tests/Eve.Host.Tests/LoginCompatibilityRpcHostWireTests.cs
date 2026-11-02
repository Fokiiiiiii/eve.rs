using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Air_and_login_support_rpcs_return_client_shapes_through_the_host()
    {
        var state = 0;
        var skipped = false;
        ClientSession? session = null;
        var airNpe = new AirNpeService(
            () => "tutorial",
            (characterId, _) => Task.FromResult<AirNpeCharacterState?>(
                characterId == 42 ? new AirNpeCharacterState("tutorial", state) : null),
            (characterId, tutorial, _) =>
            {
                skipped = characterId == 42 && tutorial;
                state = 2;
                return Task.FromResult<int?>(state);
            });

        Start(configure: registry => registry.Register(airNpe), sessionOpened: opened =>
        {
            opened.UserId = 7;
            opened.CharacterId = 42;
            session = opened;
        }, strictRpc: true);

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        long callId = 0;
        async Task<PyValue?> Call(string service, string method, PyValue[] args)
        {
            var kwargs = Py.Dict(("machoVersion", new PyInt(1)));
            await front.SendAsync(new Packet(open.SessionId,
                MarshalCodec.Encode(Packets.CallWithKwargs(
                    open, ++callId, service, method, new PyTuple(args), kwargs))));
            return Result(MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload));
        }

        Assert.Equal(new PyInt(0), await Call("air_npe", "get_air_npe_state", []));
        Assert.Equal(PyBool.True, await Call("air_npe", "is_air_npe_enabled", []));
        Assert.Empty(Assert.IsType<PyList>(await Call("authentication", "AccruedTime", [])).Items);
        Assert.Equal(PyBool.False, await Call("authentication", "AmUnderage", [new PyStr("v24-token")]));
        Assert.Same(PyNone.Instance, await Call("authentication", "SetLanguageID", [new PyStr("EN")]));
        Assert.Equal(new PyStr("EN"), session?.Get("languageID"));
        Assert.Equal(new PyStr("EN"), session?.Get("userLanguageID"));

        Assert.Same(PyNone.Instance, await Call("pingService", "Ping", []));
        Assert.Equal(new PyInt(1), await Call("subscriptionMgr", "GetCloneGrade", []));
        Assert.Equal(new PyLong(OmegaLicenses.DefaultOmegaExpiryFileTime),
            await Call("subscriptionMgr", "GetSubscriptionTime", []));

        await front.SendAsync(new Packet(open.SessionId,
            MarshalCodec.Encode(Packets.CallWithKwargs(
                open, ++callId, "air_npe", "skip_air_npe", new PyTuple([]),
                Py.Dict(("machoVersion", new PyInt(1)))))));
        var notifications = new List<string>();
        PyValue response;
        while (true)
        {
            var message = MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload);
            var packet = MachoPacket.Decode(message);
            if (packet?.Type == MachoMessage.Notification)
            {
                notifications.Add(packet.Dest.BroadcastId ?? string.Empty);
                continue;
            }
            response = message;
            break;
        }
        Assert.Equal(["OnTutorialSkipped", "OnAirNpeStateChanged"], notifications);
        Assert.Same(PyNone.Instance, Result(response));
        Assert.True(skipped);
        Assert.Equal(2, state);
    }
}

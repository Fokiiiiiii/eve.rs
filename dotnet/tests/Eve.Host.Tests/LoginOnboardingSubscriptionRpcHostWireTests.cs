using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Login_and_onboarding_rpcs_keep_client_wire_contracts_with_transport_kwargs()
    {
        var tutorialState = 1;
        ClientSession? openedSession = null;
        var airNpe = new AirNpeService(
            () => "tutorial",
            (_, _) => Task.FromResult<AirNpeCharacterState?>(new AirNpeCharacterState("tutorial", tutorialState)),
            (_, _, _) =>
            {
                tutorialState = 3;
                return Task.FromResult<int?>(tutorialState);
            });
        Start(configure: registry => registry.Register(airNpe), strictRpc: true,
            sessionOpened: session =>
            {
                openedSession = session;
                session.Set("charid", new PyInt(42));
            });
        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();
        var notifications = new List<string>();

        async Task<PyValue> Exchange(long callId, string service, string method, params PyValue[] args)
        {
            var kwargs = new PyDict([
                new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
            ]);
            await front.SendAsync(new Packet(open.SessionId,
                MarshalCodec.Encode(Packets.CallWithKwargs(
                    open, callId, service, method, new PyTuple(args), kwargs))));
            while (true)
            {
                var response = MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload);
                var packet = MachoPacket.Decode(response);
                if (packet?.Type == MachoMessage.Notification)
                {
                    notifications.Add(packet.Dest.BroadcastId ?? string.Empty);
                    continue;
                }
                return response;
            }
        }

        async Task<PyValue?> Call(long callId, string service, string method, params PyValue[] args) =>
            Result(await Exchange(callId, service, method, args));

        Assert.Equal(new PyInt(1), await Call(1, "air_npe", "get_air_npe_state"));
        Assert.Same(PyNone.Instance, await Call(2, "air_npe", "skip_air_npe"));
        Assert.Equal(new PyInt(3), await Call(3, "air_npe", "get_air_npe_state"));
        Assert.Equal(["OnAirNpeStateChanged"], notifications);

        Assert.Empty(Assert.IsType<PyList>(await Call(4, "authentication", "AccruedTime")).Items);
        Assert.Empty(Assert.IsType<PyList>(await Call(5, "authentication", "AccruedTime")).Items);
        Assert.Equal(PyBool.False, await Call(6, "authentication", "AmUnderage", new PyStr("login-jwt")));
        var invalidToken = await Exchange(7, "authentication", "AmUnderage", new PyStr(""));
        var errorState = ((PyTuple)((PyObject)invalidToken).Args).Items;
        Assert.Equal((int)MachoMessage.ErrorResponse, Assert.IsType<PyInt>(errorState[0]).Value);
        Assert.Same(PyNone.Instance, await Call(8, "authentication", "SetLanguageID", new PyStr("ja")));
        Assert.Equal(new PyStr("ja"), openedSession?.Get("languageID"));

        Assert.IsType<PyLong>(await Call(9, "subscriptionMgr", "GetSubscriptionTime"));
        Assert.IsType<PyLong>(await Call(10, "subscriptionMgr", "GetSubscriptionTime"));
        var subscriptionCloneGrade = await Call(11, "subscriptionMgr", "GetCloneGrade");
        Assert.Equal(new PyInt(1), subscriptionCloneGrade);
        Assert.Equal(new PyInt(1), await Call(12, "subscriptionMgr", "GetCloneGrade"));
        Assert.Equal(subscriptionCloneGrade, await Call(13, "loginRewardFacilities", "get_my_clone_grade"));
        Assert.Same(PyNone.Instance, await Call(14, "pingService", "Ping"));
    }
}

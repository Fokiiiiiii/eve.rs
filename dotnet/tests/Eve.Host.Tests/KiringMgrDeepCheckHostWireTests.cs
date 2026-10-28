using System.Text;
using Eve.Host;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Kiring_manager_calls_keep_client_arguments_and_fail_closed_replies_over_host_wire()
    {
        var service = new KiringMgrService
        {
            MicroservicesPublicUrl = "http://127.0.0.1:26002/",
            EveGuardServerAddress = " guard.example ",
            MonochromeStyleEnabled = true,
            ShouldKickCheater = true,
        };
        Start(strictRpc: true, configure: registry => registry.Register(service));
        var (front, open) = await OpenFrontAsync();

        var config = await KiringCallAsync(front, open, 1, "GetKiringConfiguration");
        Assert.Equal(new PyInt(1), KiringField(config, "mode"));
        Assert.Equal("evejs-local-kiring", KiringText(KiringField(config, "client_id")));
        var endpoints = KiringField(config, "endpoints");
        Assert.Equal("http://127.0.0.1:26002/kiring/mpay/", KiringText(KiringField(endpoints, "mpay")));
        Assert.Equal("http://127.0.0.1:26002/kiring/billing/", KiringText(KiringField(endpoints, "billing")));
        Assert.Equal("http://127.0.0.1:26002/kiring/redirect/", KiringText(KiringField(endpoints, "redirect_uri")));
        var channels = KiringField(config, "channels");
        Assert.Equal("evejs_local", KiringText(KiringField(channels, "login")));
        Assert.Equal("evejs_local", KiringText(KiringField(channels, "pay")));
        Assert.Equal("evejs_local", KiringText(KiringField(channels, "app")));

        Assert.Equal(new PyInt(201), await KiringCallAsync(front, open, 2, "GetAntiAddictionCode", new PyStr("netease-token")));
        Assert.Equal("guard.example", KiringText(await KiringCallAsync(front, open, 3, "GetEveGuardServerAddress")));
        Assert.Equal(PyBool.True, await KiringCallAsync(front, open, 4, "IsMonochromeStyleEnabled"));
        Assert.Equal(PyBool.True, await KiringCallAsync(front, open, 5, "ShouldKickCheater"));
        Assert.Equal(PyBool.False, await KiringCallAsync(front, open, 6, "ActivateRedeemingCode",
            new PyStr("code"), new PyStr("account"), new PyInt(42), new PyStr("user"),
            new PyStr("Pilot"), new PyStr("192.0.2.1"), new PyStr("device")));

        Assert.Equal("KIRING_AUTH_UNAVAILABLE", KiringUserError(await KiringCallAsync(front, open, 7,
            "PerformKiringServerSideAuthenticationFromCode", new PyStr("code"),
            new PyStr("device"), new PyStr("client-id"))));
        Assert.Equal("KIRING_AUTH_UNAVAILABLE", KiringUserError(await KiringCallAsync(front, open, 8,
            "GetSAuthAid", new PyStr("user"), new PyStr("account"), new PyStr("device"))));
        Assert.Equal("KIRING_PURCHASE_UNAVAILABLE", KiringUserError(await KiringCallAsync(front, open, 9,
            "PlaceKiringOrder", new PyStr("device"), new PyStr("client-id"), new PyInt(42),
            new PyInt(700001), new PyInt(1), new PyStr("user"), new PyStr("netease-token"),
            new PyStr("https://billing.example/"), new PyStr("app"), new PyStr("pay"),
            new PyStr("login"), Py.Dict(("Origin", new PyStr("https://billing.example"))), new PyInt(1))));
    }

    private async Task<PyValue> KiringCallAsync(
        TestFront front, SessionOpen open, long callId, string method, params PyValue[] args)
    {
        var transportKwargs = Py.Dict(("machoVersion", new PyInt(1)));
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.CallWithKwargs(open, callId, "kiringMgr", method, new PyTuple(args), transportKwargs))));
        var response = Assert.IsType<Send>(await front.ReceiveAsync());
        var decoded = MarshalCodec.Decode(response.Payload);
        return MachoPacket.Decode(decoded)?.Type == MachoMessage.CallRsp ? Result(decoded)! : decoded;
    }

    private static PyValue KiringField(PyValue dictionary, string key) =>
        Assert.IsType<PyDict>(dictionary).Entries.Single(entry => KiringText(entry.Key) == key).Value;

    private static string KiringText(PyValue? value) => value switch
    {
        PyStr text => text.Value,
        PyBytes bytes => Encoding.UTF8.GetString(bytes.Value.Span),
        _ => throw new Xunit.Sdk.XunitException($"Expected wire text, got {value?.GetType().Name ?? "null"}."),
    };

    private static string KiringUserError(PyValue response)
    {
        var packet = MachoPacket.Decode(response);
        Assert.NotNull(packet);
        Assert.Equal(MachoMessage.ErrorResponse, packet!.Type);
        var errorState = Assert.IsType<PyTuple>(packet.Payload).Items;
        Assert.Equal((int)MachoErrorType.WrappedException, Assert.IsType<PyInt>(errorState[1]).Value);
        var wrapped = Assert.IsType<PyTuple>(errorState[2]).Items;
        var userError = Assert.IsType<PyObjectEx>(Assert.IsType<PySubStream>(wrapped[0]).Decoded);
        var header = Assert.IsType<PyTuple>(userError.Header).Items;
        Assert.Equal(MachoErrors.UserErrorClass, Assert.IsType<PyToken>(header[0]).Value);
        var args = Assert.IsType<PyTuple>(header[1]).Items;
        return Encoding.UTF8.GetString(Assert.IsType<PyBytes>(args[0]).Value.Span);
    }
}

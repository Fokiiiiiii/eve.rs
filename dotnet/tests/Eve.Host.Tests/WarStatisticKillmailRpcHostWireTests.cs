using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Killmail_notifications_receive_the_client_keyval_through_registered_host()
    {
        const long killId = 73;
        const long killTime = 1_900_000_000_000;
        const string killBlob = "<killmail><victim/><attackers/><items/></killmail>";
        var record = new JsonObject
        {
            ["killID"] = killId,
            ["killTime"] = killTime,
            ["victimCharacterID"] = 11,
            ["finalCharacterID"] = 22,
            ["victimShipTypeID"] = 587,
            ["victimDamageTaken"] = 1234,
            ["finalDamageDone"] = 1234,
            ["killBlob"] = killBlob,
        };
        var expectedHash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes("1122587" + killTime)))
            .ToLowerInvariant();
        long loadedKillId = 0;
        var service = new WarStatisticMgrService(
            (id, _) =>
            {
                loadedKillId = id;
                return ValueTask.FromResult<JsonObject?>(id == killId ? record : null);
            },
            () => 1_900_000_000_000);
        Start(
            configure: registry => registry.Register(service),
            sessionOpened: session => session.CharacterId = 9001);

        using var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        async Task<PyValue> Call(long callId, long id, PyValue hash)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(Packets.CallWithKwargs(
                open,
                callId,
                "warStatisticMgr",
                "GetKillMail",
                new PyTuple([new PyLong(id), hash]),
                new PyDict([new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1))])))));
            var response = MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload);
            var responsePacket = Assert.IsType<MachoPacket>(MachoPacket.Decode(response));
            Assert.Equal(callId, (long)JsSemantics.NumberValue(responsePacket.CallId));
            return Result(response)!;
        }

        var killmail = Assert.IsType<PyObject>(await Call(1, killId,
            new PyBytes(Encoding.UTF8.GetBytes(expectedHash))));
        var fields = WarKeyValFields(killmail);
        Assert.Equal(new PyInt(killId), fields["killID"]);
        Assert.Equal(new PyLong(killTime), fields["killTime"]);
        Assert.Equal(new PyInt(587), fields["victimShipTypeID"]);
        Assert.Equal(killBlob, Encoding.UTF8.GetString(Assert.IsType<PyBytes>(fields["killBlob"]).Value.Span));
        Assert.Equal(killId, loadedKillId);

        Assert.IsType<PyObject>(await Call(2, killId, new PyStr(expectedHash)));
        Assert.IsType<PyObject>(await Call(3, killId, new PyWString(expectedHash)));
        Assert.Equal(PyNone.Instance, await Call(4, killId,
            new PyBytes(Encoding.UTF8.GetBytes("wrong-hash"))));
        Assert.Equal(PyNone.Instance, await Call(5, killId + 1, new PyWString(expectedHash)));
        Assert.Equal(killId + 1, loadedKillId);
    }
}

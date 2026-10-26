using Eve.Host;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services.CharacterSelect;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Theory]
    [InlineData(0u)]
    [InlineData(42u)]
    public async Task Session_initial_state_uses_reference_field_order_and_default_omissions(uint userId)
    {
        const ulong sessionId = 1234;
        const ulong role = 0x0100_0000_0000_0001;
        const string address = "203.0.113.10";

        Start();
        var front = await Handshaken(await ConnectAsync());
        await front.SendAsync(Open(sessionId, userId) with
        {
            Role = role,
            Address = address,
            Language = "FR",
            CountryCode = "JP",
        });

        var send = Assert.IsType<Send>(await front.ReceiveAsync());
        var packet = Assert.IsType<PyObject>(MarshalCodec.Decode(send.Payload));
        Assert.Equal(SessionInitialState.ClassName, T(packet.Name));

        var packetItems = Assert.IsType<PyTuple>(packet.Args).Items;
        Assert.Equal(14, packetItems.Count);
        Assert.Equal(MachoMessage.SessionInitialStateNotification, ((PyInt)packetItems[0]).Value);
        if (userId == 0)
            Assert.IsType<PyNone>(packetItems[3]);
        else
            Assert.Equal(new PyInt(userId), packetItems[3]);

        var source = Assert.IsType<PyObject>(packetItems[1]);
        Assert.Equal(MachoAddress.ClassName, T(source.Name));
        Assert.Equal(MachoMessage.ProxyNodeId, ((PyInt)((PyTuple)source.Args).Items[1]).Value);
        var destination = Assert.IsType<PyObject>(packetItems[2]);
        Assert.Equal(MachoAddress.ClassName, T(destination.Name));
        Assert.Equal(Open(sessionId, userId).ClientId,
            unchecked((ulong)((PyLong)((PyTuple)destination.Args).Items[1]).Value));

        var sessionPayload = Assert.IsType<PyTuple>(packetItems[4]).Items;
        Assert.Equal(new PyLong((long)sessionId), sessionPayload[0]);
        Assert.Equal(new PyInt(5), sessionPayload[1]);

        var entries = Assert.IsType<PyDict>(sessionPayload[2]).Entries;
        Assert.Equal(
            new[] { "userid", "userType", "role", "address", "languageID", "countryCode" },
            entries.Select(entry => T(entry.Key)).ToArray());
        var values = entries.ToDictionary(entry => T(entry.Key)!, entry => entry.Value);
        Assert.Equal(new PyInt(userId), values["userid"]);
        Assert.Equal(new PyInt(30), values["userType"]);
        Assert.Equal(new PyLong(unchecked((long)(role | CharUnboundMgrService.SessionBaseRole))), values["role"]);
        Assert.Equal(address, T(values["address"]));
        Assert.Equal("FR", T(values["languageID"]));
        Assert.Equal("JP", T(values["countryCode"]));
        Assert.DoesNotContain("maxSessionTime", values.Keys);
        Assert.DoesNotContain("inDetention", values.Keys);

        Assert.Empty(Assert.IsType<PyDict>(packetItems[5]).Entries);
        Assert.All(packetItems.Skip(6), value => Assert.IsType<PyNone>(value));
    }
}

using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.CharacterInfo;
using Eve.Universe;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Owner_recordset_batches_fit_a_normal_session_burst()
    {
        const int batchSize = 100;
        const int batchCount = 17;
        const long firstOwnerId = 3_100_000;
        var ownerIds = Enumerable.Range(0, batchSize * batchCount)
            .Select(index => firstOwnerId + index)
            .ToArray();
        var agentOwners = ownerIds.ToDictionary(
            id => id,
            id => new AgentOwnerInfo($"Agent {id}", id % 2));
        var config = new ConfigService(
            null!, null!, new StationCatalog([]), new SolarSystemCatalog([]), agentOwners: agentOwners);
        Start(configure: registry => registry.Register(config), strictRpc: true);

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();
        var kwargs = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
        ]);

        for (var batch = 0; batch < batchCount; batch++)
        {
            var requestedIds = ownerIds
                .Skip(batch * batchSize)
                .Take(batchSize)
                .Select(id => (PyValue)new PyLong(id))
                .ToArray();
            await front.SendAsync(new Packet(open.SessionId,
                MarshalCodec.Encode(Packets.CallWithKwargs(
                    open, batch + 1, "config", "GetMultiOwnersEx",
                    new PyTuple([new PyList(requestedIds)]), kwargs))));
            var response = Assert.IsType<Send>(await front.ReceiveAsync());
            var rowset = Assert.IsType<PyTuple>(Result(MarshalCodec.Decode(response.Payload)));
            Assert.Equal(
                new[] { "ownerID", "ownerName", "typeID", "gender", "ownerNameID" },
                Assert.IsType<PyTuple>(rowset.Items[0]).Items.Select(JsSemantics.StringValue));
            Assert.Equal(batchSize, Assert.IsType<PyTuple>(rowset.Items[1]).Items.Count);
        }

        await front.SendAsync(new Packet(open.SessionId,
            MarshalCodec.Encode(Packets.CallWithKwargs(
                open, batchCount + 1, "config", "GetMultiOwnersEx",
                new PyTuple([new PyInt(1)]), kwargs))));
        var malformedResponse = Assert.IsType<Send>(await front.ReceiveAsync());
        var errorPacket = MachoPacket.Decode(MarshalCodec.Decode(malformedResponse.Payload));
        Assert.NotNull(errorPacket);
        Assert.Equal(MachoMessage.ErrorResponse, errorPacket!.Type);

        var errorState = Assert.IsType<PyTuple>(errorPacket.Payload).Items;
        Assert.Equal((int)MachoErrorType.WrappedException, Assert.IsType<PyInt>(errorState[1]).Value);
        var wrapped = Assert.IsType<PyTuple>(errorState[2]).Items;
        var userError = Assert.IsType<PyObjectEx>(Assert.IsType<PySubStream>(wrapped[0]).Decoded);
        var header = Assert.IsType<PyTuple>(userError.Header).Items;
        Assert.Equal(MachoErrors.UserErrorClass, Assert.IsType<PyToken>(header[0]).Value);
        var userErrorArgs = Assert.IsType<PyTuple>(header[1]).Items;
        Assert.Equal(
            "CONFIG_IDENTIFIER_LIST_INVALID",
            System.Text.Encoding.UTF8.GetString(Assert.IsType<PyBytes>(userErrorArgs[0]).Value.Span));
    }
}

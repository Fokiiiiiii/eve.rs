using Eve.Host;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services.CharacterInfo;
using Eve.Universe;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Config_recordsets_accept_large_client_batches_without_server_only_caps()
    {
        const int locationCount = 501;
        const int ownerCount = 10_001;
        const long firstOwnerId = 4_000_000;

        var ownersById = Enumerable.Range(0, ownerCount)
            .Select(index => firstOwnerId + index)
            .ToDictionary(id => id, id => new AgentOwnerInfo($"Agent {id}", id % 2));
        var config = new ConfigService(
            null!, null!, new StationCatalog([]), new SolarSystemCatalog([]),
            agentOwners: ownersById);
        Start(strictRpc: true, configure: registry => registry.Register(config));

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();
        var kwargs = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
        ]);

        async Task<PyValue> Call(long callId, string method, PyList ids)
        {
            await front.SendAsync(new Packet(open.SessionId,
                MarshalCodec.Encode(Packets.CallWithKwargs(
                    open, callId, "config", method, new PyTuple([ids]), kwargs))));
            var response = Assert.IsType<Send>(await front.ReceiveAsync());
            var decoded = MarshalCodec.Decode(response.Payload);
            Assert.Equal(MachoMessage.CallRsp, MachoPacket.Decode(decoded)!.Type);
            return Result(decoded)!;
        }

        var locations = Assert.IsType<PyTuple>(await Call(
            1,
            "GetMultiLocationsEx",
            new PyList(Enumerable.Range(0, locationCount)
                .Select(index => (PyValue)new PyLong(30_000_000 + index))
                .ToArray())));
        Assert.Empty(Assert.IsType<PyTuple>(locations.Items[1]).Items);

        var owners = Assert.IsType<PyTuple>(await Call(
            2,
            "GetMultiOwnersEx",
            new PyList(ownersById.Keys.Select(id => (PyValue)new PyLong(id)).ToArray())));
        Assert.Equal(ownerCount, Assert.IsType<PyTuple>(owners.Items[1]).Items.Count);
    }
}

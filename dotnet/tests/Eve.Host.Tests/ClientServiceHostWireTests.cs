using Eve.Host;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Eject_encoded_item_and_epic_arc_calls_round_trip_through_host_registration()
    {
        const long characterId = 20;
        const long sourceId = 900;
        const long itemId = 901;
        const long agentId = 7001;
        long? launchedSourceId = null;
        long? launchedItemId = null;
        var epicCharacterIds = new List<long>();

        Start(configure: registry =>
        {
            registry.Register(new EjectRpcService((_, source, item, _) =>
            {
                launchedSourceId = source;
                launchedItemId = item;
                return ValueTask.CompletedTask;
            }));
            registry.Register(new EncodedItemsRpcService(
                (id, _) => ValueTask.FromResult<Item?>(id == itemId
                    ? new Item(itemId, 123, characterId, 400, 0, 1, true, null,
                        CustomInfo: """{"evejsEncodedItemUrl":"https://assets.example/item.png"}""")
                    : null),
                (_, candidate, _) => ValueTask.FromResult(candidate.TypeId == 123)));
            registry.Register(new EpicArcStatusService((agent, character, _) =>
            {
                if (agent == agentId)
                    epicCharacterIds.Add(character);
                return ValueTask.FromResult(agent == agentId);
            }));
        }, sessionOpened: session => session.Set("charid", new PyInt(characterId)));

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();

        async Task<PyValue> Call(long callId, string service, string method, params PyValue[] args)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.Call(open, callId, service, method, args))));
            return Result(MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload)) ?? PyNone.Instance;
        }

        Assert.Equal(PyNone.Instance, await Call(1, "eject", "LaunchShipOrContainerFromWreckOrContainer",
            new PyLong(sourceId), new PyLong(itemId)));
        Assert.Equal(sourceId, launchedSourceId);
        Assert.Equal(itemId, launchedItemId);

        Assert.Equal(new PyBytes("https://assets.example/item.png"u8.ToArray()),
            await Call(2, "encodedItems", "DecodeItem", new PyLong(itemId)));
        Assert.Equal(PyNone.Instance,
            await Call(3, "encodedItems", "DecodeItem", new PyLong(itemId + 1)));

        Assert.Equal(PyBool.True,
            await Call(4, "epicArcStatus", "AgentHasEpicMissionsForCharacter", new PyLong(agentId)));
        Assert.Equal(PyBool.False,
            await Call(5, "epicArcStatus", "AgentHasEpicMissionsForCharacter", new PyLong(agentId + 1)));
        Assert.Equal(new[] { characterId }, epicCharacterIds);
    }
}

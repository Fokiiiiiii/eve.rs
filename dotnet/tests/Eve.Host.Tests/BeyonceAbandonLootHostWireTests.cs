using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Ships;
using Eve.Services.Space;
using System.Text;
using Xunit;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Beyonce_abandon_loot_uses_the_bound_route_and_space_container_provider()
    {
        const long systemId = 31_000_005;
        const long characterId = 90_000_001;
        const long firstWreck = 71_000_001;
        const long secondWreck = 71_000_002;
        var containers = new AbandonLootContainers();
        var service = new BeyonceService(null!, () => FixedNowMs) { SpaceContainers = containers };
        Start(
            configure: registry => registry.Register(service),
            sessionOpened: session =>
            {
                session.CharacterId = characterId;
                session.Set("solarsystemid", new PyLong(systemId));
            });

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();
        var transportKwargs = new PyDict([
            new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1)),
        ]);
        var nestedArgs = Py.Tuple(
            new PyStr("CmdAbandonLoot"),
            Py.Tuple(new PyList([new PyInt(firstWreck), new PyLong(secondWreck), new PyLong(firstWreck)])),
            new PyDict([]));
        var request = Packets.CallWithKwargs(
            open,
            1,
            "beyonce",
            "MachoBindObject",
            Py.Tuple(Py.Tuple(new PyLong(systemId), new PyInt(6)), nestedArgs),
            transportKwargs);

        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(request)));
        var response = Assert.IsType<PyTuple>(Result(MarshalCodec.Decode(
            Assert.IsType<Send>(await front.ReceiveAsync()).Payload)));
        Assert.IsType<PySubStruct>(response.Items[0]);
        Assert.Same(PyNone.Instance, response.Items[1]);
        Assert.Equal(characterId, containers.CharacterId);
        Assert.Equal([firstWreck, secondWreck], containers.ContainerIds);

        const long refusedWreck = 71_000_003;
        containers.RefuseId = refusedWreck;
        var refusalRequest = Packets.CallWithKwargs(
            open,
            2,
            "beyonce",
            "MachoBindObject",
            Py.Tuple(
                Py.Tuple(new PyLong(systemId), new PyInt(6)),
                Py.Tuple(
                    new PyStr("CmdAbandonLoot"),
                    Py.Tuple(new PyList([new PyLong(refusedWreck)])),
                    new PyDict([]))),
            transportKwargs);
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(refusalRequest)));
        var failure = Assert.IsType<Send>(await front.ReceiveAsync()).Payload;
        var error = Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(failure)));
        Assert.Equal(MachoMessage.ErrorResponse, error.Type);
        Assert.Contains("SafetyActivated", Encoding.ASCII.GetString(failure.Span));
    }

    private sealed class AbandonLootContainers : ISpaceContainers
    {
        public long? CharacterId { get; private set; }
        public long? RefuseId { get; set; }
        public IReadOnlyList<long> ContainerIds { get; private set; } = [];

        public bool IsInSpace(ClientSession session) => true;
        public int? SolarSystemOf(ClientSession session) => 31_000_005;
        public Task<Item?> OpenAsync(ClientSession session, long containerId, CancellationToken cancellationToken) => Task.FromResult<Item?>(null);
        public Task<long?> MoveAsync(ClientSession session, long itemId, long sourceId, long destinationId, long? quantity, CancellationToken cancellationToken) =>
            Task.FromResult<long?>(null);
        public Task<bool?> MoveAllAsync(ClientSession session, IReadOnlyList<long> itemIds, long sourceId, long destinationId, CancellationToken cancellationToken) =>
            Task.FromResult<bool?>(null);
        public Task<IReadOnlyList<long>> JettisonAsync(ClientSession session, IReadOnlyList<long> itemIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<long>>([]);

        public Task AbandonLootAsync(ClientSession session, IReadOnlyList<long> containerIds, CancellationToken cancellationToken)
        {
            CharacterId = session.CharacterId;
            ContainerIds = containerIds;
            if (RefuseId is { } refused && containerIds.Contains(refused))
                throw MachoErrors.ThrowUserError("SafetyActivated");
            return Task.CompletedTask;
        }
    }
}

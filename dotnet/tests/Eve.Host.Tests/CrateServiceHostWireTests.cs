using Eve.Host;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Crate_rpcs_use_the_registered_host_route_and_accept_transport_macho_version()
    {
        const long characterId = 90000001;
        var backend = new CrateBackend();
        Start(strictRpc: true,
            configure: registry => registry.Register(new CrateService(backend)),
            sessionOpened: session =>
            {
                session.CharacterId = characterId;
                session.Set("charid", new PyLong(characterId));
            });

        var front = await Handshaken(await ConnectAsync());
        var open = Open();
        await front.SendAsync(open);
        await front.ReceiveAsync();
        var kwargs = new PyDict([new KeyValuePair<PyValue, PyValue>(new PyStr("machoVersion"), new PyInt(1))]);
        long callId = 0;
        var notifications = new List<string>();

        async Task<PyValue> Call(string method, params PyValue[] args)
        {
            await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
                Packets.CallWithKwargs(open, ++callId, "crateService", method, new PyTuple(args), kwargs))));
            while (true)
            {
                var decoded = MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload);
                var packet = Assert.IsType<MachoPacket>(MachoPacket.Decode(decoded));
                if (packet.Type == MachoMessage.Notification)
                {
                    notifications.Add(packet.Dest.BroadcastId ?? string.Empty);
                    continue;
                }
                Assert.Equal(MachoMessage.CallRsp, packet.Type);
                return Result(decoded)!;
            }
        }

        void AssertTuple(PyValue value, params PyValue[] expected)
        {
            var actual = Assert.IsType<PyTuple>(value).Items;
            Assert.Equal(expected.Length, actual.Count);
            for (var index = 0; index < expected.Length; index++)
                Assert.Equal(expected[index], actual[index]);
        }

        IReadOnlyList<PyValue> SingleRow(PyValue value) =>
            Assert.IsType<PyTuple>(Assert.Single(Assert.IsType<PyList>(value).Items)).Items;

        AssertTuple(await Call("GetCrateLocation", new PyInt(100)), new PyInt(60000001), new PyInt(4));
        var preview = SingleRow(await Call("PeekInsideFixedCrate", new PyInt(100)));
        Assert.Equal(new PyInt(34), preview[0]);
        Assert.Equal(new PyInt(6), preview[1]);
        Assert.Empty(Assert.IsType<PyBytes>(preview[2]).Value.ToArray());
        AssertTuple(Assert.Single(Assert.IsType<PyList>(await Call("OpenCrate", new PyInt(100))).Items),
            new PyInt(900), new PyInt(34), new PyInt(6));
        Assert.Equal(PyNone.Instance,
            await Call("ClaimLoot", new PyInt(900), new PyInt(60000001), new PyInt(4)));
        AssertTuple(await Call("ClaimLootFromCrateStack", new PyInt(100), new PyInt(2)),
            new PyInt(2), new PyInt(2));
        Assert.Equal(PyNone.Instance, await Call("ClaimSkillBundle", new PyInt(100), new PyInt(123)));
        Assert.Equal(PyNone.Instance, await Call("ClaimFitting", new PyInt(100), new PyInt(456)));
        Assert.Equal(["OnItemChange", "OnItemChange"], notifications);
        Assert.Equal(3, backend.OpenCount);

        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.CallWithKwargs(open, ++callId, "crateService", "OpenCrate", new PyTuple([new PyInt(0)]), kwargs))));
        var invalid = MarshalCodec.Decode(Assert.IsType<Send>(await front.ReceiveAsync()).Payload);
        Assert.Equal(MachoMessage.ErrorResponse, Assert.IsType<MachoPacket>(MachoPacket.Decode(invalid)).Type);
    }

    private sealed class CrateBackend : ICrateServiceBackend
    {
        private long _quantity = 3;
        public int OpenCount { get; private set; }

        public Task<CrateItem?> GetItemAsync(long itemId, CancellationToken cancellationToken) =>
            Task.FromResult<CrateItem?>(itemId == 100
                ? new CrateItem(100, 500, 90000001, 60000001, 4, _quantity, false, 1194, 2, null,
                    "{\"evejsCrateLoot\":[[34,6]]}")
                : null);

        public Task<bool> TypeExistsAsync(int typeId, CancellationToken cancellationToken) =>
            Task.FromResult(typeId == 34);

        public Task<CrateOpenResult?> OpenAsync(
            long itemId, long characterId, int crateCount, long destinationId, int destinationFlagId,
            long grantLocationId, int grantFlagId, IReadOnlyList<CrateLootEntry> loot,
            CancellationToken cancellationToken)
        {
            _quantity -= crateCount;
            OpenCount += crateCount;
            var rewards = loot.Select((entry, index) => new CrateRewardItem(
                900 + index, entry.TypeId, entry.Quantity)).ToArray();
            return Task.FromResult<CrateOpenResult?>(new CrateOpenResult(rewards));
        }

        public Task<CrateClaimResult?> ClaimLootAsync(
            long itemId, long characterId, long pendingLocationId, long destinationId, int destinationFlagId,
            CancellationToken cancellationToken) =>
            Task.FromResult<CrateClaimResult?>(itemId == 900
                ? new CrateClaimResult(
                    new CrateItem(itemId, 34, characterId, destinationId, destinationFlagId, 6, false, 18, 4, null, null),
                    pendingLocationId, 0, 6)
                : null);
    }
}
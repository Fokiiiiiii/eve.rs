using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Services.Core.Tests;

public sealed class CrateServiceTests
{
    private static readonly string[] RpcMethods =
    [
        "GetCrateLocation", "OpenCrate", "ClaimLootFromCrateStack", "PeekInsideFixedCrate",
        "ClaimLoot", "ClaimSkillBundle", "ClaimFitting",
    ];

    [Fact]
    public async Task Crate_rpc_methods_are_registered_with_client_safe_unwired_shapes()
    {
        IService service = new CrateService();

        Assert.Equal("crateService", service.Name);
        Assert.All(RpcMethods, method => Assert.True(service.HasMethod(method), method));
        Assert.Equal(PyTuple.Of(new PyInt(0), new PyInt(4)),
            await service.CallAsync(Call("GetCrateLocation", new PyInt(1))));
        Assert.IsType<PyList>(await service.CallAsync(Call("OpenCrate", new PyInt(1))));
        Assert.IsType<PyList>(await service.CallAsync(Call("PeekInsideFixedCrate", new PyInt(1))));
        Assert.Equal(PyTuple.Of(new PyInt(0), new PyInt(30)),
            await service.CallAsync(Call("ClaimLootFromCrateStack", new PyInt(1), new PyInt(99))));
        Assert.IsType<PyNone>(await service.CallAsync(Call("ClaimLoot", new PyInt(1))));
        Assert.IsType<PyNone>(await service.CallAsync(Call("ClaimSkillBundle", new PyInt(1), new PyInt(2))));
        Assert.IsType<PyNone>(await service.CallAsync(Call("ClaimFitting", new PyInt(1), new PyInt(2))));
    }

    [Fact]
    public async Task Crate_rpcs_preserve_reference_rows_and_inventory_claim_transitions()
    {
        var backend = new FakeBackend();
        var service = new CrateService(backend);

        Assert.Equal(PyTuple.Of(new PyInt(60000001), new PyInt(4)),
            await service.CallAsync(Call("GetCrateLocation", new PyInt(100))));
        Assert.Equal(
            new PyList([PyTuple.Of(new PyInt(34), new PyInt(6), new PyStr(""))]),
            await service.CallAsync(Call("PeekInsideFixedCrate", new PyInt(100))));

        var opened = Assert.IsType<PyList>(await service.CallAsync(Call("OpenCrate", new PyInt(100))));
        Assert.Equal(new PyList([PyTuple.Of(new PyInt(900), new PyInt(34), new PyInt(6))]), opened);
        Assert.Equal(1, backend.OpenCount);
        Assert.True(backend.LastGrantLocationId < 0);
        Assert.Equal(0, backend.LastGrantFlagId);
        Assert.Single(backend.LastLoot);
        Assert.Equal(new CrateLootEntry(34, 6), backend.LastLoot[0]);

        Assert.IsType<PyNone>(await service.CallAsync(Call("ClaimLoot",
            new PyInt(900), new PyInt(60000001), new PyInt(4))));
        Assert.Equal((100L, 900L, 60000001L, 4), backend.LastClaim);

        Assert.Equal(PyTuple.Of(new PyInt(30), new PyInt(30)),
            await service.CallAsync(Call("ClaimLootFromCrateStack", new PyInt(100), new PyInt(99))));
        Assert.Equal(30, backend.LastCrateCount);

        var skillClaim = Assert.IsType<PyDict>(
            await service.CallAsync(Call("ClaimSkillBundle", new PyInt(100), new PyInt(123))));
        Assert.Equal(1, backend.SkillBundlesClaimed);
        Assert.Contains(skillClaim.Entries, pair => pair.Key == new PyStr("changedSkillCount") && pair.Value == new PyInt(1));
        Assert.Equal(new PyInt(456), await service.CallAsync(Call("ClaimFitting", new PyInt(100), new PyInt(456))));
        Assert.Equal(1, backend.FittingsClaimed);
    }

    [Fact]
    public async Task Concurrent_fitting_claims_only_save_the_single_pending_reward_once()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<long?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new FakeBackend
        {
            FittingClaim = async _ => { entered.TrySetResult(); return await finish.Task; },
        };
        var service = new CrateService(backend);
        await service.CallAsync(Call("OpenCrate", new PyInt(100)));
        var first = service.CallAsync(Call("ClaimFitting", new PyInt(100), new PyInt(456))).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = service.CallAsync(Call("ClaimFitting", new PyInt(100), new PyInt(456))).AsTask();
        finish.SetResult(456);
        var answers = await Task.WhenAll(first, second);
        Assert.Equal(1, backend.FittingsClaimed);
        Assert.Contains(answers, answer => answer == new PyInt(456));
        Assert.Contains(answers, answer => answer == PyNone.Instance);
    }

    [Fact]
    public async Task A_failed_in_flight_claim_restores_the_reward_after_another_claim_finishes()
    {
        var failed = new TaskCompletionSource<long?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var backend = new FakeBackend
        {
            FittingClaim = _ => ++attempts == 1 ? failed.Task : Task.FromResult<long?>(456),
        };
        var service = new CrateService(backend);
        await service.CallAsync(Call("OpenCrate", new PyInt(100)));
        await service.CallAsync(Call("OpenCrate", new PyInt(100)));
        var first = service.CallAsync(Call("ClaimFitting", new PyInt(100), new PyInt(456))).AsTask();
        Assert.Equal(new PyInt(456), await service.CallAsync(Call("ClaimFitting", new PyInt(100), new PyInt(456))));
        failed.SetResult(null);
        Assert.Equal(PyNone.Instance, await first);
        Assert.Equal(new PyInt(456), await service.CallAsync(Call("ClaimFitting", new PyInt(100), new PyInt(456))));
        Assert.Equal(PyNone.Instance, await service.CallAsync(Call("ClaimFitting", new PyInt(100), new PyInt(456))));
        Assert.Equal(3, backend.FittingsClaimed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_failed_fitting_save_keeps_the_pending_reward_for_retry(bool throws)
    {
        var attempts = 0;
        var backend = new FakeBackend
        {
            FittingClaim = _ => ++attempts == 1
                ? throws ? Task.FromException<long?>(new InvalidOperationException("save failed")) : Task.FromResult<long?>(null)
                : Task.FromResult<long?>(456),
        };
        var service = new CrateService(backend);
        await service.CallAsync(Call("OpenCrate", new PyInt(100)));
        if (throws)
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.CallAsync(Call("ClaimFitting", new PyInt(100), new PyInt(456))).AsTask());
        else
            Assert.Equal(PyNone.Instance, await service.CallAsync(Call("ClaimFitting", new PyInt(100), new PyInt(456))));
        Assert.Equal(new PyInt(456), await service.CallAsync(Call("ClaimFitting", new PyInt(100), new PyInt(456))));
        Assert.Equal(2, backend.FittingsClaimed);
    }

    [Fact]
    public async Task Claim_loot_recovers_pending_inventory_state_after_service_restart()
    {
        var backend = new FakeBackend
        {
            PendingReward = new CrateItem(900, 34, 42, -9000000000001, 0, 6, false, 18, 18, "Tritanium", null),
        };
        var service = new CrateService(backend);

        await service.CallAsync(Call("ClaimLoot", new PyInt(900), new PyInt(60000001), new PyInt(4)));

        Assert.Equal(-9000000000001, backend.LastPendingLocationId);
        Assert.Equal((100L, 900L, 60000001L, 4), backend.LastClaim);
    }

    [Fact]
    public async Task Stack_claim_notifies_the_inventory_of_consumed_crates_and_granted_loot()
    {
        var service = new CrateService(new FakeBackend());
        var sink = new RecordingSink();
        var call = Call("ClaimLootFromCrateStack", new PyInt(100), new PyInt(2));
        call.Session.Sink = sink;

        Assert.Equal(PyTuple.Of(new PyInt(2), new PyInt(2)), await service.CallAsync(call));

        Assert.Equal(2, sink.Packets.Count);
    }

    private static ServiceCall Call(string method, params PyValue[] args) => new()
    {
        Method = method,
        Args = args,
        Session = new ClientSession { CharacterId = 42 },
    };

    private sealed class FakeBackend : ICrateServiceBackend
    {
        private const string Metadata =
            "{\"evejsCrateLoot\":[[34,6]],\"evejsCrateSkillBundles\":[{\"id\":123,\"freeSkillPoints\":10,\"skills\":[[333,2]]}],\"evejsCrateFittings\":[456]}";
        private readonly CrateItem _crate = new(
            100, 5000, 42, 60000001, 4, 40, false, 1194, 18, "Reward Crate", Metadata);

        public CrateItem? PendingReward { get; init; }
        public int OpenCount { get; private set; }
        public int LastCrateCount { get; private set; }
        public long LastGrantLocationId { get; private set; }
        public int LastGrantFlagId { get; private set; }
        public IReadOnlyList<CrateLootEntry> LastLoot { get; private set; } = [];
        public (long CrateId, long ItemId, long DestinationId, int FlagId) LastClaim { get; private set; }
        public int SkillBundlesClaimed { get; private set; }
        public int FittingsClaimed { get; private set; }
        public Func<CancellationToken, Task<long?>>? FittingClaim { get; init; }
        public long LastPendingLocationId { get; private set; }

        public Task<CrateItem?> GetItemAsync(long itemId, CancellationToken cancellationToken) =>
            Task.FromResult<CrateItem?>(itemId == _crate.ItemId ? _crate : itemId == PendingReward?.ItemId ? PendingReward : null);

        public Task<bool> TypeExistsAsync(int typeId, CancellationToken cancellationToken) =>
            Task.FromResult(typeId == 34);

        public Task<CrateOpenResult?> OpenAsync(
            long itemId, long characterId, int crateCount, long destinationId, int destinationFlagId,
            long grantLocationId, int grantFlagId, IReadOnlyList<CrateLootEntry> loot,
            CancellationToken cancellationToken)
        {
            OpenCount++;
            LastCrateCount = crateCount;
            LastGrantLocationId = grantLocationId;
            LastGrantFlagId = grantFlagId;
            LastLoot = loot;
            return Task.FromResult<CrateOpenResult?>(new CrateOpenResult(
                [new CrateRewardItem(900, 34, loot[0].Quantity, 18, 18)],
                new CrateInventoryChange(_crate with { Quantity = _crate.Quantity - crateCount },
                    destinationId, destinationFlagId, _crate.Quantity, _crate.Quantity)));
        }

        public Task<CrateClaimResult?> ClaimLootAsync(
            long itemId, long characterId, long pendingLocationId, long destinationId, int destinationFlagId,
            CancellationToken cancellationToken)
        {
            LastClaim = (100, itemId, destinationId, destinationFlagId);
            LastPendingLocationId = pendingLocationId;
            var reward = new CrateItem(itemId, 34, characterId, destinationId, destinationFlagId, 6, false, 18, 18, "Tritanium", null);
            return Task.FromResult<CrateClaimResult?>(new CrateClaimResult(reward, pendingLocationId, 0, 6));
        }

        public Task<CrateSkillClaim?> ClaimSkillBundleAsync(
            long characterId, CrateSkillBundle bundle, CancellationToken cancellationToken)
        {
            SkillBundlesClaimed++;
            Assert.Equal(10, bundle.FreeSkillPoints);
            Assert.Equal(new CrateSkillGrant(333, 2), bundle.Skills.Single());
            return Task.FromResult<CrateSkillClaim?>(new CrateSkillClaim(1, 10));
        }

        public Task<long?> ClaimFittingAsync(long characterId, int fittingId, CancellationToken cancellationToken)
        {
            FittingsClaimed++;
            return FittingClaim?.Invoke(cancellationToken) ?? Task.FromResult<long?>((long)fittingId);
        }
    }

    private sealed class RecordingSink : IPacketSink
    {
        public List<PyValue> Packets { get; } = [];

        public void Send(PyValue packet) => Packets.Add(packet);
    }
}

using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Services.Core.Tests;

public sealed class JumpCloneServiceTests
{
    [Fact]
    public async Task Clone_jump_cooldown_uses_the_injected_game_clock()
    {
        const long nowUnixMs = 1_577_836_800_000;
        var store = new TestJumpCloneStore(Py.FileTimeFromUnixMs(nowUnixMs - 23 * 60 * 60 * 1000));
        var service = new JumpCloneService(() => nowUnixMs, store);
        var session = new ClientSession(new Sink()) { SessionId = 1, CharacterId = 42 };
        session.Set("stationid", new PyLong(100));

        var error = await Assert.ThrowsAsync<MachoWrappedException>(() =>
            ((IService)service).CallAsync(new ServiceCall
            {
                Session = session,
                Method = "CloneJump",
                Args = [new PyLong(200), new PyLong(900), new PyReal(900_000), PyBool.False],
            }).AsTask());

        Assert.Equal("CustomNotify", error.Error.UserErrorMessage);
        Assert.Equal(0, store.JumpCalls);
    }

    [Fact]
    public async Task Clone_jump_uses_the_quoted_origin_bay_fee_and_owner()
    {
        var store = new SettlingStore();
        var link = new JumpCloneSpaceLink { Space = new CloneSpace() };
        var service = new JumpCloneService(() => 1_700_000_000_000, store, link);
        var session = new ClientSession(new Sink()) { SessionId = 1, CharacterId = 42 };
        session.Set("structureid", new PyLong(700));

        var quote = await ((IService)service).CallAsync(new ServiceCall
        {
            Session = session,
            Method = "GetPriceForClone",
            Args = [],
        });
        var result = await ((IService)service).CallAsync(new ServiceCall
        {
            Session = session,
            Method = "CloneJump",
            Args = [new PyLong(200), new PyLong(900), quote, PyBool.False],
        });

        Assert.Same(PyNone.Instance, result);
        Assert.Equal(new PyReal(1250), quote);
        Assert.Equal(1250m, store.Fee);
        Assert.Equal(99, store.OwnerCorporationId);
        Assert.Equal("structure", store.OriginLocationKind);
    }

    [Fact]
    public async Task Clone_jump_refuses_insufficient_funds_for_the_quoted_fee()
    {
        var store = new SettlingStore { Refusal = "INSUFFICIENT_FUNDS" };
        var service = new JumpCloneService(() => 1_700_000_000_000, store, new JumpCloneSpaceLink { Space = new CloneSpace() });
        var session = new ClientSession(new Sink()) { SessionId = 1, CharacterId = 42 };
        session.Set("stationid", new PyLong(100));

        var error = await Assert.ThrowsAsync<MachoWrappedException>(() =>
            ((IService)service).CallAsync(new ServiceCall
            {
                Session = session,
                Method = "CloneJump",
                Args = [new PyLong(200), new PyLong(900), new PyReal(900_000), PyBool.False],
            }).AsTask());

        Assert.Equal("NotEnoughMoney", error.Error.UserErrorMessage);
        Assert.Equal(900_000m, store.Fee);
    }

    private sealed class Sink : IPacketSink
    {
        public void Send(PyValue packet) { }
    }

    private sealed class CloneSpace : IJumpCloneSpace
    {
        public ValueTask<ShipCloneBay?> ActiveShipAsync(ClientSession session, CancellationToken cancellationToken) => ValueTask.FromResult<ShipCloneBay?>(null);
        public ValueTask<double?> DistanceAsync(ClientSession first, long firstShipId, ClientSession second, long secondShipId, CancellationToken cancellationToken) => ValueTask.FromResult<double?>(null);
        public ClientSession? SessionOf(long characterId) => null;
        public ValueTask<StructureCloneBay?> StructureBayAsync(long characterId, long structureId, CancellationToken cancellationToken) =>
            ValueTask.FromResult<StructureCloneBay?>(new StructureCloneBay(structureId, 99, 1250m));
    }

    private sealed class SettlingStore : IJumpCloneStore
    {
        public string? Refusal { get; init; }
        public decimal Fee { get; private set; }
        public long OwnerCorporationId { get; private set; }
        public string OriginLocationKind { get; private set; } = "";

        public ValueTask<JumpCloneSnapshot> ReadAsync(long characterId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new JumpCloneSnapshot([new JumpCloneRecord(900, 200, "station", "Remote", [])], 0));
        public ValueTask<bool> RenameAsync(long characterId, long jumpCloneId, string name, CancellationToken cancellationToken) => ValueTask.FromResult(false);
        public ValueTask<JumpCloneRecord?> DestroyAsync(long characterId, long jumpCloneId, CancellationToken cancellationToken) => ValueTask.FromResult<JumpCloneRecord?>(null);
        public ValueTask ResetLastJumpAsync(long characterId, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask<int> SkillLevelAsync(long characterId, int typeId, CancellationToken cancellationToken) => ValueTask.FromResult(0);
        public ValueTask<JumpCloneRecord?> InstallAsync(long characterId, JumpCloneInstall install, CancellationToken cancellationToken) => ValueTask.FromResult<JumpCloneRecord?>(null);
        public ValueTask<decimal> IskBalanceAsync(long characterId, CancellationToken cancellationToken) => ValueTask.FromResult(100m);

        public ValueTask<CloneJumpResult> JumpAsync(long characterId, long targetCloneId, long originLocationId, string originLocationKind,
            decimal fee, long ownerCorporationId, long nowFileTime, CancellationToken cancellationToken)
        {
            (Fee, OwnerCorporationId, OriginLocationKind) = (fee, ownerCorporationId, originLocationKind);
            return ValueTask.FromResult(new CloneJumpResult(Refusal, 200, 300, 400, 500));
        }
    }

    private sealed class TestJumpCloneStore(long lastJumpFileTime) : IJumpCloneStore
    {
        public int JumpCalls { get; private set; }

        public ValueTask<JumpCloneSnapshot> ReadAsync(long characterId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new JumpCloneSnapshot(
                [new JumpCloneRecord(900, 200, "station", "Remote", [])],
                lastJumpFileTime));

        public ValueTask<bool> RenameAsync(long characterId, long jumpCloneId, string name, CancellationToken cancellationToken) =>
            ValueTask.FromResult(false);

        public ValueTask<JumpCloneRecord?> DestroyAsync(long characterId, long jumpCloneId, CancellationToken cancellationToken) =>
            ValueTask.FromResult<JumpCloneRecord?>(null);

        public ValueTask ResetLastJumpAsync(long characterId, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask<int> SkillLevelAsync(long characterId, int typeId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(0);

        public ValueTask<JumpCloneRecord?> InstallAsync(long characterId, JumpCloneInstall install, CancellationToken cancellationToken) =>
            ValueTask.FromResult<JumpCloneRecord?>(null);

        public ValueTask<decimal> IskBalanceAsync(long characterId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(0m);

        public ValueTask<CloneJumpResult> JumpAsync(
            long characterId,
            long targetCloneId,
            long originLocationId,
            string originLocationKind,
            decimal fee,
            long ownerCorporationId,
            long nowFileTime,
            CancellationToken cancellationToken)
        {
            JumpCalls++;
            return ValueTask.FromResult(new CloneJumpResult("MUST_LEAVE_SHIP"));
        }
    }
}

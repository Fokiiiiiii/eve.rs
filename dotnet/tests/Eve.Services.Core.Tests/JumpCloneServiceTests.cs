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

    private sealed class Sink : IPacketSink
    {
        public void Send(PyValue packet) { }
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
            decimal fee,
            long nowFileTime,
            CancellationToken cancellationToken)
        {
            JumpCalls++;
            return ValueTask.FromResult(new CloneJumpResult("MUST_LEAVE_SHIP"));
        }
    }
}

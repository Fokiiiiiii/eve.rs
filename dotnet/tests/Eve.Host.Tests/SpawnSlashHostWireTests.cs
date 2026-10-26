using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using Eve.Services.Slash;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [Fact]
    public async Task Spawn_slash_reply_is_the_ship_id_consumed_by_client_load_calls()
    {
        var spawns = new TestSpaceSpawns();
        var service = new SlashService(
            new ShipHealthCommands(null!, null!, () => FixedNowMs),
            spawn: new SpawnCommands(
                new ItemTypeNames([new ItemTypeEntry(587, "Rifter", 25, 6, true)]),
                spawns));
        Start(
            strictRpc: true,
            configure: registry => registry.Register(service),
            sessionOpened: session =>
            {
                session.CharacterId = 900001;
                session.Set("accountRole", new PyLong(GameMasterRoles.Gml));
            });
        var (front, open) = await OpenFrontAsync();

        var spawnedId = await HostRpcAsync(front, open, 1, "slash", "SlashCmd", new PyStr("/spawn Rifter"));
        Assert.Equal(new PyLong(9000), spawnedId);
        Assert.Equal(587, spawns.TypeId);

        spawns.Result = new SpawnResult([], SpawnResult.NotInSpace);
        var refused = await HostRpcAsync(front, open, 2, "slash", "SlashCmd", new PyStr("/spawn Rifter"));
        Assert.Equal(new PyStr("You must be in space before using /spawn."), refused);
    }

    private sealed class TestSpaceSpawns : ISpaceSpawns
    {
        public SpawnResult Result { get; set; } = new([9000]);
        public int TypeId { get; private set; }

        public Task<SpawnResult> SpawnHullsAsync(ClientSession session, int typeId, int count, double deviationMeters, CancellationToken cancellationToken)
        {
            TypeId = typeId;
            return Task.FromResult(Result);
        }

        public Task<SpawnedView?> SpawnedAsync(ClientSession session, CancellationToken cancellationToken) =>
            Task.FromResult<SpawnedView?>(new SpawnedView([], []));

        public Task<int> RemoveAsync(IReadOnlyList<long> shipIds, CancellationToken cancellationToken) =>
            Task.FromResult(0);
    }
}

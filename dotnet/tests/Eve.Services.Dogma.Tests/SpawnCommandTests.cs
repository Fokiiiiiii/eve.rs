using Eve.Data;
using Eve.Macho;
using Eve.Marshal;
using Eve.Sde;
using Eve.Services;
using Eve.Services.Slash;
using Npgsql;

namespace Eve.Services.Dogma.Tests;

/// <summary>
/// <c>/spawn</c>, <c>/spawnn</c>, <c>/unspawn</c> and <c>/nuke</c> (the words and the checks of the commands; the ships are a fake host's, whose removal is the
/// simulation's own leave in <c>SpaceSessions</c>). The names are the pinned SDE's.
/// </summary>
public class SpawnCommandTests
{
    private sealed class FakeSpawns : ISpaceSpawns
    {
        private long _next = 9000;

        public SpawnResult? Refusal { get; set; }

        public List<(int TypeId, int Count, double Deviation)> Spawns { get; } = [];

        /// <summary>The caller's solar system: null when the pilot is not in space.</summary>
        public SpawnedView? View { get; set; }

        public Task<SpawnResult> SpawnHullsAsync(ClientSession session, int typeId, int count, double deviationMeters, CancellationToken cancellationToken)
        {
            Spawns.Add((typeId, count, deviationMeters));
            if (Refusal is { } refusal) return Task.FromResult(refusal);
            return Task.FromResult(new SpawnResult(Enumerable.Range(0, count).Select(_ => _next++).ToArray()));
        }

        public Task<SpawnedView?> SpawnedAsync(ClientSession session, CancellationToken cancellationToken) => Task.FromResult(View);

        public Task<int> RemoveAsync(IReadOnlyList<long> shipIds, CancellationToken cancellationToken)
        {
            var removed = 0;
            foreach (var shipId in shipIds)
            {
                if (View is not { } view || view.Ships.FirstOrDefault(ship => ship.ShipId == shipId) is not { } found) continue;
                View = view with { Ships = view.Ships.Where(ship => ship != found).ToArray() };
                removed++;
            }
            return Task.FromResult(removed);
        }
    }

    private static ClientSession Gm(ClientSession session)
    {
        session.Set("accountRole", new PyLong(GameMasterRoles.Gml));
        return session;
    }

    private static SpawnedView Ships(params (long Id, double[]? At)[] ships) =>
        new([0d, 0d, 0d], ships.Select(ship => new SpawnedShip(ship.Id, 587, ship.At)).ToArray());

    private static async Task<string> CallAsync(SlashService service, string command, ClientSession? session = null) =>
        Assert.IsType<PyStr>(await ((IService)service).CallAsync(new ServiceCall
        {
            Method = "SlashCmd",
            Args = [new PyStr(command)],
            Session = session ?? Gm(new ClientSession()),
            CallId = new PyInt(1),
        })).Value;

    [PgFact]
    public async Task Spawn_needs_a_character_a_ship_type_and_a_ship()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Rig.Url!));
        var fake = new FakeSpawns();
        var commands = new SpawnCommands(new ItemTypeNames(new SdeReader(source)), fake);

        Assert.Equal("Select a character before using /spawn.", await commands.SpawnAsync(Gm(new ClientSession()), "Rifter", default));
        Assert.Equal(SpawnCommands.SpawnUsage, await commands.SpawnAsync(Rig.Session(1001), "  ", default));
        Assert.StartsWith("Ship type not found: Nonsuch Hull.", await commands.SpawnAsync(Rig.Session(1001), "Nonsuch Hull", default));
        Assert.Equal("Tritanium is not a ship type. /spawn makes ship hulls only.", await commands.SpawnAsync(Rig.Session(1001), "Tritanium", default));
        Assert.Empty(fake.Spawns);
    }

    [PgFact]
    public async Task Spawn_makes_one_hull_of_the_type_and_names_it_and_a_refusal_of_space_is_said()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Rig.Url!));
        var fake = new FakeSpawns();
        var commands = new SpawnCommands(new ItemTypeNames(new SdeReader(source)), fake);

        Assert.Equal("Spawned 1 Rifter in space around you: 9000.", await commands.SpawnAsync(Rig.Session(1001), "Rifter", default));
        Assert.Equal((587, 1, 0d), Assert.Single(fake.Spawns));

        fake.Refusal = new SpawnResult([], SpawnResult.NotInSpace);
        Assert.Equal("You must be in space before using /spawn.", await commands.SpawnAsync(Rig.Session(1001), "Rifter", default));
    }

    [PgFact]
    public async Task Spawnn_takes_an_amount_a_deviation_and_a_type_and_checks_them()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Rig.Url!));
        var fake = new FakeSpawns();
        var commands = new SpawnCommands(new ItemTypeNames(new SdeReader(source)), fake);

        Assert.Equal("Spawned 3 Rifter in space around you: 9000, 9001, 9002.", await commands.SpawnnAsync(Rig.Session(1001), "3 500 Rifter", default));
        Assert.Equal((587, 3, 500d), Assert.Single(fake.Spawns));
        Assert.Equal("Spawn amount must be between 1 and 50.", await commands.SpawnnAsync(Rig.Session(1001), "0 5 Rifter", default));
        Assert.Equal("Spawn amount must be between 1 and 50.", await commands.SpawnnAsync(Rig.Session(1001), "51 5 Rifter", default));
        Assert.Equal(SpawnCommands.SpawnnUsage, await commands.SpawnnAsync(Rig.Session(1001), "x 5 Rifter", default));
        Assert.Equal(SpawnCommands.SpawnnUsage, await commands.SpawnnAsync(Rig.Session(1001), "3 -1 Rifter", default));
        Assert.Equal(SpawnCommands.SpawnnUsage, await commands.SpawnnAsync(Rig.Session(1001), "3 5", default));
        Assert.Single(fake.Spawns);
    }

    [PgFact]
    public async Task Unspawn_takes_out_the_named_ship_and_says_when_it_is_not_one_of_the_spawned()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Rig.Url!));
        var fake = new FakeSpawns { View = Ships((1001, [100d, 0d, 0d]), (1002, [9000d, 0d, 0d])) };
        var commands = new SpawnCommands(new ItemTypeNames(new SdeReader(source)), fake);

        Assert.Equal("Unspawned 1 ship.", await commands.UnspawnAsync(Rig.Session(1001), "1001", default));
        Assert.Equal(new long[] { 1002 }, fake.View!.Ships.Select(ship => ship.ShipId).ToArray());
        Assert.Equal("Not spawned in your solar system: 1001.", await commands.UnspawnAsync(Rig.Session(1001), "1001", default));
        Assert.Equal(SpawnCommands.UnspawnUsage, await commands.UnspawnAsync(Rig.Session(1001), "", default));
    }

    [PgFact]
    public async Task Unspawn_by_range_takes_only_the_ships_within_it_and_a_category_other_than_ship_takes_none()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Rig.Url!));
        var fake = new FakeSpawns { View = Ships((1001, [100d, 0d, 0d]), (1002, [9000d, 0d, 0d]), (1003, null)) };
        var commands = new SpawnCommands(new ItemTypeNames(new SdeReader(source)), fake);

        Assert.Equal("Nothing to unspawn.", await commands.UnspawnAsync(Rig.Session(1001), "range=500 only=categoryDrone", default));
        Assert.Equal(3, fake.View!.Ships.Count);
        Assert.Equal("Unspawned 1 ship.", await commands.UnspawnAsync(Rig.Session(1001), "range=500 only=categoryShip", default));
        Assert.Equal(new long[] { 1002, 1003 }, fake.View!.Ships.Select(ship => ship.ShipId).ToArray());
        var outside = new SpawnCommands(new ItemTypeNames(new SdeReader(source)), new FakeSpawns());
        Assert.Equal("You must be in space before using /unspawn.", await outside.UnspawnAsync(Rig.Session(1001), "range=500", default));
    }

    [PgFact]
    public async Task Nuke_takes_out_every_spawned_ship_of_the_system_and_then_there_are_none()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Rig.Url!));
        var fake = new FakeSpawns { View = Ships((1001, null), (1002, null)) };
        var service = new SlashService(new ShipHealthCommands(null!, null!, () => 0), spawn: new SpawnCommands(new ItemTypeNames(new SdeReader(source)), fake));
        var pilot = Gm(Rig.Session(1001));

        Assert.Equal("Removed 2 NPC ships from your solar system.", await CallAsync(service, "/nuke", pilot));
        Assert.Equal("No spawned NPC ship is in your solar system.", await CallAsync(service, "/nuke", pilot));
        Assert.Equal("Nothing to unspawn.", await CallAsync(service, "/unspawn range=5 only=categoryShip", pilot));
    }

    [PgFact]
    public async Task The_bare_and_slashed_spawn_names_reach_the_commands()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Rig.Url!));
        var service = new SlashService(new ShipHealthCommands(null!, null!, () => 0), spawn: new SpawnCommands(new ItemTypeNames(new SdeReader(source)), new FakeSpawns()));

        var pilot = Gm(Rig.Session(1001));

        Assert.Equal("Spawned 1 Rifter in space around you: 9000.", await CallAsync(service, "spawn Rifter", pilot));
        Assert.Equal("Spawned 1 Rifter in space around you: 9001.", await CallAsync(service, "/spawn Rifter", pilot));
    }
}

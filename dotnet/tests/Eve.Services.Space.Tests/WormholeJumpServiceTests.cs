using Eve.Data;
using Eve.Macho;
using Npgsql;
using Eve.Marshal;
using Eve.Space;
using Eve.Universe;
using Eve.Wormholes;

namespace Eve.Services.Space.Tests;

/// <summary>
/// The wormhole jump of a pilot in a J-space system, through the space service (slice 7, service layer): the mass pre-check, the jump the simulation takes
/// and the end the destination gets, the polarization of an end, and the collapse of an emptied connection taken out of the simulation.
/// </summary>
public sealed class WormholeJumpServiceTests
{
    private const int JSystem = 31_000_001, OtherJSystem = 31_000_002, KSystem = 30_000_001;

    private static WormholeType Type(int typeId, double maxStableMassKg, double maxJumpMassKg) => new(typeId, 7, 60, maxStableMassKg, 0, maxJumpMassKg, 302);

    /// <summary>A world in its own schema, with one J-system whose static (type <paramref name="type"/>) leads to the class 7 system of the test.</summary>
    private sealed class JWorld : IAsyncDisposable
    {
        private readonly NpgsqlDataSource _source;
        private readonly string _schema;

        private JWorld(NpgsqlDataSource source, string schema, WormholeWorld world)
        {
            _source = source;
            _schema = schema;
            World = world;
        }

        public WormholeWorld World { get; }

        public static async Task<JWorld> CreateAsync(Rig rig, WormholeType type)
        {
            var schema = "wh_svc_" + Guid.NewGuid().ToString("N")[..12];
            await WormholeWorld.MigrateAsync(rig.Source, schema);
            var classes = new Dictionary<int, int> { [JSystem] = 1, [OtherJSystem] = 2, [KSystem] = 7 };
            var systems = new[]
            {
                new JSpaceSystem(JSystem, "J1", 1, null, null, null, [new JSpaceStatic("AAA", type.TypeId)], "test"),
                new JSpaceSystem(OtherJSystem, "J2", 2, null, null, null, [], "test"),
            };
            var world = new WormholeWorld(rig.Source, rig.Scheduler, schema, new Dictionary<int, WormholeType> { [type.TypeId] = type }, classes, systems);
            await world.EnsureWorldAsync(7, rig.Now);
            world.RegisterJobs();
            rig.Space.Wormholes = world;
            world.Changed += rig.Space.PublishWormholeChange;
            return new JWorld(rig.Source, schema, world);
        }

        public async ValueTask DisposeAsync()
        {
            await using var connection = await _source.OpenConnectionAsync();
            await Sql.ExecuteAsync(connection, null, $"DROP SCHEMA IF EXISTS {_schema} CASCADE", CancellationToken.None);
        }
    }

    /// <summary>A pilot undocked in the J-system: its location is set before the undock, as the drone tests do.</summary>
    private static async Task<(ClientSession Session, Eve.Characters.Created Character, string Oid)> PilotInJSpaceAsync(Rig rig)
    {
        var place = rig.Systems.Get(JSystem) ?? throw new InvalidOperationException($"the static data has no solar system {JSystem}");
        var character = await rig.NewCharacterAsync("Wormhole Pilot");
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await rig.Characters.SetLocationAsync(connection, transaction, character.CharacterId, JSystem, place.ConstellationId, place.RegionId);
            await transaction.CommitAsync();
        }
        var session = await rig.SessionAsync(character);
        var oid = await rig.BindShipAsync(session);
        await rig.ShipCallAsync(session, "Undock", oid, new PyLong(session.GetInt("shipid")!.Value), PyBool.False);
        await rig.BindBeyonceAsync(session);
        return (session, character, oid);
    }

    private static string NotifyOf(MachoWrappedException error)
    {
        var arguments = ((PyTuple)((PyTuple)((PyObjectEx)error.Error.Payload).Header).Items[1]).Items;
        Assert.Equal("CustomNotify", ((PyStr)arguments[0]).Value);
        return ((PyStr)((PyDict)arguments[1]).Entries.Single().Value).Value;
    }

    [PgFact]
    public async Task A_ship_over_the_mass_limit_of_the_connection_is_refused_with_the_notice_and_the_simulation_hears_nothing()
    {
        await using var rig = await Rig.CreateAsync();
        await using var world = await JWorld.CreateAsync(rig, Type(1001, 1e9, 1_000));
        var (session, _, oid) = await PilotInJSpaceAsync(rig);
        var end = Assert.Single(await world.World.EndsInSystemAsync(JSystem));
        var before = rig.Sim.Heard.Count;

        var error = await Assert.ThrowsAsync<MachoWrappedException>(() => rig.Space.WormholeJumpAsync(session, end.BodyId, oid, CancellationToken.None));

        Assert.Equal("Your ship is too large for this wormhole.", NotifyOf(error));
        Assert.DoesNotContain(rig.Sim.Heard.Skip(before), message => message is SimCommand { Code: SimCommands.Jump });
        Assert.Single(await world.World.EndsInSystemAsync(JSystem));
    }

    [PgFact]
    public async Task A_jump_is_taken_by_the_simulation_places_the_destination_end_and_polarizes_the_pilot_so_the_second_try_is_refused()
    {
        await using var rig = await Rig.CreateAsync();
        await using var world = await JWorld.CreateAsync(rig, Type(1001, 1e9, 1e8));
        var (session, character, oid) = await PilotInJSpaceAsync(rig);
        var end = Assert.Single(await world.World.EndsInSystemAsync(JSystem));
        rig.Sim.JumpAnswer = new CommandReply(true, 0, null, null, KSystem, 0);
        var before = rig.Sim.Heard.Count;

        var answer = Assert.IsType<PyTuple>(await rig.Space.WormholeJumpAsync(session, end.BodyId, oid, CancellationToken.None));

        Assert.Equal(oid, ((PyStr)answer.Items[0]).Value);
        var jump = Assert.Single(rig.Sim.Heard.Skip(before).OfType<SimCommand>(), command => command.Code == SimCommands.Jump);
        Assert.Equal(new double[] { end.BodyId, 0 }, jump.Args);
        Assert.Contains(rig.Sim.Heard.Skip(before), message => message is SimPlaceWormhole);
        Assert.NotNull(rig.Space.PolarizationOf(end.BodyId, character.CharacterId));

        var refused = await Assert.ThrowsAsync<MachoWrappedException>(() => rig.Space.WormholeJumpAsync(session, end.BodyId, oid, CancellationToken.None));
        Assert.Equal("You are polarized to this wormhole: you cannot go into it from this side yet.", NotifyOf(refused));
    }

    [PgFact]
    public async Task An_emptied_connection_collapses_and_both_its_ends_are_taken_out_of_the_simulation()
    {
        await using var rig = await Rig.CreateAsync();
        // a connection of 10 tonnes: the first transit of the ship empties it
        await using var world = await JWorld.CreateAsync(rig, Type(1001, 1e4, 1e8));
        var (session, _, oid) = await PilotInJSpaceAsync(rig);
        var end = Assert.Single(await world.World.EndsInSystemAsync(JSystem));
        rig.Sim.JumpAnswer = new CommandReply(true, 0, null, null, KSystem, 0);
        var before = rig.Sim.Heard.Count;

        await rig.Space.WormholeJumpAsync(session, end.BodyId, oid, CancellationToken.None);

        Assert.Equal(2, rig.Sim.Heard.Skip(before).OfType<SimRemoveWormhole>().Count());
        Assert.DoesNotContain(await world.World.EndsInSystemAsync(JSystem), other => other.BodyId == end.BodyId);
    }
}

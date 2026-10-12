using System.Collections.Concurrent;
using System.Reflection;
using Eve.Data;
using Eve.Dogma;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Space;
using Eve.Space;
using Eve.Ships;
using Eve.Structures;
using Npgsql;
using Xunit;

namespace Eve.Host.Tests;

public sealed class StructureControlBackendTests
{
    private const long Structure = 1_040_000_000_001;
    private const int System = 30_000_142;

    public class ControlSim : DispatchProxy
    {
        public ConcurrentQueue<SimControlStructure> Heard { get; } = new();
        public Func<SimControlStructure, Task<string?>> Answer { get; set; } = _ => Task.FromResult<string?>(null);
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == "get_IsConnected") return true;
            if (method.Name == "ControlStructureAsync")
            {
                var request = (SimControlStructure)args![0]!;
                Heard.Enqueue(request);
                return Answer(request);
            }
            if (method.Name == "CommandAsync") return Task.FromResult(new CommandReply(true, 0, null, null, System) { Position = [0d, 0d, 0d] });
            if (method.ReturnType == typeof(Task)) return Task.CompletedTask;
            return null;
        }
    }

    private sealed class Capture(List<string> order, long sessionId) : IPacketSink
    {
        public void Send(PyValue packet) => order.Add($"session:{sessionId}");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required NpgsqlDataSource Source { get; init; }
        public required StructureStore Structures { get; init; }
        public required StructurePilotStore Pilots { get; init; }
        public required PgItemStore Items { get; init; }
        public required SpaceSessions Space { get; init; }
        public required StructureControlBackend Backend { get; set; }
        public required string StructureSchema { get; init; }
        public required string ItemSchema { get; init; }
        public required ControlSim Sim { get; init; }
        public ConnectedSessions Connected { get; } = new();
        public List<string> Order { get; } = [];
        public bool Allowed { get; set; } = true;

        public static async Task<Fixture> CreateAsync()
        {
            var suffix = Guid.NewGuid().ToString("N")[..12];
            var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
            var structureSchema = "control_s_" + suffix;
            var itemSchema = "control_i_" + suffix;
            await StructureStore.MigrateAsync(source, structureSchema);
            await PgItemStore.MigrateAsync(source, itemSchema);
            var structures = new StructureStore(source, structureSchema);
            var pilots = new StructurePilotStore(structures);
            var items = new PgItemStore(source, itemSchema);
            var statics = new DogmaStatic([], [], [
                new TypeDef(35832, 1657, 65, new Dictionary<int, double> { [162] = 45000, [1547] = 2 }, [], "Astrahus"),
                new TypeDef(587, 25, 6, new Dictionary<int, double> { [162] = 50 }, [], "Rifter"),
                new TypeDef(CharacterShips.CapsuleTypeId, 29, 6, new Dictionary<int, double> { [162] = 2 }, [], "Capsule"),
                new TypeDef(35841, 1404, 65, new Dictionary<int, double> { [162] = 1000 }, [], "FLEX fixture")],
                groupNames: new Dictionary<int, string> { [29] = "Capsule" },
                categoryNames: new Dictionary<int, string> { [65] = "Structure", [6] = "Ship" });
            var simulation = DispatchProxy.Create<ISimulation, ControlSim>();
            var sim = (ControlSim)(object)simulation;
            var space = new SpaceSessions(simulation, null!, statics, null!, null!, () => 0);
            var f = new Fixture { Source = source, Structures = structures, Pilots = pilots, Items = items,
                Space = space, Sim = sim, StructureSchema = structureSchema, ItemSchema = itemSchema, Backend = null! };
            f.Backend = new StructureControlBackend(source, structures, pilots, items, statics, space,
                (_, _, setting, _) => { Assert.Equal(17, setting); return ValueTask.FromResult(f.Allowed); }, f.Connected);
            sim.Answer = request => { f.Order.Add($"sim:{request.SessionId}:{request.Active}"); return Task.FromResult<string?>(null); };
            await using var connection = await source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await structures.CreateAsync(connection, transaction, new NewStructure(Structure, 35832, System, 0, 0, 0, 0, 98_000_001, 0, "Controller", ""));
            await structures.SaveAsync(connection, transaction, new StructureSnapshot { StructureId = Structure, State = StructureState.ShieldVulnerable });
            await transaction.CommitAsync();
            return f;
        }

        public async Task<ClientSession> SessionAsync(long id)
        {
            await using var connection = await Source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            var ship = await Items.CreateAsync(connection, transaction, new NewItem(587, id, Structure, ItemFlags.Hangar, 1, true));
            await transaction.CommitAsync();
            var session = new ClientSession(new Capture(Order, id)) { SessionId = id, ClientId = id, CharacterId = id, UserId = 1 };
            session.Set("corpid", new PyLong(98_000_001));
            session.Set("shipid", new PyLong(ship));
            session.Set("structureid", new PyLong(Structure));
            session.Set("locationid", new PyInt(System));
            session.Set("solarsystemid", new PyInt(System));
            session.Set("solarsystemid2", new PyInt(System));
            Connected.Add(session);
            return session;
        }

        public async Task<ClientSession> FlyingSessionAsync(long id, bool capsule = false)
        {
            await using var connection = await Source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            var typeId = capsule ? CharacterShips.CapsuleTypeId : 587;
            var ship = await Items.CreateAsync(connection, transaction, new NewItem(typeId, id, System, CharacterShips.InSpaceFlag, 1, true));
            if (await Structures.GetAsync(connection, transaction, Structure + 1, false) is null)
            {
                await Structures.CreateAsync(connection, transaction, new NewStructure(Structure + 1, 35841, System, 0, 0, 0, 0, 98_000_001, 0, "FLEX fixture", ""));
                await Structures.SaveAsync(connection, transaction, new StructureSnapshot { StructureId = Structure + 1, State = StructureState.ShieldVulnerable });
            }
            await transaction.CommitAsync();
            var session = new ClientSession(new Capture(Order, id)) { SessionId = id, ClientId = id, CharacterId = id, UserId = 1 };
            session.Set("corpid", new PyLong(98_000_001));
            session.Set("shipid", new PyLong(ship));
            session.Set("locationid", new PyInt(System));
            session.Set("solarsystemid", new PyInt(System));
            session.Set("solarsystemid2", new PyInt(System));
            // Seed only FakeSim's physical pilot binding; inventory and exclusive ownership remain real PostgreSQL records.
            var pilotType = typeof(SpaceSessions).GetNestedType("Pilot", BindingFlags.NonPublic)!;
            var pilot = Activator.CreateInstance(pilotType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, [session, id, ship, typeId, System, 0L, 0L, false], null)!;
            var pilots = typeof(SpaceSessions).GetField("_pilots", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Space)!;
            Assert.True((bool)pilots.GetType().GetMethod("TryAdd")!.Invoke(pilots, [(ulong)id, pilot])!);
            Connected.Add(session);
            return session;
        }

        public ServiceCall Call(ClientSession session) => new() { Session = session, Method = "TakeControl", Args = [] };
        public async Task<StructurePilotRecord?> OwnerAsync(long structureId = Structure)
        {
            await using var connection = await Source.OpenConnectionAsync();
            return await Pilots.GetByStructureAsync(connection, null, structureId);
        }
        public async ValueTask DisposeAsync()
        {
            await Space.QuiesceAsync();
            Space.Dispose();
            foreach (var schema in new[] { StructureSchema, ItemSchema })
            {
                await using var command = Source.CreateCommand($"DROP SCHEMA {schema} CASCADE");
                await command.ExecuteNonQueryAsync();
            }
            await Source.DisposeAsync();
        }
    }

    [PgFact]
    public async Task Takeover_detaches_the_old_observer_before_binding_and_notifying_the_new_owner()
    {
        await using var f = await Fixture.CreateAsync();
        var first = await f.SessionAsync(20); var second = await f.SessionAsync(21);
        var firstShip = first.GetInt("shipid"); var secondShip = second.GetInt("shipid");
        await f.Backend.TakeControlAsync(f.Call(first), Structure);
        f.Order.Clear();
        await f.Backend.TakeControlAsync(f.Call(second), Structure);
        Assert.Equal(["sim:20:False", "sim:21:True", "session:20", "session:20", "session:21", "session:21"], f.Order);
        var owner = (await f.OwnerAsync())!;
        Assert.Equal(21, owner.CharacterId);
        Assert.Equal(secondShip, owner.OriginalShipId);
        Assert.Equal(firstShip, first.GetInt("shipid"));
        Assert.Equal(Structure, second.GetInt("shipid"));
        Assert.Equal(Structure, first.GetInt("structureid"));
        Assert.Equal(Structure, (await f.Items.GetAsync(firstShip!.Value))!.LocationId);
        Assert.Equal(Structure, (await f.Items.GetAsync(secondShip!.Value))!.LocationId);
        Assert.Equal(21, await f.Backend.GetStructurePilotAsync(f.Call(first), Structure));
        var view = (await f.Backend.ReadStructureControlViewAsync(second, Structure))!;
        Assert.Equal((Structure, 35832, 98_000_001L, (long)System), (view.Item.Id, view.Item.TypeId, view.Item.OwnerId, view.Item.LocationId));
        Assert.Equal(45000, view.Type.Attributes[162]);
        Assert.Null(await f.Backend.ReadStructureControlViewAsync(first, Structure));
    }

    [PgFact]
    public async Task Simulation_refusal_restores_the_displaced_expected_owner_with_a_new_version()
    {
        await using var f = await Fixture.CreateAsync();
        var first = await f.SessionAsync(20); var second = await f.SessionAsync(21);
        await f.Backend.TakeControlAsync(f.Call(first), Structure);
        var before = (await f.OwnerAsync())!;
        var secondShip = second.GetInt("shipid");
        f.Sim.Answer = request => Task.FromResult<string?>(request.SessionId == 21 && request.Active ? "NO_STRUCTURE" : null);
        await Assert.ThrowsAsync<MachoWrappedException>(() => f.Backend.TakeControlAsync(f.Call(second), Structure).AsTask());
        var after = (await f.OwnerAsync())!;
        Assert.Equal(before.CharacterId, after.CharacterId);
        Assert.True(after.Version > before.Version);
        Assert.Equal(Structure, first.GetInt("shipid"));
        Assert.Equal(secondShip, second.GetInt("shipid"));
        Assert.True(f.Space.IsStructureObserver(first));
        Assert.False(f.Space.IsStructureObserver(second));
    }

    [PgFact]
    public async Task Sql_acquisition_failure_rolls_back_before_touching_either_observer()
    {
        await using var f = await Fixture.CreateAsync();
        var first = await f.SessionAsync(20); var second = await f.SessionAsync(21);
        await f.Backend.TakeControlAsync(f.Call(first), Structure);
        var before = await f.OwnerAsync(); var heard = f.Sim.Heard.Count;
        await using var constraint = f.Source.CreateCommand($"ALTER TABLE {f.StructureSchema}.structure_pilots ADD CONSTRAINT reject_second CHECK (character_id <> 21)");
        await constraint.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<PostgresException>(() => f.Backend.TakeControlAsync(f.Call(second), Structure).AsTask());
        Assert.Equal(before, await f.OwnerAsync());
        Assert.Equal(heard, f.Sim.Heard.Count);
        Assert.Equal(Structure, first.GetInt("shipid"));
        Assert.NotEqual(Structure, second.GetInt("shipid"));
    }

    [PgFact]
    public async Task Concurrent_takeover_waits_for_the_first_acknowledgement_then_leaves_one_owner()
    {
        await using var f = await Fixture.CreateAsync();
        var first = await f.SessionAsync(20); var second = await f.SessionAsync(21);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ack = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Sim.Answer = request => { if (request.SessionId == 20 && request.Active) { entered.TrySetResult(); return ack.Task; } return Task.FromResult<string?>(null); };
        var taking = f.Backend.TakeControlAsync(f.Call(first), Structure).AsTask();
        await entered.Task;
        var replacing = f.Backend.TakeControlAsync(f.Call(second), Structure).AsTask();
        Assert.False(replacing.IsCompleted);
        Assert.Single(f.Sim.Heard);
        Assert.NotEqual(Structure, first.GetInt("shipid"));
        ack.SetResult(null);
        await Task.WhenAll(taking, replacing);
        Assert.Equal(21, (await f.OwnerAsync())!.CharacterId);
        Assert.False(f.Space.IsStructureObserver(first));
        Assert.True(f.Space.IsStructureObserver(second));
    }

    [PgFact]
    public async Task Disconnect_and_rights_loss_clear_the_relation_and_restore_the_personal_ship()
    {
        await using var f = await Fixture.CreateAsync();
        var session = await f.SessionAsync(20); var ship = session.GetInt("shipid");
        await f.Backend.TakeControlAsync(f.Call(session), Structure);
        f.Allowed = false;
        await f.Backend.ReconcileAsync();
        Assert.Null(await f.OwnerAsync());
        Assert.Equal(ship, session.GetInt("shipid"));
        Assert.False(f.Space.IsStructureObserver(session));
        f.Allowed = true;
        await f.Backend.TakeControlAsync(f.Call(session), Structure);
        session.Close();
        await f.Space.QuiesceAsync();
        Assert.Null(await f.OwnerAsync());
        Assert.Equal(ship, session.GetInt("shipid"));
    }

    [PgFact]
    public async Task Restart_clear_and_structure_removal_do_not_leave_a_session_pointing_at_a_structure_ship()
    {
        await using var f = await Fixture.CreateAsync();
        var session = await f.SessionAsync(20); var ship = session.GetInt("shipid");
        await f.Backend.TakeControlAsync(f.Call(session), Structure);
        await using (var connection = await f.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        { await f.Pilots.ClearOnRestartAsync(connection, transaction); await transaction.CommitAsync(); }
        await f.Backend.ReconcileAsync();
        Assert.Equal(ship, session.GetInt("shipid"));
        Assert.False(f.Space.IsStructureObserver(session));
        await f.Backend.TakeControlAsync(f.Call(session), Structure);
        await using (var connection = await f.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        { await f.Structures.SaveAsync(connection, transaction, new StructureSnapshot { StructureId = Structure, DestroyedMs = 1 }); await transaction.CommitAsync(); }
        await f.Backend.ReconcileAsync();
        Assert.Null(await f.OwnerAsync());
        Assert.Equal(ship, session.GetInt("shipid"));
    }

    [PgFact]
    public async Task Profile_permission_and_docked_identity_are_checked_before_acquisition()
    {
        await using var f = await Fixture.CreateAsync();
        var session = await f.SessionAsync(20);
        f.Allowed = false;
        await Assert.ThrowsAsync<MachoWrappedException>(() => f.Backend.TakeControlAsync(f.Call(session), Structure).AsTask());
        f.Allowed = true;
        session.Set("structureid", new PyLong(Structure + 1));
        await Assert.ThrowsAsync<MachoWrappedException>(() => f.Backend.TakeControlAsync(f.Call(session), Structure).AsTask());
        Assert.Null(await f.OwnerAsync());
        Assert.Empty(f.Sim.Heard);
    }

    [PgFact]
    public async Task Flex_boarding_and_explicit_original_ship_return_use_the_preserved_physical_body()
    {
        await using var f = await Fixture.CreateAsync();
        var session = await f.FlyingSessionAsync(20);
        var ship = session.GetInt("shipid")!.Value;
        await f.Backend.BoardStructureAsync(f.Call(session), Structure + 1);
        Assert.Equal(ship, f.Space.ShipIdOf(session));
        Assert.Equal(Structure + 1, session.GetInt("shipid"));
        Assert.Equal(Structure + 1, session.GetInt("structureid"));
        Assert.Equal(System, session.GetInt("locationid"));
        var owner = (await f.OwnerAsync(Structure + 1))!;
        Assert.True(owner.FromSpace);
        Assert.Equal(ship, owner.OriginalShipId);
        var heard = f.Sim.Heard.Count;
        await Assert.ThrowsAsync<MachoWrappedException>(() => f.Backend.EjectFromStructureAsync(f.Call(session), Structure + 1).AsTask());
        Assert.Equal(heard, f.Sim.Heard.Count);
        Assert.Equal(owner, await f.OwnerAsync(Structure + 1));
        await f.Backend.EjectFromStructureAsync(f.Call(session), ship);
        Assert.Null(await f.OwnerAsync(Structure + 1));
        Assert.Null(session.GetInt("structureid"));
        Assert.Equal(ship, session.GetInt("shipid"));
        Assert.Equal(ship, f.Space.ShipIdOf(session));
        Assert.Equal(System, (await f.Items.GetAsync(ship))!.LocationId);
    }

    [PgFact]
    public async Task None_return_from_a_capsule_restores_that_real_capsule()
    {
        await using var f = await Fixture.CreateAsync();
        var session = await f.FlyingSessionAsync(20, true);
        var capsule = session.GetInt("shipid");
        await f.Backend.BoardStructureAsync(f.Call(session), Structure + 1);
        await f.Backend.EjectFromStructureAsync(f.Call(session), 0);
        Assert.Null(await f.OwnerAsync(Structure + 1));
        Assert.Equal(capsule, session.GetInt("shipid"));
        Assert.Equal(capsule, f.Space.ShipIdOf(session));
        Assert.Null(session.GetInt("structureid"));
    }

    [PgFact]
    public async Task Refused_release_preserves_the_existing_owner_and_control_session()
    {
        await using var f = await Fixture.CreateAsync();
        var session = await f.SessionAsync(20);
        await f.Backend.TakeControlAsync(f.Call(session), Structure);
        var before = await f.OwnerAsync();
        f.Sim.Answer = request => Task.FromResult<string?>(request.Active ? null : "DETACH_REFUSED");
        await Assert.ThrowsAsync<MachoWrappedException>(() => f.Backend.ReleaseControlAsync(f.Call(session)).AsTask());
        Assert.Equal(before, await f.OwnerAsync());
        Assert.Equal(Structure, session.GetInt("shipid"));
        Assert.True(f.Space.IsStructureObserver(session));
    }

    [PgFact]
    public async Task Disconnect_during_the_bind_ack_rolls_back_without_resurrecting_the_closed_session()
    {
        await using var f = await Fixture.CreateAsync();
        var session = await f.SessionAsync(20); var ship = session.GetInt("shipid");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ack = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Sim.Answer = request => { if (request.Active) { entered.TrySetResult(); return ack.Task; } return Task.FromResult<string?>(null); };
        var taking = f.Backend.TakeControlAsync(f.Call(session), Structure).AsTask();
        await entered.Task;
        session.Close();
        ack.SetResult(null);
        await Assert.ThrowsAsync<MachoWrappedException>(() => taking);
        await f.Space.QuiesceAsync();
        Assert.Null(await f.OwnerAsync());
        Assert.Equal(ship, session.GetInt("shipid"));
        Assert.False(f.Space.IsStructureObserver(session));
        Assert.True(f.Space.StructureControlSessionClosed(session));
    }
}

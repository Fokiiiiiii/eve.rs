using Eve.Data;
using Eve.Dogma;
using Eve.Macho;
using Npgsql;
using Eve.Marshal;
using Eve.Space;
using Eve.Universe;
using Eve.Wormholes;
using Eve.Services.Space;

namespace Eve.Services.Space.Tests;

/// <summary>
/// and the end the destination gets, the polarization of an end, and the collapse of an emptied connection taken out of the simulation.
/// </summary>
public sealed class WormholeJumpServiceTests
{
    private const int JSystem = 31_000_001, OtherJSystem = 31_000_002, KSystem = 30_000_001;

    private static WormholeType Type(int typeId, double maxStableMassKg, double maxJumpMassKg) => new(typeId, 7, 60, maxStableMassKg, 0, maxJumpMassKg, 302);

    private sealed class PacketSink : Eve.Services.IPacketSink
    {
        public List<PyValue> Packets { get; } = [];
        public void Send(PyValue packet) => Packets.Add(packet);
    }

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
        public string Schema => _schema;

        public WormholeWorld Reopen(Rig rig, WormholeType type, Eve.Scheduler.DurableScheduler? scheduler = null)
        {
            var classes = new Dictionary<int, int> { [JSystem] = 1, [OtherJSystem] = 2, [KSystem] = 7 };
            var systems = new[]
            {
                new JSpaceSystem(JSystem, "J1", 1, null, null, null, [new JSpaceStatic("AAA", type.TypeId)], "test"),
                new JSpaceSystem(OtherJSystem, "J2", 2, null, null, null, [], "test"),
            };
            return new WormholeWorld(rig.Source, scheduler ?? rig.Scheduler, _schema,
                new Dictionary<int, WormholeType> { [type.TypeId] = type }, classes, systems,
                new Dictionary<int, int> { [JSystem] = 111, [OtherJSystem] = 222, [KSystem] = 333 });
        }

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
            var world = new WormholeWorld(rig.Source, rig.Scheduler, schema, new Dictionary<int, WormholeType> { [type.TypeId] = type }, classes, systems,
                new Dictionary<int, int> { [JSystem] = 111, [OtherJSystem] = 222, [KSystem] = 333 });
            await world.EnsureWorldAsync(7, rig.Now);
            world.RegisterJobs();
            rig.Space.BindWormholes(world);
            return new JWorld(rig.Source, schema, world);
        }

        public async ValueTask DisposeAsync()
        {
            await using var connection = await _source.OpenConnectionAsync();
            await Sql.ExecuteAsync(connection, null, $"DROP SCHEMA IF EXISTS {_schema} CASCADE", CancellationToken.None);
        }
    }

    /// <summary>A pilot undocked in the J-system: its location is set before the undock, as the drone tests do.</summary>
    private static async Task<(ClientSession Session, Eve.Characters.Created Character, string Oid)> PilotInJSpaceAsync(Rig rig, long id = 1)
    {
        var place = rig.Systems.Get(JSystem) ?? throw new InvalidOperationException($"the static data has no solar system {JSystem}");
        var character = await rig.NewCharacterAsync($"Wormhole Pilot {id}");
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await rig.Characters.SetLocationAsync(connection, transaction, character.CharacterId, JSystem, place.ConstellationId, place.RegionId);
            await transaction.CommitAsync();
        }
        var session = await rig.SessionAsync(character, id);
        var oid = await rig.BindShipAsync(session);
        await rig.ShipCallAsync(session, "Undock", oid, new PyLong(session.GetInt("shipid")!.Value), PyBool.False);
        rig.Space.Transitions.EndSessionChange((ulong)session.SessionId);
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
        var placement = Assert.Single(rig.Sim.Heard.OfType<SimPlaceWormhole>(), message => message.WormholeId == end.BodyId);
        Assert.Equal(333, placement.NebulaTypeId);
        Assert.Equal(3, placement.WormholeAge);
        Assert.InRange(placement.WormholeSize, 0.5, 1.0);
        Assert.Equal(2, placement.MaxShipJumpMassCategory);
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            var stored = await world.World.Store.GetAsync(connection, transaction, end.BodyId / 2, forUpdate: true)
                ?? throw new InvalidOperationException("test wormhole is missing");
            await world.World.Store.UpdateAsync(connection, transaction, stored with { MassKg = stored.MaxStableMassKg * 0.5005 });
            await transaction.CommitAsync();
        }
        rig.Sim.JumpAnswer = new CommandReply(true, 0, null, null, KSystem, 0);
        var before = rig.Sim.Heard.Count;

        var answer = Assert.IsType<PyTuple>(await rig.Space.WormholeJumpAsync(session, end.BodyId, oid, CancellationToken.None));

        Assert.Equal(oid, ((PyStr)answer.Items[0]).Value);
        var jump = Assert.Single(rig.Sim.Heard.Skip(before).OfType<SimCommand>(), command => command.Code == SimCommands.Jump);
        Assert.Equal(new double[] { end.BodyId, 0 }, jump.Args);
        Assert.Contains(rig.Sim.Heard.Skip(before), message => message is SimPlaceWormhole);
        var sizeChanges = rig.Sim.Heard.Skip(before).OfType<SimSetSlimField>().Where(message => message.FieldName == "wormholeSize").ToArray();
        Assert.Equal(new[] { end.BodyId, end.DestinationEnd }, sizeChanges.Select(message => message.ItemId).Order());
        Assert.All(sizeChanges, message => Assert.Equal(new SimSlimReal(0.4), message.Value));
        Assert.NotNull(await rig.Space.PolarizationOfAsync(end.BodyId, character.CharacterId));
        var reloaded = world.Reopen(rig, Type(1001, 1e9, 1e8));
        Assert.Equal(rig.Now + (long)WormholeRules.Polarization.TotalMilliseconds,
            await reloaded.PolarizedUntilAsync(end.DestinationEnd, character.CharacterId, rig.Now));

        var refused = await Assert.ThrowsAsync<MachoWrappedException>(() => rig.Space.WormholeJumpAsync(session, end.BodyId, oid, CancellationToken.None));
        Assert.Equal("You are polarized to this wormhole: you cannot go into it from this side yet.", NotifyOf(refused));
    }

    [PgFact]
    public async Task A_durable_age_bucket_transition_updates_both_wormhole_ends_in_the_simulation()
    {
        await using var rig = await Rig.CreateAsync();
        await using var world = await JWorld.CreateAsync(rig, new WormholeType(1001, 7, 25 * 60, 1e9, 0, 1e8, 302));
        var end = Assert.Single(await world.World.EndsInSystemAsync(JSystem));
        Assert.Equal(0, end.AgeState);
        rig.Sim.Heard.Clear();

        rig.Now += (long)TimeSpan.FromHours(1).TotalMilliseconds;
        await rig.Scheduler.RunDueAsync();

        var changes = rig.Sim.Heard.OfType<SimSetSlimField>().Where(message => message.FieldName == "wormholeAge").ToArray();
        Assert.Equal(new[] { end.BodyId, end.DestinationEnd }, changes.Select(message => message.ItemId).Order());
        Assert.All(changes, message => Assert.Equal(new SimSlimInt(1), message.Value));
    }

    [PgFact]
    public async Task A_failed_age_bucket_slim_update_retries_after_a_scheduler_restart()
    {
        await using var rig = await Rig.CreateAsync();
        const int maxStableMinutes = 25 * 60;
        var type = new WormholeType(1001, 7, maxStableMinutes, 1e9, 0, 1e8, 302);
        await using var world = await JWorld.CreateAsync(rig, type);
        var end = Assert.Single(await world.World.EndsInSystemAsync(JSystem));
        var jobKey = $"{end.BodyId / 2}:1";
        rig.Sim.Heard.Clear();
        rig.Sim.FailNextSlimField = true;

        rig.Now += (long)TimeSpan.FromHours(1).TotalMilliseconds;
        Assert.Equal(1, await rig.Scheduler.RunDueAsync());

        Assert.Empty(rig.Sim.Heard.OfType<SimSetSlimField>());
        var pending = await rig.Scheduler.FindAsync(WormholeJobs.SlimStateKind, jobKey);
        Assert.NotNull(pending);
        Assert.Equal(("pending", 1), (pending.State, pending.Attempts));

        await using var restartedScheduler = new Eve.Scheduler.DurableScheduler(rig.Source, () => rig.Now, null, rig.SchedulerSchema);
        var restartedWorld = world.Reopen(rig, type, restartedScheduler);
        rig.Space.BindWormholes(restartedWorld);
        restartedWorld.RegisterJobs();

        rig.Now += 5_000;
        Assert.Equal(1, await restartedScheduler.RunDueAsync());

        Assert.Null(await restartedScheduler.FindAsync(WormholeJobs.SlimStateKind, jobKey));
        var expectedEnds = await restartedWorld.EndsForConnectionAsync(end.BodyId / 2);
        var updates = rig.Sim.Heard.OfType<SimSetSlimField>().ToArray();
        var expectedUpdates = expectedEnds.SelectMany(wormholeEnd => new[]
        {
            new SimSetSlimField(wormholeEnd.BodyId, "wormholeAge", new SimSlimInt(wormholeEnd.AgeState)),
            new SimSetSlimField(wormholeEnd.BodyId, "wormholeSize", new SimSlimReal(wormholeEnd.Size)),
        });
        Assert.Equal(expectedUpdates, updates);
    }

    [PgFact]
    public async Task A_pilot_is_polarized_at_the_arrival_end_and_cannot_immediately_jump_back()
    {
        await using var rig = await Rig.CreateAsync();
        await using var world = await JWorld.CreateAsync(rig, Type(1001, 1e9, 1e8));
        var (session, character, oid) = await PilotInJSpaceAsync(rig);
        var source = Assert.Single(await world.World.EndsInSystemAsync(JSystem));
        rig.Sim.JumpAnswer = new CommandReply(true, 0, null, null, KSystem, 0);

        await rig.Space.WormholeJumpAsync(session, source.BodyId, oid, CancellationToken.None);
        rig.Sim.Say(new SimJumped((ulong)session.SessionId, JSystem, source.BodyId, KSystem, source.DestinationEnd));
        await Rig.UntilAsync(() => session.GetInt("solarsystemid") == KSystem);
        var arrival = Assert.Single(await world.World.EndsInSystemAsync(KSystem));
        var jumpsBeforeReturn = rig.Sim.Heard.OfType<SimCommand>().Count(command => command.Code == SimCommands.Jump);

        Assert.NotNull(await rig.Space.PolarizationOfAsync(arrival.BodyId, character.CharacterId));
        var refused = await Assert.ThrowsAsync<MachoWrappedException>(
            () => rig.Space.WormholeJumpAsync(session, arrival.BodyId, oid, CancellationToken.None));

        Assert.Equal("You are polarized to this wormhole: you cannot go into it from this side yet.", NotifyOf(refused));
        Assert.Equal(jumpsBeforeReturn, rig.Sim.Heard.OfType<SimCommand>().Count(command => command.Code == SimCommands.Jump));
    }

    [PgFact]
    public async Task An_expired_connection_is_collapsed_and_the_registered_rpc_sends_the_client_cancel_notification()
    {
        await using var rig = await Rig.CreateAsync();
        await using var world = await JWorld.CreateAsync(rig, Type(1001, 1e9, 1e8));
        var (session, _, oid) = await PilotInJSpaceAsync(rig);
        var end = Assert.Single(await world.World.EndsInSystemAsync(JSystem));
        rig.Now += (long)TimeSpan.FromMinutes(60).TotalMilliseconds + 1;
        rig.Sim.JumpAnswer = new CommandReply(true, 0, null, null, KSystem, 0);
        var before = rig.Sim.Heard.Count;
        var packets = new PacketSink();
        session.Sink = packets;
        var service = new WormholeMgrService(rig.Space,
            jump: async (call, endId, cancellationToken) =>
                await rig.Space.WormholeJumpAsync(call.Session, endId, call.BoundObjectId, cancellationToken));

        var result = await service.CallAsync(new ServiceCall
        {
            Method = "WormholeJump",
            Args = [new PyLong(end.BodyId)],
            Session = session,
            BoundObjectId = oid,
            Cancellation = CancellationToken.None,
        });

        Assert.IsType<PyNone>(result);
        Assert.DoesNotContain(rig.Sim.Heard.Skip(before).OfType<SimCommand>(), command => command.Code == SimCommands.Jump);
        Assert.Empty(await world.World.EndsInSystemAsync(JSystem));
        Assert.Equal(2, rig.Sim.Heard.Skip(before).OfType<SimRemoveWormhole>().Count());
        var notification = Assert.IsType<PyObject>(Assert.Single(packets.Packets));
        var notificationPacket = Assert.IsType<PyTuple>(notification.Args);
        Assert.Equal("OnWormholeJumpCancel", MachoAddress.Decode(notificationPacket.Items[2]).BroadcastId);
    }

    [PgFact]
    public async Task Concurrent_jumps_cannot_both_enter_a_connection_that_only_has_mass_for_one_ship()
    {
        await using var rig = await Rig.CreateAsync();
        await using var world = await JWorld.CreateAsync(rig, Type(1001, 1e4, 1e8));
        var (firstSession, _, firstOid) = await PilotInJSpaceAsync(rig, id: 1);
        var (secondSession, _, secondOid) = await PilotInJSpaceAsync(rig, id: 2);
        var secondPackets = new PacketSink();
        secondSession.Sink = secondPackets;
        var end = Assert.Single(await world.World.EndsInSystemAsync(JSystem));
        var firstAtSimulation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSimulation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Sim.JumpHandler = async (_, cancellationToken) =>
        {
            firstAtSimulation.TrySetResult();
            await releaseSimulation.Task.WaitAsync(cancellationToken);
            return new CommandReply(true, 0, null, null, KSystem, 0);
        };

        var firstJump = rig.Space.WormholeJumpAsync(firstSession, end.BodyId, firstOid, CancellationToken.None);
        await firstAtSimulation.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var secondJump = rig.Space.WormholeJumpAsync(secondSession, end.BodyId, secondOid, CancellationToken.None);
        await Task.Delay(50);
        Assert.Single(rig.Sim.Heard.OfType<SimCommand>(), command => command.Code == SimCommands.Jump);

        releaseSimulation.TrySetResult();
        await firstJump;
        Assert.IsType<PyNone>(await secondJump);
        Assert.Single(rig.Sim.Heard.OfType<SimCommand>(), command => command.Code == SimCommands.Jump);
        Assert.Empty(await world.World.EndsInSystemAsync(JSystem));
        var notification = Assert.IsType<PyObject>(Assert.Single(secondPackets.Packets));
        Assert.Equal("OnWormholeJumpCancel", MachoAddress.Decode(Assert.IsType<PyTuple>(notification.Args).Items[2]).BroadcastId);
    }

    [PgFact]
    public async Task Concurrent_jumps_from_separate_host_instances_are_serialized_before_simulation_acceptance()
    {
        await using var rig = await Rig.CreateAsync();
        await using var world = await JWorld.CreateAsync(rig, Type(1001, 10_000, 1e8));
        var (firstSession, _, firstOid) = await PilotInJSpaceAsync(rig, id: 1);
        var (secondSession, secondCharacter, secondOid) = await PilotInJSpaceAsync(rig, id: 2);
        var end = Assert.Single(await world.World.EndsInSystemAsync(JSystem));
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            var stored = await world.World.Store.GetAsync(connection, transaction, end.BodyId / 2, forUpdate: true)
                ?? throw new InvalidOperationException("test wormhole is missing");
            await world.World.Store.UpdateAsync(connection, transaction, stored with { MassKg = 10_000 });
            await transaction.CommitAsync();
        }

        using var secondHostSpace = new SpaceSessions(rig.Sim, rig.Ships, rig.Statics, rig.Stations, rig.Systems, () => rig.Now, rig.Log.Add);
        secondHostSpace.BindWormholes(world.World);
        secondHostSpace.EnterPilotForTest(secondSession, secondCharacter.CharacterId, secondSession.GetInt("shipid")!.Value, 601, JSystem);

        var firstInSimulation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondInSimulation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var simJumpCount = 0;
        rig.Sim.JumpHandler = async (_, cancellationToken) =>
        {
            if (Interlocked.Increment(ref simJumpCount) == 1)
            {
                firstInSimulation.TrySetResult();
                await releaseFirst.Task.WaitAsync(cancellationToken);
            }
            else
            {
                secondInSimulation.TrySetResult();
            }
            return new CommandReply(true, 0, null, null, KSystem, 0);
        };

        var firstJump = rig.Space.WormholeJumpAsync(firstSession, end.BodyId, firstOid, CancellationToken.None);
        await firstInSimulation.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var secondJump = secondHostSpace.WormholeJumpAsync(secondSession, end.BodyId, secondOid, CancellationToken.None);
        var racedIntoSimulation = await Task.WhenAny(secondInSimulation.Task, Task.Delay(TimeSpan.FromMilliseconds(100)));
        var secondWasAcceptedBySimulationBeforeRelease = ReferenceEquals(secondInSimulation.Task, racedIntoSimulation);

        releaseFirst.TrySetResult();
        var firstResult = await firstJump;
        var secondResult = await secondJump;
        Assert.False(secondWasAcceptedBySimulationBeforeRelease);
        Assert.IsType<PyTuple>(firstResult);
        Assert.IsType<PyNone>(secondResult);
        Assert.Equal(1, rig.Sim.Heard.OfType<SimCommand>().Count(command => command.Code == SimCommands.Jump));
    }

    [PgFact]
    public async Task A_jump_into_a_system_with_an_environment_gives_the_simulation_the_ship_again_with_that_environment()
    {
        await using var rig = await Rig.CreateAsync();
        await using var world = await JWorld.CreateAsync(rig, Type(1001, 1e9, 1e8));
        var (session, _, oid) = await PilotInJSpaceAsync(rig);
        var end = Assert.Single(await world.World.EndsInSystemAsync(JSystem));
        // the destination's beacon raises the ship's maximum velocity by 30 percent (the attribute of the dogma layer)
        var environment = SystemEnvironment.EffectOf(1, "Class 7 Test Effects", new Dictionary<string, double> { ["maxVelocityMultiplier"] = 1.3 }, name => name == "maxVelocity" ? 37 : null).Effect!;
        rig.Space.SystemEnvironment = id => id == KSystem ? [environment] : [];
        var inspections = new List<int>();
        rig.Space.ApplyContrabandPenaltyAsync = (_, _, systemId, _) =>
        {
            inspections.Add(systemId);
            return Task.FromResult<Eve.Services.Space.ContrabandPenalty?>(null);
        };
        rig.Sim.JumpAnswer = new CommandReply(true, 0, null, null, KSystem, 0);
        await rig.Space.WormholeJumpAsync(session, end.BodyId, oid, CancellationToken.None);

        rig.Sim.Say(new SimJumped((ulong)session.SessionId, JSystem, end.BodyId, KSystem, end.BodyId + 1));
        await Rig.UntilAsync(() => session.GetInt("solarsystemid") == KSystem);
        await Rig.UntilAsync(() => rig.Sim.Heard.Any(message => message is SimShipChanged));
        Assert.Empty(inspections);

        // the ship's maximum velocity as the simulation was given it on entering the J-system, and as it is given now
        var entered = rig.Sim.Heard.OfType<SimEnter>().Single().Ship.MaxVelocity;
        var changed = rig.Sim.Heard.OfType<SimShipChanged>().Last();
        Assert.Equal((ulong)session.SessionId, changed.SessionId);
        Assert.Equal(entered * 1.3, changed.Ship.MaxVelocity, 6);
    }

    [PgFact]
    public async Task The_polarization_lapses_five_minutes_after_the_jump_and_the_service_reports_none_then()
    {
        await using var rig = await Rig.CreateAsync();
        await using var world = await JWorld.CreateAsync(rig, Type(1001, 1e9, 1e8));
        var (session, character, oid) = await PilotInJSpaceAsync(rig);
        var end = Assert.Single(await world.World.EndsInSystemAsync(JSystem));
        rig.Sim.JumpAnswer = new CommandReply(true, 0, null, null, KSystem, 0);
        await rig.Space.WormholeJumpAsync(session, end.BodyId, oid, CancellationToken.None);
        Assert.NotNull(await rig.Space.PolarizationOfAsync(end.BodyId, character.CharacterId));

        rig.Now += (long)WormholeRules.Polarization.TotalMilliseconds + 1_000;

        Assert.Null(await rig.Space.PolarizationOfAsync(end.BodyId, character.CharacterId));
    }

    [PgFact]
    public async Task The_wormhole_jump_through_the_rpc_service_is_the_space_jump_and_its_answer_is_the_list_the_client_reads()
    {
        await using var rig = await Rig.CreateAsync();
        await using var world = await JWorld.CreateAsync(rig, Type(1001, 1e9, 1e8));
        var (session, _, oid) = await PilotInJSpaceAsync(rig);
        var end = Assert.Single(await world.World.EndsInSystemAsync(JSystem));
        rig.Sim.JumpAnswer = new CommandReply(true, 0, null, null, KSystem, 0);
        // the service as the composition builds it: the jump and the polarization are the space sessions'
        var service = new WormholeMgrService(
            rig.Space,
            readPolarization: async (endId, characterId, cancellationToken) => await rig.Space.PolarizationOfAsync(endId, characterId, cancellationToken),
            jump: async (call, endId, cancellation) => await rig.Space.WormholeJumpAsync(call.Session, endId, call.BoundObjectId, cancellation));
        var before = rig.Sim.Heard.Count;

        var answer = Assert.IsType<PyList>(await service.CallAsync(new ServiceCall
        {
            Method = "WormholeJump",
            Args = [new PyInt((int)end.BodyId)],
            Session = session,
            BoundObjectId = oid,
            Cancellation = CancellationToken.None,
        }));

        Assert.Equal(oid, ((PyStr)answer.Items[0]).Value);
        Assert.Single(rig.Sim.Heard.Skip(before).OfType<SimCommand>(), command => command.Code == SimCommands.Jump);
        var polarization = Assert.IsType<PyList>(await service.CallAsync(new ServiceCall
        {
            Method = "GetWormholePolarization",
            Args = [new PyInt((int)end.BodyId)],
            Session = session,
            Cancellation = CancellationToken.None,
        }));
        Assert.Equal(300, ((PyInt)polarization.Items[1]).Value);
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

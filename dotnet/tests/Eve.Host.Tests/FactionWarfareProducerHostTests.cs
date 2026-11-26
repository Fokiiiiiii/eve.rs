using System.Reflection;
using Eve.Characters;
using Eve.Data;
using Eve.Dogma;
using Eve.Events;
using Eve.FactionWarfare;
using Eve.Host.Persistence.FactionWarfare;
using Eve.Scheduler;
using Eve.Sde;
using Eve.Services.Space;
using Eve.Space;
using Eve.Structures;
using Npgsql;

namespace Eve.Host.Tests;

public sealed class FactionWarfareProducerPgFactAttribute : FactAttribute
{
    public FactionWarfareProducerPgFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("EVE_PG_URL") is null)
            Skip = "EVE_PG_URL is not set";
    }
}

public sealed class FactionWarfareProducerHostTests
{
    private const long Now = 1_699_956_000_000;
    private const long AttackerCorporation = 98_200_001;
    private const long AttackerFaction = FwFactions.MinmatarRepublic;
    private const long VictimFaction = FwFactions.AmarrEmpire;
    private const int FactionDogmaAttribute = 1341;

    [FactionWarfareProducerPgFact]
    public async Task Player_kill_producer_uses_final_blow_hostility_and_committed_outbox_cursor()
    {
        await using var rig = await ProducerRig.CreateAsync();
        var attacker = await rig.AddCharacterAsync("FW kill producer attacker", AttackerCorporation);
        var validVictim = await rig.AddCharacterAsync("FW kill producer victim", 98_200_002, VictimFaction);
        var friendlyVictim = await rig.AddCharacterAsync("FW friendly kill victim", 98_200_003, AttackerFaction);
        var outsideVictim = await rig.AddCharacterAsync("FW outside-zone victim", 98_200_004, VictimFaction);
        var noFinalBlowVictim = await rig.AddCharacterAsync("FW missing final blow victim", 98_200_005, VictimFaction);
        var warzone = checked((int)FwWarzones.SystemsOf(FwWarzones.AmarrMinmatar)[0]);
        var otherShip = 7_100_002L;

        var valid = Kill(validVictim, warzone, attacker, 7_100_001, finalBlowLastOrder: 2, otherShip: otherShip, assistantLastOrder: 100);
        await rig.PublishAndPumpAsync(valid);
        await rig.PublishAndPumpAsync(Kill(friendlyVictim, warzone, attacker, 7_100_003, 3));
        await rig.PublishAndPumpAsync(Kill(outsideVictim, 30_000_142, attacker, 7_100_004, 4));
        await rig.PublishAndPumpAsync(Kill(null, warzone, attacker, 7_100_005, 5));
        await rig.PublishAndPumpAsync(Kill(noFinalBlowVictim, warzone, attacker, 7_100_006, 6, otherShip: 7_100_007, includeFinalBlow: false));

        var day = FwRules.DayOf(Now);
        Assert.Equal(1, (await rig.PilotStatsAsync(attacker, day)).KillsTotal);
        Assert.Equal(1, (await rig.CorporationStatsAsync(AttackerCorporation, day)).KillsTotal);

        await rig.ReopenAndPumpAsync();
        Assert.Equal(1, (await rig.PilotStatsAsync(attacker, day)).KillsTotal);
        Assert.Equal(1, (await rig.CorporationStatsAsync(AttackerCorporation, day)).KillsTotal);
    }

    [FactionWarfareProducerPgFact]
    public async Task Control_bunker_lifecycle_uses_sde_sun_geometry_and_captures_at_downtime()
    {
        var connectionString = PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!);
        await using var source = NpgsqlDataSource.Create(connectionString);
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var fwSchema = "fw_bunker_" + suffix;
        var schedulerSchema = "sch_bunker_" + suffix;
        var eventSchema = "evt_bunker_" + suffix;
        var now = Now;
        try
        {
            await FwStore.MigrateAsync(source, fwSchema);
            await DurableScheduler.MigrateAsync(source, schedulerSchema);
            await EventBus.MigrateAsync(source, eventSchema);
            var store = new FwStore(source, fwSchema);
            var systemId = checked((int)FwWarzones.SystemsOf(FwWarzones.AmarrMinmatar)[0]);
            var oldOccupier = FwFactions.AmarrEmpire;
            var newOccupier = FwFactions.MinmatarRepublic;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await store.SeedSystemsAsync(connection, transaction,
                    [new FwSystem(systemId, FwWarzones.AmarrMinmatar, oldOccupier, 6, FwRules.VulnerabilityPoints, Now)]);
                await transaction.CommitAsync();
            }

            await using var scheduler = new DurableScheduler(source, () => now, new SchedulerOptions { Concurrency = 1 }, schedulerSchema);
            var events = new EventBus(source, scheduler, schema: eventSchema);
            var sde = new SdeReader(source);
            Assert.Equal(3_396_210L, await sde.BuildAsync());
            var statics = await DogmaStatic.LoadAsync(sde);

            await events.PumpAsync(); // establish the durable capture subscriber before the damage event
            var simContract = DispatchProxy.Create<ISimulation, RecordingSimulation>();
            var sim = (RecordingSimulation)(object)simContract;
            using var space = new SpaceSessions(simContract, null!, statics, null!, null!, () => now);
            var log = new List<string>();
            var bunkers = new FactionWarfareControlBunkers(source, store, scheduler, events, simContract, space, statics, sde, log.Add);

            var star = Assert.Single(await sde.ReadByFieldAsync("mapStars", "solarSystemID", [systemId]));
            var starRadius = star.Doc.GetProperty("radius").GetDouble();
            var sunWarpDistance = starRadius + 100_000d;
            var expectedAnchor = new[]
            {
                sunWarpDistance * Math.Cos(starRadius),
                starRadius / 5d + 100_000d,
                -sunWarpDistance * Math.Sin(starRadius),
            };

            await bunkers.RestoreAsync(systemId);
            var first = Assert.Single(sim.Placed);
            Assert.Equal(80_000_000, first.StructureId);
            Assert.Equal(925, first.GroupId);
            Assert.Equal(oldOccupier, (long)statics.Type(first.TypeId)!.Attributes[FactionDogmaAttribute]);
            Assert.Equal(StructureState.ShieldVulnerable, first.Status.State);
            Assert.Equal(expectedAnchor, first.Position);
            Assert.DoesNotContain(log, message => message.Contains("placement was skipped", StringComparison.Ordinal));
            Assert.Equal(first.StructureId, await bunkers.ReadLiveControlBunkerIdAsync(systemId));

            await bunkers.RestoreAsync(systemId);
            Assert.Single(sim.Placed); // an existing ball is updated rather than placed twice
            Assert.Single(sim.States);
            sim.Deliver(new SimStructureDamaged(first.StructureId, 2, true));
            await Task.Delay(30);
            var beforeHull = await ReadSystemAsync(source, store, systemId);
            Assert.Equal(0, beforeHull.PendingCaptureMs);

            sim.Deliver(new SimStructureDamaged(first.StructureId, 3, true));
            FwSystem? pending = null;
            for (var attempt = 0; attempt < 200; attempt++)
            {
                pending = await ReadSystemAsync(source, store, systemId);
                if (pending.PendingCaptureMs > 0)
                    break;
                await Task.Delay(10);
            }
            Assert.NotNull(pending);
            var due = FwRules.NextDowntimeMs(Now);
            Assert.Equal(due, pending.PendingCaptureMs);
            Assert.Equal(newOccupier, pending.PendingOccupierId);
            Assert.Equal(due, (await scheduler.FindAsync(FactionWarfareControlBunkers.CaptureJobKind, $"system:{systemId}"))!.RunAtMs);
            Assert.Contains(first.StructureId, sim.Removed);

            now = due;
            Assert.True(await scheduler.RunNowAsync(FactionWarfareControlBunkers.CaptureJobKind, $"system:{systemId}"));
            await events.PumpAsync();
            var captured = await ReadSystemAsync(source, store, systemId);
            Assert.Equal(newOccupier, captured.OccupierId);
            Assert.Equal(0, captured.AttackerPoints);
            Assert.Equal(0, captured.LpPool);
            Assert.Equal(0, captured.PendingCaptureMs);

            await bunkers.RestoreAsync(systemId); // the next pilot scene-entry publishes the captured occupier's bunker
            Assert.Equal(2, sim.Placed.Count);
            var replacement = sim.Placed[^1];
            Assert.Equal(newOccupier, (long)statics.Type(replacement.TypeId)!.Attributes[FactionDogmaAttribute]);
            Assert.Equal(StructureState.FobInvulnerable, replacement.Status.State);
            Assert.Equal(expectedAnchor, replacement.Position);
            Assert.Equal(replacement.StructureId, await bunkers.ReadLiveControlBunkerIdAsync(systemId));
        }
        finally
        {
            await using var cleanup = await source.OpenConnectionAsync();
            foreach (var schema in new[] { fwSchema, schedulerSchema, eventSchema })
                await Sql.ExecuteAsync(cleanup, null, $"DROP SCHEMA IF EXISTS {schema} CASCADE", CancellationToken.None);
        }
    }

    private static ShipKillmailEvent Kill(long? victim, int system, long attacker, long killerShip, long finalBlowLastOrder,
        long otherShip = 0, long assistantLastOrder = 0, bool includeFinalBlow = true)
    {
        var attackers = new List<ShipKillmailAttacker>();
        if (otherShip > 0)
            attackers.Add(new ShipKillmailAttacker(attacker + 1, AttackerCorporation, 0, 0, otherShip, 587, 0, 200, 0, assistantLastOrder));
        if (includeFinalBlow)
            attackers.Add(new ShipKillmailAttacker(attacker, AttackerCorporation, 0, 0, killerShip, 587, 0, 10, 0, finalBlowLastOrder));
        return new ShipKillmailEvent(victim, 98_200_010, 0, 0, 7_110_001, 587, system, Now, 100, killerShip, attackers, []);
    }

    private static async Task<FwSystem> ReadSystemAsync(NpgsqlDataSource source, FwStore store, long systemId)
    {
        await using var connection = await source.OpenConnectionAsync();
        return (await store.SystemAsync(connection, null, systemId, forUpdate: false))!;
    }

    private sealed class ProducerRig(NpgsqlDataSource source, string fwSchema, string characterSchema, string schedulerSchema, string eventSchema,
        DurableScheduler scheduler, EventBus events, FwStore store, PgCharacterStore characters) : IAsyncDisposable
    {
        public NpgsqlDataSource Source { get; } = source;
        public FwStore Store { get; } = store;
        public PgCharacterStore Characters { get; } = characters;
        public DurableScheduler Scheduler { get; } = scheduler;
        public EventBus Events { get; } = events;
        private readonly string _fwSchema = fwSchema;
        private readonly string _characterSchema = characterSchema;
        private readonly string _schedulerSchema = schedulerSchema;
        private readonly string _eventSchema = eventSchema;

        public static async Task<ProducerRig> CreateAsync()
        {
            var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
            var suffix = Guid.NewGuid().ToString("N")[..12];
            var fw = "fw_kill_" + suffix;
            var charactersSchema = "char_kill_" + suffix;
            var schedulerSchema = "sch_kill_" + suffix;
            var eventsSchema = "evt_kill_" + suffix;
            await FwStore.MigrateAsync(source, fw);
            await PgCharacterStore.MigrateAsync(source, charactersSchema);
            await DurableScheduler.MigrateAsync(source, schedulerSchema);
            await EventBus.MigrateAsync(source, eventsSchema);
            var scheduler = new DurableScheduler(source, () => Now, schema: schedulerSchema);
            var events = new EventBus(source, scheduler, schema: eventsSchema);
            var store = new FwStore(source, fw);
            var characters = new PgCharacterStore(source, charactersSchema);
            new FactionWarfareKillProducer(store, characters).Subscribe(events);
            var rig = new ProducerRig(source, fw, charactersSchema, schedulerSchema, eventsSchema, scheduler, events, store, characters);
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await store.SaveEnlistmentAsync(connection, transaction,
                    new FwEnlistment(FwStore.CorporationKind, AttackerCorporation, AttackerFaction, 0, 0, Now));
                await transaction.CommitAsync();
            }
            await events.PumpAsync();
            return rig;
        }

        public async Task<long> AddCharacterAsync(string name, long corporation, long? directFaction = null)
        {
            await using var connection = await Source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            var id = await Characters.InsertAsync(connection, transaction,
                new NewCharacter(1, $"{name} {Guid.NewGuid():N}", 0, 1, 1, 1, 11, 1376, corporation, null, 60_000_610, 30_000_142, 20_000_001, 10_000_001, null, null));
            if (directFaction is > 0)
                await Characters.SetFactionWarfareStateAsync(connection, transaction, id, directFaction, 0);
            await transaction.CommitAsync();
            return id;
        }

        public async Task<FwStatsWindow> PilotStatsAsync(long characterId, int day)
        {
            await using var connection = await Source.OpenConnectionAsync();
            return await Store.PilotStatsAsync(connection, null, characterId, day);
        }

        public async Task<FwStatsWindow> CorporationStatsAsync(long corporationId, int day)
        {
            await using var connection = await Source.OpenConnectionAsync();
            return await Store.CorporationStatsAsync(connection, null, corporationId, day);
        }

        public async Task PublishAndPumpAsync(ShipKillmailEvent kill)
        {
            await Events.PublishAsync(ShipKillmailEvent.Topic, kill);
            var result = await Events.PumpAsync();
            Assert.Equal(1, result.Delivered);
        }

        public async Task ReopenAndPumpAsync()
        {
            await using var restartedScheduler = new DurableScheduler(Source, () => Now, schema: _schedulerSchema);
            var restartedEvents = new EventBus(Source, restartedScheduler, schema: _eventSchema);
            new FactionWarfareKillProducer(Store, Characters).Subscribe(restartedEvents);
            Assert.Equal(0, (await restartedEvents.PumpAsync()).Delivered);
        }

        public async ValueTask DisposeAsync()
        {
            await Scheduler.DisposeAsync();
            await using var connection = await Source.OpenConnectionAsync();
            foreach (var schema in new[] { _fwSchema, _characterSchema, _schedulerSchema, _eventSchema })
                await Sql.ExecuteAsync(connection, null, $"DROP SCHEMA {schema} CASCADE", CancellationToken.None);
            await Source.DisposeAsync();
        }
    }

    public class RecordingSimulation : DispatchProxy
    {
        private Action<SimMessage>? _received;
        public List<SimPlaceStructure> Placed { get; } = [];
        public List<SimGetSceneBody> SceneBodyRequests { get; } = [];
        public List<long> Removed { get; } = [];
        public List<SimSetStructureState> States { get; } = [];
        private readonly Dictionary<long, SimPlaceStructure> _liveStructures = [];
        public void Deliver(SimMessage message) => _received?.Invoke(message);

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(method);
            if (method.Name == "get_IsConnected") return true;
            if (method.Name == "add_Received") { _received += (Action<SimMessage>)args![0]!; return null; }
            if (method.Name == "remove_Received") { _received -= (Action<SimMessage>)args![0]!; return null; }
            if (method.Name == nameof(ISimulation.PlaceStructure))
            {
                var structure = (SimPlaceStructure)args![0]!;
                Placed.Add(structure);
                _liveStructures[structure.StructureId] = structure;
                return null;
            }
            if (method.Name == nameof(ISimulation.GetSceneBodyAsync))
            {
                var request = (SimGetSceneBody)args![0]!;
                SceneBodyRequests.Add(request);
                var body = _liveStructures.GetValueOrDefault(request.ItemId);
                return Task.FromResult(body is null
                    ? new SimGetSceneBodyReply(request.RequestId, request.Scene, request.ItemId, 0, 0, 0, [], "item is not in scene")
                    : new SimGetSceneBodyReply(request.RequestId, request.Scene, request.ItemId,
                        body.TypeId, body.GroupId, 65, body.Position, null));
            }
            if (method.Name == nameof(ISimulation.RemoveStructure))
            {
                var structureId = (long)args![0]!;
                Removed.Add(structureId);
                _liveStructures.Remove(structureId);
                return null;
            }
            if (method.Name == nameof(ISimulation.SetStructureState)) { States.Add((SimSetStructureState)args![0]!); return null; }
            if (method.Name.StartsWith("add_") || method.Name.StartsWith("remove_")) return null;
            if (method.ReturnType == typeof(Task)) return Task.CompletedTask;
            if (method.ReturnType == typeof(void)) return null;
            return method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null;
        }
    }
}

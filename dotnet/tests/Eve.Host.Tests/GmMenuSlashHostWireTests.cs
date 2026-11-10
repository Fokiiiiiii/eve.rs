using System.Text.Json.Nodes;
using Eve.Data;
using Eve.Events;
using Eve.Characters;
using Eve.Dogma;
using Eve.Fitting;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Sde;
using Eve.Scheduler;
using Eve.Services;
using Eve.Services.Core;
using Eve.Services.Slash;
using Eve.Skills;
using Eve.Ships;
using Eve.Structures;
using Npgsql;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    private const long GmStructureId = 1_000_000_000_301;
    private const long GmOtherStructureId = GmStructureId + 1;
    private const long GmCorporationId = 98_000_001;
    private const long GmCharacterId = 90_000_001;
    private const int GmSolarSystemId = 30_000_142;
    private const int GmAthanorTypeId = 35835;

    [PgFact]
    public async Task V24_structure_menu_actions_mutate_the_persisted_timeline_through_host_slash_and_keep_the_staff_gate()
    {
        await using var route = await GmRoute.CreateAsync(GmPgUrl());
        await route.SeedStructureAsync(GmStructureId);
        var sink = new RecordingTeeSink();
        ClientSession? gmSession = null;
        Start(
            strictRpc: true,
            configure: registry => registry.Register(route.Slash),
            sessionOpened: session =>
            {
                session.CharacterId = GmCharacterId;
                session.Set("accountRole", new PyLong(GameMasterRoles.Gml));
                session.Set("structureid", new PyLong(GmStructureId));
                session.Set("shipid", new PyLong(GmStructureId));
                session.Set("corpid", new PyLong(GmCorporationId));
                session.Sink = sink.Attach(session.Sink!);
                gmSession = session;
                route.Sessions.Enter(GmCharacterId, session);
            });
        var (front, open) = await OpenFrontAsync();
        long callId = 1;

        var stateReply = await SlashAsync("/structure state 1000000000301 110");
        Assert.Contains("GM set structure state=shield_vulnerable", GmWireText(stateReply));
        var snapshot = await route.SnapshotAsync(GmStructureId);
        Assert.Equal(StructureState.ShieldVulnerable, snapshot.State);
        Assert.Equal(0, snapshot.StateEndsMs);
        await route.PumpEventsAsync();
        Assert.Contains("OnCurrentStructureStateChanged", GmNotificationNames(sink.Packets));

        var timerReply = await SlashAsync("/structure timer 1000000000301 17");
        Assert.Contains("GM set structure state timer to 17s", GmWireText(timerReply));
        snapshot = await route.SnapshotAsync(GmStructureId);
        Assert.Equal(FixedNowMs, snapshot.StateStartedMs);
        Assert.Equal(FixedNowMs + 17_000, snapshot.StateEndsMs);

        var deployReply = await SlashAsync("/structure deploytimer 1000000000301 18");
        Assert.Contains("GM set deploy timer to 18s", GmWireText(deployReply));
        snapshot = await route.SnapshotAsync(GmStructureId);
        Assert.Equal(StructureState.DeployVulnerable, snapshot.State);
        Assert.Equal(FixedNowMs + 18_000, snapshot.StateEndsMs);

        await SlashAsync("/structure state 1000000000301 110");
        var unanchorStart = await SlashAsync("/structure unanchor 1000000000301");
        Assert.Contains("GM started unanchoring", GmWireText(unanchorStart));
        Assert.Equal(FixedNowMs + 7 * StructureTimes.Day, (await route.SnapshotAsync(GmStructureId)).UnanchorEndsMs);

        var unanchorTimer = await SlashAsync("/structure unanchor 1000000000301 19");
        Assert.Contains("GM set unanchoring timer to 19s", GmWireText(unanchorTimer));
        Assert.Equal(FixedNowMs + 19_000, (await route.SnapshotAsync(GmStructureId)).UnanchorEndsMs);
        var unanchorCancel = await SlashAsync("/structure unanchor 1000000000301 cancel");
        Assert.Contains("GM cancelled unanchoring", GmWireText(unanchorCancel));
        Assert.Equal(0, (await route.SnapshotAsync(GmStructureId)).UnanchorEndsMs);

        var abandonReply = await SlashAsync("/structure abandontimer 1000000000301 21");
        Assert.Contains("GM set abandon timer to 21s", GmWireText(abandonReply));
        Assert.Equal(FixedNowMs + 21_000, (await route.SnapshotAsync(GmStructureId)).AbandonAtMs);

        var invalidState = await SlashAsync("/structure state 1000000000301 999");
        Assert.Contains("INVALID_STRUCTURE_STATE", GmWireText(invalidState));
        snapshot = await route.SnapshotAsync(GmStructureId);
        Assert.Equal(StructureState.ShieldVulnerable, snapshot.State);

        gmSession!.Set("accountRole", new PyLong(0));
        var denied = await SlashAsync("/structure state 1000000000301 113");
        Assert.Equal(SlashService.NotGameMaster, GmWireText(denied));
        Assert.Equal(StructureState.ShieldVulnerable, (await route.SnapshotAsync(GmStructureId)).State);

        Task<PyValue> SlashAsync(string command) => GmHostRpcAsync(front, open, callId++, "slash", "SlashCmd", new PyStr(command));
    }

    [PgFact]
    public async Task V24_moon_scheduling_advance_persists_phase_times_reschedules_jobs_and_notifies_over_host_wire()
    {
        await using var route = await GmRoute.CreateAsync(GmPgUrl());
        await route.SeedExtractionAsync(GmStructureId);
        await route.SeedStructureAsync(GmOtherStructureId);
        var sink = new RecordingTeeSink();
        ClientSession? gmSession = null;
        Start(
            strictRpc: true,
            configure: registry => registry.Register(route.Slash),
            sessionOpened: session =>
            {
                session.CharacterId = GmCharacterId;
                session.Set("accountRole", new PyLong(GameMasterRoles.Gml));
                session.Set("structureid", new PyLong(GmStructureId));
                session.Set("shipid", new PyLong(GmStructureId));
                session.Set("corpid", new PyLong(GmCorporationId));
                session.Sink = sink.Attach(session.Sink!);
                gmSession = session;
                route.Sessions.Enter(GmCharacterId, session);
            });
        var (front, open) = await OpenFrontAsync();

        var readyReply = await GmHostRpcAsync(front, open, 1, "slash", "SlashCmd", new PyStr("/extraction advance ready"));
        Assert.Contains("GM advanced the extraction", GmWireText(readyReply));
        await route.PumpEventsAsync();

        var extraction = await route.ExtractionAsync(1);
        var expectedDurationMs = 518_460_000L;
        Assert.Equal(FixedNowMs - expectedDurationMs, extraction.StartMs);
        Assert.Equal(FixedNowMs, extraction.ChunkAvailableMs);
        Assert.Equal(FixedNowMs + StructureTimes.Hour, extraction.NaturalDecayMs);
        Assert.Equal("active", extraction.State);
        Assert.Contains("OnMoonExtractionUpdate", GmNotificationNames(sink.Packets));
        var arrival = await route.Scheduler.FindAsync(MoonExtractions.ArrivalJobKind, "extraction:1");
        var fracture = await route.Scheduler.FindAsync(MoonExtractions.FractureJobKind, "extraction:1");
        Assert.Equal(FixedNowMs, arrival!.RunAtMs);
        Assert.Equal(extraction.NaturalDecayMs, fracture!.RunAtMs);

        // Repeating the same phase is idempotent in state and keeps the durable deadline.
        var retry = await GmHostRpcAsync(front, open, 2, "slash", "SlashCmd", new PyStr("/extraction advance ready"));
        Assert.Contains("GM advanced the extraction", GmWireText(retry));
        AssertSameExtraction(extraction, await route.ExtractionAsync(1));

        var noActive = await GmHostRpcAsync(front, open, 3, "slash", "SlashCmd", new PyStr("/extraction advance ready 1000000000302"));
        Assert.Contains("NO_ACTIVE_EXTRACTION", GmWireText(noActive));

        gmSession!.Set("accountRole", new PyLong(0));
        var denied = await GmHostRpcAsync(front, open, 4, "slash", "SlashCmd", new PyStr("/extraction advance stop"));
        Assert.Equal(SlashService.NotGameMaster, GmWireText(denied));
        AssertSameExtraction(extraction, await route.ExtractionAsync(1));
    }

    [PgFact]
    public async Task V24_native_module_repair_targets_only_the_owned_module_over_host_slash()
    {
        await using var route = await GmHealthRoute.CreateAsync(GmPgUrl());
        var owner = await route.SeedCharacterAsync("Native Repair Owner");
        var stranger = await route.SeedCharacterAsync("Native Repair Stranger");
        var sink = new RecordingTeeSink();
        ClientSession? gmSession = null;
        Start(
            strictRpc: true,
            configure: registry => registry.Register(route.Slash),
            sessionOpened: session =>
            {
                session.CharacterId = owner.CharacterId;
                session.Set("accountRole", new PyLong(GameMasterRoles.Gml));
                session.Set("stationid", new PyLong(GmHealthRoute.StationId));
                session.Sink = sink.Attach(session.Sink!);
                gmSession = session;
            });
        var (front, open) = await OpenFrontAsync();

        var reply = await GmHostRpcAsync(front, open, 1, "slash", "SlashCmd", new PyStr($"/heal {owner.TargetModuleId}"));
        Assert.Equal($"Repaired module {owner.TargetModuleId}.", GmWireText(reply));
        Assert.Equal(Eve.Fitting.ModuleCondition.Mended, await route.ModuleConditionAsync(owner.TargetModuleId));
        Assert.Equal(owner.DamagedCondition, await route.ModuleConditionAsync(owner.OtherModuleId));
        Assert.Equal(owner.ShipCondition, await route.ShipConditionAsync(owner.ShipId));
        var dogma = await route.Ships.LoadAsync(owner.CharacterId);
        Assert.Equal(Eve.Fitting.ModuleCondition.Mended, dogma!.ConditionOf(owner.TargetModuleId));
        Assert.Equal(owner.DamagedCondition, dogma.ConditionOf(owner.OtherModuleId));
        var repair = Assert.Single(GmModuleAttributeChanges(sink.Packets));
        Assert.Equal(owner.TargetModuleId, repair.ModuleId);
        Assert.Equal([3], repair.Attributes.Select(attribute => attribute.AttributeId));
        Assert.Equal([0d], repair.Attributes.Select(attribute => attribute.NewValue));
        Assert.Equal([owner.ModuleHp * 0.5], repair.Attributes.Select(attribute => attribute.OldValue));

        sink.Packets.Clear();
        var retry = await GmHostRpcAsync(front, open, 2, "slash", "SlashCmd", new PyStr($"/heal {owner.TargetModuleId}"));
        Assert.Equal($"Repaired module {owner.TargetModuleId}.", GmWireText(retry));
        Assert.Empty(GmModuleAttributeChanges(sink.Packets));

        var otherOwner = await GmHostRpcAsync(front, open, 3, "slash", "SlashCmd", new PyStr($"/heal {stranger.TargetModuleId}"));
        Assert.Contains("Usage: /heal", GmWireText(otherOwner));
        Assert.Equal(stranger.DamagedCondition, await route.ModuleConditionAsync(stranger.TargetModuleId));

        gmSession!.Set("accountRole", new PyLong(0));
        var denied = await GmHostRpcAsync(front, open, 4, "slash", "SlashCmd", new PyStr($"/heal {owner.OtherModuleId}"));
        Assert.Equal(SlashService.NotGameMaster, GmWireText(denied));
        Assert.Equal(owner.DamagedCondition, await route.ModuleConditionAsync(owner.OtherModuleId));
    }

    [PgFact]
    public async Task V24_native_fitting_menu_repairs_the_module_on_the_character_active_ship_in_space()
    {
        await using var route = await GmHealthRoute.CreateAsync(GmPgUrl());
        var owner = await route.SeedCharacterAsync("In Space Module Repair");
        var sink = new RecordingTeeSink();
        Start(
            strictRpc: true,
            configure: registry => registry.Register(route.Slash),
            sessionOpened: session =>
            {
                session.CharacterId = owner.CharacterId;
                session.Set("accountRole", new PyLong(GameMasterRoles.Gml));
                session.Set("shipid", new PyLong(owner.ShipId));
                session.Set("solarsystemid", new PyLong(GmHealthRoute.SolarSystemId));
                session.Sink = sink.Attach(session.Sink!);
            });
        var (front, open) = await OpenFrontAsync();

        var reply = await GmHostRpcAsync(front, open, 1, "slash", "SlashCmd", new PyStr($"/heal {owner.TargetModuleId}"));

        Assert.Equal($"Repaired module {owner.TargetModuleId}.", GmWireText(reply));
        Assert.Equal(Eve.Fitting.ModuleCondition.Mended, await route.ModuleConditionAsync(owner.TargetModuleId));
        Assert.Equal(owner.DamagedCondition, await route.ModuleConditionAsync(owner.OtherModuleId));
        Assert.Equal(owner.ShipCondition, await route.ShipConditionAsync(owner.ShipId));
        var dogma = await route.Ships.LoadAsync(owner.CharacterId);
        Assert.Equal(Eve.Fitting.ModuleCondition.Mended, dogma!.ConditionOf(owner.TargetModuleId));
        Assert.Equal(owner.DamagedCondition, dogma.ConditionOf(owner.OtherModuleId));
        Assert.Equal(owner.TargetModuleId, Assert.Single(GmModuleAttributeChanges(sink.Packets)).ModuleId);
    }

    [PgFact]
    public async Task V24_native_destroy_menu_does_not_fall_through_to_ship_heal_without_a_space_destruction_owner()
    {
        await using var route = await GmHealthRoute.CreateAsync(GmPgUrl());
        var owner = await route.SeedCharacterAsync("Native Destroy Guard");
        var sink = new RecordingTeeSink();
        Start(
            strictRpc: true,
            configure: registry => registry.Register(route.Slash),
            sessionOpened: session =>
            {
                session.CharacterId = owner.CharacterId;
                session.Set("accountRole", new PyLong(GameMasterRoles.Gml));
                session.Set("stationid", new PyLong(GmHealthRoute.StationId));
                session.Sink = sink.Attach(session.Sink!);
            });
        var (front, open) = await OpenFrontAsync();

        var reply = await GmHostRpcAsync(front, open, 1, "slash", "SlashCmd", new PyStr($"/heal {owner.ShipId} 0"));

        Assert.Contains("destruction", GmWireText(reply), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(owner.ShipCondition, await route.ShipConditionAsync(owner.ShipId));
        Assert.Equal(owner.DamagedCondition, await route.ModuleConditionAsync(owner.TargetModuleId));
        Assert.Empty(GmModuleAttributeChanges(sink.Packets));
    }

    private static string GmPgUrl() => Environment.GetEnvironmentVariable("EVE_PG_URL") ?? throw new InvalidOperationException("EVE_PG_URL is not set");

    private static IReadOnlyList<GmModuleRepairNotification> GmModuleAttributeChanges(IEnumerable<PyValue> packets) =>
        packets.Select(MachoPacket.Decode)
            .Where(packet => packet is { Type: MachoMessage.Notification, Dest.BroadcastId: "OnModuleAttributeChanges" })
            .Select(packet =>
            {
                var frame = Assert.IsType<PyTuple>(packet!.Payload);
                var envelope = Assert.IsType<PyTuple>(Assert.Single(frame.Items));
                var buffer = Assert.IsType<PyBuffer>(envelope.Items[1]);
                var inner = Assert.IsType<PyTuple>(MarshalCodec.Decode(buffer.Value));
                var invocation = Assert.IsType<PyTuple>(inner.Items[1]);
                var args = Assert.IsType<PyTuple>(invocation.Items[1]);
                var lines = Assert.IsType<PyList>(Assert.Single(args.Items));
                var attributes = lines.Items.Select(value =>
                {
                    var line = Assert.IsType<PyTuple>(value);
                    return new GmModuleAttribute(
                        (int)JsSemantics.NumberValue(line.Items[3]),
                        JsSemantics.NumberValue(line.Items[5]),
                        JsSemantics.NumberValue(line.Items[6]));
                }).ToArray();
                var first = Assert.IsType<PyTuple>(lines.Items[0]);
                return new GmModuleRepairNotification(
                    Convert.ToInt64(JsSemantics.NumberValue(first.Items[2])), attributes);
            })
            .ToArray();

    private sealed record GmModuleAttribute(int AttributeId, double NewValue, double OldValue);
    private sealed record GmModuleRepairNotification(long ModuleId, IReadOnlyList<GmModuleAttribute> Attributes);

    private async Task<PyValue> GmHostRpcAsync(TestFront front, SessionOpen open, long callId, string service, string method, params PyValue[] args)
    {
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.CallWithKwargs(open, callId, service, method, new PyTuple(args), TransportKwargs()))));
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var send = Assert.IsType<Send>(await front.ReceiveAsync());
            var decoded = MarshalCodec.Decode(send.Payload);
            var packet = Assert.IsType<MachoPacket>(MachoPacket.Decode(decoded));
            if (packet.Type == MachoMessage.CallRsp)
                return Result(decoded)!;
            if (packet.Type == MachoMessage.ErrorResponse)
                throw new Xunit.Sdk.XunitException($"GM slash returned an error response: {decoded}");
            Assert.Contains(packet.Type, new[] { MachoMessage.Notification, MachoMessage.SessionChangeNotification, MachoMessage.SessionInitialStateNotification });
        }
        throw new Xunit.Sdk.XunitException("The Host did not answer the GM slash call.");
    }

    private static string GmWireText(PyValue? value) => value switch
    {
        PyStr text => text.Value,
        PyBytes bytes => System.Text.Encoding.UTF8.GetString(bytes.Value.Span),
        _ => throw new Xunit.Sdk.XunitException($"Expected a wire string, got {value?.GetType().Name ?? "null"}")
    };

    private static List<string> GmNotificationNames(IEnumerable<PyValue> packets)
    {
        var names = new List<string>();
        foreach (var packet in packets.OfType<PyObject>())
        {
            if (packet.Args is not PyTuple tuple || tuple.Items.Count < 3 || tuple.Items[0] is not PyInt { Value: MachoMessage.Notification } || tuple.Items[2] is not PyObject address)
                continue;
            var addressItems = ((PyTuple)address.Args).Items;
            if (addressItems.Count > 1 && GmWireText(addressItems[1]) is { } name)
                names.Add(name);
        }
        return names;
    }

    private static void AssertSameExtraction(MoonExtraction expected, MoonExtraction actual)
    {
        Assert.Equal(expected.ExtractionId, actual.ExtractionId);
        Assert.Equal(expected.StructureId, actual.StructureId);
        Assert.Equal(expected.MoonId, actual.MoonId);
        Assert.Equal(expected.SolarSystemId, actual.SolarSystemId);
        Assert.Equal(expected.OwnerCorporationId, actual.OwnerCorporationId);
        Assert.Equal(expected.StartMs, actual.StartMs);
        Assert.Equal(expected.ChunkAvailableMs, actual.ChunkAvailableMs);
        Assert.Equal(expected.NaturalDecayMs, actual.NaturalDecayMs);
        Assert.Equal(expected.DurationSeconds, actual.DurationSeconds);
        Assert.Equal(expected.YieldMultiplier, actual.YieldMultiplier);
        Assert.Equal(expected.CalendarEventId, actual.CalendarEventId);
        Assert.Equal(expected.State, actual.State);
        Assert.Equal(expected.ReadyNotifiedMs, actual.ReadyNotifiedMs);
        Assert.Equal(expected.FractureStartedMs, actual.FractureStartedMs);
        Assert.Equal(expected.FractureCompleteMs, actual.FractureCompleteMs);
        Assert.Equal(expected.OreVolumeM3.OrderBy(pair => pair.Key), actual.OreVolumeM3.OrderBy(pair => pair.Key));
        Assert.Equal(expected.CreatedMs, actual.CreatedMs);
        Assert.Equal(expected.CompletedMs, actual.CompletedMs);
        Assert.Equal(expected.CompletedReason, actual.CompletedReason);
        Assert.Equal(expected.FieldDecayedMs, actual.FieldDecayedMs);
        Assert.Equal(expected.FieldDecayMs, actual.FieldDecayMs);
    }

    private sealed class RecordingTeeSink : IPacketSink
    {
        private IPacketSink? _inner;
        public List<PyValue> Packets { get; } = [];
        public IPacketSink Attach(IPacketSink inner) { _inner = inner; return this; }
        public void Send(PyValue packet)
        {
            Packets.Add(packet);
            _inner?.Send(packet);
        }
    }

    private sealed class GmHealthRoute : IAsyncDisposable
    {
        public const long StationId = 60_015_249;
        public const int SolarSystemId = 30_000_142;
        private const int IbisTypeId = 601;
        private const int CivilianRailgunTypeId = 3638;
        private const long CorporationId = 98_000_001;
        private readonly string[] _schemas;

        private GmHealthRoute(NpgsqlDataSource source, string[] schemas, DogmaStatic statics)
        {
            Source = source;
            _schemas = schemas;
            Statics = statics;
            Characters = new PgCharacterStore(source, schemas[0]);
            Items = new PgItemStore(source, schemas[1]);
            Skills = new PgSkillStore(source, schemas[2]);
            Modules = new PgModuleStateStore(source, schemas[3]);
            Ships = new CharacterShips(source, statics, Characters, Items, Skills, Modules);
            Slash = new SlashService(new ShipHealthCommands(Ships, statics, () => FixedNowMs));
            var moduleType = statics.Type(CivilianRailgunTypeId)!;
            ModuleHp = moduleType.Attributes.GetValueOrDefault(9);
            ModuleShieldCapacity = moduleType.Attributes.GetValueOrDefault(263);
            ModuleArmorHp = moduleType.Attributes.GetValueOrDefault(265);
            if (ModuleHp <= 0)
                throw new InvalidOperationException("SDE module health capacities are missing.");
        }

        public NpgsqlDataSource Source { get; }
        public DogmaStatic Statics { get; }
        public PgCharacterStore Characters { get; }
        public PgItemStore Items { get; }
        public PgSkillStore Skills { get; }
        public PgModuleStateStore Modules { get; }
        public CharacterShips Ships { get; }
        public SlashService Slash { get; }
        public double ModuleHp { get; }
        public double ModuleShieldCapacity { get; }
        public double ModuleArmorHp { get; }

        public static async Task<GmHealthRoute> CreateAsync(string pgUrl)
        {
            var source = NpgsqlDataSource.Create(PgConnection.ForTest(pgUrl));
            var suffix = Guid.NewGuid().ToString("N")[..12];
            var schemas = new[] { $"gmhc_{suffix}", $"gmhi_{suffix}", $"gmhs_{suffix}", $"gmhf_{suffix}" };
            await PgCharacterStore.MigrateAsync(source, schemas[0]);
            await PgItemStore.MigrateAsync(source, schemas[1]);
            await PgSkillStore.MigrateAsync(source, schemas[2]);
            await PgModuleStateStore.MigrateAsync(source, schemas[3]);
            var sde = new SdeReader(source);
            Assert.NotNull(await sde.BuildAsync());
            var statics = await DogmaStatic.LoadAsync(sde);
            await statics.EnsureTypesAsync([IbisTypeId, CivilianRailgunTypeId]);
            Assert.Equal(7, statics.Type(CivilianRailgunTypeId)!.CategoryId);
            return new GmHealthRoute(source, schemas, statics);
        }

        public async Task<GmHealthSeed> SeedCharacterAsync(string name)
        {
            const long systemId = SolarSystemId;
            const int constellationId = 20_000_020;
            const int regionId = 10_000_002;
            await using var connection = await Source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            var characterId = await Characters.InsertAsync(connection, transaction,
                new NewCharacter(1, name, 0, 1, 1, 1, 35, 1373, CorporationId, null,
                    StationId, (int)systemId, constellationId, regionId, null, null));
            var shipId = await Items.CreateAsync(connection, transaction,
                new NewItem(IbisTypeId, characterId, StationId, ItemFlags.Hangar, 1, Singleton: true));
            var targetModuleId = await Items.CreateAsync(connection, transaction,
                new NewItem(CivilianRailgunTypeId, characterId, shipId, ItemFlags.HiSlot0, 1, Singleton: true));
            var otherModuleId = await Items.CreateAsync(connection, transaction,
                new NewItem(CivilianRailgunTypeId, characterId, shipId, ItemFlags.HiSlot0 + 1, 1, Singleton: true));
            await Characters.SetShipAsync(connection, transaction, characterId, shipId);
            var shipCondition = new ShipCondition(0.3, 0.5, 0.3, 0.4, false);
            var damaged = new ModuleCondition(0.5, 0.25, 0.4, true);
            await Modules.SetShipConditionAsync(connection, transaction, shipId, shipCondition);
            await Modules.SetModuleConditionsAsync(connection, transaction,
                new Dictionary<long, ModuleCondition> { [targetModuleId] = damaged, [otherModuleId] = damaged });
            await transaction.CommitAsync();
            return new GmHealthSeed(characterId, shipId, targetModuleId, otherModuleId, shipCondition, damaged,
                ModuleHp, ModuleShieldCapacity, ModuleArmorHp);
        }

        public async Task<ModuleCondition> ModuleConditionAsync(long moduleId) =>
            (await Modules.ModuleConditionsAsync([moduleId])).GetValueOrDefault(moduleId, ModuleCondition.Untouched);

        public Task<ShipCondition> ShipConditionAsync(long shipId) => Modules.ShipConditionAsync(shipId);

        public async ValueTask DisposeAsync()
        {
            foreach (var schema in _schemas.Reverse())
            {
                await using var drop = Source.CreateCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
                await drop.ExecuteNonQueryAsync();
            }
            await Source.DisposeAsync();
        }
    }

    private sealed record GmHealthSeed(long CharacterId, long ShipId, long TargetModuleId, long OtherModuleId,
        ShipCondition ShipCondition, ModuleCondition DamagedCondition, double ModuleHp, double ModuleShieldCapacity, double ModuleArmorHp);

    private sealed class GmRoute : IAsyncDisposable
    {
        private readonly string _schema;

        private GmRoute(string schema, NpgsqlDataSource source)
        {
            _schema = schema;
            Source = source;
            StructureSchema = schema + "_str";
            SchedulerSchema = schema + "_sch";
            EventsSchema = schema + "_evt";
            Structures = new StructureStore(source, StructureSchema);
            ExtractionStore = new MoonExtractionStore(source, StructureSchema);
            Scheduler = new DurableScheduler(source, () => FixedNowMs, new SchedulerOptions { Concurrency = 1 }, SchedulerSchema, _ => { });
            Events = new EventBus(source, Scheduler, new EventBusOptions(), EventsSchema, _ => { });
            Sessions = new CharacterSessions();
            var rules = new TestStructureRules();
            Timers = new StructureTimers(Structures, Scheduler, Events, rules, () => 0.5);
            Timers.Register();
            Moons = new MoonExtractions(Structures, ExtractionStore, Scheduler, Events, new TestMoonRules());
            Moons.Register();
            Moons.Subscribe();
            Authority = new StructureGmAuthority(source, Structures, Timers, Moons, Scheduler, Events);
            Slash = new SlashService(new ShipHealthCommands(null!, null!, () => FixedNowMs),
                structureGm: new StructureGmCommands(Authority.ExecuteAsync));
            StructureNotifications.Subscribe(Events, Structures, Sessions, (_, _) => ValueTask.FromResult<PyValue?>(null));
            MoonExtractionGmNotifications.Subscribe(Events, Sessions);
        }

        public NpgsqlDataSource Source { get; }
        public string StructureSchema { get; }
        public string SchedulerSchema { get; }
        public string EventsSchema { get; }
        public StructureStore Structures { get; }
        public MoonExtractionStore ExtractionStore { get; }
        public DurableScheduler Scheduler { get; }
        public EventBus Events { get; }
        public CharacterSessions Sessions { get; }
        public StructureTimers Timers { get; }
        public MoonExtractions Moons { get; }
        public StructureGmAuthority Authority { get; }
        public SlashService Slash { get; }

        public static async Task<GmRoute> CreateAsync(string pgUrl)
        {
            var source = NpgsqlDataSource.Create(PgConnection.ForTest(pgUrl));
            var schema = "gm_menu_" + Guid.NewGuid().ToString("N")[..12];
            var structureSchema = schema + "_str";
            await StructureStore.MigrateAsync(source, structureSchema);
            await DurableScheduler.MigrateAsync(source, schema + "_sch");
            await EventBus.MigrateAsync(source, schema + "_evt");
            var route = new GmRoute(schema, source);
            await route.Events.PumpAsync();
            return route;
        }

        public async Task SeedStructureAsync(long structureId)
        {
            await using var connection = await Source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            Assert.True(await Structures.CreateAsync(connection, transaction,
                new NewStructure(structureId, GmAthanorTypeId, GmSolarSystemId, 1, 2, 3, 0, GmCorporationId, 0, "GM test refinery", "")));
            await transaction.CommitAsync();
        }

        public async Task SeedExtractionAsync(long structureId)
        {
            await using var connection = await Source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            Assert.True(await Structures.CreateAsync(connection, transaction,
                new NewStructure(structureId, GmAthanorTypeId, GmSolarSystemId, 1, 2, 3, 0, GmCorporationId, 0, "GM test refinery", "")));
            await Structures.SetServiceAsync(connection, transaction, structureId, StructureServiceIds.MoonMining, ServiceState.Online, FixedNowMs);
            Assert.Equal(1, await ExtractionStore.ImportAsync(connection, transaction, new JsonObject
            {
                ["resourcesByStructureID"] = new JsonObject
                {
                    [structureId.ToString()] = new JsonObject
                    {
                        ["moonID"] = 40_000_001,
                        ["solarSystemID"] = GmSolarSystemId,
                        ["ownerID"] = GmCorporationId,
                        ["resources"] = new JsonObject { ["34"] = 0.75, ["35"] = 0.25 },
                    },
                },
            }));
            var started = await Moons.StartAsync(connection, transaction, structureId, 518_460, addToCalendar: false);
            Assert.Equal(MoonExtractions.StartOutcome.Started, started.Outcome);
            await transaction.CommitAsync();
        }

        public async Task<StructureSnapshot> SnapshotAsync(long structureId) => (await Structures.GetAsync(structureId))!.Snapshot;

        public async Task<MoonExtraction> ExtractionAsync(long extractionId)
        {
            await using var connection = await Source.OpenConnectionAsync();
            return (await ExtractionStore.GetAsync(connection, null, extractionId, forUpdate: false))!;
        }

        public Task<EventBus.PumpResult> PumpEventsAsync() => Events.PumpAsync();

        public async ValueTask DisposeAsync()
        {
            await Events.StopAsync();
            await Scheduler.StopAsync();
            await using var connection = await Source.OpenConnectionAsync();
            foreach (var schema in new[] { EventsSchema, SchedulerSchema, StructureSchema })
            {
                await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", connection);
                await command.ExecuteNonQueryAsync();
            }
            await Source.DisposeAsync();
        }

        private sealed class TestStructureRules : IStructureRulesSource
        {
            public StructureRules RulesOf(int typeId, int solarSystemId) => StructureRules.Of(StructureSize.Medium, SecurityBand.HighSec);
        }

        private sealed class TestMoonRules : IMoonRulesSource
        {
            public MoonRules RulesOf(int typeId) => new(StructureTimes.Hour, 7 * StructureTimes.Day);
        }
    }
}

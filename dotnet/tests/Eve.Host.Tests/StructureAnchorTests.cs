using System.Globalization;
using System.Text.Json.Nodes;
using Eve.Data;
using Eve.Dogma;
using Eve.Events;
using Eve.Host;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Scheduler;
using Eve.Services;
using Eve.Services.Core;
using Eve.Services.Space;
using Eve.Sde;
using Eve.Structures;
using Npgsql;
using Xunit;

namespace Eve.Host.Tests;

/// <summary>
/// <c>structureDeployment.Anchor</c> through the anchor authority on the real item, structure, scheduler and bus stores and the pinned static data (skipped without
/// EVE_PG_URL; skipped is not passed). The ship's place is the simulation's: a fixed place here, so the simulation's own placement is tested in
/// <c>SpaceStructuresTests</c>. Each refusal is the named error and changes nothing; the success consumes the item and creates the structure with its
/// <c>structure.anchored</c> event in one transaction.
/// </summary>
public sealed class StructureAnchorTests
{
    private const long Character = 20, Corporation = 98_000_001, Ship = 1_000_000_000_050, Structure = 1_000_000_000_051;
    private const int Astrahus = 35832, Keepstar = 35834, Tritanium = 34;
    private const int Tanoo = 30000001, Jita = 30000142;
    private static readonly WarpCoordinates Spot = new(Tanoo, 1e13, 0, 1e13);
    private const long Now = 1_700_006_400_000;

    private sealed class Route : IAsyncDisposable
    {
        private readonly string[] _schemas;

        private Route(string[] schemas, NpgsqlDataSource source, PgItemStore items, StructureStore structures, DurableScheduler scheduler, EventBus bus,
            StructureAnchorAuthority authority, List<long> placed, Sink packets)
        {
            _schemas = schemas;
            Source = source;
            Items = items;
            Structures = structures;
            Scheduler = scheduler;
            Bus = bus;
            Authority = authority;
            Placed = placed;
            Packets = packets;
        }

        public NpgsqlDataSource Source { get; }
        public PgItemStore Items { get; }
        public StructureStore Structures { get; }
        public DurableScheduler Scheduler { get; }
        public EventBus Bus { get; }
        public StructureAnchorAuthority Authority { get; }
        public List<long> Placed { get; }
        public Sink Packets { get; }
        public List<StructureAnchored> Anchored { get; } = [];
        public WarpCoordinates? ShipPlace { get; set; } = Spot;
        public bool Warping { get; set; }
        public long? CorporationCreatedMs { get; set; } = Now - 30 * StructureTimes.Day;
        public IReadOnlyList<StructureProfileRecord> Profiles { get; set; } = [new(41, "Test default", "", true)];
        public SovFwReadSnapshot SovSnapshot { get; set; } = SovFwReadSnapshot.Empty;
        public IReadOnlyList<DevelopmentIndexRecord> DevelopmentIndices { get; set; } = [];

        public static async Task<Route> CreateAsync(string pgUrl, bool enableAnchorDuration = false)
        {
            var source = NpgsqlDataSource.Create(Eve.Data.PgConnection.ForTest(pgUrl));
            var sde = new SdeReader(source);
            Assert.NotNull(await sde.BuildAsync());
            var dogma = await DogmaStatic.LoadAsync(sde);
            var rules = await StructureRulesSource.LoadAsync(sde);
            var id = Guid.NewGuid().ToString("N")[..12];
            var schemas = new[] { $"anc_i_{id}", $"anc_s_{id}", $"anc_k_{id}", $"anc_e_{id}" };
            await PgItemStore.MigrateAsync(source, schemas[0]);
            await StructureStore.MigrateAsync(source, schemas[1]);
            await DurableScheduler.MigrateAsync(source, schemas[2]);
            await EventBus.MigrateAsync(source, schemas[3]);
            var items = new PgItemStore(source, schemas[0]);
            var structures = new StructureStore(source, schemas[1]);
            var scheduler = new DurableScheduler(source, () => Now, new SchedulerOptions { Concurrency = 1 }, schemas[2], _ => { });
            var bus = new EventBus(source, scheduler, new EventBusOptions { MaxAttempts = 3 }, schemas[3], _ => { });
            var timers = new StructureTimers(structures, scheduler, bus, rules, () => 0.5);
            timers.Register();
            var placed = new List<long>();
            var route = (Route)null!;
            Func<NpgsqlConnection, NpgsqlTransaction, NewStructure, long, CancellationToken, Task<StructureTransition?>>? anchorWithDuration = null;
            if (enableAnchorDuration)
            {
                anchorWithDuration = (connection, transaction, structure, anchoringMs, token) =>
                    timers.AnchorAsync(connection, transaction, structure with { AnchoringMs = anchoringMs }, token);
            }
            var authority = new StructureAnchorAuthority(source, structures, timers, rules, scheduler, bus, items, dogma,
                (_, _, _, _) => Task.FromResult(route.CorporationCreatedMs),
                (_, _, _) => Task.FromResult(route.ShipPlace),
                _ => route.Warping,
                id2 => { placed.Add(id2); return Task.CompletedTask; },
                (_, _) => ValueTask.FromResult(route.Profiles),
                _ => ValueTask.FromResult((route.SovSnapshot, route.DevelopmentIndices)),
                anchorWithDuration);
            route = new Route(schemas, source, items, structures, scheduler, bus, authority, placed, new Sink());
            bus.Subscribe("anchor.collector", [StructureAnchorAuthority.AnchoredTopic], (context, _) =>
            {
                route.Anchored.Add(context.Event.Payload<StructureAnchored>()!);
                return Task.CompletedTask;
            });
            await bus.PumpAsync();
            return route;
        }

        /// <summary>An item of the character in the given place (the ship's cargo by default).</summary>
        public async Task<long> GiveAsync(int type, long location, int flag = ItemFlags.Cargo, long owner = Character)
        {
            await using var connection = await Source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            var id = await Items.CreateAsync(connection, transaction, new NewItem(type, owner, location, flag, 1, true));
            await transaction.CommitAsync();
            return id;
        }

        public ClientSession Pilot(long corporation = Corporation, int roles = 2048, long? system = Tanoo, long? ship = Ship, long? alliance = null)
        {
            var session = new ClientSession(Packets) { SessionId = 403 };
            session.Set("charid", new PyInt(Character));
            session.Set("corpid", new PyInt(corporation));
            session.Set("corprole", new PyInt(roles));
            if (system is { } systemId) session.Set("solarsystemid2", new PyInt(systemId));
            if (ship is { } shipId) session.Set("shipid", new PyLong(shipId));
            if (alliance is { } allianceId) session.Set("allianceID", new PyInt(allianceId));
            return session;
        }

        public static StructureAnchorRequest Request(long itemId, double? x = null, double? z = null, long? weekday = 2, long? hour = 10, string name = "Anchored Test", long? profileId = null) =>
            new(itemId, x, z, 0.5, profileId, name, "a bio", weekday, hour, PyNone.Instance);

        public async ValueTask DisposeAsync()
        {
            await Scheduler.StopAsync();
            await using var connection = await Source.OpenConnectionAsync();
            foreach (var schema in _schemas)
            {
                await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", connection);
                await command.ExecuteNonQueryAsync();
            }
            await Source.DisposeAsync();
        }
    }

    /// <summary>The packets a session was sent, in order.</summary>
    private sealed class Sink : IPacketSink
    {
        public List<PyValue> Sent { get; } = [];
        public void Send(PyValue packet) => Sent.Add(packet);
    }

    /// <summary>The user error of a refusal: its name and, for a CustomNotify, the notice it shows.</summary>
    private static (string Name, string? Notice) Refusal(Exception exception)
    {
        var error = Assert.IsType<MachoWrappedException>(exception).Error;
        if (error.Payload is not PyObjectEx payload || payload.Header is not PyTuple header || header.Items.Count < 2 || header.Items[1] is not PyTuple arguments
            || arguments.Items.Count < 2 || arguments.Items[1] is not PyDict dict)
        {
            return (error.UserErrorMessage, null);
        }
        var notice = dict.Entries.SingleOrDefault(e => JsSemantics.StringValue(e.Key) == "notify").Value is PyStr text ? text.Value : null;
        return (error.UserErrorMessage, notice);
    }

    private static string PgUrl() => Environment.GetEnvironmentVariable("EVE_PG_URL") ?? throw new InvalidOperationException("EVE_PG_URL is not set");

    private static ServiceCall Call(ClientSession session) => new() { Method = "Anchor", Args = [], Session = session };

    [PgFact]
    public async Task An_anchored_structure_is_dockable_by_its_owner_corporation_pilot_with_the_default_profile()
    {
        // the docking access of a structure the Anchor authority created with its corporation's default profile, read as the Host reads it: the
        // structures table as the directory sees it and the profile table as the profile backend writes it (its default profile, no docking settings)
        await using var route = await Route.CreateAsync(PgUrl());
        var item = await route.GiveAsync(Astrahus, Ship);
        await route.Authority.AnchorAsync(Call(route.Pilot()), Route.Request(item, x: 0, z: 0, weekday: 2, hour: 10));
        var structure = await route.Structures.GetAsync(item);
        Assert.NotNull(structure);
        var profileId = structure.ProfileId;
        var profiles = new JsonObject
        {
            ["meta"] = new JsonObject { ["version"] = 1 },
            ["nextProfileID"] = profileId + 1,
            ["profilesByID"] = new JsonObject
            {
                [profileId.ToString(CultureInfo.InvariantCulture)] = new JsonObject
                {
                    ["profileID"] = profileId,
                    ["corporationID"] = Corporation,
                    ["name"] = "Default Profile",
                    ["description"] = "",
                    ["isDefault"] = true,
                    ["settingsBySettingID"] = new JsonObject(),
                },
            },
        };
        async Task<JsonObject> Read(string table, CancellationToken token) => table switch
        {
            "structures" => await route.Structures.ReadLegacyTableAsync(token),
            "structureProfiles" => profiles,
            _ => new JsonObject(),
        };
        var sde = new SdeReader(route.Source);
        async Task<bool> MayDock(long corporation)
        {
            var directory = new PersistenceStructureDirectoryReadModel(Read,
                (_, _) => Task.FromResult(new Eve.Relations.Affiliation(corporation, null, null)), sde, null);
            var access = await directory.GetDockingAccessSnapshotsAsync(Character, corporation, 0, [item], CancellationToken.None);
            return access.SingleOrDefault() is { } row && StructureDirectoryService.CanDock(row, item, Character, corporation, 0);
        }

        Assert.True(await MayDock(Corporation));
        Assert.False(await MayDock(Corporation + 1));
    }

    [PgFact]
    public async Task Anchoring_an_item_from_the_ship_consumes_it_and_creates_the_structure_with_its_event_and_placement()
    {
        await using var route = await Route.CreateAsync(PgUrl());
        var item = await route.GiveAsync(Astrahus, Ship);
        var pilot = route.Pilot();

        await route.Authority.AnchorAsync(Call(pilot), Route.Request(item, x: 0, z: 0, weekday: 2, hour: 10));

        Assert.Null(await route.Items.GetAsync(item));
        var record = await route.Structures.GetAsync(item);
        Assert.NotNull(record);
        Assert.Equal((Astrahus, Tanoo, Corporation, "Anchored Test", 41L), (record.TypeId, record.SolarSystemId, record.OwnerCorporationId, record.Name, record.ProfileId));
        Assert.Equal((StructureState.AnchorVulnerable, 2, 10), (record.Snapshot.State, record.Snapshot.ReinforceWeekday, record.Snapshot.ReinforceHour));
        Assert.Equal((1e13, 0.0, 1e13), (record.X, record.Y, record.Z));
        Assert.Equal(Now + StructureTimes.RepairMs, record.Snapshot.StateEndsMs);
        // the item's removal reaches the pilot, and the simulation shows the structure (a pilot is in the system)
        Assert.NotEmpty(route.Packets.Sent);
        Assert.Equal(new[] { item }, route.Placed);
        await route.Bus.PumpAsync();
        Assert.Equal(new[] { new StructureAnchored(item, Tanoo, Astrahus, Corporation) }, route.Anchored);
    }

    [PgFact]
    public async Task Each_refusal_is_named_and_leaves_the_item_where_it_is()
    {
        await using var route = await Route.CreateAsync(PgUrl());
        var item = await route.GiveAsync(Astrahus, Ship);

        async Task<(string Name, string? Notice)> Refused(ClientSession session, long itemId, double? x = 0, double? z = 0)
        {
            var exception = await Assert.ThrowsAsync<MachoWrappedException>(() => route.Authority.AnchorAsync(Call(session), Route.Request(itemId, x, z)));
            return Refusal(exception);
        }

        Assert.Equal("TargetingAttemptCancelled", (await Refused(route.Pilot(), await route.GiveAsync(Astrahus, Ship + 1))).Name);
        Assert.Equal("TargetingAttemptCancelled", (await Refused(route.Pilot(), await route.GiveAsync(Astrahus, Ship, ItemFlags.Hangar))).Name);
        Assert.Equal("TargetingAttemptCancelled", (await Refused(route.Pilot(), await route.GiveAsync(Astrahus, Ship, owner: Character + 1))).Name);
        var outside = await Refused(route.Pilot(system: null, ship: null), item);
        Assert.Equal(("CustomNotify", "You must be in space to deploy a structure."), (outside.Name, outside.Notice));
        route.Warping = true;
        Assert.Equal("ShipInWarp", (await Refused(route.Pilot(), item)).Name);
        route.Warping = false;
        Assert.Equal("DropNeedsPlayerCorp", (await Refused(route.Pilot(corporation: 1_000_010), item)).Name);
        Assert.Equal("CrpAccessDenied", (await Refused(route.Pilot(roles: 0), item)).Name);
        route.CorporationCreatedMs = Now - StructureTimes.Day;
        Assert.Equal("CustomNotify", (await Refused(route.Pilot(), item)).Name);
        route.CorporationCreatedMs = Now - 30 * StructureTimes.Day;
        var far = await Refused(route.Pilot(), item, 100_000, 0);
        Assert.Equal(("CustomNotify", "That structure is too far away to deploy."), (far.Name, far.Notice));
        var unsupported = await Refused(route.Pilot(), await route.GiveAsync(Tritanium, Ship));
        Assert.Equal(("CustomNotify", "That structure type is not supported by the current deployment service."), (unsupported.Name, unsupported.Notice));
        // SDE 3396210 gives Guardian-Vexor a rigSize dogma attribute, but its category is Ship.
        var riggedShip = await route.GiveAsync(11011, Ship);
        var riggedShipRefusal = await Refused(route.Pilot(), riggedShip);
        Assert.Equal(("CustomNotify", "That structure type is not supported by the current deployment service."), (riggedShipRefusal.Name, riggedShipRefusal.Notice));

        // a trade hub refuses the anchor, named as the blocked type
        route.ShipPlace = new WarpCoordinates(Jita, 1e13, 0, 1e13);
        Assert.Equal("CantDeployBlocked", (await Refused(route.Pilot(system: Jita), item)).Name);
        route.ShipPlace = Spot;

        // the item is still the pilot's, and no structure was made
        Assert.NotNull(await route.Items.GetAsync(item));
        Assert.NotNull(await route.Items.GetAsync(riggedShip));
        Assert.Null(await route.Structures.GetAsync(item));
        Assert.Empty(route.Placed);
    }

    [PgFact]
    public async Task Pinned_SDE_anchor_restrictions_refuse_before_consumption()
    {
        await using var route = await Route.CreateAsync(PgUrl());
        var systems = await new SdeReader(route.Source).ReadAsync("mapSolarSystems");
        int SystemId(string name) => checked((int)systems.Single(row =>
            row.Doc.GetProperty("name").GetProperty("en").GetString() == name).Key);

        // Thera, starter/career systems, and trade hubs are read from the pinned SDE or the retained EveJS hub list.
        foreach (var name in new[] { "Thera", "J000895", "J015092", "Kisogo", "Amsen", "Manifest", "Jita", "Hek" })
        {
            var systemId = SystemId(name);
            route.ShipPlace = new WarpCoordinates(systemId, 1e20, 1e20, 1e20);
            var item = await route.GiveAsync(Astrahus, Ship);
            var refusal = Refusal(await Assert.ThrowsAsync<MachoWrappedException>(() =>
                route.Authority.AnchorAsync(Call(route.Pilot(system: systemId)), Route.Request(item))));

            Assert.Equal("CantDeployBlocked", refusal.Name);
            Assert.NotNull(await route.Items.GetAsync(item));
            Assert.Null(await route.Structures.GetAsync(item));
        }
    }

    [PgFact]
    public async Task Anchor_uses_a_requested_corporation_profile_or_the_current_default()
    {
        await using var route = await Route.CreateAsync(PgUrl());
        route.Profiles =
        [
            new StructureProfileRecord(41, "Default", "", true),
            new StructureProfileRecord(42, "Requested", "", false),
        ];

        var requested = await route.GiveAsync(Astrahus, Ship);
        await route.Authority.AnchorAsync(Call(route.Pilot()), Route.Request(requested, profileId: 42));
        Assert.Equal(42, (await route.Structures.GetAsync(requested))!.ProfileId);

        route.ShipPlace = Spot with { X = Spot.X + 2_000_000 };
        var fallback = await route.GiveAsync(Astrahus, Ship);
        await route.Authority.AnchorAsync(Call(route.Pilot()), Route.Request(fallback, profileId: 99));
        Assert.Equal(41, (await route.Structures.GetAsync(fallback))!.ProfileId);

        route.Profiles = [];
        var missing = await route.GiveAsync(Astrahus, Ship);
        var beforePlaced = route.Placed.Count;
        var refusal = Refusal(await Assert.ThrowsAsync<MachoWrappedException>(() =>
            route.Authority.AnchorAsync(Call(route.Pilot()), Route.Request(missing))));

        Assert.Equal(("CustomNotify", "The corporation has no usable default structure profile."), (refusal.Name, refusal.Notice));
        Assert.NotNull(await route.Items.GetAsync(missing));
        Assert.Null(await route.Structures.GetAsync(missing));
        Assert.Equal(beforePlaced, route.Placed.Count);
    }

    [PgFact]
    public async Task Foreign_medium_deployments_allow_ADM_four_and_refuse_only_above_four()
    {
        const int NullSec = 30000208;
        await using var route = await Route.CreateAsync(PgUrl());
        route.SovSnapshot = new SovFwReadSnapshot(
            new Dictionary<long, SovClaimRecord> { [NullSec] = new(1, Corporation, 700) },
            new Dictionary<long, SovHubRecord>(),
            new Dictionary<long, IReadOnlyList<SovStructureRecord>>(),
            [],
            new Dictionary<long, long?>());
        route.DevelopmentIndices = [new DevelopmentIndexRecord(NullSec, 19_200_000, 19_200_000, 21)];

        route.ShipPlace = new WarpCoordinates(NullSec, 1e20, 1e20, 1e20);
        var boundary = await route.GiveAsync(Astrahus, Ship);
        await route.Authority.AnchorAsync(Call(route.Pilot(system: NullSec, alliance: 800)), Route.Request(boundary));
        Assert.NotNull(await route.Structures.GetAsync(boundary));

        route.DevelopmentIndices = [new DevelopmentIndexRecord(NullSec, 28_800_000, 19_200_000, 21)];
        route.ShipPlace = new WarpCoordinates(NullSec, 2e20, 1e20, 1e20);
        var hostile = await route.GiveAsync(Astrahus, Ship);
        var refusal = Refusal(await Assert.ThrowsAsync<MachoWrappedException>(() =>
            route.Authority.AnchorAsync(Call(route.Pilot(system: NullSec, alliance: 800)), Route.Request(hostile))));
        Assert.Equal("CantDeployBlocked", refusal.Name);
        Assert.NotNull(await route.Items.GetAsync(hostile));
        Assert.Null(await route.Structures.GetAsync(hostile));

        route.ShipPlace = new WarpCoordinates(NullSec, 3e20, 1e20, 1e20);
        var owner = await route.GiveAsync(Astrahus, Ship);
        await route.Authority.AnchorAsync(Call(route.Pilot(system: NullSec, alliance: 700)), Route.Request(owner));
        Assert.Null(await route.Items.GetAsync(owner));
        Assert.NotNull(await route.Structures.GetAsync(owner));
    }

    [PgFact]
    public async Task Foreign_IHub_delay_uses_current_strategic_index_and_refuses_when_timer_support_is_missing()
    {
        const int NullSec = 30000208;
        await using var route = await Route.CreateAsync(PgUrl());
        route.SovSnapshot = new SovFwReadSnapshot(
            new Dictionary<long, SovClaimRecord> { [NullSec] = new(1, Corporation, 700) },
            new Dictionary<long, SovHubRecord> { [NullSec] = new(2, Corporation, 700, null) },
            new Dictionary<long, IReadOnlyList<SovStructureRecord>>(),
            [],
            new Dictionary<long, long?>());
        route.DevelopmentIndices = [new DevelopmentIndexRecord(NullSec, 0, 0, 21)];

        var baseRules = StructureRules.Of(StructureSize.Medium, SecurityBand.NullSec);
        Assert.Equal(baseRules.AnchoringMs + 2 * StructureTimes.Day,
            await route.Authority.ResolveSovereigntyAnchoringMsAsync(baseRules, Astrahus, NullSec, 800, default));

        route.ShipPlace = new WarpCoordinates(NullSec, 1e20, 1e20, 1e20);
        var item = await route.GiveAsync(Astrahus, Ship);
        var refusal = Refusal(await Assert.ThrowsAsync<MachoWrappedException>(() =>
            route.Authority.AnchorAsync(Call(route.Pilot(system: NullSec, alliance: 800)), Route.Request(item))));

        Assert.Equal(("CustomNotify", "The sovereignty anchoring delay is unavailable."), (refusal.Name, refusal.Notice));
        Assert.NotNull(await route.Items.GetAsync(item));
        Assert.Null(await route.Structures.GetAsync(item));
        Assert.Empty(route.Placed);
    }


    [PgFact]
    public async Task Foreign_IHub_deployment_persists_the_computed_duration_on_the_structure_row()
    {
        const int NullSec = 30000208;
        await using var route = await Route.CreateAsync(PgUrl(), enableAnchorDuration: true);
        route.SovSnapshot = new SovFwReadSnapshot(
            new Dictionary<long, SovClaimRecord> { [NullSec] = new(1, Corporation, 700) },
            new Dictionary<long, SovHubRecord> { [NullSec] = new(2, Corporation, 700, null) },
            new Dictionary<long, IReadOnlyList<SovStructureRecord>>(),
            [],
            new Dictionary<long, long?>());
        route.DevelopmentIndices = [new DevelopmentIndexRecord(NullSec, 0, 0, 21)];
        route.ShipPlace = new WarpCoordinates(NullSec, 1e20, 1e20, 1e20);
        var item = await route.GiveAsync(Astrahus, Ship);

        await route.Authority.AnchorAsync(Call(route.Pilot(system: NullSec, alliance: 800)), Route.Request(item));

        var record = await route.Structures.GetAsync(item);
        Assert.NotNull(record);
        Assert.Equal(StructureTimes.AnchoringMs + 2 * StructureTimes.Day, record!.Snapshot.AnchoringMs);
        Assert.Equal(StructureState.AnchorVulnerable, record.Snapshot.State);
        Assert.Equal(Now + StructureTimes.RepairMs, record.Snapshot.StateEndsMs);
        var job = await route.Scheduler.FindAsync(StructureTimers.JobKind, $"structure:{item}");
        Assert.NotNull(job);
        Assert.Equal(Now + StructureTimes.RepairMs, job!.RunAtMs);
    }

    [PgFact]
    public async Task A_structure_within_a_thousand_kilometres_blocks_the_anchor_and_an_extra_large_one_is_refused_in_high_security()
    {
        await using var route = await Route.CreateAsync(PgUrl());
        var first = await route.GiveAsync(Astrahus, Ship);
        await route.Authority.AnchorAsync(Call(route.Pilot()), Route.Request(first, x: 0, z: 0));

        var second = await route.GiveAsync(Astrahus, Ship);
        var blocked = Refusal(await Assert.ThrowsAsync<MachoWrappedException>(() => route.Authority.AnchorAsync(Call(route.Pilot()), Route.Request(second, x: 0, z: 0))));
        Assert.Equal("CantDeployBlocked", blocked.Name);
        Assert.NotNull(await route.Items.GetAsync(second));

        var keepstar = await route.GiveAsync(Keepstar, Ship);
        var refused = Refusal(await Assert.ThrowsAsync<MachoWrappedException>(() => route.Authority.AnchorAsync(Call(route.Pilot()), Route.Request(keepstar, x: 0, z: 0))));
        Assert.Equal("CantDeployBlocked", refused.Name);
        Assert.NotNull(await route.Items.GetAsync(keepstar));
    }

    [PgFact]
    public async Task An_item_id_already_taken_by_a_structure_is_refused_and_the_item_stays()
    {
        await using var route = await Route.CreateAsync(PgUrl());
        var item = await route.GiveAsync(Astrahus, Ship);
        // a structure with the item's id exists already (the same id space): the anchor is refused and rolls back the removal
        await using (var connection = await route.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await route.Structures.CreateAsync(connection, transaction, new NewStructure(item, Astrahus, Tanoo, 0, 0, 0, 0, Corporation, 0, "Taken", ""));
            await transaction.CommitAsync();
        }

        var refused = Refusal(await Assert.ThrowsAsync<MachoWrappedException>(() => route.Authority.AnchorAsync(Call(route.Pilot()), Route.Request(item, x: 0, z: 0))));

        Assert.Equal(("CustomNotify", "That inventory item ID is already in use by a live structure."), (refused.Name, refused.Notice));
        Assert.NotNull(await route.Items.GetAsync(item));
        Assert.Empty(route.Placed);
    }
}

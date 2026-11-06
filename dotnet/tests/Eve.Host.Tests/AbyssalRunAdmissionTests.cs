using System.Text.Json;
using Eve.Characters;
using Eve.Content;
using Eve.Data;
using Eve.Events;
using Eve.Inventory;
using Eve.Scheduler;
using Eve.Sde;
using Eve.Services.Space;
using Eve.Space;
using Eve.Ships;
using Eve.Universe;
using Npgsql;
using Xunit;

namespace Eve.Host.Tests;

public sealed class AbyssalRunAdmissionTests
{
    [PgFact]
    public async Task Deployment_commits_filament_trace_and_domain_plan_together_and_conflict_rolls_back()
    {
        foreach (var mode in Enum.GetValues<AbyssalGameMode>())
        foreach (var exact in new[] { false, true })
        {
        await using var rig = await AdmissionRig.Create();
        var sde = new SdeReader(rig.Source);
        var catalog = await AbyssalStaticCatalog.LoadAsync(sde);
        var itemTypes = new ItemTypeCatalog(sde);
        var creation = await CreationCatalog.LoadAsync(sde, (long?)null);
        var stations = await StationCatalog.LoadAsync(sde);
        var blueprintCategoryId = await AbyssalLootMaterializer.LoadBlueprintCategoryIdAsync(sde);
        var traceTypeRow = (await sde.ReadAsync("types", [AbyssalTraceFactory.TraceTypeId])).Single();
        var traceRadius = traceTypeRow.Doc.GetProperty("radius").GetDouble();

        const int originSystemId = 30000142;
        var originSystem = (await sde.ReadAsync("mapSolarSystems", [originSystemId])).Single();
        var constellationId = originSystem.Doc.GetProperty("constellationID").GetInt32();
        var regionId = originSystem.Doc.GetProperty("regionID").GetInt32();
        long? fleetId = mode == AbyssalGameMode.SoloCruiser ? null : 9001;
        var initialQuantity = catalog.RequiredFilamentsFor(mode) + (exact ? 0 : 1);
        var shipTypeId = catalog.AllowedShipTypeIdsByMode[mode].Order().First();
        var filament = catalog.FilamentsByTypeId.Values.OrderBy(x => x.TypeId).First();
        var race = creation.Races.OrderBy(x => x.Id).First();
        var bloodline = creation.Bloodlines.First(x => x.RaceId == race.Id);
        var school = CreationCatalog.Schools.First(x => x.RaceId == race.Id);
        var starter = creation.StarterFor(school.CorporationId)
            ?? throw new InvalidDataException($"No starter location for corporation {school.CorporationId}.");
        var home = stations.Get(starter.StationId)
            ?? throw new InvalidDataException($"Starter station {starter.StationId} is absent from the SDE.");

        var characters = new PgCharacterStore(rig.Source, rig.CharacterSchema);
        var items = new PgItemStore(rig.Source, rig.ItemSchema);
        var characterName = "Abyssal admission " + Guid.NewGuid().ToString("N")[..10];
        long characterId;
        long shipId;
        long firstFilamentId;
        long secondFilamentId;
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            characterId = await characters.InsertAsync(connection, transaction,
                new NewCharacter(
                    1, characterName, 0, race.Id, bloodline.Id, 1, school.Id,
                    CreationCatalog.CharacterTypeId(bloodline.Id), school.CorporationId,
                    null, home.Id, originSystemId, constellationId, regionId, null, null));
            shipId = await items.CreateAsync(connection, transaction,
                new NewItem(shipTypeId, characterId, originSystemId, CharacterShips.InSpaceFlag, 1, Singleton: true));
            await characters.SetShipAsync(connection, transaction, characterId, shipId);
            await characters.SetStationAsync(connection, transaction, characterId, 0);
            firstFilamentId = await items.CreateAsync(connection, transaction,
                new NewItem(filament.TypeId, characterId, shipId, ItemFlags.Cargo, initialQuantity, Singleton: false));
            secondFilamentId = await items.CreateAsync(connection, transaction,
                new NewItem(filament.TypeId, characterId, shipId, ItemFlags.Cargo, initialQuantity, Singleton: false));
            await transaction.CommitAsync();
        }

        var runtime = new AbyssalContentRuntime(rig.Content, rig.Bus, rig.Scheduler);
        var publishedTraces = new List<SimAbyssalTrace>();
        var spawnRequests = new List<AbyssalRoomSpawnRequestedEvent>();
        rig.Bus.Subscribe("host.test.abyssal-trace", [AbyssalRunAdmission.TracePublishRequestedTopic], (context, _) =>
        {
            var request = context.Event.Payload<AbyssalTracePublishRequestedEvent>()
                ?? throw new InvalidDataException("The trace publish outbox payload is missing.");
            publishedTraces.Add(request.Trace);
            return Task.CompletedTask;
        });
        rig.Bus.Subscribe("host.test.abyssal-room-spawn", [AbyssalContentRuntime.RoomSpawnRequestedTopic], (context, _) =>
        {
            var request = context.Event.Payload<AbyssalRoomSpawnRequestedEvent>()
                ?? throw new InvalidDataException("The room-spawn outbox payload is missing.");
            spawnRequests.Add(request);
            return Task.CompletedTask;
        });
        await rig.Ready();
        var probeState = new SimAbyssalDeploymentSnapshot(false, false, false, false, false, []);
        var inspections = 0;
        TaskCompletionSource? inspectionEntered = null;
        TaskCompletionSource? releaseInspection = null;
        async Task<AbyssalParticipantPoseResult> Inspect(AbyssalRunAdmissionRequest input, CancellationToken ct)
        {
            Interlocked.Increment(ref inspections);
            if (input.CharacterId == characterId && releaseInspection is { } held)
            {
                inspectionEntered!.TrySetResult();
                await held.Task.WaitAsync(ct);
            }
            return new AbyssalParticipantPoseResult(input.ShipId, SimSceneKey.SolarSystem(input.SolarSystemId),
                [input.SourceShipPosition.X, input.SourceShipPosition.Y, input.SourceShipPosition.Z],
                [input.SourceShipDirection.X, input.SourceShipDirection.Y, input.SourceShipDirection.Z], null) { Deployment = probeState };
        }
        var admission = new AbyssalRunAdmission(
            rig.Source,
            characters,
            items,
            itemTypes,
            catalog,
            runtime,
            rig.Bus,
            new AbyssalFilamentConsumer(items, itemTypes),
            await AbyssalDeploymentEligibility.LoadAsync(sde, itemTypes),
            Inspect,
            blueprintCategoryId,
            traceRadius,
            () => rig.Now);
        var request = new AbyssalRunAdmissionRequest(
            characterId,
            shipId,
            firstFilamentId,
            mode,
            fleetId,
            originSystemId,
            new AbyssalPosition(10, 20, 30),
            new AbyssalPosition(0, 0, 1));

        AbyssalRunAdmissionResult deployed;
        if (mode == AbyssalGameMode.SoloCruiser && !exact)
        {
            probeState = probeState with { NearbyObjects = [new(98765, AbyssalTraceFactory.TraceTypeId, 1_000_000)] };
            await Assert.ThrowsAsync<Eve.Services.MachoWrappedException>(() => admission.DeployAsync(request));
            Assert.Equal(initialQuantity, (await items.GetAsync(firstFilamentId))!.Quantity);
            Assert.Empty(publishedTraces);
            probeState = probeState with { NearbyObjects = [], Warping = true };
            await Assert.ThrowsAsync<Eve.Services.MachoWrappedException>(() => admission.DeployAsync(request));
            Assert.Equal(initialQuantity, (await items.GetAsync(firstFilamentId))!.Quantity);
            probeState = probeState with { Warping = false };
            long otherCharacter = 0, otherShip = 0, otherFilament = 0;
            await using (var c = await rig.Source.OpenConnectionAsync())
            await using (var tx = await c.BeginTransactionAsync())
            {
                otherCharacter = await characters.InsertAsync(c, tx, new NewCharacter(
                    2, "Abyssal simultaneous " + Guid.NewGuid().ToString("N")[..10], 0, race.Id, bloodline.Id, 1, school.Id,
                    CreationCatalog.CharacterTypeId(bloodline.Id), school.CorporationId, null, home.Id, originSystemId, constellationId, regionId, null, null));
                otherShip = await items.CreateAsync(c, tx, new NewItem(shipTypeId, otherCharacter, originSystemId, CharacterShips.InSpaceFlag, 1, Singleton: true));
                await characters.SetShipAsync(c, tx, otherCharacter, otherShip);
                await characters.SetStationAsync(c, tx, otherCharacter, 0);
                otherFilament = await items.CreateAsync(c, tx, new NewItem(filament.TypeId, otherCharacter, otherShip, ItemFlags.Cargo, 1));
                await tx.CommitAsync();
            }
            inspectionEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            releaseInspection = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var first = admission.DeployAsync(request);
            await inspectionEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var competing = admission.DeployAsync(request with
            {
                CharacterId = otherCharacter, ShipId = otherShip, FilamentItemId = otherFilament,
            });
            releaseInspection.SetResult();
            deployed = await first;
            var blocked = await Assert.ThrowsAsync<Eve.Services.MachoWrappedException>(() => competing);
            Assert.Equal(AbyssalErrorAdapter.ClientError(8, new Eve.Marshal.PyInt(AbyssalTraceFactory.TraceTypeId)).Error.Payload, blocked.Error.Payload);
            Assert.Equal(1, (await items.GetAsync(otherFilament))!.Quantity);
        }
        else deployed = await admission.DeployAsync(request);
        await rig.Drain();

        Assert.NotNull(deployed.Consumption);
        Assert.Equal((firstFilamentId, (long)initialQuantity, exact ? initialQuantity : 1L), (deployed.Consumption.Before.Id,
            deployed.Consumption.Before.Quantity, deployed.Consumption.After.Quantity));
        var inspectionsBeforeRetry = inspections;
        var retry = await admission.DeployAsync(request);
        Assert.Equal(inspectionsBeforeRetry, inspections);
        Assert.NotNull(retry);
        Assert.Equal(deployed.Run.InstanceId, retry.Run.InstanceId);
        if (exact) Assert.Null(await items.GetAsync(firstFilamentId));
        else Assert.Equal(1, (await items.GetAsync(firstFilamentId))!.Quantity);
        Assert.Single(publishedTraces);
        Assert.Equal(deployed.Trace.ItemId, publishedTraces[0].ItemId);

        var shipTypeRow = (await sde.ReadAsync("types", [(long)shipTypeId])).Single();
        var shipRadius = shipTypeRow.Doc.GetProperty("radius").GetDouble();
        var roomReservation = new AbyssalRoomSpawnReservation(
            sde,
            items,
            itemTypes,
            catalog,
            runtime);
        var participant = new AbyssalParticipantAdmission(
            characterId,
            shipId,
            shipTypeId,
            mode switch { AbyssalGameMode.SoloCruiser => "cruiser", AbyssalGameMode.ThreeFrigates => "frigate", _ => "destroyer" },
            fleetId,
            new AbyssalParticipantOrigin(
                originSystemId,
                originSystemId,
                request.SourceShipPosition,
                request.SourceShipDirection,
                shipRadius));
        AbyssalRoomSpawnRequestedEvent preparedRoom;
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            var run = await runtime.FindByTraceItemIdAsync(
                connection, transaction, deployed.Trace.ItemId)
                ?? throw new InvalidDataException("The trace has no ContentRuntime run.");
            var entered = await runtime.EnterAsync(
                connection, transaction, new AbyssalEnterRequest(run.InstanceId, participant));
            Assert.Equal(AbyssalOperationStatus.Applied, entered.Status);
            preparedRoom = await roomReservation.PrepareAsync(
                connection, transaction, entered.State, 1);
            await transaction.CommitAsync();
        }
        await rig.Drain();
        Assert.Single(spawnRequests);
        Assert.Equal(preparedRoom.Entities, spawnRequests[0].Entities);
        Assert.Equal(
            preparedRoom.Loot.Select(loot => (loot.TypeId, loot.Count, loot.IsBlueprintCopy)),
            spawnRequests[0].Loot.Select(loot => (loot.TypeId, loot.Count, loot.IsBlueprintCopy)));
        Assert.All(preparedRoom.Loot.Zip(spawnRequests[0].Loot),
            pair => Assert.Equal(pair.First.BlueprintRunsPerCopy, pair.Second.BlueprintRunsPerCopy));
        Assert.Equal(47685, Assert.Single(preparedRoom.Entities, entity => entity.Role == AbyssalEntityRole.Gate).TypeId);
        Assert.Equal(catalog.MainCacheTypeFor(mode).TypeId, Assert.Single(preparedRoom.Entities, entity => entity.Role == AbyssalEntityRole.Container &&
            preparedRoom.Artifacts.Any(artifact => artifact.Key == entity.PlanKey && artifact.Kind == AbyssalArtifactKind.MainCache)).TypeId);
        Assert.Equal(
            deployed.Run.Data.Plan.Rooms.Single(room => room.LocationId == 1).NpcSpawns.Sum(spawn => spawn.Count),
            preparedRoom.Entities.Count(entity => entity.Role == AbyssalEntityRole.Npc));
        Assert.Equal(deployed.Trace.ItemId, deployed.Run.Data.TraceItemId);
        Assert.Equal(originSystemId, deployed.Run.Data.SourceSolarSystemId);
        Assert.Equal(originSystemId, deployed.Trace.SolarSystemId);
        Assert.Equal(deployed.Run.Data.TraceExpiresAtMs, deployed.Trace.ExpiresAtMs);
        Assert.Contains(deployed.Run.Data.SpaceLocationId, catalog.AbyssalSystemsById.Keys);
        Assert.Equal(new[] { 1, 2, 3 }, deployed.Run.Data.Plan.Rooms.Select(x => x.LocationId));
        Assert.All(deployed.Run.Data.Plan.Rooms.SelectMany(x => x.Artifacts.SelectMany(artifact => artifact.Loot)).Where(x => x.IsBlueprintCopy),
            item => Assert.Equal(item.Count, item.BlueprintRunsPerCopy!.Count));
        Assert.All(deployed.Run.Data.Plan.Rooms.SelectMany(x => x.Artifacts.SelectMany(artifact => artifact.Loot)).Where(x => x.IsBlueprintCopy)
            .SelectMany(x => x.BlueprintRunsPerCopy!), runs => Assert.InRange(runs, 1, 5));

        var traceItem = await items.GetAsync(deployed.Trace.ItemId);
        Assert.Equal((AbyssalTraceFactory.TraceTypeId, AbyssalTraceFactory.TraceOwnerId,
            originSystemId, CharacterShips.InSpaceFlag),
            (traceItem!.TypeId, traceItem.OwnerId, traceItem.LocationId, traceItem.Flag));
        using var pose = JsonDocument.Parse(traceItem.CustomInfo!);
        Assert.Equal(1, pose.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(deployed.Trace.Position,
            pose.RootElement.GetProperty("position").EnumerateArray().Select(value => value.GetDouble()));
        Assert.Equal(deployed.Trace.Direction,
            pose.RootElement.GetProperty("direction").EnumerateArray().Select(value => value.GetDouble()));
        if (exact) Assert.Null(await items.GetAsync(firstFilamentId));
        else Assert.Equal(1, (await items.GetAsync(firstFilamentId))!.Quantity);

        var conflict = await Assert.ThrowsAsync<AbyssalRuleException>(() => admission.DeployAsync(
            request with { FilamentItemId = secondFilamentId }));
        Assert.Equal(AbyssalRuleCode.AdmissionConflict, conflict.Code);
        Assert.Equal(initialQuantity, (await items.GetAsync(secondFilamentId))!.Quantity);
        Assert.Single(publishedTraces);
        }
    }

    private sealed class AdmissionRig : IAsyncDisposable
    {
        private readonly string[] _schemas;

        private AdmissionRig(NpgsqlDataSource source, string[] schemas)
        {
            Source = source;
            _schemas = schemas;
            Scheduler = new DurableScheduler(source, () => Now, new SchedulerOptions { Concurrency = 1 }, schemas[2]);
            Bus = new EventBus(source, Scheduler, null, schemas[3]);
            Content = new ContentRuntime(source, Scheduler, Bus, schemas[4]);
        }

        public NpgsqlDataSource Source { get; }
        public DurableScheduler Scheduler { get; }
        public EventBus Bus { get; }
        public ContentRuntime Content { get; }
        public long Now { get; set; } = 1_000_000;
        public string CharacterSchema => _schemas[0];
        public string ItemSchema => _schemas[1];

        public static async Task<AdmissionRig> Create()
        {
            var url = Environment.GetEnvironmentVariable("EVE_PG_URL")
                ?? throw new InvalidOperationException("EVE_PG_URL is not set");
            var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
            var suffix = Guid.NewGuid().ToString("N");
            string[] schemas = [
                $"abyssal_ac_{suffix}", $"abyssal_ai_{suffix}",
                $"abyssal_as_{suffix}", $"abyssal_ae_{suffix}", $"abyssal_an_{suffix}"];

            await PgCharacterStore.MigrateAsync(source, schemas[0]);
            await PgItemStore.MigrateAsync(source, schemas[1]);
            await DurableScheduler.MigrateAsync(source, schemas[2]);
            await EventBus.MigrateAsync(source, schemas[3]);
            await ContentRuntime.MigrateAsync(source, schemas[4]);
            return new AdmissionRig(source, schemas);
        }

        public async Task Ready()
        {
            await Content.AttachAsync();
            await Bus.PumpAsync();
        }

        public async Task Drain()
        {
            for (var attempt = 0; attempt < 20 && (await Bus.PumpAsync()).Delivered > 0; attempt++)
            {
            }
        }

        public async ValueTask DisposeAsync()
        {
            await using var connection = await Source.OpenConnectionAsync();
            foreach (var schema in _schemas)
            {
                await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", connection);
                await command.ExecuteNonQueryAsync();
            }
            await Source.DisposeAsync();
        }
    }
}
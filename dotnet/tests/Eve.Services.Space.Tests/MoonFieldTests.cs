using System.Globalization;
using Eve.Events;
using Eve.Host;
using Eve.Inventory;
using Eve.Marshal;
using Eve.Sde;
using Eve.Ships;
using Eve.Space;
using Eve.Structures;
using Npgsql;

namespace Eve.Services.Space.Tests;

public sealed class MoonFieldTests
{
    private sealed record Moon(long Id, int SolarSystemId, double X, double Y, double Z);
    private sealed record FieldFixture(
        long StructureId, long ExtractionId, long OwnerCorporationId, Moon Moon,
        StructureStore Structures, MoonExtractionStore Extractions, MoonExtractionEnded Ended);

    private sealed class TestMoonRules(long fieldDecayMs) : IMoonRulesSource
    {
        public MoonRules RulesOf(int typeId) => new(10_800_000, fieldDecayMs);
    }

    [PgFact]
    public async Task Fractured_and_automatically_decayed_chunks_seed_a_multiore_field_that_survives_restore()
    {
        await using var rig = await Rig.CreateAsync();
        var oreVolumes = await MultiOreVolumesAsync(rig);
        var fractured = await FixtureAsync(rig, oreVolumes, MoonExtractions.Fractured);
        var automaticallyFractured = await FixtureAsync(rig, oreVolumes, MoonExtractions.Decayed);
        var fields = Fields(rig, fractured);
        fields.Subscribe(rig.Events);
        await rig.Events.PumpAsync();

        var first = await PublishEndedAndWaitAsync(rig, fractured);
        var second = await PublishEndedAndWaitAsync(rig, automaticallyFractured);
        Assert.Equal(30, first.Count);
        Assert.Equal(30, second.Count);
        Assert.NotEqual(fractured.ExtractionId, automaticallyFractured.ExtractionId);

        var groups = new HashSet<int>();
        foreach (var spaceItem in first)
        {
            var item = await rig.Items.GetAsync(spaceItem.ItemId);
            Assert.NotNull(item);
            var type = rig.Statics.Type(item!.TypeId);
            Assert.NotNull(type);
            groups.Add(type!.GroupId);
            Assert.True(type.GroupId is 1884 or 1920 or 1921 or 1922 or 1923);
            Assert.True(spaceItem.Radius > 0);
            Assert.All(spaceItem.Position, value => Assert.True(double.IsFinite(value)));
            Assert.True(spaceItem.MoonGraphicId > 0);
            Assert.Equal(fractured.ExtractionId, spaceItem.MoonExtractionId);
            Assert.Equal(fractured.Moon.Id, spaceItem.MoonId);
            Assert.Equal(fractured.StructureId, spaceItem.MoonStructureId);
            Assert.Equal(fractured.OwnerCorporationId, spaceItem.MoonOwnerCorporationId);
            Assert.True(spaceItem.MoonRockOrdinal > 0);
        }
        Assert.Equal(2, groups.Count);
        var volumeAttribute = rig.Statics.AttributeId("volume")!.Value;
        var persistedVolume = 0d;
        foreach (var rock in first)
        {
            var item = (await rig.Items.GetAsync(rock.ItemId))!;
            persistedVolume += item.Quantity * rig.Statics.Type(item.TypeId)!.Attributes[volumeAttribute];
        }
        Assert.Equal(oreVolumes.Values.Sum(), persistedVolume, 6);

        var beforeRetry = rig.Sim.Heard.OfType<SimPlaceAsteroid>().Count();
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            Assert.Null(await fields.SeedInTransactionAsync(connection, transaction, fractured.Ended));
            await transaction.CommitAsync();
        }
        Assert.Equal(first.Count, (await rig.SpaceItems.MoonFieldAsync(fractured.ExtractionId)).Count);
        Assert.Equal(beforeRetry, rig.Sim.Heard.OfType<SimPlaceAsteroid>().Count());

        rig.Containers.Dispose();
        rig.Sim.Lose();
        using var restarted = new SpaceContainers(
            rig.Sim, rig.Ships, rig.Items, rig.SpaceItems, new PgWorkCycleLedger(rig.Source, rig.SpaceSchema),
            new EventBusFacts(rig.Events), rig.Statics, rig.Space, () => rig.Now);
        var beforeRestore = rig.Sim.Heard.OfType<SimPlaceAsteroid>().Count();
        await restarted.RestoreAsync(fractured.Moon.SolarSystemId);
        Assert.Equal(first.Count + second.Count, rig.Sim.Heard.OfType<SimPlaceAsteroid>().Count() - beforeRestore);
        await restarted.RestoreAsync(fractured.Moon.SolarSystemId);
        Assert.Equal(first.Count + second.Count, rig.Sim.Heard.OfType<SimPlaceAsteroid>().Count() - beforeRestore);
    }

    [PgFact]
    public async Task Material_ids_34_and_20_are_refused_as_moon_ore_type_ids()
    {
        await using var rig = await Rig.CreateAsync();
        var fixture = await FixtureAsync(rig, new Dictionary<long, double> { [34] = 12_000, [20] = 12_000 }, MoonExtractions.Fractured);
        var fields = Fields(rig, fixture);
        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await fields.SeedInTransactionAsync(connection, transaction, fixture.Ended));
        await transaction.RollbackAsync();

        Assert.Empty(await rig.SpaceItems.MoonFieldAsync(fixture.ExtractionId));
        Assert.Equal(0, FieldCount(rig, fixture.ExtractionId));
    }

    [PgFact]
    public async Task Concurrent_and_retried_seeds_create_one_persisted_rock_set()
    {
        await using var rig = await Rig.CreateAsync();
        var fixture = await FixtureAsync(rig, await MultiOreVolumesAsync(rig), MoonExtractions.Fractured);
        var fields = Fields(rig, fixture);

        async Task<MoonFieldMaterialized?> SeedAsync()
        {
            await using var connection = await rig.Source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            var result = await fields.SeedInTransactionAsync(connection, transaction, fixture.Ended);
            await transaction.CommitAsync();
            return result;
        }

        var results = await Task.WhenAll(SeedAsync(), SeedAsync());
        Assert.Single(results, result => result is not null);
        Assert.Equal(30, (await rig.SpaceItems.MoonFieldAsync(fixture.ExtractionId)).Count);
        Assert.Equal(30, FieldCount(rig, fixture.ExtractionId));
    }

    [PgFact]
    public async Task Field_expiry_uses_the_extraction_snapshot_after_rig_changes()
    {
        await using var rig = await Rig.CreateAsync();
        var lifetime = (long)TimeSpan.FromDays(3).TotalMilliseconds;
        var fixture = await FixtureAsync(rig, await MultiOreVolumesAsync(rig), MoonExtractions.Fractured, lifetime);
        var fields = Fields(rig, fixture);
        fields.Subscribe(rig.Events);
        await rig.Events.PumpAsync();
        var rocks = await PublishEndedAndWaitAsync(rig, fixture);
        Assert.All(rocks, rock => Assert.Equal(fixture.Ended.EndedMs + lifetime, rock.ExpiresAtMs));
    }

    [PgFact]
    public async Task Field_radius_applies_only_owned_correct_size_rigs_in_available_slots()
    {
        await using var rig = await Rig.CreateAsync();
        var fixture = await FixtureAsync(rig, await MultiOreVolumesAsync(rig), MoonExtractions.Fractured);
        var fields = Fields(rig, fixture);
        var types = await new SdeReader(rig.Source).ReadTypeSummariesAsync();
        var medium = Assert.Single(types, type => type.Published && type.Name == "Standup M-Set Moon Drilling Stability I").TypeId;
        var large = Assert.Single(types, type => type.Published && type.Name == "Standup L-Set Moon Drilling Proficiency I").TypeId;
        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var structure = (await fixture.Structures.GetAsync(connection, transaction, fixture.StructureId, false))!;
        var baseline = await fields.FieldRadiusMultiplierAsync(connection, transaction, structure, default);
        await rig.Items.CreateAsync(connection, transaction, new NewItem(large, fixture.OwnerCorporationId, fixture.StructureId, 92, 1, true));
        await rig.Items.CreateAsync(connection, transaction, new NewItem(medium, fixture.OwnerCorporationId + 1, fixture.StructureId, 93, 1, true));
        await rig.Items.CreateAsync(connection, transaction, new NewItem(medium, fixture.OwnerCorporationId, fixture.StructureId, 95, 1, true));
        Assert.Equal(baseline, await fields.FieldRadiusMultiplierAsync(connection, transaction, structure, default), 8);
        await rig.Items.CreateAsync(connection, transaction, new NewItem(medium, fixture.OwnerCorporationId, fixture.StructureId, 94, 1, true));
        Assert.Equal(baseline * 0.8d, await fields.FieldRadiusMultiplierAsync(connection, transaction, structure, default), 8);
        await transaction.RollbackAsync();
    }

    [PgFact]
    public async Task Field_decay_removes_the_persisted_items_and_sends_the_asteroid_removal_wire()
    {
        await using var rig = await Rig.CreateAsync();
        var fixture = await FixtureAsync(rig, await MultiOreVolumesAsync(rig), MoonExtractions.Decayed);
        var fields = Fields(rig, fixture);
        fields.Subscribe(rig.Events);
        await rig.Events.PumpAsync();
        var rocks = await PublishEndedAndWaitAsync(rig, fixture);
        await rig.Containers.ExpireDueAsync(rocks[0].ExpiresAtMs);
        Assert.Equal(rocks.Count, FieldCount(rig, fixture.ExtractionId));
        Assert.Empty(rig.Sim.Heard.OfType<SimRemoveWreck>());
        var removedBefore = rig.Sim.Heard.OfType<SimRemoveWreck>().Count();

        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            var extraction = await fixture.Extractions.GetAsync(connection, transaction, fixture.ExtractionId, false);
            Assert.NotNull(extraction);
            await fixture.Extractions.UpdateAsync(connection, transaction, extraction! with { FieldDecayedMs = rig.Now });
            await transaction.CommitAsync();
        }
        await rig.Events.PublishAsync(MoonExtractions.FieldDecayedTopic,
            new MoonFieldDecayed(fixture.ExtractionId, fixture.StructureId, fixture.Moon.SolarSystemId),
            $"structure:{fixture.StructureId}");
        await rig.Events.PumpAsync();
        await rig.Events.PumpAsync();
        await Rig.UntilAsync(() => FieldCount(rig, fixture.ExtractionId) == 0
            && rig.Sim.Heard.OfType<SimRemoveWreck>().Count() - removedBefore == rocks.Count);

        var removals = rig.Sim.Heard.OfType<SimRemoveWreck>().Skip(removedBefore).ToArray();
        Assert.Equal(rocks.Select(rock => rock.ItemId).Order(), removals.Select(remove => remove.WreckId).Order());
        Assert.All(removals, remove => Assert.Equal(2, remove.Effect));
        foreach (var rock in rocks)
        {
            Assert.Null(await rig.Items.GetAsync(rock.ItemId));
            Assert.Null(await rig.SpaceItems.GetAsync(rock.ItemId));
        }

        await using var retryConnection = await rig.Source.OpenConnectionAsync();
        await using var retryTransaction = await retryConnection.BeginTransactionAsync();
        Assert.Null(await fields.SeedInTransactionAsync(retryConnection, retryTransaction, fixture.Ended));
        await retryTransaction.CommitAsync();
        Assert.Equal(0, FieldCount(rig, fixture.ExtractionId));
    }

    [PgFact]
    public async Task Mining_moon_ore_writes_character_and_refinery_observer_ledger_rows()
    {
        await using var rig = await Rig.CreateAsync();
        var fixture = await FixtureAsync(rig, await MultiOreVolumesAsync(rig), MoonExtractions.Fractured);
        var fields = Fields(rig, fixture);
        var recorder = new MiningLedgerRecorder(rig.SpaceSchema);
        var observerRecorded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        fields.Subscribe(rig.Events);
        rig.Events.Subscribe("test.mining_ledger", [OreMinedEvent.Topic, MoonMiningObserverEvent.Topic], async (context, token) =>
        {
            await recorder.RecordAsync(context, token);
            if (context.Event.Topic == MoonMiningObserverEvent.Topic)
            {
                observerRecorded.TrySetResult(true);
            }
        });
        await rig.Events.PumpAsync();
        var rocks = await PublishEndedAndWaitAsync(rig, fixture);

        var typeSummaries = await new SdeReader(rig.Source).ReadTypeSummariesAsync();
        var minerTypeId = Assert.Single(typeSummaries, type => type.Published && type.Name == "Mining Drone I").TypeId;
        var flying = await DroneHostTests.FlyingWithADroneAsync(rig, droneTypeId: minerTypeId, solarSystemId: fixture.Moon.SolarSystemId);
        await DroneHostTests.LaunchAsync(flying);
        var target = rocks[0];
        await using (var depletionConnection = await rig.Source.OpenConnectionAsync())
        await using (var depletionTransaction = await depletionConnection.BeginTransactionAsync())
        {
            await rig.Items.SetQuantityAsync(depletionConnection, depletionTransaction, target.ItemId, 1);
            await depletionTransaction.CommitAsync();
        }
        Assert.True(target.ExpiresAtMs > rig.Now);
        rig.Sim.MeasureAnswer = 1d;
        var order = await rig.EntityCallAsync(flying.Session, "CmdMineRepeatedly", flying.Entity,
            new PyList([InventoryWire.Id(flying.Drone)]), new PyLong(target.ItemId));
        Assert.Empty(Assert.IsType<PyDict>(order).Entries);
        var work = Assert.Single(rig.Sim.Heard.OfType<SimLaunchDrone>()).Work;
        const ulong cycleId = 711;
        rig.Sim.Say(new SimDroneWorkCycle(
            (ulong)flying.Session.SessionId, flying.Character, flying.Session.GetInt("shipid")!.Value, flying.System,
            flying.Drone, target.ItemId, cycleId, SimDroneCommands.MineRepeatedly, work.MiningAmountM3,
            work.MiningWasteVolumeMultiplier, work.MiningWasteProbability, work.SalvageAccessDifficultyBonus, 0.5d));

        await Rig.UntilAsync(() => rig.Sim.Heard.OfType<SimDroneWorkResult>().Any(result => result.CycleId == cycleId));
        await rig.Events.PumpAsync();
        await rig.Events.PumpAsync();
        await observerRecorded.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var result = Assert.Single(rig.Sim.Heard.OfType<SimDroneWorkResult>(), result => result.CycleId == cycleId);
        Assert.True(result.Accepted);
        Assert.Equal(1, result.DeliveredUnits);
        Assert.Equal(0, result.UnitsRemaining);
        Assert.Equal(0, (await rig.Items.GetAsync(target.ItemId))!.Quantity);

        var expiring = rocks[1];
        var quantityBeforeExpiry = (await rig.Items.GetAsync(expiring.ItemId))!.Quantity;
        var refused = await rig.Ships.MineAsteroidAsync(
            flying.Character, flying.Session.GetInt("shipid")!.Value, flying.System, flying.Drone, expiring.ItemId,
            MiningSource.Drone, new MiningNumbers(work.MiningAmountM3, 0, 0, 0, 0), new MiningRolls(1, 0, 0, 1, 0),
            712, new PgWorkCycleLedger(rig.Source, rig.SpaceSchema), rig.SpaceItems, new EventBusFacts(rig.Events),
            (int)(flying.Session.GetInt("corpid") ?? 0), nowUnixMs: expiring.ExpiresAtMs);
        Assert.False(refused.Accepted);
        Assert.Equal("NO_TARGET", refused.Reason);
        Assert.Equal(quantityBeforeExpiry, (await rig.Items.GetAsync(expiring.ItemId))!.Quantity);
        rig.Containers.Dispose();
        rig.Sim.Lose();
        using var restarted = new SpaceContainers(
            rig.Sim, rig.Ships, rig.Items, rig.SpaceItems, new PgWorkCycleLedger(rig.Source, rig.SpaceSchema),
            new EventBusFacts(rig.Events), rig.Statics, rig.Space, () => rig.Now);
        var placedBefore = rig.Sim.Heard.OfType<SimPlaceAsteroid>().Count();
        await restarted.RestoreAsync(fixture.Moon.SolarSystemId);
        Assert.Equal(rocks.Count - 1, rig.Sim.Heard.OfType<SimPlaceAsteroid>().Count() - placedBefore);
        Assert.DoesNotContain(rig.Sim.Heard.OfType<SimPlaceAsteroid>().Skip(placedBefore), rock => rock.AsteroidId == target.ItemId);
        var targetTypeId = (await rig.Items.GetAsync(target.ItemId))!.TypeId;

        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            SELECT owner_id, character_id, corporation_id, solar_system_id, event_date, type_id, quantity, observer_item_id, yield_kind
            FROM {rig.SpaceSchema}.mining_ledger_entries
            WHERE owner_kind = 'observer' AND owner_id = $1
            """, connection);
        command.Parameters.AddWithValue(fixture.StructureId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(fixture.StructureId, reader.GetInt64(0));
        Assert.Equal(flying.Character, reader.GetInt64(1));
        Assert.Equal(fixture.OwnerCorporationId, reader.GetInt64(2));
        Assert.Equal(fixture.Moon.SolarSystemId, reader.GetInt64(3));
        Assert.Equal(MiningLedgerStore.FileTimeOf(rig.Now), reader.GetInt64(4));
        Assert.Equal((long)targetTypeId, reader.GetInt64(5));
        Assert.Equal(result.DeliveredUnits, reader.GetInt64(6));
        Assert.Equal(fixture.StructureId, reader.GetInt64(7));
        Assert.Equal("ore", reader.GetString(8));
        Assert.False(await reader.ReadAsync());
        await reader.DisposeAsync();

        await using var observer = new NpgsqlCommand(
            $"SELECT corporation_id, solar_system_id, moon_id FROM {rig.SpaceSchema}.mining_ledger_observers WHERE item_id = $1", connection);
        observer.Parameters.AddWithValue(fixture.StructureId);
        await using var observerReader = await observer.ExecuteReaderAsync();
        Assert.True(await observerReader.ReadAsync());
        Assert.Equal(fixture.OwnerCorporationId, observerReader.GetInt64(0));
        Assert.Equal(fixture.Moon.SolarSystemId, observerReader.GetInt64(1));
        Assert.Equal(fixture.Moon.Id, observerReader.GetInt64(2));
    }

    private static SpaceMoonFields Fields(Rig rig, FieldFixture fixture) => new(
        fixture.Extractions, fixture.Structures, rig.Items, rig.SpaceItems, rig.Statics,
        new TestMoonRules((long)TimeSpan.FromDays(2).TotalMilliseconds), rig.Containers);

    private static async Task<IReadOnlyList<SpaceItem>> PublishEndedAndWaitAsync(Rig rig, FieldFixture fixture)
    {
        var placedBefore = rig.Sim.Heard.OfType<SimPlaceAsteroid>().Count();
        await rig.Events.PublishAsync(MoonExtractions.EndedTopic, fixture.Ended, $"structure:{fixture.StructureId}");
        await rig.Events.PumpAsync();
        await rig.Events.PumpAsync();
        await Rig.UntilAsync(() =>
        {
            var count = FieldCount(rig, fixture.ExtractionId);
            return count > 0 && rig.Sim.Heard.OfType<SimPlaceAsteroid>().Count() - placedBefore == count;
        });
        return await rig.SpaceItems.MoonFieldAsync(fixture.ExtractionId);
    }

    private static async Task<FieldFixture> FixtureAsync(
        Rig rig,
        IReadOnlyDictionary<long, double> oreVolumes,
        string reason,
        long fieldDecayMs = -1)
    {
        await StructureStore.MigrateAsync(rig.Source, rig.StructureSchema);
        var summaries = await new SdeReader(rig.Source).ReadTypeSummariesAsync();
        var refinery = Assert.Single(summaries, type => type.Published && type.Name == "Athanor");
        var moon = await FirstMoonAsync(rig.Source);
        await rig.Statics.EnsureTypesAsync([refinery.TypeId]);

        var id = Random.Shared.NextInt64(1_000_000_000, 8_000_000_000_000);
        var structureId = id;
        var extractionId = id + 1;
        const long ownerCorporationId = 98_000_123;
        var structures = new StructureStore(rig.Source, rig.StructureSchema);
        var extractions = new MoonExtractionStore(rig.Source, rig.StructureSchema);
        var durationSeconds = MoonExtractions.MinimumDurationSeconds;
        var extraction = new MoonExtraction(
            extractionId, structureId, moon.Id, moon.SolarSystemId, ownerCorporationId,
            rig.Now - durationSeconds * 1000, rig.Now, rig.Now + 10_800_000, durationSeconds, 1d, 0, "completed",
            rig.Now, 0, 0, oreVolumes, rig.Now - durationSeconds * 1000, rig.Now, reason, 0, fieldDecayMs);
        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        Assert.True(await structures.CreateAsync(connection, transaction,
            new NewStructure(structureId, refinery.TypeId, moon.SolarSystemId, moon.X - 300_000d, moon.Y, moon.Z, 0d,
                ownerCorporationId, 0, "Moon Field Test", "")));
        Assert.Equal(1, await extractions.InsertExtractionAsync(connection, transaction, extraction));
        await transaction.CommitAsync();

        var ended = new MoonExtractionEnded(extractionId, structureId, moon.SolarSystemId, ownerCorporationId, reason, rig.Now);
        return new FieldFixture(structureId, extractionId, ownerCorporationId, moon, structures, extractions, ended);
    }

    private static async Task<IReadOnlyDictionary<long, double>> MultiOreVolumesAsync(Rig rig)
    {
        var summaries = await new SdeReader(rig.Source).ReadTypeSummariesAsync();
        var zeolites = Assert.Single(summaries, type => type.Published && type.Name == "Zeolites");
        var cobaltite = Assert.Single(summaries, type => type.Published && type.Name == "Cobaltite");
        return new Dictionary<long, double> { [zeolites.TypeId] = 12_000d, [cobaltite.TypeId] = 12_000d };
    }

    private static async Task<Moon> FirstMoonAsync(NpgsqlDataSource source)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            SELECT key, (doc->>'solarSystemID')::integer,
                   (doc->'position'->>'x')::double precision,
                   (doc->'position'->>'y')::double precision,
                   (doc->'position'->>'z')::double precision
            FROM sde."mapMoons" ORDER BY ord LIMIT 1
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new Moon(long.Parse(reader.GetString(0), CultureInfo.InvariantCulture), reader.GetInt32(1),
            reader.GetDouble(2), reader.GetDouble(3), reader.GetDouble(4));
    }

    private static int FieldCount(Rig rig, long extractionId)
    {
        using var connection = rig.Source.OpenConnection();
        using var command = new NpgsqlCommand(
            $"SELECT count(*) FROM {rig.SpaceSchema}.space_items WHERE moon_extraction_id = $1", connection);
        command.Parameters.AddWithValue(extractionId);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
}

using Eve.Inventory;
using Eve.Sde;
using Eve.Ships;
using Eve.Space;
using Eve.Structures;
using Npgsql;

namespace Eve.Services.Space.Tests;

public sealed class MoonMiningChunkTests
{
    private const long StructureId = 1_040_000_000_651;
    private const long CorporationId = 98_000_651;
    private const long MoonId = 40_000_004;
    private const int SolarSystemId = 30_000_001;
    private const int AthanorTypeId = 35_835;

    private sealed class Rules : IMoonRulesSource
    {
        public MoonRules RulesOf(int typeId) => new(10_800_000, 172_800_000);
    }

    [PgFact]
    public async Task Starting_a_moon_extraction_projects_a_persisted_chunk_to_the_space_simulation()
    {
        await using var rig = await Rig.CreateAsync();
        await StructureStore.MigrateAsync(rig.Source, rig.StructureSchema);
        var structures = new StructureStore(rig.Source, rig.StructureSchema);
        var extractionsStore = new MoonExtractionStore(rig.Source, rig.StructureSchema);
        var rules = new Rules();
        var extractions = new MoonExtractions(structures, extractionsStore, rig.Scheduler, rig.Events, rules);
        extractions.Register();
        var chunks = new SpaceMoonChunks(rig.Sim, rig.Space, rig.Source, structures, extractionsStore,
            rig.Items, rig.SpaceItems, rig.Statics, () => rig.Now, log: rig.Log.Add);
        chunks.Subscribe(rig.Events);
        new SpaceMoonFields(extractionsStore, structures, rig.Items, rig.SpaceItems, rig.Statics, rules, rig.Containers)
            .Subscribe(rig.Events);
        await rig.Events.PumpAsync();

        long drillId;

        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await structures.CreateAsync(connection, transaction,
                new NewStructure(StructureId, AthanorTypeId, SolarSystemId, 0, 0, 0, 0, CorporationId, 0, "Moon refinery", ""));
            await structures.SetServiceAsync(connection, transaction, StructureId, StructureServiceIds.MoonMining,
                ServiceState.Online, rig.Now, manageFuelCycle: false);
            drillId = await rig.Items.CreateAsync(connection, transaction,
                new NewItem(45_009, CorporationId, StructureId, 164, 1, true));
            await extractionsStore.ImportAsync(connection, transaction, new System.Text.Json.Nodes.JsonObject
            {
                ["resourcesByStructureID"] = new System.Text.Json.Nodes.JsonObject
                {
                    [StructureId.ToString()] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["moonID"] = MoonId,
                        ["solarSystemID"] = SolarSystemId,
                        ["ownerID"] = CorporationId,
                        ["resources"] = new System.Text.Json.Nodes.JsonObject { ["45490"] = 0.75, ["45494"] = 0.25 },
                    },
                },
            });
            await transaction.CommitAsync();
        }

        var effectCountBefore = rig.Sim.Heard.Count(message => message.GetType().Name == "SimUpsertMoonChunk");
        MoonExtraction? started = null;
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            var result = await extractions.StartAsync(connection, transaction, StructureId, 518_460, false);
            Assert.Equal(MoonExtractions.StartOutcome.Started, result.Outcome);
            await transaction.CommitAsync();
            started = result.Extraction;
        }
        await Task.WhenAll(chunks.RestoreAsync(SolarSystemId), chunks.RestoreAsync(SolarSystemId));
        await rig.Events.PumpAsync();

        var projections = rig.Sim.Heard.OfType<SimUpsertMoonChunk>().ToArray();
        Assert.NotEmpty(projections);
        Assert.All(projections, projection => Assert.Equal(projections[0].ChunkId, projection.ChunkId));
        var tractor = projections[^1];
        Assert.True(projections.Length > effectCountBefore,
            "a committed StartNewExtraction must create and project its moon chunk into the simulation");
        Assert.Equal(45_009, tractor.ModuleTypeId);
        Assert.Equal(drillId, tractor.ModuleId);
        Assert.Equal(MoonId, tractor.MoonId);
        Assert.Equal("tractor_phase", tractor.Phase);
        var structureState = rig.Sim.Heard.OfType<SimSetStructureMoonMiningState>().Last();
        Assert.Equal(MoonId, structureState.ClosestMoonId);
        Assert.Contains(structureState.Modules, module => module.ItemId == drillId && module.TypeId == 45_009 && module.FlagId == 164);

        var chunk = Assert.Single(await rig.SpaceItems.InSystemAsync(SolarSystemId), item => item.MoonExtractionId > 0);
        Assert.Equal(MoonId, chunk.MoonId);
        Assert.Equal(StructureId, chunk.MoonStructureId);
        Assert.Equal(CorporationId, chunk.CorporationId);
        Assert.Equal(SpaceItemKind.MoonChunk, chunk.Kind);
        Assert.Equal(0, chunk.MoonRockOrdinal);
        var chunkInventory = await rig.Items.GetAsync(chunk.ItemId);
        Assert.NotNull(chunkInventory);
        Assert.Equal(CorporationId, chunkInventory.OwnerId);

        var extractionRow = Assert.IsType<MoonExtraction>(started);
        Assert.Equal(extractionRow.StartMs * 10_000 + 116_444_736_000_000_000L, tractor.PhaseStartedFileTime);

        rig.Now = extractionRow.ChunkAvailableMs;
        await rig.Scheduler.RunDueAsync();
        await rig.Events.PumpAsync();
        var stasis = rig.Sim.Heard.OfType<SimUpsertMoonChunk>().Last();
        Assert.Equal(tractor.ChunkId, stasis.ChunkId);
        Assert.Equal("stasis_phase", stasis.Phase);
        Assert.Equal(extractionRow.ChunkAvailableMs * 10_000 + 116_444_736_000_000_000L, stasis.PhaseStartedFileTime);
        var arrivedChunk = await rig.SpaceItems.GetAsync(stasis.ChunkId);
        Assert.NotNull(arrivedChunk);
        Assert.False(chunk.Position.SequenceEqual(arrivedChunk.Position));

        await chunks.RestoreAsync(SolarSystemId);
        var restored = rig.Sim.Heard.OfType<SimUpsertMoonChunk>().Last();
        Assert.Equal(stasis.ChunkId, restored.ChunkId);
        Assert.Equal("stasis_phase", restored.Phase);

        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            Assert.Equal(MoonExtractions.FractureOutcome.Fired,
                await extractions.FractureAsync(connection, transaction, StructureId));
            await transaction.CommitAsync();
        }
        await rig.Events.PumpAsync();
        var fracture = rig.Sim.Heard.OfType<SimUpsertMoonChunk>().Last();
        Assert.Equal(stasis.ChunkId, fracture.ChunkId);
        Assert.Equal("fracture_phase", fracture.Phase);
        Assert.Equal(rig.Now * 10_000 + 116_444_736_000_000_000L, fracture.PhaseStartedFileTime);

        rig.Now += MoonExtractions.ChunkExplosionDelayMs;
        await rig.Scheduler.RunDueAsync();
        await rig.Events.PumpAsync();
        var explosion = Assert.Single(rig.Sim.Heard.OfType<SimRemoveMoonChunk>());
        Assert.Equal(stasis.ChunkId, explosion.ChunkId);
        Assert.Equal(3, explosion.Effect);
        Assert.Null(await rig.SpaceItems.GetAsync(stasis.ChunkId));
        Assert.Null(await rig.Items.GetAsync(stasis.ChunkId));
        Assert.Empty(await rig.SpaceItems.MoonFieldAsync(extractionRow.ExtractionId));
        await using var explodedConnection = await rig.Source.OpenConnectionAsync();
        var explodedExtraction = await extractionsStore.GetAsync(explodedConnection, null, extractionRow.ExtractionId, forUpdate: false);
        Assert.NotNull(explodedExtraction);
        Assert.Equal(rig.Now, explodedExtraction.ChunkExplodedMs);
        var upsertsAtExplosion = rig.Sim.Heard.OfType<SimUpsertMoonChunk>().Count();
        await chunks.RestoreAsync(SolarSystemId);
        Assert.Equal(upsertsAtExplosion, rig.Sim.Heard.OfType<SimUpsertMoonChunk>().Count());
        await using (var restartedScheduler = new Eve.Scheduler.DurableScheduler(
            rig.Source, () => rig.Now, new Eve.Scheduler.SchedulerOptions { Concurrency = 1 }, rig.SchedulerSchema, _ => { }))
        {
            var restartedExtractions = new MoonExtractions(structures, extractionsStore, restartedScheduler, rig.Events, rules);
            restartedExtractions.Register();
            await using var connection = await rig.Source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await restartedExtractions.EnsureScheduledAsync(connection, transaction);
            await transaction.CommitAsync();
            Assert.Null(await restartedScheduler.FindAsync(MoonExtractions.ChunkExplosionJobKind, $"extraction:{extractionRow.ExtractionId}"));
        }

        rig.Now += MoonExtractions.FractureMs - MoonExtractions.ChunkExplosionDelayMs;
        await rig.Scheduler.RunDueAsync();
        await rig.Events.PumpAsync();
        await rig.Events.PumpAsync();
        var field = await rig.SpaceItems.MoonFieldAsync(extractionRow.ExtractionId);
        Assert.NotEmpty(field);
        Assert.All(field, rock => Assert.True(rock.MoonRockOrdinal > 0));
    }

    [PgFact]
    public async Task A_persisted_extraction_without_a_drill_still_projects_its_chunk_without_a_drill_effect()
    {
        await using var rig = await Rig.CreateAsync();
        await StructureStore.MigrateAsync(rig.Source, rig.StructureSchema);
        var structures = new StructureStore(rig.Source, rig.StructureSchema);
        var extractionsStore = new MoonExtractionStore(rig.Source, rig.StructureSchema);
        var rules = new Rules();
        var extractions = new MoonExtractions(structures, extractionsStore, rig.Scheduler, rig.Events, rules);
        extractions.Register();
        var chunks = new SpaceMoonChunks(rig.Sim, rig.Space, rig.Source, structures, extractionsStore,
            rig.Items, rig.SpaceItems, rig.Statics, () => rig.Now, log: rig.Log.Add);
        chunks.Subscribe(rig.Events);
        await rig.Events.PumpAsync();

        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await structures.CreateAsync(connection, transaction,
                new NewStructure(StructureId, AthanorTypeId, SolarSystemId, 0, 0, 0, 0, CorporationId, 0, "Moon refinery", ""));
            await structures.SetServiceAsync(connection, transaction, StructureId, StructureServiceIds.MoonMining,
                ServiceState.Online, rig.Now, manageFuelCycle: false);
            await extractionsStore.ImportAsync(connection, transaction, new System.Text.Json.Nodes.JsonObject
            {
                ["resourcesByStructureID"] = new System.Text.Json.Nodes.JsonObject
                {
                    [StructureId.ToString()] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["moonID"] = MoonId,
                        ["solarSystemID"] = SolarSystemId,
                        ["ownerID"] = CorporationId,
                        ["resources"] = new System.Text.Json.Nodes.JsonObject { ["45490"] = 0.75, ["45494"] = 0.25 },
                    },
                },
            });
            await transaction.CommitAsync();
        }

        MoonExtraction started;
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            var result = await extractions.StartAsync(connection, transaction, StructureId, 518_460, false);
            Assert.Equal(MoonExtractions.StartOutcome.Started, result.Outcome);
            started = Assert.IsType<MoonExtraction>(result.Extraction);
            await transaction.CommitAsync();
        }

        var pump = await rig.Events.PumpAsync();
        Assert.Equal(1, pump.Delivered);
        Assert.Null(pump.RetryAtMs);
        var projection = Assert.Single(rig.Sim.Heard.OfType<SimUpsertMoonChunk>());
        Assert.Equal(0, projection.ModuleId);
        Assert.Equal(0, projection.ModuleTypeId);
        Assert.Empty(rig.Sim.Heard.OfType<SimSetStructureMoonMiningState>().Last().Modules);
        var chunk = Assert.Single(await rig.SpaceItems.InSystemAsync(SolarSystemId), item => item.Kind == SpaceItemKind.MoonChunk);
        Assert.Equal(started.ExtractionId, chunk.MoonExtractionId);
        Assert.Equal(chunk.ItemId, projection.ChunkId);
        Assert.DoesNotContain(rig.Log, message => message.Contains("no fitted Standup Moon Drill", StringComparison.Ordinal));
    }
}

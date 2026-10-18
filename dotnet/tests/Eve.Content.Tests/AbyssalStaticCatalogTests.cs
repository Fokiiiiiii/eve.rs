using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Eve.Content;
using Eve.Sde;

namespace Eve.Content.Tests;

public sealed class AbyssalStaticCatalogTests
{
    [Fact]
    public void Embedded_EveJS_data_contains_all_profiles_and_provenance()
    {
        var assembly = typeof(AbyssalStaticCatalog).Assembly;
        var resourceName = Assert.Single(assembly.GetManifestResourceNames(),
            x => x.EndsWith("AbyssalCatalogDataV1.json.gz.b64", StringComparison.Ordinal));
        using var resource = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(resource, Encoding.UTF8);
        var compressed = Convert.FromBase64String(reader.ReadToEnd().Trim());
        using var input = new MemoryStream(compressed, writable: false);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var data = JsonDocument.Parse(gzip);
        var root = data.RootElement;
        var profiles = root.GetProperty("profiles").EnumerateArray().ToArray();

        var source = root.GetProperty("source");
        Assert.Equal("7cc6f323c7c5c92c2738476a62768318799d8077", source.GetProperty("evejsCommit").GetString());
        Assert.Equal(3436472, source.GetProperty("authoredEnvironmentClientBuild").GetInt32());
        Assert.Equal(3396210, source.GetProperty("targetClientBuild").GetInt32());
        Assert.Equal(35, profiles.Length);
        Assert.Equal(640, profiles.Sum(x => x.GetProperty("spawnFamilies").GetArrayLength()));
        Assert.Equal(4590, profiles.Sum(x => x.GetProperty("spawnFamilies").EnumerateArray()
            .Sum(f => f.GetProperty("npcs").GetArrayLength())));
        Assert.Equal(8248, profiles.Sum(x => x.GetProperty("rewardOutcomes").GetArrayLength()));
        Assert.All(profiles, profile =>
        {
            Assert.Equal(3, profile.GetProperty("environment").GetProperty("rooms").GetArrayLength());
            Assert.All(profile.GetProperty("environment").GetProperty("rooms").EnumerateArray(), room =>
            {
                Assert.True(room.GetProperty("roomIndex").GetInt32() is >= 1 and <= 3);
                Assert.True(room.GetProperty("nebulaGraphicID").GetInt32() > 0);
                Assert.False(string.IsNullOrWhiteSpace(room.GetProperty("roomLayoutProfileID").GetString()));
            });
        });
    }


    [PgFact]
    public async Task Telemetry_weather_final_loot_modes_and_blueprint_copies_are_deterministic()
    {
        await using var rig = await Rig.Create();
        var catalog = await AbyssalStaticCatalog.LoadAsync(new SdeReader(rig.Source));
        var spaceId = catalog.AbyssalSystemsById.Keys.Min();
        var modes = new[] { AbyssalGameMode.SoloCruiser, AbyssalGameMode.ThreeFrigates, AbyssalGameMode.TwoDestroyers };
        var expectedFilaments = new[] { 1, 3, 2 };
        var expectedCacheIds = new[] { 47951, 49689, 49689 };
        foreach (var tier in new[] { 0, 1 })
        {
            var filament = catalog.FilamentsByTypeId.Values.Single(x => x.Tier == tier && x.WeatherType == "electrical");
            string? baselineLoot = null;
            for (var index = 0; index < modes.Length; index++)
            {
                Assert.Equal(expectedFilaments[index], catalog.RequiredFilamentsFor(modes[index]));
                Assert.Equal(expectedCacheIds[index], catalog.MainCacheTypeFor(modes[index]).TypeId);
                Assert.Equal(2009, catalog.MainCacheTypeFor(modes[index]).GroupId);
                var plan = catalog.CreatePlan(filament.TypeId, modes[index], 700001, RunId, spaceId);
                var loot = JsonSerializer.Serialize(plan.Rooms.Select(x => x.Artifacts.SelectMany(a => a.Loot)));
                baselineLoot ??= loot;
                // retail_final quantities bypass the hull multiplier in T0 as well.
                Assert.Equal(baselineLoot, loot);
            }
        }

        // T1 electrical has a captured stage-2 ball, but its live telemetry authority
        // bypasses that visual template and reaches both allowed runtime stages.
        foreach (var tier in new[] { 1, 4 })
        {
            var stages = new HashSet<int>();
            for (var index = 0; index < 128; index++)
            {
                var seed = $"weather-regression-{index}";
                var selected = catalog.ResolveWeather(tier, "electrical", seed);
                var retry = catalog.ResolveWeather(tier, "electrical", seed);
                Assert.Equal(JsonSerializer.Serialize(selected), JsonSerializer.Serialize(retry));
                Assert.Equal("deterministic-per-run-weighted", selected.SelectionModel);
                var definition = Assert.Single(catalog.WeatherStages, x => x.TypeId == selected.TypeId);
                Assert.Equal(selected.Stage, definition.Stage);
                Assert.Equal(selected.Effects, definition.Effects);
                stages.Add(selected.Stage);
            }
            Assert.Equal(tier <= 3 ? new[] { 1, 2 } : new[] { 2, 3 }, stages.Order());
        }

        // Exercise actual sampled blueprint rewards, not hand-authored synthetic plans.
        var foundCopies = false;
        AbyssalRunPlan? blueprintPlan = null;
        var highTier = catalog.FilamentsByTypeId.Values.Single(x => x.Tier == 6 && x.WeatherType == "electrical");
        for (var index = 0; index < 64 && !foundCopies; index++)
        {
            var seed = $"blueprint-regression-{index}";
            var plan = catalog.CreatePlan(highTier.TypeId, modes[0], 700001, seed, spaceId);
            foreach (var item in plan.Rooms.SelectMany(x => x.Artifacts).SelectMany(x => x.Loot))
            {
                Assert.Equal(catalog.StaticTypesById[item.TypeId].CategoryId == 9, item.IsBlueprintCopy);
                if (item.IsBlueprintCopy)
                {
                    foundCopies = true;
                    blueprintPlan = plan;
                    Assert.NotNull(item.BlueprintRunsPerCopy);
                    Assert.Equal(item.Count, item.BlueprintRunsPerCopy.Count);
                    Assert.All(item.BlueprintRunsPerCopy, runs => Assert.InRange(runs, 1, 5));
                }
                else
                {
                    Assert.Null(item.BlueprintRunsPerCopy);
                }
            }
            var retry = catalog.CreatePlan(highTier.TypeId, modes[0], 700001, seed, spaceId);
            Assert.Equal(JsonSerializer.Serialize(plan), JsonSerializer.Serialize(retry));
        }
        Assert.True(foundCopies, "The selected empirical corpus must exercise at least one blueprint copy.");
        await rig.Ready();
        var abyssal = new AbyssalContentRuntime(rig.Content, rig.Bus, rig.Scheduler);
        var request = new AbyssalStartRequest(
            700003, 1001, 2001, null, AbyssalGameMode.SoloCruiser, highTier.TypeId,
            30000142, 60003760, new AbyssalPosition(10, 20, 30), 2_000_000, blueprintPlan!);
        var started = await rig.Commit((connection, transaction) =>
            abyssal.StartAsync(connection, transaction, request));
        Assert.Equal(AbyssalOperationStatus.Applied, started.Status);
        Assert.Contains(started.State.Data.Plan.Rooms.SelectMany(x => x.Artifacts).SelectMany(x => x.Loot), x => x.IsBlueprintCopy);
    }

    [PgFact]
    public async Task Resolver_local_objects_and_SDE_effects_preserve_identity_filters_and_transforms()
    {
        await using var rig = await Rig.Create();
        var catalog = await AbyssalStaticCatalog.LoadAsync(new SdeReader(rig.Source));
        Assert.Equal(13, catalog.EnvironmentalTypesById.Count);
        var rooms = catalog.FilamentsByTypeId.Values.SelectMany(filament =>
            catalog.CreatePlan(filament.TypeId, AbyssalGameMode.SoloCruiser, 700001, RunId,
                catalog.AbyssalSystemsById.Keys.Min()).Rooms).ToArray();
        var populated = rooms.Where(room => catalog.GetEnvironmentObjects(room.EnvironmentProfileId)
            .Any(x => x.MechanicsEnabled && (x.EffectKind is "localized_modifier" or "point_defense"))).ToArray();
        Assert.Equal(7, populated.Length);
        var objects = populated.SelectMany(room => catalog.GetEnvironmentObjects(room.EnvironmentProfileId))
            .Where(x => x.MechanicsEnabled && (x.EffectKind is "localized_modifier" or "point_defense")).ToArray();
        Assert.Equal(7, objects.Length);
        Assert.Equal(7, objects.Select(x => x.Key).Distinct().Count());
        Assert.Equal(new[] { 47437, 47437, 47437, 47438, 47438, 47469, 47473 }, objects.Select(x => x.TypeId).Order());
        var captured = Assert.Single(objects, x => x.Key == "abyssal:tier0:dark:retail-captured-v1:room-1:props:2");
        Assert.Equal(new AbyssalPosition(-11925.94921875, 31574.6484375, 29643.697265625), captured.LocalPosition);
        Assert.Equal(new double[] { 0, 0, 0 }, captured.Rotation);
        Assert.Equal("suppressor", captured.Role);
        Assert.True(captured.Destructible);
        Assert.Equal("TQTranquilAbyssal5.txt", captured.SourceEvidence.GetProperty("capture").GetString());
        Assert.Equal("not_exposed_in_slim_item", captured.SourceEvidence.GetProperty("rotationEvidence").GetString());
        Assert.All(objects, x =>
        {
            Assert.Contains(x.TypeId, catalog.EnvironmentalTypesById.Keys);
            Assert.Equal(3, x.Rotation.Count);
            Assert.False(string.IsNullOrWhiteSpace(x.Key));
        });
        var filamentCloud = catalog.EnvironmentalTypesById[47473];
        Assert.Equal(40000, filamentCloud.RadiusMeters);
        Assert.Equal(new[] { 7058 }, filamentCloud.DogmaEffectIds);
        Assert.All(filamentCloud.Buffs, x =>
        {
            Assert.Equal(-30, x.Value);
            Assert.Equal("PostPercent", x.Operation);
            Assert.Equal("Maximum", x.AggregateMode);
            Assert.Contains(x.Modifiers, m => m.RequiredSkillId == 3416);
        });
        Assert.Equal(new[] { 88, 89 }, filamentCloud.Buffs.Select(x => x.CollectionId));
        var pylon = catalog.EnvironmentalTypesById[47469];
        var tracking = Assert.Single(pylon.Buffs);
        Assert.Equal(84, tracking.CollectionId);
        Assert.Equal(80, tracking.Value);
        Assert.Contains(tracking.Modifiers, m => m.RequiredSkillId == 3300 && m.AttributeId == 160);
        foreach (var typeId in new[] { 47437, 47438 })
        {
            var type = catalog.EnvironmentalTypesById[typeId];
            var defense = Assert.IsType<AbyssalPointDefenseDefinition>(type.PointDefense);
            Assert.Equal(2000, defense.TickIntervalMs);
            Assert.Equal(typeId == 47437 ? 15000 : 40000, type.RadiusMeters);
            Assert.Equal(typeId == 47437 ? new double[] { 0, 30, 0, 30 } : new double[] { 0, 10, 0, 10 }, defense.Damage);
            Assert.Equal(46, defense.Targets.TypeListId);
            Assert.Equal(new[] { 8, 18, 87 }, defense.Targets.IncludedCategories.Order());
            Assert.Equal(new[] { 1997 }, defense.Targets.IncludedGroups);
            Assert.Equal(new[] { 48262, 48263, 48264, 48265 }, defense.Targets.ExcludedTypes.Order());
            Assert.Equal("effects.TriGlavianBS01a", defense.EffectName);
        }
        Assert.Throws<KeyNotFoundException>(() => catalog.GetEnvironmentObjects("unknown-room"));
    }

    [PgFact]
    public async Task Procedural_main_and_side_match_executed_EveJS_vectors_for_every_profile_and_mode()
    {
        await using var rig = await Rig.Create();
        var catalog = await AbyssalStaticCatalog.LoadAsync(new SdeReader(rig.Source));
        using var data = ReadCatalogResource();
        var vectors = data.RootElement.GetProperty("proceduralReferenceVectors").EnumerateArray().ToArray();
        Assert.Equal(210, vectors.Length);
        Assert.Equal("abyssal:procedural-sde-reward-catalogue-v1", catalog.NewRunRewardModel);
        var emptyNodes = 0;
        var populatedNodes = 0;
        var copies = 0;
        foreach (var vector in vectors)
        {
            var tier = vector.GetProperty("tier").GetInt32();
            var weather = vector.GetProperty("weather").GetString()!;
            var mode = (AbyssalGameMode)vector.GetProperty("mode").GetInt32();
            var seed = vector.GetProperty("seed").GetString()!;
            var filament = catalog.FilamentsByTypeId.Values.Single(f => f.Tier == tier && f.WeatherType == weather);
            var plan = catalog.CreatePlan(filament.TypeId, mode, 700001, seed, catalog.AbyssalSystemsById.Keys.Min());
            Assert.Equal(JsonSerializer.Serialize(plan), JsonSerializer.Serialize(
                catalog.CreatePlan(filament.TypeId, mode, 700001, seed, plan.SpaceLocationId)));
            foreach (var expected in vector.GetProperty("rooms").EnumerateArray())
            {
                var room = plan.Rooms[expected.GetProperty("room").GetInt32() - 1];
                Assert.Empty(room.Loot);
                var main = Assert.Single(room.Artifacts, a => a.Kind == AbyssalArtifactKind.MainCache);
                Assert.Equal(catalog.MainCacheTypeFor(mode).TypeId, main.TypeId);
                Assert.Equal(new AbyssalPosition(35000, 10000, 0), main.LocalPosition);
                Assert.Equal("retail_final", main.QuantityPolicy);
                Assert.Equal(ExpectedLoot(expected.GetProperty("main")), ActualLoot(main.Loot));
                copies += main.Loot.Where(x => x.IsBlueprintCopy).Sum(x => x.Count);
                var expectedSide = expected.GetProperty("side");
                if (expectedSide.ValueKind == JsonValueKind.Null)
                {
                    Assert.DoesNotContain(room.Artifacts, a => a.Kind == AbyssalArtifactKind.ExtractionNode);
                }
                else
                {
                    var side = Assert.Single(room.Artifacts, a => a.Kind == AbyssalArtifactKind.ExtractionNode);
                    Assert.Equal(expected.GetProperty("sideTypeId").GetInt32(), side.TypeId);
                    var pos = expected.GetProperty("sidePosition");
                    Assert.Equal(new AbyssalPosition(pos.GetProperty("x").GetDouble(), pos.GetProperty("y").GetDouble(), pos.GetProperty("z").GetDouble()), side.LocalPosition);
                    Assert.Equal("pre_scaled", side.QuantityPolicy);
                    Assert.Equal(ExpectedLoot(expectedSide), ActualLoot(side.Loot));
                    if (side.Loot.Count == 0) emptyNodes++; else populatedNodes++;
                    Assert.All(side.Loot, loot => Assert.Contains(loot.TypeId, new[] { 48121, 48112, 47975 }));
                }
                var sourceObjects = catalog.GetEnvironmentObjects(room.EnvironmentProfileId);
                Assert.Equal(sourceObjects.Select(x => x.Key), room.EnvironmentObjects.Select(x => x.Key));
                Assert.Equal(sourceObjects.Select(x => x.LocalPosition), room.EnvironmentObjects.Select(x => x.LocalPosition));
                Assert.Equal(room.Artifacts.Count, room.Artifacts.Select(x => x.Key).Distinct().Count());
            }
        }
        Assert.True(emptyNodes > 0);
        Assert.True(populatedNodes > 0);
        Assert.True(copies > 0);

        static string[] ExpectedLoot(JsonElement entries) => entries.EnumerateArray().Select(e =>
            $"{e[0].GetInt32()}:{e[1].GetInt32()}:" +
            (e.GetArrayLength() > 2 && e[2].ValueKind == JsonValueKind.Array
                ? string.Join(",", e[2].EnumerateArray().Select(r => r.GetInt32())) : ""))
            .Order(StringComparer.Ordinal).ToArray();
        static string[] ActualLoot(IReadOnlyList<AbyssalLootPlan> entries) => entries.Select(e =>
            $"{e.TypeId}:{e.Count}:" + (e.BlueprintRunsPerCopy is null ? "" : string.Join(",", e.BlueprintRunsPerCopy)))
            .Order(StringComparer.Ordinal).ToArray();
    }

    private static JsonDocument ReadCatalogResource()
    {
        var assembly = typeof(AbyssalStaticCatalog).Assembly;
        var name = assembly.GetManifestResourceNames().Single(x => x.EndsWith("AbyssalCatalogDataV1.json.gz.b64", StringComparison.Ordinal));
        using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!, Encoding.UTF8);
        using var input = new MemoryStream(Convert.FromBase64String(reader.ReadToEnd().Trim()));
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        return JsonDocument.Parse(gzip);
    }

    [PgFact]
    public async Task Presentation_objects_and_weather_are_frozen_from_resolver_output()
    {
        await using var rig = await Rig.Create();
        var catalog = await AbyssalStaticCatalog.LoadAsync(new SdeReader(rig.Source));
        var roomCount = 0;
        var objectCount = 0;
        var localMechanics = 0;
        foreach (var filament in catalog.FilamentsByTypeId.Values)
        {
            foreach (var seed in new[] { RunId, "presentation-weather-regression" })
            {
                var selection = catalog.ResolveWeather(filament.Tier, filament.WeatherType, seed);
                var plan = catalog.CreatePlan(filament.TypeId, AbyssalGameMode.SoloCruiser, 700001, seed,
                    catalog.AbyssalSystemsById.Keys.Min());
                foreach (var room in plan.Rooms)
                {
                    var source = catalog.GetEnvironmentObjects(room.EnvironmentProfileId);
                    Assert.Equal(source.Count, room.EnvironmentObjects.Count);
                    var weather = Assert.Single(room.EnvironmentObjects, x => x.Role == "weather_object");
                    Assert.Equal(selection.TypeId, weather.TypeId);
                    Assert.False(weather.MechanicsEnabled);
                    Assert.Contains("weather-selection-seed:" + seed, weather.SourceAuthority);
                    foreach (var original in source)
                    {
                        var saved = Assert.Single(room.EnvironmentObjects, x => x.Key == original.Key);
                        Assert.Equal(original.Role == "weather_object" ? selection.TypeId : original.TypeId, saved.TypeId);
                        Assert.Equal(original.LocalPosition, saved.LocalPosition);
                        Assert.Equal(new AbyssalRotation(original.Rotation[0], original.Rotation[1], original.Rotation[2]), saved.Rotation);
                        Assert.Equal(original.MechanicsEnabled, saved.MechanicsEnabled);
                        Assert.Equal(original.PresentationRadiusMeters, saved.PresentationRadiusMeters);
                        Assert.Contains(saved.TypeId, catalog.StaticTypesById.Keys);
                        if (original.EffectKind is "decorative" or "weather_presentation")
                            Assert.False(saved.MechanicsEnabled);
                        if (original.TypeId == 54196)
                        {
                            Assert.Equal(new AbyssalPosition(35000, 0, 0), saved.LocalPosition);
                            Assert.Equal(7500d, original.PresentationRadiusMeters);
                        }
                        if (original.TypeId == 47465)
                            Assert.Equal(75000d, original.PresentationRadiusMeters);
                    }
                    if (seed == RunId)
                    {
                        roomCount++;
                        objectCount += room.EnvironmentObjects.Count;
                        localMechanics += source.Count(x => x.MechanicsEnabled && (x.EffectKind is "localized_modifier" or "point_defense"));
                    }
                }
                var retry = catalog.CreatePlan(filament.TypeId, AbyssalGameMode.SoloCruiser, 700001, seed, plan.SpaceLocationId);
                Assert.Equal(JsonSerializer.Serialize(plan), JsonSerializer.Serialize(retry));
            }
        }
        Assert.Equal(105, roomCount);
        Assert.Equal(477, objectCount);
        Assert.Equal(7, localMechanics);
    }

    [Fact]
    public void Legacy_environment_object_without_presentation_radius_keeps_null()
    {
        var legacyJson = JsonSerializer.Serialize(new
        {
            Key = "legacy-environment", TypeId = 47465,
            LocalPosition = new AbyssalPosition(0, 0, 0), Rotation = new AbyssalRotation(0, 0, 0),
            Role = "depth_boundary_object", SourceAuthority = "legacy-source",
        });
        var legacy = JsonSerializer.Deserialize<AbyssalEnvironmentObjectPlan>(legacyJson)!;
        Assert.Null(legacy.PresentationRadiusMeters);
        var replay = JsonSerializer.Deserialize<AbyssalEnvironmentObjectPlan>(JsonSerializer.Serialize(legacy))!;
        Assert.Null(replay.PresentationRadiusMeters);
        var explicitRadius = legacy with { PresentationRadiusMeters = 75000d };
        var explicitReplay = JsonSerializer.Deserialize<AbyssalEnvironmentObjectPlan>(JsonSerializer.Serialize(explicitRadius))!;
        Assert.Equal(75000d, explicitReplay.PresentationRadiusMeters);
    }

    private const string RunId = "f9c997ed-9fc0-4ea5-94b5-3e2771d9a713";

    [PgFact]
    public async Task SDE_and_EveJS_sources_resolve_all_filaments_modes_rooms_weather_encounters_and_rewards()
    {
        await using var rig = await Rig.Create();
        var catalog = await AbyssalStaticCatalog.LoadAsync(new SdeReader(rig.Source));
        var abyssal = new AbyssalContentRuntime(rig.Content, rig.Bus, rig.Scheduler);
        await rig.Ready();

        Assert.Equal(AbyssalStaticCatalog.RequiredSdeBuild, catalog.SdeBuildNumber);
        Assert.Equal(3436472, catalog.EnvironmentSourceClientBuild);
        Assert.Equal(3396210, catalog.TargetClientBuild);
        Assert.Equal(35, catalog.FilamentsByTypeId.Count);
        Assert.Equal(200, catalog.AbyssalSystemsById.Count);
        Assert.Equal(101, catalog.NpcTypesByTypeId.Count);
        Assert.All(catalog.NpcTypesByTypeId.Values, npc => Assert.True(npc.HasHitPoints));
        Assert.Equal(15, catalog.WeatherStages.Count);
        Assert.All(catalog.WeatherStages, stage => Assert.Single(stage.DogmaEffectIds));
        Assert.Equal(13, catalog.EnvironmentGraphicsById.Count);
        Assert.All(catalog.EnvironmentGraphicsById.Values, graphic => Assert.False(string.IsNullOrWhiteSpace(graphic.GraphicFile)));

        Assert.Equal(
            new[] { 26, 833, 906, 358, 894, 832 },
            catalog.AllowedShipGroupIdsByMode[AbyssalGameMode.SoloCruiser]);
        Assert.Equal(
            new[] { 324, 25, 831, 1527, 830, 893, 834, 1283 },
            catalog.AllowedShipGroupIdsByMode[AbyssalGameMode.ThreeFrigates]);
        Assert.Equal(
            new[] { 420, 1305, 541, 1534 },
            catalog.AllowedShipGroupIdsByMode[AbyssalGameMode.TwoDestroyers]);
        Assert.Equal(4, (int)catalog.ContentTypeFor(AbyssalGameMode.TwoDestroyers));
        Assert.NotEmpty(catalog.AllowedShipTypeIdsByMode[AbyssalGameMode.SoloCruiser]);
        Assert.NotEmpty(catalog.AllowedShipTypeIdsByMode[AbyssalGameMode.ThreeFrigates]);
        Assert.NotEmpty(catalog.AllowedShipTypeIdsByMode[AbyssalGameMode.TwoDestroyers]);

        var spaceLocationId = catalog.AbyssalSystemsById.Keys.Order().First();
        var modes = new[]
        {
            AbyssalGameMode.SoloCruiser,
            AbyssalGameMode.ThreeFrigates,
            AbyssalGameMode.TwoDestroyers,
        };
        foreach (var filament in catalog.FilamentsByTypeId.Values.OrderBy(x => x.Tier).ThenBy(x => x.WeatherType, StringComparer.Ordinal))
        {
            foreach (var mode in modes)
            {
                var plan = catalog.CreatePlan(filament.TypeId, mode, 700001, RunId, spaceLocationId);
                Assert.Equal(filament.Tier, plan.Tier);
                Assert.Equal(filament.WeatherType, plan.WeatherType);
                Assert.Equal(mode switch
                {
                    AbyssalGameMode.SoloCruiser => 1,
                    AbyssalGameMode.ThreeFrigates => 3,
                    AbyssalGameMode.TwoDestroyers => 2,
                    _ => throw new InvalidOperationException(),
                }, plan.MaximumParticipants);
                Assert.Equal(new[] { 1, 2, 3 }, plan.Rooms.Select(x => x.LocationId));
                Assert.Equal(spaceLocationId, plan.SpaceLocationId);
                Assert.Equal(3, plan.Rooms.Count);
                for (var roomIndex = 0; roomIndex < plan.Rooms.Count; roomIndex++)
                {
                    var room = plan.Rooms[roomIndex];
                    Assert.NotEmpty(room.NpcSpawns);
                    Assert.All(room.NpcSpawns, spawn =>
                    {
                        Assert.True(spawn.TypeId > 0);
                        Assert.True(spawn.Count > 0);
                        Assert.Contains(spawn.TypeId, catalog.NpcTypesByTypeId.Keys);
                    });
                    Assert.Single(room.Objectives);
                    Assert.Equal(
                        room.NpcSpawns.Select(x => x.TypeId).Distinct().Order(),
                        room.Objectives[0].NpcTypeIds.Order());
                    Assert.NotNull(room.NebulaGraphicId);
                    var environment = catalog.GetEnvironmentRoom(room.EnvironmentProfileId);
                    Assert.Equal(roomIndex + 1, environment.RoomIndex);
                    Assert.Equal(room.NebulaGraphicId, environment.NebulaGraphicId);
                    Assert.False(string.IsNullOrWhiteSpace(environment.ContentAuthority));
                    Assert.All(environment.SdeTypeIds, typeId => Assert.Contains(typeId, catalog.StaticTypesById.Keys));
                    Assert.All(environment.SdeGraphicIds, graphicId => Assert.Contains(graphicId, catalog.EnvironmentGraphicsById.Keys));
                }
                Assert.True(plan.Rooms.SelectMany(x => x.Artifacts).SelectMany(x => x.Loot).Sum(x => x.Count) > 0);
                Assert.All(plan.Rooms.SelectMany(x => x.Artifacts).SelectMany(x => x.Loot), loot =>
                {
                    Assert.True(loot.TypeId > 0);
                    Assert.True(loot.Count > 0);
                });

                var retry = catalog.CreatePlan(filament.TypeId, mode, 700001, RunId, spaceLocationId);
                Assert.Equal(JsonSerializer.Serialize(plan), JsonSerializer.Serialize(retry));
            }

            for (var tier = 0; tier <= 6; tier++)
            {
                var selection = catalog.ResolveWeather(tier, filament.WeatherType, RunId);
                Assert.True(selection.TypeId > 0);
                Assert.Equal(filament.WeatherType, selection.WeatherType);
                Assert.Contains(selection.Stage, tier <= 3 ? new[] { 1, 2 } : new[] { 2, 3 });
                Assert.Equal(2, selection.Effects.Count);
                Assert.True(selection.PenaltyPercent > 0);
                Assert.True(selection.BonusPercent > 0);
                Assert.Equal("deterministic-per-run-weighted", selection.SelectionModel);
            }
        }

        var startFilament = catalog.FilamentsByTypeId.Values.OrderBy(x => x.TypeId).First();
        var startPlan = catalog.CreatePlan(startFilament.TypeId, AbyssalGameMode.ThreeFrigates, 700002, RunId, spaceLocationId);
        var request = new AbyssalStartRequest(
            700002,
            1001,
            2001,
            3001,
            AbyssalGameMode.ThreeFrigates,
            startFilament.TypeId,
            30000142,
            60003760,
            new AbyssalPosition(10, 20, 30),
            2_000_000,
            startPlan);
        var started = await rig.Commit((connection, transaction) =>
            abyssal.StartAsync(connection, transaction, request));
        Assert.Equal(AbyssalOperationStatus.Applied, started.Status);
        Assert.Equal(spaceLocationId, started.State.Data.Plan.SpaceLocationId);
        Assert.Equal(new[] { 1, 2, 3 }, started.State.Data.Rooms.Select(x => x.LocationId));
    }
}

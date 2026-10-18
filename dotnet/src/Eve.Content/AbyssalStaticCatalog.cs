using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Eve.Sde;

namespace Eve.Content;

/// <summary>A published Abyssal filament resolved from the pinned SDE.</summary>
public sealed record AbyssalFilamentStaticDefinition(int TypeId, int Tier, string WeatherType, string Name);

/// <summary>An Abyssal-region solar system and its SDE region association.</summary>
public sealed record AbyssalSystemStaticDefinition(int SolarSystemId, int RegionId, string RegionName, int? RegionNebulaId);

/// <summary>An SDE type resolved for an Abyssal visual or content reference.</summary>
public sealed record AbyssalSdeTypeDefinition(int TypeId, int GroupId, string Name, bool Published)
{
    public int CategoryId { get; init; }
}

/// <summary>An SDE graphic resolved for an Abyssal nebula graphic reference.</summary>
public sealed record AbyssalSdeGraphicDefinition(int GraphicId, string GraphicFile, JsonElement Data);

/// <summary>SDE hull identity and the HP dogma attributes consumed by NPC materialization.</summary>
public sealed record AbyssalNpcStaticType(int TypeId, int GroupId, string Name, double? ShieldHp, double? ArmorHp, double? StructureHp)
{
    public bool HasHitPoints => Positive(ShieldHp) || Positive(ArmorHp) || Positive(StructureHp);

    private static bool Positive(double? value) => value is > 0;
}

/// <summary>One SDE dbuff collection/value pair used by a weather stage.</summary>
public sealed record AbyssalWeatherEffect(int CollectionId, double Value, string DisplayName, string OperationName, string ShowOutputValueInUi);

/// <summary>One weather stage from SDE typeDogma and dbuffCollections.</summary>
public sealed record AbyssalWeatherTypeDefinition(int TypeId, string WeatherType, int Stage, string Name, IReadOnlyList<int> DogmaEffectIds, int PenaltyPercent, int BonusPercent, IReadOnlyList<AbyssalWeatherEffect> Effects);

/// <summary>A deterministic per-run selection of a weather stage.</summary>
public sealed record AbyssalWeatherStageSelection(
    int Tier,
    string WeatherType,
    int Stage,
    int TypeId,
    string Name,
    int PenaltyPercent,
    int BonusPercent,
    string SelectionModel,
    uint SelectionSeed,
    double SelectionRoll,
    IReadOnlyList<double> SelectionWeights,
    IReadOnlyList<AbyssalWeatherEffect> Effects);

/// <summary>Per-room client environment data from the EveJS resolver, keyed by its room layout profile ID.</summary>
public sealed record AbyssalEnvironmentRoomDefinition(
    string EnvironmentProfileId,
    string ProfileId,
    string ContentAuthority,
    JsonElement Evidence,
    int RoomIndex,
    int NebulaGraphicId,
    JsonElement Data,
    IReadOnlyList<int> SdeTypeIds,
    IReadOnlyList<int> SdeGraphicIds);

/// <summary>A scoped modifier copied from the SDE dbuff collection, including fitted-module skill filters.</summary>
public sealed record AbyssalLocalBuffModifier(string Scope, int AttributeId, int? RequiredSkillId, int? GroupId);
public sealed record AbyssalLocalBuffDefinition(int CollectionId, double Value, string Operation, string AggregateMode, IReadOnlyList<AbyssalLocalBuffModifier> Modifiers);
public sealed record AbyssalTargetTypeListDefinition(int TypeListId, IReadOnlyList<int> IncludedCategories, IReadOnlyList<int> IncludedGroups, IReadOnlyList<int> IncludedTypes, IReadOnlyList<int> ExcludedCategories, IReadOnlyList<int> ExcludedGroups, IReadOnlyList<int> ExcludedTypes);
/// <summary>Damage channel order: EM, thermal, kinetic, explosive. No player module or ammunition is implied.</summary>
public sealed record AbyssalPointDefenseDefinition(long TickIntervalMs, IReadOnlyList<double> Damage, AbyssalTargetTypeListDefinition Targets, string EffectName);
public sealed record AbyssalEnvironmentalTypeDefinition(int TypeId, string Name, double RadiusMeters, IReadOnlyList<int> DogmaEffectIds, IReadOnlyList<AbyssalLocalBuffDefinition> Buffs, AbyssalPointDefenseDefinition? PointDefense);
/// <summary>Room-local resolver output. Key is stable within the saved environment profile.</summary>
public sealed record AbyssalEnvironmentalObjectDefinition(string Key, string ProfileId, int TypeId, AbyssalPosition LocalPosition, IReadOnlyList<double> Rotation, string Role, string EffectKind, bool Destructible, string Evidence, JsonElement SourceEvidence)
{
    public bool MechanicsEnabled { get; init; }
    public double? PresentationRadiusMeters { get; init; }
}

/// <summary>Pinned SDE identities, registered environmental placements and executable EveJS reward policies.</summary>
public sealed class AbyssalStaticCatalog
{
    public const long RequiredSdeBuild = 3396210;

    private static readonly int[] EnvironmentalTypeIds = [47436, 47437, 47438, 47439, 47440, 47441, 47467, 47468, 47469, 47470, 47472, 47473, 47620];
    public IReadOnlyDictionary<int, AbyssalEnvironmentalTypeDefinition> EnvironmentalTypesById { get; private init; } = null!;
    private IReadOnlyDictionary<string, IReadOnlyList<AbyssalEnvironmentalObjectDefinition>> EnvironmentObjectsByProfileId { get; init; } = null!;

    public IReadOnlyList<AbyssalEnvironmentalObjectDefinition> GetEnvironmentObjects(string environmentProfileId) =>
        EnvironmentObjectsByProfileId.TryGetValue(environmentProfileId, out var objects)
            ? objects
            : throw new KeyNotFoundException($"Unknown Abyssal environment room profile '{environmentProfileId}'.");

    private IReadOnlyDictionary<string, JsonElement> ProceduralRewardProfiles { get; init; } = null!;
    public string NewRunRewardModel => "abyssal:procedural-sde-reward-catalogue-v1";

    private const int FilamentGroupId = 1979;
    private const int WeatherTypeIdFirst = 47378;
    private const int WeatherTypeIdCount = 15;
    private const string SpawnCatalogueId = "abyssal-spawns:qsna-abyssal-space-observed-v1";

    private static readonly ModeContract[] Modes =
    [
        new(AbyssalGameMode.SoloCruiser, AbyssalContentType.SoloCruiser, 57, 1),
        new(AbyssalGameMode.ThreeFrigates, AbyssalContentType.FrigateFleet, 92, 3),
        new(AbyssalGameMode.TwoDestroyers, AbyssalContentType.TwoPlayerDestroyers, 233, 2),
    ];

    private readonly IReadOnlyDictionary<string, AbyssalPlanProfile> _profiles;
    private readonly IReadOnlyDictionary<(string Weather, int Stage), AbyssalWeatherTypeDefinition> _weatherStages;
    private readonly IReadOnlyDictionary<string, AbyssalEnvironmentRoomDefinition> _environmentRooms;
    private readonly IReadOnlyDictionary<AbyssalGameMode, IReadOnlySet<int>> _allowedShipTypes;
    private readonly IReadOnlyDictionary<AbyssalGameMode, IReadOnlyList<int>> _allowedShipGroups;
    private readonly IReadOnlyDictionary<int, double[]> _weatherWeights;
    private readonly IReadOnlyDictionary<int, int[]> _weatherStagesByTier;

    private AbyssalStaticCatalog(
        long sdeBuildNumber,
        int environmentSourceClientBuild,
        int targetClientBuild,
        IReadOnlyDictionary<int, AbyssalFilamentStaticDefinition> filaments,
        IReadOnlyDictionary<int, AbyssalSystemStaticDefinition> systems,
        IReadOnlyDictionary<int, AbyssalNpcStaticType> npcTypes,
        IReadOnlyDictionary<int, AbyssalSdeTypeDefinition> staticTypes,
        IReadOnlyDictionary<int, AbyssalSdeGraphicDefinition> environmentGraphics,
        IReadOnlyDictionary<(string Weather, int Stage), AbyssalWeatherTypeDefinition> weatherStages,
        IReadOnlyDictionary<string, AbyssalEnvironmentRoomDefinition> environmentRooms,
        IReadOnlyDictionary<string, AbyssalPlanProfile> profiles,
        IReadOnlyDictionary<AbyssalGameMode, IReadOnlySet<int>> allowedShipTypes,
        IReadOnlyDictionary<AbyssalGameMode, IReadOnlyList<int>> allowedShipGroups,
        IReadOnlyDictionary<int, double[]> weatherWeights,
        IReadOnlyDictionary<int, int[]> weatherStagesByTier)
    {
        SdeBuildNumber = sdeBuildNumber;
        EnvironmentSourceClientBuild = environmentSourceClientBuild;
        TargetClientBuild = targetClientBuild;
        FilamentsByTypeId = filaments;
        AbyssalSystemsById = systems;
        NpcTypesByTypeId = npcTypes;
        StaticTypesById = staticTypes;
        EnvironmentGraphicsById = environmentGraphics;
        _weatherStages = weatherStages;
        _environmentRooms = environmentRooms;
        _profiles = profiles;
        _allowedShipTypes = allowedShipTypes;
        _allowedShipGroups = allowedShipGroups;
        _weatherWeights = weatherWeights;
        _weatherStagesByTier = weatherStagesByTier;
        WeatherStages = Array.AsReadOnly(weatherStages.Values.OrderBy(x => x.WeatherType, StringComparer.Ordinal).ThenBy(x => x.Stage).ToArray());
        AllowedShipGroupIdsByMode = allowedShipGroups;
        AllowedShipTypeIdsByMode = allowedShipTypes;
    }

    public long SdeBuildNumber { get; }

    public int EnvironmentSourceClientBuild { get; }

    public int TargetClientBuild { get; }

    public IReadOnlyDictionary<int, AbyssalFilamentStaticDefinition> FilamentsByTypeId { get; }

    public IReadOnlyDictionary<int, AbyssalSystemStaticDefinition> AbyssalSystemsById { get; }

    public IReadOnlyDictionary<int, AbyssalNpcStaticType> NpcTypesByTypeId { get; }

    public IReadOnlyDictionary<int, AbyssalSdeTypeDefinition> StaticTypesById { get; }

    public IReadOnlyDictionary<int, AbyssalSdeGraphicDefinition> EnvironmentGraphicsById { get; }

    public IReadOnlyList<AbyssalWeatherTypeDefinition> WeatherStages { get; }

    public IReadOnlyDictionary<AbyssalGameMode, IReadOnlyList<int>> AllowedShipGroupIdsByMode { get; }

    public IReadOnlyDictionary<AbyssalGameMode, IReadOnlySet<int>> AllowedShipTypeIdsByMode { get; }

    /// <summary>Loads the matching SDE data and the pinned, embedded EveJS catalog resource.</summary>
    public static async Task<AbyssalStaticCatalog> LoadAsync(SdeReader sde, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sde);

        var build = await sde.BuildAsync(cancellationToken);
        if (build != RequiredSdeBuild)
        {
            throw new InvalidDataException($"Abyssal catalog requires SDE build {RequiredSdeBuild}; loaded build was {build?.ToString(CultureInfo.InvariantCulture) ?? "none"}.");
        }

        using var embedded = ReadEmbeddedData();
        var root = embedded.RootElement;
        if (ReadRequiredInt(root, "schemaVersion") != 1)
        {
            throw new InvalidDataException("Unsupported embedded Abyssal catalog schema.");
        }

        var source = root.GetProperty("source");
        var environmentSourceClientBuild = ReadRequiredInt(source, "authoredEnvironmentClientBuild");
        var targetClientBuild = ReadRequiredInt(source, "targetClientBuild");
        if (targetClientBuild != RequiredSdeBuild)
        {
            throw new InvalidDataException($"Embedded environment target build {targetClientBuild} does not match SDE build {RequiredSdeBuild}.");
        }
        if (ReadRequiredInt(source, "proceduralSdeBuild") != RequiredSdeBuild)
            throw new InvalidDataException("Procedural inputs require the matching SDE build.");
        var proceduralProfiles = root.GetProperty("proceduralProfiles").EnumerateArray().ToDictionary(
            p => ProfileKey(ReadRequiredInt(p, "tier"), ReadRequiredString(p, "weather")), p => p.Clone());
        if (proceduralProfiles.Count != 35) throw new InvalidDataException("Procedural rewards must cover 35 profiles.");
        var proceduralInputs = root.GetProperty("proceduralSdeInputs").EnumerateArray().ToArray();
        var rawProfiles = ReadProfiles(root.GetProperty("profiles"));
        if (rawProfiles.Count != 35
            || rawProfiles.Sum(x => x.SpawnFamilies.Count) != 640
            || rawProfiles.Sum(x => x.RewardOutcomes.Count) != 8248)
        {
            throw new InvalidDataException("Embedded EveJS Abyssal catalog data is incomplete.");
        }

        var filamentRows = await sde.ReadByFieldAsync("types", "groupID", [FilamentGroupId], cancellationToken);
        var filaments = new Dictionary<int, AbyssalFilamentStaticDefinition>();
        foreach (var row in filamentRows)
        {
            if (!IsPublished(row.Doc) || !TryParseFilament(SdeReader.English(GetProperty(row.Doc, "name")), out var tier, out var weather))
            {
                continue;
            }

            filaments.Add(checked((int)row.Key), new AbyssalFilamentStaticDefinition(checked((int)row.Key), tier, weather, SdeReader.English(GetProperty(row.Doc, "name"))));
        }
        if (filaments.Count != 35)
        {
            throw new InvalidDataException($"Expected 35 published Abyssal filaments in SDE group {FilamentGroupId}; found {filaments.Count}.");
        }

        var modeListIds = Modes.Select(x => (long)x.TypeListId).ToArray();
        var typeListRows = await sde.ReadAsync("typeLists", modeListIds, cancellationToken);
        var typeLists = typeListRows.ToDictionary(x => checked((int)x.Key), x => ReadIntArray(x.Doc, "includedGroupIDs"));
        var groupsByMode = new Dictionary<AbyssalGameMode, IReadOnlyList<int>>();
        foreach (var mode in Modes)
        {
            if (!typeLists.TryGetValue(mode.TypeListId, out var groups) || groups.Length == 0)
            {
                throw new InvalidDataException($"SDE Abyssal ship type list {mode.TypeListId} is missing or empty.");
            }
            groupsByMode.Add(mode.Mode, Array.AsReadOnly(groups.Distinct().ToArray()));
        }

        var modeGroupIds = groupsByMode.Values.SelectMany(x => x).Distinct().Select(x => (long)x).ToArray();
        var allowedShipRows = await sde.ReadByFieldAsync("types", "groupID", modeGroupIds, cancellationToken);
        var publishedShipTypesByGroup = allowedShipRows
            .Where(x => IsPublished(x.Doc))
            .GroupBy(x => ReadRequiredInt(x.Doc, "groupID"))
            .ToDictionary(g => g.Key, g => g.Select(x => checked((int)x.Key)).ToHashSet());
        var allowedShipTypes = new Dictionary<AbyssalGameMode, IReadOnlySet<int>>();
        foreach (var mode in Modes)
        {
            var ships = new HashSet<int>();
            foreach (var groupId in groupsByMode[mode.Mode])
            {
                if (publishedShipTypesByGroup.TryGetValue(groupId, out var types))
                {
                    ships.UnionWith(types);
                }
            }
            if (ships.Count == 0)
            {
                throw new InvalidDataException($"SDE Abyssal ship type list {mode.TypeListId} resolved no published hull types.");
            }
            allowedShipTypes.Add(mode.Mode, ships);
        }

        var systems = await LoadAbyssalSystemsAsync(sde, cancellationToken);

        var npcIds = rawProfiles
            .SelectMany(x => x.SpawnFamilies)
            .SelectMany(x => x.Npcs)
            .Select(x => x.TypeId)
            .Distinct()
            .ToArray();
        var rewardIds = rawProfiles
            .SelectMany(x => x.RewardOutcomes)
            .SelectMany(x => x.Entries)
            .Select(x => x.TypeId)
            .Distinct()
            .ToArray();
        var environmentTypeIds = rawProfiles
            .SelectMany(x => x.EnvironmentRooms)
            .SelectMany(x => x.SdeTypeIds)
            .Distinct()
            .ToArray();
        var environmentGraphicIds = rawProfiles
            .SelectMany(x => x.EnvironmentRooms)
            .SelectMany(x => x.SdeGraphicIds)
            .Distinct()
            .ToArray();

        // The pinned 3396210 SDE stores the 15 Abyssal weather stages at 47378-47392
        // across weather groups 1975 and 1882; source names supply weather and stage.
        var weatherIds = Enumerable.Range(WeatherTypeIdFirst, WeatherTypeIdCount).Select(x => (long)x).ToArray();
        var staticTypeIds = npcIds.Select(x => (long)x)
            .Concat(rewardIds.Select(x => (long)x))
            .Concat(weatherIds)
            .Concat(EnvironmentalTypeIds.Select(x => (long)x))
            .Concat(proceduralInputs.Select(x => (long)ReadRequiredInt(x, "typeID")))
            .Concat(new long[] { 47951, 49689 }) // EveJS main cache families, SDE group 2009.
            .Concat(environmentTypeIds.Select(x => (long)x))
            .Distinct()
            .ToArray();
        var staticTypeRows = await sde.ReadAsync("types", staticTypeIds, cancellationToken);
        var typeRowsById = staticTypeRows.ToDictionary(x => checked((int)x.Key));
        var missingTypeIds = staticTypeIds.Select(x => checked((int)x)).Where(x => !typeRowsById.ContainsKey(x)).ToArray();
        if (missingTypeIds.Length > 0)
        {
            throw new InvalidDataException($"SDE is missing catalog type IDs: {string.Join(",", missingTypeIds)}.");
        }
        var staticGroupRows = await sde.ReadAsync("groups",
            typeRowsById.Values.Select(x => (long)ReadRequiredInt(x.Doc, "groupID")).Distinct(), cancellationToken);
        var categoryByGroup = staticGroupRows.ToDictionary(
            x => checked((int)x.Key), x => ReadRequiredInt(x.Doc, "categoryID"));
        var staticTypes = typeRowsById.ToDictionary(
            x => x.Key,
            x => new AbyssalSdeTypeDefinition(
                x.Key,
                ReadRequiredInt(x.Value.Doc, "groupID"),
                SdeReader.English(GetProperty(x.Value.Doc, "name")),
                IsPublished(x.Value.Doc))
            {
                CategoryId = categoryByGroup[ReadRequiredInt(x.Value.Doc, "groupID")],
            });
        foreach (var input in proceduralInputs)
        {
            var id = ReadRequiredInt(input, "typeID");
            var actual = staticTypes[id];
            if (actual.Name != ReadRequiredString(input, "name") ||
                actual.GroupId != ReadRequiredInt(input, "groupID") ||
                actual.CategoryId != ReadRequiredInt(input, "categoryID") ||
                actual.Published != input.GetProperty("published").GetBoolean())
                throw new InvalidDataException($"Procedural SDE input {id} does not match build {RequiredSdeBuild}.");
        }
        var graphicsRows = await sde.ReadAsync("graphics", environmentGraphicIds.Select(x => (long)x), cancellationToken);
        var graphicsById = graphicsRows.ToDictionary(x => checked((int)x.Key));
        var missingGraphics = environmentGraphicIds.Where(x => !graphicsById.ContainsKey(x)).ToArray();
        if (missingGraphics.Length > 0)
        {
            throw new InvalidDataException($"SDE 3396210 graphics are missing environment IDs: {string.Join(",", missingGraphics)}.");
        }
        var environmentGraphics = graphicsById.ToDictionary(
            x => x.Key,
            x => new AbyssalSdeGraphicDefinition(
                x.Key,
                ReadRequiredString(x.Value.Doc, "graphicFile"),
                x.Value.Doc.Clone()));

        var dogmaIds = npcIds.Select(x => (long)x).Concat(weatherIds).Distinct().ToArray();
        var dogmaRows = await sde.ReadAsync("typeDogma", dogmaIds, cancellationToken);
        var dogmaByTypeId = dogmaRows.ToDictionary(x => checked((int)x.Key));
        var missingNpcDogma = npcIds.Where(x => !dogmaByTypeId.ContainsKey(x)).ToArray();
        if (missingNpcDogma.Length > 0)
        {
            throw new InvalidDataException($"SDE is missing typeDogma for Abyssal NPC types: {string.Join(",", missingNpcDogma)}.");
        }

        var npcTypes = new Dictionary<int, AbyssalNpcStaticType>();
        foreach (var typeId in npcIds)
        {
            var type = typeRowsById[typeId].Doc;
            var attributes = ReadDogmaAttributes(dogmaByTypeId[typeId].Doc);
            var npc = new AbyssalNpcStaticType(
                typeId,
                ReadRequiredInt(type, "groupID"),
                SdeReader.English(GetProperty(type, "name")),
                Attribute(attributes, 263),
                Attribute(attributes, 265),
                Attribute(attributes, 9));
            if (!npc.HasHitPoints)
            {
                throw new InvalidDataException($"SDE NPC type {typeId} has no positive shield, armor or structure HP.");
            }
            npcTypes.Add(typeId, npc);
        }

        var weatherDogmaRows = weatherIds.Select(x => checked((int)x)).Where(dogmaByTypeId.ContainsKey).ToArray();
        if (weatherDogmaRows.Length != WeatherTypeIdCount)
        {
            throw new InvalidDataException($"SDE typeDogma did not resolve all {WeatherTypeIdCount} Abyssal weather stages.");
        }
        var weatherAttributes = weatherDogmaRows.ToDictionary(x => x, x => ReadDogmaAttributes(dogmaByTypeId[x].Doc));
        var buffIds = weatherAttributes.Values
            .SelectMany(x => new[] { Attribute(x, 2468), Attribute(x, 2470) })
            .Where(x => x is > 0)
            .Select(x => checked((long)x!.Value))
            .Distinct()
            .ToArray();
        var buffRows = await sde.ReadAsync("dbuffCollections", buffIds, cancellationToken);
        var buffsById = buffRows.ToDictionary(x => checked((int)x.Key));
        var weatherDefinitions = new Dictionary<(string Weather, int Stage), AbyssalWeatherTypeDefinition>();
        foreach (var typeId in weatherDogmaRows)
        {
            var typeDoc = typeRowsById[typeId].Doc;
            var name = SdeReader.English(GetProperty(typeDoc, "name"));
            if (!TryParseWeatherStage(name, out var weatherType, out var stage))
            {
                throw new InvalidDataException($"SDE weather type {typeId} has an unrecognized name '{name}'.");
            }

            var attributes = weatherAttributes[typeId];
            var effects = new List<AbyssalWeatherEffect>(2);
            AddBuffEffect(effects, attributes, 2468, 2469, buffsById);
            AddBuffEffect(effects, attributes, 2470, 2471, buffsById);
            if (effects.Count != 2)
            {
                throw new InvalidDataException($"SDE weather type {typeId} did not resolve both Dogma buff collections.");
            }
            var penalty = effects.FirstOrDefault(x => IsNamedEffect(x, "penalty"));
            var bonus = effects.FirstOrDefault(x => IsNamedEffect(x, "bonus"));
            if (penalty is null || bonus is null)
            {
                throw new InvalidDataException($"SDE weather type {typeId} lacks a named penalty/bonus collection.");
            }

            var definition = new AbyssalWeatherTypeDefinition(
                typeId,
                weatherType,
                stage,
                name,
                Array.AsReadOnly(ReadDogmaEffectIds(dogmaByTypeId[typeId].Doc)),
                checked((int)Math.Abs(penalty.Value)),
                checked((int)Math.Abs(bonus.Value)),
                Array.AsReadOnly(effects.ToArray()));
            if (!weatherDefinitions.TryAdd((weatherType, stage), definition))
            {
                throw new InvalidDataException($"Duplicate SDE Abyssal weather stage {weatherType}/{stage}.");
            }
        }
        if (weatherDefinitions.Count != 15
            || weatherDefinitions.Keys.Select(x => x.Weather).Distinct(StringComparer.Ordinal).Count() != 5)
        {
            throw new InvalidDataException("SDE did not resolve the 15 stages for all five Abyssal weather types.");
        }

        var loadedProfiles = rawProfiles.ToDictionary(x => ProfileKey(x.Tier, x.WeatherType));
        foreach (var profile in loadedProfiles.Values)
        {
            foreach (var npc in profile.SpawnFamilies.SelectMany(x => x.Npcs))
            {
                if (!npcTypes.ContainsKey(npc.TypeId))
                {
                    throw new InvalidDataException($"QSNA NPC type {npc.TypeId} has no SDE-backed hull definition.");
                }
            }
            foreach (var itemTypeId in profile.RewardOutcomes.SelectMany(x => x.Entries).Select(x => x.TypeId).Distinct())
            {
                if (!typeRowsById.ContainsKey(itemTypeId))
                {
                    throw new InvalidDataException($"Observed reward type {itemTypeId} is missing from SDE.");
                }
            }
        }

        var environmentRooms = new Dictionary<string, AbyssalEnvironmentRoomDefinition>(StringComparer.Ordinal);
        foreach (var profile in loadedProfiles.Values)
        {
            foreach (var room in profile.EnvironmentRooms)
            {
                if (!environmentRooms.TryAdd(room.EnvironmentProfileId, room))
                {
                    throw new InvalidDataException($"Duplicate EveJS environment room profile ID '{room.EnvironmentProfileId}'.");
                }
            }
        }

        var weatherWeights = ReadWeatherWeights(source.GetProperty("weatherWeightsByTier"));
        var stageChoices = ReadIntMatrix(source.GetProperty("allowedDarkWeatherStagesByTier"));
        if (weatherWeights.Count != 7 || stageChoices.Count != 7)
        {
            throw new InvalidDataException("EveJS weather-stage policy must cover tiers 0-6.");
        }

        var environmentalTypes = await LoadEnvironmentalTypesAsync(sde, staticTypes, cancellationToken);
        var environmentalObjects = ReadEnvironmentObjects(environmentRooms, staticTypes);
        return new AbyssalStaticCatalog(
            build!.Value,
            environmentSourceClientBuild,
            targetClientBuild,
            new ReadOnlyDictionary<int, AbyssalFilamentStaticDefinition>(filaments),
            new ReadOnlyDictionary<int, AbyssalSystemStaticDefinition>(systems),
            new ReadOnlyDictionary<int, AbyssalNpcStaticType>(npcTypes),
            new ReadOnlyDictionary<int, AbyssalSdeTypeDefinition>(staticTypes),
            new ReadOnlyDictionary<int, AbyssalSdeGraphicDefinition>(environmentGraphics),
            new ReadOnlyDictionary<(string Weather, int Stage), AbyssalWeatherTypeDefinition>(weatherDefinitions),
            new ReadOnlyDictionary<string, AbyssalEnvironmentRoomDefinition>(environmentRooms),
            new ReadOnlyDictionary<string, AbyssalPlanProfile>(loadedProfiles),
            new ReadOnlyDictionary<AbyssalGameMode, IReadOnlySet<int>>(allowedShipTypes),
            new ReadOnlyDictionary<AbyssalGameMode, IReadOnlyList<int>>(groupsByMode),
            new ReadOnlyDictionary<int, double[]>(weatherWeights),
            new ReadOnlyDictionary<int, int[]>(stageChoices))
        {
            ProceduralRewardProfiles = new ReadOnlyDictionary<string, JsonElement>(proceduralProfiles),
            EnvironmentalTypesById = environmentalTypes,
            EnvironmentObjectsByProfileId = environmentalObjects,
        };
    }

    private static async Task<IReadOnlyDictionary<int, AbyssalEnvironmentalTypeDefinition>> LoadEnvironmentalTypesAsync(
        SdeReader sde, IReadOnlyDictionary<int, AbyssalSdeTypeDefinition> types, CancellationToken cancellationToken)
    {
        var rows = await sde.ReadAsync("typeDogma", EnvironmentalTypeIds.Select(x => (long)x), cancellationToken);
        var dogma = rows.ToDictionary(x => checked((int)x.Key));
        var attributes = dogma.ToDictionary(x => x.Key, x => ReadDogmaAttributes(x.Value.Doc));
        var buffIds = attributes.Values.SelectMany(a => new[] { Attribute(a, 2468), Attribute(a, 2470), Attribute(a, 2472) })
            .Where(x => x is > 0).Select(x => checked((long)x!.Value)).Distinct().ToArray();
        var buffRows = (await sde.ReadAsync("dbuffCollections", buffIds, cancellationToken)).ToDictionary(x => checked((int)x.Key));
        var listIds = attributes.Values.Select(a => Attribute(a, 2759)).Where(x => x is > 0)
            .Select(x => checked((long)x!.Value)).Distinct().ToArray();
        var lists = (await sde.ReadAsync("typeLists", listIds, cancellationToken)).ToDictionary(x => checked((int)x.Key));
        var result = new Dictionary<int, AbyssalEnvironmentalTypeDefinition>();
        foreach (var id in EnvironmentalTypeIds)
        {
            if (!attributes.TryGetValue(id, out var a) || Attribute(a, 54) is not { } radius || radius <= 0)
                throw new InvalidDataException($"SDE local effect {id} has no positive radius.");
            var buffs = new List<AbyssalLocalBuffDefinition>();
            foreach (var pair in new[] { (2468, 2469), (2470, 2471), (2472, 2473) })
            {
                if (Attribute(a, pair.Item1) is not { } collection) continue;
                var collectionId = checked((int)collection);
                var doc = buffRows[collectionId].Doc;
                var modifiers = new List<AbyssalLocalBuffModifier>();
                foreach (var scope in new[] { "itemModifiers", "locationRequiredSkillModifiers", "locationGroupModifiers" })
                {
                    if (!doc.TryGetProperty(scope, out var entries)) continue;
                    foreach (var entry in entries.EnumerateArray())
                        modifiers.Add(new AbyssalLocalBuffModifier(scope, ReadRequiredInt(entry, "dogmaAttributeID"),
                            ReadNullableInt(entry, "skillID"), ReadNullableInt(entry, "groupID")));
                }
                buffs.Add(new AbyssalLocalBuffDefinition(collectionId,
                    Attribute(a, pair.Item2) ?? throw new InvalidDataException($"SDE local effect {id} has no buff value."),
                    ReadRequiredString(doc, "operationName"), ReadRequiredString(doc, "aggregateMode"),
                    Array.AsReadOnly(modifiers.ToArray())));
            }
            AbyssalPointDefenseDefinition? pointDefense = null;
            if (Attribute(a, 2759) is { } listIdValue)
            {
                var listId = checked((int)listIdValue);
                var list = lists[listId].Doc;
                var targetList = new AbyssalTargetTypeListDefinition(listId,
                    Array.AsReadOnly(ReadIntArray(list, "includedCategoryIDs")), Array.AsReadOnly(ReadIntArray(list, "includedGroupIDs")),
                    Array.AsReadOnly(ReadIntArray(list, "includedTypeIDs")), Array.AsReadOnly(ReadIntArray(list, "excludedCategoryIDs")),
                    Array.AsReadOnly(ReadIntArray(list, "excludedGroupIDs")), Array.AsReadOnly(ReadIntArray(list, "excludedTypeIDs")));
                var effectId = ReadDogmaEffectIds(dogma[id].Doc).Single();
                var effectRows = await sde.ReadAsync("dogmaEffects", [(long)effectId], cancellationToken);
                pointDefense = new AbyssalPointDefenseDefinition(
                    checked((long)(Attribute(a, 73) ?? throw new InvalidDataException($"SDE local effect {id} has no duration."))),
                    Array.AsReadOnly(new[] { a[114], a[118], a[117], a[116] }), targetList,
                    ReadRequiredString(effectRows.Single().Doc, "guid"));
            }
            result.Add(id, new AbyssalEnvironmentalTypeDefinition(id, types[id].Name, radius,
                Array.AsReadOnly(ReadDogmaEffectIds(dogma[id].Doc)), Array.AsReadOnly(buffs.ToArray()), pointDefense));
        }
        return new ReadOnlyDictionary<int, AbyssalEnvironmentalTypeDefinition>(result);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<AbyssalEnvironmentalObjectDefinition>> ReadEnvironmentObjects(
        IReadOnlyDictionary<string, AbyssalEnvironmentRoomDefinition> rooms,
        IReadOnlyDictionary<int, AbyssalSdeTypeDefinition> types)
    {
        var result = new Dictionary<string, IReadOnlyList<AbyssalEnvironmentalObjectDefinition>>(StringComparer.Ordinal);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var room in rooms.Values)
        {
            var objects = new List<AbyssalEnvironmentalObjectDefinition>();
            foreach (var entry in room.Data.GetProperty("runtimeObjects").EnumerateArray())
            {
                var typeId = ReadRequiredInt(entry, "typeID");
                if (!types.ContainsKey(typeId)) throw new InvalidDataException($"Unresolved environmental type {typeId}.");
                var key = ReadRequiredString(entry, "key");
                if (!keys.Add(key)) throw new InvalidDataException($"Duplicate environmental key {key}.");
                var offset = entry.GetProperty("offset");
                var rotation = entry.GetProperty("rotation").EnumerateArray().Select(x => x.GetDouble()).ToArray();
                var position = new AbyssalPosition(offset.GetProperty("x").GetDouble(), offset.GetProperty("y").GetDouble(), offset.GetProperty("z").GetDouble());
                if (rotation.Length != 3 || rotation.Any(x => !double.IsFinite(x)) ||
                    !double.IsFinite(position.X) || !double.IsFinite(position.Y) || !double.IsFinite(position.Z))
                    throw new InvalidDataException($"Non-finite environmental transform {key}.");
                objects.Add(new AbyssalEnvironmentalObjectDefinition(key, ReadRequiredString(entry, "profileID"), typeId,
                    position, Array.AsReadOnly(rotation), ReadRequiredString(entry, "sourceRole"), ReadRequiredString(entry, "effectKind"),
                    entry.GetProperty("destructible").GetBoolean(), ReadRequiredString(entry, "evidence"),
                    entry.GetProperty("sourceEvidence").Clone())
                {
                    MechanicsEnabled = entry.GetProperty("mechanicsEnabled").GetBoolean(),
                    PresentationRadiusMeters = entry.TryGetProperty("presentationRadius", out var radius) && radius.ValueKind == JsonValueKind.Number
                        ? radius.GetDouble() : null,
                });
            }
            result.Add(room.EnvironmentProfileId, Array.AsReadOnly(objects.ToArray()));
        }
        return new ReadOnlyDictionary<string, IReadOnlyList<AbyssalEnvironmentalObjectDefinition>>(result);
    }

    private IReadOnlyList<AbyssalArtifactPlan> BuildProceduralArtifacts(int tier, string weather, AbyssalGameMode mode,
        long traceItemId, string runId, int roomIndex)
    {
        var profile = ProceduralRewardProfiles[ProfileKey(tier, weather)];
        var main = profile.GetProperty("main");
        var seed = $"abyssal-reward-roll-v1:{runId}:0:0:{ReadRequiredString(main, "rewardProfileID")}:room-{roomIndex}";
        var random = CreateDeterministicRandom(seed);
        var quantities = new Dictionary<int, int>();
        void Add(JsonElement entry, int quantity)
        {
            var id = ReadRequiredInt(entry, "itemTypeID");
            if (quantity > 0) quantities[id] = checked(quantities.GetValueOrDefault(id) + quantity);
        }
        foreach (var entry in main.GetProperty("baseLootEntries").EnumerateArray())
            Add(entry, ReadRequiredInt(entry, "quantity"));
        foreach (var pool in main.GetProperty("weightedLootPools").EnumerateArray())
        {
            var chance = pool.GetProperty("chance").GetDouble();
            if (chance <= 0 || random() >= chance) continue;
            var entries = pool.GetProperty("entries").EnumerateArray().ToArray();
            for (var roll = 0; roll < ReadRequiredInt(pool, "rolls"); roll++)
            {
                var total = entries.Sum(x => x.GetProperty("weight").GetDouble());
                var threshold = random() * total;
                var cumulative = 0d;
                var selected = entries[^1];
                foreach (var entry in entries)
                {
                    cumulative += entry.GetProperty("weight").GetDouble();
                    if (threshold < cumulative) { selected = entry; break; }
                }
                var min = ReadRequiredInt(selected, "minQuantity");
                var max = ReadRequiredInt(selected, "maxQuantity");
                Add(selected, max <= min ? min : min + (int)Math.Floor(random() * (max - min + 1)));
            }
        }
        var mainLoot = quantities.OrderBy(x => x.Key).Select(x => BuildLootPlan(x.Key, x.Value, runId, roomIndex)).ToArray();
        var artifacts = new List<AbyssalArtifactPlan>
        {
            new($"abyssal-run-reward-artifact:{runId}:room-{roomIndex}:main-cache", MainCacheTypeFor(mode).TypeId,
                AbyssalArtifactKind.MainCache, new AbyssalPosition(35000, 10000, 0), Array.AsReadOnly(mainLoot),
                ReadRequiredString(main, "quantityPolicy"), "project_authored:abyssal:procedural-sde-reward-catalogue-v1"),
        };
        var side = profile.GetProperty("side");
        if (side.GetProperty("enabled").GetBoolean() && ReadIntArray(side, "eligibleRoomIndexes").Contains(roomIndex))
        {
            var sideProfileId = ReadRequiredString(side, "extractionNodeProfileID");
            var typeIds = ReadIntArray(side, "structureTypeIDs");
            var selection = Fnv1a32($"{traceItemId}:{roomIndex}:{sideProfileId}:type:0") / 4294967296d;
            var typeId = typeIds[(int)Math.Floor(selection * typeIds.Length)];
            var sideRandom = CreateDeterministicRandom($"abyssal-extraction-loot-v1:{runId}:room-{roomIndex}:node-1");
            var empty = side.GetProperty("lootEmptyChance").GetDouble();
            var sideLoot = empty > 0 && sideRandom() < empty
                ? Array.Empty<AbyssalLootPlan>()
                : side.GetProperty("guaranteedLootEntries").EnumerateArray()
                    .Select(e => BuildLootPlan(ReadRequiredInt(e, "itemTypeID"), ReadRequiredInt(e, "quantity"), runId, roomIndex))
                    .OrderBy(x => x.TypeId).ToArray();
            // JS clamps spawnOffsetMeters=2800 to MIN_EXTRACTION_NODE_FROM_CONDUIT_METERS=15000.
            artifacts.Add(new AbyssalArtifactPlan($"abyssal-run-reward-artifact:{runId}:room-{roomIndex}:extraction-1",
                typeId, AbyssalArtifactKind.ExtractionNode, new AbyssalPosition(35000, 15000, 0),
                Array.AsReadOnly(sideLoot), ReadRequiredString(side, "quantityPolicy"),
                "project_authored:abyssal:procedural-extraction-v1"));
        }
        return Array.AsReadOnly(artifacts.ToArray());
    }

    public AbyssalRunPlan CreatePlan(
        int filamentTypeId,
        AbyssalGameMode gameModeId,
        long traceItemId,
        string runId,
        int spaceLocationId)
    {
        if (!FilamentsByTypeId.TryGetValue(filamentTypeId, out var filament))
        {
            throw new ArgumentOutOfRangeException(nameof(filamentTypeId), filamentTypeId, "Type is not a published Abyssal filament in the loaded SDE.");
        }
        var mode = GetMode(gameModeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        if (!AbyssalSystemsById.ContainsKey(spaceLocationId))
        {
            throw new ArgumentOutOfRangeException(nameof(spaceLocationId), spaceLocationId, "Location is not one of the SDE Abyssal systems.");
        }

        var profile = _profiles[ProfileKey(filament.Tier, filament.WeatherType)];
        var weatherSelection = ResolveWeather(filament.Tier, filament.WeatherType, runId);
        var roomPlans = new AbyssalRoomPlan[3];
        for (var index = 0; index < roomPlans.Length; index++)
        {
            var roomIndex = index + 1;
            var (family, random) = SelectSpawnFamily(profile, traceItemId, runId, roomIndex);
            var spawns = SampleSpawnFamily(family, random);
            var environment = profile.EnvironmentRooms[index];
            var objectiveNpcTypes = spawns.Select(x => x.TypeId).Distinct().Order().ToArray();
            roomPlans[index] = new AbyssalRoomPlan(
                roomIndex,
                Array.AsReadOnly(spawns.ToArray()),
                Array.Empty<AbyssalLootPlan>(),
                Array.AsReadOnly(new[] { new AbyssalObjectivePlan($"clear-room-{roomIndex}", Array.AsReadOnly(objectiveNpcTypes)) }),
                environment.EnvironmentProfileId)
            {
                NebulaGraphicId = environment.NebulaGraphicId,
                EnvironmentObjects = Array.AsReadOnly(GetEnvironmentObjects(environment.EnvironmentProfileId)
                    .Select(x => new AbyssalEnvironmentObjectPlan(x.Key,
                        x.Role == "weather_object" ? weatherSelection.TypeId : x.TypeId, x.LocalPosition,
                        new AbyssalRotation(x.Rotation[0], x.Rotation[1], x.Rotation[2]), x.Role,
                        x.Role == "weather_object"
                            ? $"pinned-evejs-resolver:weather-selection-seed:{runId}:stage:{weatherSelection.Stage}"
                            : $"pinned-evejs-resolver:{x.Evidence}:{x.ProfileId}")
                    {
                        MechanicsEnabled = x.MechanicsEnabled,
                        PresentationRadiusMeters = x.PresentationRadiusMeters,
                    }).ToArray()),
                Artifacts = BuildProceduralArtifacts(filament.Tier, filament.WeatherType, gameModeId, traceItemId, runId, roomIndex),
            };
        }

        return new AbyssalRunPlan(
            filament.Tier,
            filament.WeatherType,
            roomPlans[0].NebulaGraphicId ?? throw new InvalidDataException("Room 1 has no environment nebula graphic."),
            mode.MaximumParticipants,
            Array.AsReadOnly(roomPlans))
        {
            SpaceLocationId = spaceLocationId,
        };
    }

    public AbyssalContentType ContentTypeFor(AbyssalGameMode gameModeId) => GetMode(gameModeId).ContentType;

    /// <summary>Filament stack size is a mode contract, independent of actual fleet size.</summary>
    public int RequiredFilamentsFor(AbyssalGameMode gameModeId) => GetMode(gameModeId).MaximumParticipants;

    /// <summary>Main-cache family follows EveJS resolveMainAbyssalCacheFamily; loot is already final.</summary>
    public AbyssalSdeTypeDefinition MainCacheTypeFor(AbyssalGameMode gameModeId) =>
        StaticTypesById[RequiredFilamentsFor(gameModeId) == 1 ? 47951 : 49689];

    public int MaximumParticipantsFor(AbyssalGameMode gameModeId) => GetMode(gameModeId).MaximumParticipants;

    public bool IsShipAllowed(AbyssalGameMode gameModeId, int shipTypeId) =>
        _allowedShipTypes[GetMode(gameModeId).Mode].Contains(shipTypeId);

    public AbyssalEnvironmentRoomDefinition GetEnvironmentRoom(string environmentProfileId) =>
        _environmentRooms.TryGetValue(environmentProfileId, out var room)
            ? room
            : throw new KeyNotFoundException($"Unknown Abyssal environment room profile '{environmentProfileId}'.");

    public IReadOnlyList<AbyssalWeatherTypeDefinition> GetWeatherStages(int tier, string weatherType)
    {
        ValidateTier(tier);
        var weather = NormalizeWeather(weatherType);
        var stages = _weatherStages.Values
            .Where(x => x.WeatherType == weather)
            .OrderBy(x => x.Stage)
            .ToArray();
        if (stages.Length != 3)
        {
            throw new KeyNotFoundException($"No SDE weather stages exist for '{weather}'.");
        }
        return Array.AsReadOnly(stages);
    }

    /// <summary>
    /// Resolves the telemetry profile's deterministic per-run stage and its matching SDE ball/effects.
    /// Captured visual templates do not override telemetry weather strength.
    /// </summary>
    public AbyssalWeatherStageSelection ResolveWeather(int tier, string weatherType, string selectionSeed, int? explicitStage = null)
    {
        ValidateTier(tier);
        var weather = NormalizeWeather(weatherType);
        ArgumentException.ThrowIfNullOrWhiteSpace(selectionSeed);
        var allowed = _weatherStagesByTier[tier];
        var weights = _weatherWeights[tier];
        var seed = Fnv1a32("explicit:" + selectionSeed);
        var roll = (seed % 1_000_000) / 1_000_000d;
        var stage = explicitStage ?? allowed[roll < weights[0] ? 0 : 1];
        if (!allowed.Contains(stage))
        {
            throw new ArgumentOutOfRangeException(nameof(explicitStage), stage, $"Stage is not allowed for tier {tier}.");
        }

        // These profiles use QSNA/observed telemetry in every tier. The EveJS
        // authored-ball override applies only when all telemetry flags are false.
        var definition = _weatherStages[(weather, stage)];
        return new AbyssalWeatherStageSelection(
            tier, weather, stage, definition.TypeId, definition.Name,
            definition.PenaltyPercent, definition.BonusPercent,
            explicitStage.HasValue ? "explicit-stage" : "deterministic-per-run-weighted",
            seed, roll,
            explicitStage.HasValue ? Array.Empty<double>() : Array.AsReadOnly(weights.ToArray()),
            definition.Effects);
    }

    private AbyssalLootPlan BuildLootPlan(int typeId, int count, string runId, int roomIndex)
    {
        if (StaticTypesById[typeId].CategoryId != 9)
        {
            return new AbyssalLootPlan(typeId, count);
        }

        // EveJS applyAbyssalBlueprintCopyStates: individual copies, zero ME/TE,
        // each copy receives one uniformly sampled run count in the range 1..5.
        var random = CreateDeterministicRandom($"abyssal-blueprint-copies:{runId}:{roomIndex}:{typeId}");
        return new AbyssalLootPlan(typeId, count)
        {
            IsBlueprintCopy = true,
            BlueprintRunsPerCopy = Array.AsReadOnly(Enumerable.Range(0, count)
                .Select(_ => 1 + (int)Math.Floor(random() * 5)).ToArray()),
        };
    }



    private (AbyssalSpawnFamily Family, Func<double> Random) SelectSpawnFamily(AbyssalPlanProfile profile, long traceItemId, string runId, int roomIndex)
    {
        if (profile.SpawnFamilies.Count == 0)
        {
            throw new InvalidDataException($"No QSNA spawn family exists for tier {profile.Tier} weather {profile.WeatherType}.");
        }
        var seed = $"{SpawnCatalogueId}:{profile.Tier}:{profile.WeatherType}:{roomIndex}:{traceItemId}:{runId}";
        var random = CreateDeterministicRandom(seed);
        var index = Math.Min(profile.SpawnFamilies.Count - 1, (int)Math.Floor(random() * profile.SpawnFamilies.Count));
        return (profile.SpawnFamilies[index], random);
    }

    private IReadOnlyList<AbyssalNpcSpawnPlan> SampleSpawnFamily(AbyssalSpawnFamily family, Func<double> random)
    {
        var sampled = new int[family.Npcs.Count];
        for (var index = 0; index < family.Npcs.Count; index++)
        {
            var npc = family.Npcs[index];
            if (npc.AppearanceChancePct <= 0)
            {
                sampled[index] = 0;
                continue;
            }
            if (npc.AppearanceChancePct < 100 && random() >= npc.AppearanceChancePct / 100d)
            {
                sampled[index] = 0;
                continue;
            }
            var minimum = Math.Max(1, npc.ObservedMinQuantity);
            sampled[index] = RandomIntegerInclusive(minimum, Math.Max(minimum, npc.ObservedMaxQuantity), random);
        }

        var reconciled = ReconcileQuantities(family, sampled);
        return family.Npcs
            .Select((npc, index) => new AbyssalNpcSpawnPlan(npc.TypeId, reconciled[index]))
            .Where(x => x.Count > 0)
            .GroupBy(x => x.TypeId)
            .Select(x => new AbyssalNpcSpawnPlan(x.Key, x.Sum(y => y.Count)))
            .OrderBy(x => x.TypeId)
            .ToArray();
    }

    private static int[] ReconcileQuantities(AbyssalSpawnFamily family, IReadOnlyList<int> sampled)
    {
        var minimumTotal = Math.Max(0, family.MinimumEnemyCount);
        var maximumTotal = Math.Max(minimumTotal, family.MaximumEnemyCount);
        var states = new Dictionary<int, QuantityState> { [0] = new(0, Array.Empty<int>()) };
        for (var index = 0; index < family.Npcs.Count; index++)
        {
            var npc = family.Npcs[index];
            var desired = Math.Max(0, sampled[index]);
            var next = new Dictionary<int, QuantityState>();
            foreach (var (currentTotal, state) in states.OrderBy(x => x.Key))
            {
                foreach (var quantity in AllowedQuantities(npc))
                {
                    var total = currentTotal + quantity;
                    if (total > maximumTotal) continue;
                    var quantities = new int[state.Quantities.Length + 1];
                    state.Quantities.CopyTo(quantities, 0);
                    quantities[^1] = quantity;
                    var candidate = new QuantityState(state.Cost + Math.Abs(quantity - desired), quantities);
                    if (!next.TryGetValue(total, out var existing)
                        || candidate.Cost < existing.Cost
                        || candidate.Cost == existing.Cost && CompareQuantitySignatures(candidate.Quantities, existing.Quantities) < 0)
                    {
                        next[total] = candidate;
                    }
                }
            }
            states = next;
        }

        var sampledTotal = sampled.Sum(x => Math.Max(0, x));
        var targetTotal = Math.Max(minimumTotal, Math.Min(maximumTotal, sampledTotal));
        var best = states
            .Where(x => x.Key >= minimumTotal && x.Key <= maximumTotal)
            .Select(x => new QuantityCandidate(x.Key, x.Value.Cost, x.Value.Quantities))
            .OrderBy(x => x.Cost)
            .ThenBy(x => Math.Abs(x.Total - targetTotal))
            .ThenBy(x => x.Total)
            .ThenBy(x => string.Join(",", x.Quantities), StringComparer.Ordinal)
            .FirstOrDefault();
        if (best is null)
        {
            throw new InvalidDataException($"Observed QSNA family '{family.Id}' cannot satisfy its recorded enemy-count bounds.");
        }
        return best.Quantities;
    }

    private static IEnumerable<int> AllowedQuantities(AbyssalSpawnNpc npc)
    {
        var maximum = Math.Max(0, npc.ObservedMaxQuantity);
        if (maximum <= 0)
        {
            yield return 0;
            yield break;
        }
        var minimum = Math.Max(1, Math.Min(maximum, npc.ObservedMinQuantity));
        yield return 0;
        for (var quantity = minimum; quantity <= maximum; quantity++)
        {
            yield return quantity;
        }
    }

    private static int CompareQuantitySignatures(IReadOnlyList<int> left, IReadOnlyList<int> right) =>
        StringComparer.Ordinal.Compare(string.Join(",", left), string.Join(",", right));

    private static int RandomIntegerInclusive(int minimum, int maximum, Func<double> random)
    {
        var min = Math.Max(0, minimum);
        var max = Math.Max(min, maximum);
        if (max <= min) return min;
        return min + (int)Math.Floor(NormalizeRandom(random()) * ((max - min) + 1));
    }

    private static Func<double> CreateDeterministicRandom(string seed)
    {
        var counter = 0;
        return () =>
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{seed}:{counter++}"));
            return BinaryPrimitives.ReadUInt32BigEndian(bytes) / 4_294_967_296d;
        };
    }

    private static double NormalizeRandom(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, 0, 0.9999999999999999) : 0;



    private static uint Fnv1a32(string value)
    {
        unchecked
        {
            var hash = 0x811c9dc5u;
            foreach (var character in value)
            {
                hash ^= character;
                hash *= 0x01000193u;
            }
            return hash;
        }
    }

    private static ModeContract GetMode(AbyssalGameMode mode) =>
        Modes.FirstOrDefault(x => x.Mode == mode)
        ?? throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown Abyssal client game mode.");

    private static void ValidateTier(int tier)
    {
        if (tier is < 0 or > 6)
        {
            throw new ArgumentOutOfRangeException(nameof(tier), tier, "Abyssal tiers range from 0 through 6.");
        }
    }

    private static string NormalizeWeather(string weatherType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(weatherType);
        var weather = weatherType.Trim().ToLowerInvariant();
        if (weather is not ("dark" or "electrical" or "exotic" or "firestorm" or "gamma"))
        {
            throw new ArgumentOutOfRangeException(nameof(weatherType), weatherType, "Unknown Abyssal weather type.");
        }
        return weather;
    }

    private static string ProfileKey(int tier, string weatherType) => $"{tier}:{weatherType}";

    private static async Task<Dictionary<int, AbyssalSystemStaticDefinition>> LoadAbyssalSystemsAsync(SdeReader sde, CancellationToken cancellationToken)
    {
        var regionRows = await sde.ReadAsync("mapRegions", cancellationToken: cancellationToken);
        var abyssalRegions = regionRows
            .Select(row => new
            {
                Id = checked((int)row.Key),
                Name = SdeReader.English(GetProperty(row.Doc, "name")),
                NebulaId = ReadNullableInt(row.Doc, "nebulaID"),
            })
            .Where(x => IsAbyssalRegionName(x.Name))
            .ToDictionary(x => x.Id);
        if (abyssalRegions.Count != 5)
        {
            throw new InvalidDataException($"Expected five SDE Abyssal regions (ADR01-ADR05); found {abyssalRegions.Count}.");
        }

        var systems = await sde.ReadByFieldAsync("mapSolarSystems", "regionID", abyssalRegions.Keys.Select(x => (long)x), cancellationToken);
        var byId = systems.ToDictionary(
            x => checked((int)x.Key),
            x =>
            {
                var regionId = ReadRequiredInt(x.Doc, "regionID");
                var region = abyssalRegions[regionId];
                return new AbyssalSystemStaticDefinition(checked((int)x.Key), regionId, region.Name, region.NebulaId);
            });
        if (byId.Count != 200)
        {
            throw new InvalidDataException($"Expected 200 SDE Abyssal solar systems; found {byId.Count}.");
        }
        return byId;
    }

    private static bool IsAbyssalRegionName(string name) =>
        name.Length == 5
        && name.StartsWith("ADR", StringComparison.Ordinal)
        && int.TryParse(name.AsSpan(3), NumberStyles.None, CultureInfo.InvariantCulture, out var regionNumber)
        && regionNumber is >= 1 and <= 5;

    private static JsonDocument ReadEmbeddedData()
    {
        var assembly = typeof(AbyssalStaticCatalog).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .SingleOrDefault(x => x.EndsWith("AbyssalCatalogDataV1.json.gz.b64", StringComparison.Ordinal));
        if (resourceName is null)
        {
            throw new InvalidDataException("Embedded Abyssal catalog resource is missing.");
        }
        using var resource = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidDataException("Embedded Abyssal catalog resource could not be opened.");
        using var reader = new StreamReader(resource, Encoding.UTF8, leaveOpen: false);
        var bytes = Convert.FromBase64String(reader.ReadToEnd().Trim());
        using var compressed = new MemoryStream(bytes, writable: false);
        using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
        return JsonDocument.Parse(gzip);
    }

    private static List<AbyssalPlanProfile> ReadProfiles(JsonElement profiles)
    {
        var result = new List<AbyssalPlanProfile>();
        foreach (var profile in profiles.EnumerateArray())
        {
            var tier = ReadRequiredInt(profile, "tier");
            var weather = ReadRequiredString(profile, "weatherType");
            var families = new List<AbyssalSpawnFamily>();
            foreach (var family in profile.GetProperty("spawnFamilies").EnumerateArray())
            {
                var npcs = family.GetProperty("npcs").EnumerateArray()
                    .Select(x =>
                    {
                        var values = x.EnumerateArray().ToArray();
                        if (values.Length != 4)
                        {
                            throw new InvalidDataException("QSNA NPC source row must contain type, appearance chance and observed quantity bounds.");
                        }
                        return new AbyssalSpawnNpc(
                            values[0].GetInt32(),
                            values[1].GetDouble(),
                            values[2].GetInt32(),
                            values[3].GetInt32());
                    })
                    .ToArray();
                families.Add(new AbyssalSpawnFamily(
                    tier,
                    weather,
                    ReadRequiredString(family, "id"),
                    ReadRequiredInt(family, "roomsAnalyzed"),
                    ReadRequiredInt(family, "enemyMin"),
                    ReadRequiredInt(family, "enemyMax"),
                    family.GetProperty("enemyAverage").GetDouble(),
                    Array.AsReadOnly(npcs)));
            }

            var outcomes = new List<AbyssalRewardOutcome>();
            foreach (var outcome in profile.GetProperty("rewardOutcomes").EnumerateArray())
            {
                var entries = outcome.GetProperty("entries").EnumerateArray()
                    .Select(x =>
                    {
                        var values = x.EnumerateArray().ToArray();
                        if (values.Length != 2)
                        {
                            throw new InvalidDataException("Reward outcome entry must contain type ID and quantity.");
                        }
                        return new AbyssalRewardEntry(values[0].GetInt32(), values[1].GetInt32());
                    })
                    .ToArray();
                outcomes.Add(new AbyssalRewardOutcome(
                    ReadRequiredString(outcome, "id"),
                    ReadRequiredInt(outcome, "weight"),
                    Array.AsReadOnly(entries)));
            }

            var environment = profile.GetProperty("environment");
            var profileId = ReadRequiredString(environment, "profileID");
            var authority = ReadRequiredString(environment, "contentAuthority");
            var evidence = environment.GetProperty("evidence").Clone();
            var environmentRooms = new List<AbyssalEnvironmentRoomDefinition>();
            foreach (var room in environment.GetProperty("rooms").EnumerateArray())
            {
                var visualReferences = ReadVisualSdeReferences(room);
                environmentRooms.Add(new AbyssalEnvironmentRoomDefinition(
                    ReadRequiredString(room, "roomLayoutProfileID"),
                    profileId,
                    authority,
                    evidence,
                    ReadRequiredInt(room, "roomIndex"),
                    ReadRequiredInt(room, "nebulaGraphicID"),
                    room.Clone(),
                    Array.AsReadOnly(visualReferences.TypeIds),
                    Array.AsReadOnly(visualReferences.GraphicIds)));
            }
            if (environmentRooms.Count != 3)
            {
                throw new InvalidDataException($"EveJS environment profile '{profileId}' has no complete three-room mapping.");
            }

            result.Add(new AbyssalPlanProfile(
                tier,
                weather,
                ReadRequiredString(profile, "rewardProfileKey"),
                Array.AsReadOnly(families.ToArray()),
                Array.AsReadOnly(outcomes.ToArray()),
                Array.AsReadOnly(environmentRooms.OrderBy(x => x.RoomIndex).ToArray())));
        }
        return result;
    }

    private static Dictionary<int, double[]> ReadWeatherWeights(JsonElement matrix)
    {
        var result = new Dictionary<int, double[]>();
        var tier = 0;
        foreach (var pair in matrix.EnumerateArray())
        {
            var values = pair.EnumerateArray().Select(x => x.GetDouble()).ToArray();
            if (values.Length != 2 || values.Any(x => x < 0 || x > 1) || Math.Abs(values.Sum() - 1) > 0.000001)
            {
                throw new InvalidDataException($"Invalid EveJS weather weights for tier {tier}.");
            }
            result.Add(tier++, values);
        }
        return result;
    }

    private static Dictionary<int, int[]> ReadIntMatrix(JsonElement matrix)
    {
        var result = new Dictionary<int, int[]>();
        var tier = 0;
        foreach (var row in matrix.EnumerateArray())
        {
            var values = row.EnumerateArray().Select(x => x.GetInt32()).ToArray();
            if (values.Length != 2 || values.Distinct().Count() != 2)
            {
                throw new InvalidDataException($"Invalid EveJS weather stage choices for tier {tier}.");
            }
            result.Add(tier++, values);
        }
        return result;
    }

    private static bool TryParseFilament(string name, out int tier, out string weather)
    {
        tier = -1;
        weather = "";
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 3 || !parts[2].Equals("Filament", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        tier = parts[0] switch
        {
            "Tranquil" => 0,
            "Calm" => 1,
            "Agitated" => 2,
            "Fierce" => 3,
            "Raging" => 4,
            "Chaotic" => 5,
            "Cataclysmic" => 6,
            _ => -1,
        };
        weather = parts[1] switch
        {
            "Electrical" => "electrical",
            "Dark" => "dark",
            "Exotic" => "exotic",
            "Firestorm" => "firestorm",
            "Gamma" => "gamma",
            _ => "",
        };
        return tier >= 0 && weather.Length > 0;
    }

    private static bool TryParseWeatherStage(string name, out string weather, out int stage)
    {
        weather = "";
        stage = 0;
        var parts = name.Trim().ToLowerInvariant().Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3 || parts[^2] != "weather" || !int.TryParse(parts[^1], NumberStyles.None, CultureInfo.InvariantCulture, out stage))
        {
            return false;
        }
        var prefix = string.Join("_", parts[..^2]);
        weather = prefix switch
        {
            "darkness" => "dark",
            "electric_storm" => "electrical",
            "caustic_toxin" => "exotic",
            "infernal" => "firestorm",
            "xenon_gas" => "gamma",
            _ => "",
        };
        return weather.Length > 0 && stage is >= 1 and <= 3;
    }

    private static void AddBuffEffect(
        ICollection<AbyssalWeatherEffect> effects,
        IReadOnlyDictionary<int, double> attributes,
        int collectionAttributeId,
        int valueAttributeId,
        IReadOnlyDictionary<int, SdeRow> buffRows)
    {
        var collection = Attribute(attributes, collectionAttributeId);
        var value = Attribute(attributes, valueAttributeId);
        if (collection is null || value is null)
        {
            return;
        }
        var collectionId = checked((int)collection.Value);
        if (!buffRows.TryGetValue(collectionId, out var row))
        {
            throw new InvalidDataException($"SDE dbuff collection {collectionId} referenced by weather Dogma is missing.");
        }
        var doc = row.Doc;
        effects.Add(new AbyssalWeatherEffect(
            collectionId,
            value.Value,
            SdeReader.English(GetProperty(doc, "displayName")),
            ReadOptionalString(doc, "operationName"),
            ReadOptionalString(doc, "showOutputValueInUI")));
    }

    private static bool IsNamedEffect(AbyssalWeatherEffect effect, string term) =>
        effect.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase);

    private static Dictionary<int, double> ReadDogmaAttributes(JsonElement typeDogma)
    {
        var result = new Dictionary<int, double>();
        if (!typeDogma.TryGetProperty("dogmaAttributes", out var attributes) || attributes.ValueKind != JsonValueKind.Array)
        {
            return result;
        }
        foreach (var attribute in attributes.EnumerateArray())
        {
            if (TryReadInt(attribute, "attributeID", out var id)
                && TryReadDouble(attribute, "value", out var value))
            {
                result[id] = value;
            }
        }
        return result;
    }

    private static double? Attribute(IReadOnlyDictionary<int, double> attributes, int id) =>
        attributes.TryGetValue(id, out var value) ? value : null;

    private static (int[] TypeIds, int[] GraphicIds) ReadVisualSdeReferences(JsonElement room)
    {
        var typeIds = new HashSet<int>();
        var graphicIds = new HashSet<int>();
        Visit(room);
        return (typeIds.Order().ToArray(), graphicIds.Order().ToArray());

        void Visit(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in value.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out var id) && id > 0)
                    {
                        if (property.Name.EndsWith("TypeID", StringComparison.OrdinalIgnoreCase))
                        {
                            typeIds.Add(id);
                        }
                        else if (property.Name.EndsWith("GraphicID", StringComparison.OrdinalIgnoreCase))
                        {
                            graphicIds.Add(id);
                        }
                    }
                    Visit(property.Value);
                }
            }
            else if (value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in value.EnumerateArray()) Visit(item);
            }
        }
    }

    private static int[] ReadDogmaEffectIds(JsonElement typeDogma)
    {
        if (!typeDogma.TryGetProperty("dogmaEffects", out var effects) || effects.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        return effects.EnumerateArray()
            .Where(x => TryReadInt(x, "effectID", out _))
            .Select(x =>
            {
                TryReadInt(x, "effectID", out var effectId);
                return effectId;
            })
            .ToArray();
    }

    private static bool IsPublished(JsonElement type)
    {
        if (!type.TryGetProperty("published", out var published))
        {
            return true;
        }
        return published.ValueKind == JsonValueKind.True;
    }

    private static int[] ReadIntArray(JsonElement document, string name)
    {
        if (!document.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        return value.EnumerateArray().Select(x => x.GetInt32()).ToArray();
    }

    private static JsonElement GetProperty(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value : default;

    private static int ReadRequiredInt(JsonElement element, string name) =>
        TryReadInt(element, name, out var value) ? value : throw new InvalidDataException($"Required integer field '{name}' is missing.");

    private static string ReadRequiredString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? throw new InvalidDataException($"Required string field '{name}' is null.")
            : throw new InvalidDataException($"Required string field '{name}' is missing.");

    private static string ReadOptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static int? ReadNullableInt(JsonElement element, string name) =>
        TryReadInt(element, name, out var value) ? value : null;

    private static bool TryReadInt(JsonElement element, string name, out int value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out value);
    }

    private static bool TryReadDouble(JsonElement element, string name, out double value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetDouble(out value);
    }

    private sealed record ModeContract(AbyssalGameMode Mode, AbyssalContentType ContentType, int TypeListId, int MaximumParticipants);

    private sealed record AbyssalSpawnNpc(int TypeId, double AppearanceChancePct, int ObservedMinQuantity, int ObservedMaxQuantity);

    private sealed record AbyssalSpawnFamily(
        int Tier,
        string WeatherType,
        string Id,
        int RoomsAnalyzed,
        int MinimumEnemyCount,
        int MaximumEnemyCount,
        double AverageEnemyCount,
        IReadOnlyList<AbyssalSpawnNpc> Npcs);

    private sealed record AbyssalRewardEntry(int TypeId, int Quantity);

    private sealed record AbyssalRewardOutcome(string Id, int Weight, IReadOnlyList<AbyssalRewardEntry> Entries);

    private sealed record AbyssalPlanProfile(
        int Tier,
        string WeatherType,
        string RewardProfileKey,
        IReadOnlyList<AbyssalSpawnFamily> SpawnFamilies,
        IReadOnlyList<AbyssalRewardOutcome> RewardOutcomes,
        IReadOnlyList<AbyssalEnvironmentRoomDefinition> EnvironmentRooms);

    private sealed record QuantityState(int Cost, int[] Quantities);

    private sealed record QuantityCandidate(int Total, int Cost, int[] Quantities);


}
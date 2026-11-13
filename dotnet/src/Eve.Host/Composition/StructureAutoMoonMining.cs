using System.Text.Json;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Host;

internal sealed partial class CharacterServicesComposition
{
    private const int AutoMoonMiningSettingId = 36;
    private const int AutoMoonMiningServiceId = Eve.Structures.StructureServiceIds.AutoMoonMining;
    private const int AutoMoonMinerTypeId = 81_826;
    private const int ColonyReagentLavaTypeId = 81_143;
    private const int FuelBlockGroupId = 1_136;
    private const int MoonMaterialGroupId = 427;
    private const int OutputMaterialTypeListId = 611;
    private const int OutputBayCapacityAttributeId = 5_693;
    private const int StructureFuelBayFlag = 172;
    private const int MoonMaterialBayFlag = 186;
    private const double OreCubicMetersPerCycle = 30_000;
    private const double ReprocessingEfficiency = 0.4;
    private const double DefaultOutputBayCapacity = 500_000;
    private const long FileTimeEpochOffset = 116_444_736_000_000_000;
    private const long FileTimeTicksPerMillisecond = 10_000;

    private async ValueTask<PyValue> ReadAutoMoonMiningDetailsAsync(
        ClientSession session,
        long structureId,
        CancellationToken cancellationToken)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        var (structure, profile, contents) = await ReadAutoMoonMiningContextAsync(
            connection, session, structureId, requireProfile: true, cancellationToken);
        var structureItems = await items.InContainerAsync(connection, null, structureId, cancellationToken);
        var typeIds = structureItems.Select(item => item.TypeId).Append(structure.TypeId).Distinct().ToArray();
        await dogma.EnsureTypesAsync(typeIds, cancellationToken);
        var types = await itemTypes.GetAsync(typeIds, cancellationToken);
        var outputTypeList = await ReadOutputMaterialTypeListAsync(cancellationToken);

        var availableFuel = SumItemsByType(structureItems, types, StructureFuelBayFlag,
            (typeId, type) => type.GroupId == FuelBlockGroupId || typeId == ColonyReagentLavaTypeId);
        var outputMaterials = SumItemsByType(structureItems, types, MoonMaterialBayFlag,
            (typeId, type) => IsAutoMoonOutput(typeId, type, outputTypeList));
        var relevantOutputTypes = structureItems
            .Where(item => item.Flag == MoonMaterialBayFlag && types.TryGetValue(item.TypeId, out var type) &&
                IsAutoMoonOutput(item.TypeId, type, outputTypeList))
            .Select(item => item.TypeId)
            .Distinct()
            .ToArray();
        var volumes = await ReadTypeVolumesAsync(relevantOutputTypes, cancellationToken);
        var usedVolume = structureItems
            .Where(item => item.Flag == MoonMaterialBayFlag && types.TryGetValue(item.TypeId, out var type) &&
                IsAutoMoonOutput(item.TypeId, type, outputTypeList))
            .Sum(item => volumes.GetValueOrDefault(item.TypeId) * ItemQuantity(item));
        var capacity = dogma.Type(structure.TypeId)?.Attributes.GetValueOrDefault(OutputBayCapacityAttributeId)
            ?? DefaultOutputBayCapacity;
        if (!double.IsFinite(capacity) || capacity < 0)
            capacity = DefaultOutputBayCapacity;

        var nextHarvestMs = await ReadNextAutoMoonHarvestMsAsync(connection, structureId, cancellationToken);
        var details = new PyDict(
        [
            Entry("moonID", WireId(profile!.MoonId)),
            Entry("nextHarvestTime", nextHarvestMs is > 0
                ? Py.FileTimeLong(checked(FileTimeEpochOffset + nextHarvestMs.Value * FileTimeTicksPerMillisecond))
                : PyNone.Instance),
            Entry("availableFuel", NumericDictionary(availableFuel)),
            Entry("outputMaterials", NumericDictionary(outputMaterials)),
            Entry("remainingOutputBayCapacity", new PyReal(Math.Max(0, capacity - usedVolume))),
        ]);
        return details;
    }

    private async ValueTask<PyValue> ReadAutoMoonMiningCycleOutputAsync(
        ClientSession session,
        long structureId,
        CancellationToken cancellationToken)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        var (_, profile, _) = await ReadAutoMoonMiningContextAsync(
            connection, session, structureId, requireProfile: false, cancellationToken);
        return profile is null ? new PyDict([]) : NumericDictionary(await CalculateAutoMoonCycleOutputAsync(profile, cancellationToken));
    }

    private async Task<(Eve.Structures.StructureRecord Structure, Eve.Structures.MoonResourceProfile? Profile,
        IReadOnlyList<Eve.Inventory.Item> Contents)> ReadAutoMoonMiningContextAsync(
        Npgsql.NpgsqlConnection connection,
        ClientSession session,
        long structureId,
        bool requireProfile,
        CancellationToken cancellationToken)
    {
        var structure = await structureStore.GetAsync(connection, null, structureId, false, cancellationToken);
        if (structure is null || structure.Snapshot.DestroyedMs > 0)
            throw AutoMoonAccessDenied($"Structure {structureId}");

        var system = solarSystems.Get(structure.SolarSystemId);
        if (system?.RegionId == Eve.Services.Space.Pochven.PochvenRules.RegionId)
            throw MachoErrors.ThrowUserError("CustomInfo",
                ("info", new PyStr("Auto moon mining is not available in Pochven.")));

        if (!await HasAutoMoonMiningAccessAsync(session, structure, cancellationToken) ||
            structure.Snapshot.State is Eve.Structures.StructureState.ArmorReinforce or Eve.Structures.StructureState.HullReinforce)
            throw AutoMoonAccessDenied(string.IsNullOrWhiteSpace(structure.Name) ? $"Structure {structureId}" : structure.Name);

        var profile = await moonExtractionStore.ProfileAsync(connection, null, structureId, cancellationToken);
        if (requireProfile && (profile is null || profile.MoonId <= 0))
            throw MachoErrors.ThrowUserError("CustomInfo",
                ("info", new PyStr("Auto moon miner details require a seeded moon resource profile.")));

        var contents = await items.InContainerAsync(connection, null, structureId, cancellationToken);
        return (structure, profile, contents);
    }

    private async ValueTask<bool> HasAutoMoonMiningAccessAsync(
        ClientSession session,
        Eve.Structures.StructureRecord structure,
        CancellationToken cancellationToken)
    {
        var characterId = session.CharacterId ?? 0;
        if (characterId <= 0)
            return false;

        if (structure.OwnerCorporationId > 0 &&
            await corporations.MemberAsync(characterId, cancellationToken) is { } member &&
            member.CorporationId == structure.OwnerCorporationId &&
            await corporations.GetAsync(structure.OwnerCorporationId, cancellationToken) is { IsNpc: false } corporation)
        {
            var roles = Eve.Corporations.CorporationRoles.Session(
                corporation, member, null, await corporations.TitlesAsync(structure.OwnerCorporationId, cancellationToken));
            if ((roles.CorpRole & Eve.Corporations.CorporationRoles.StationManager) != 0)
                return true;
        }

        return await structureDirectoryReadModel.CharacterHasSettingAsync(
            characterId, structure.Snapshot.StructureId, AutoMoonMiningSettingId, cancellationToken);
    }

    private async Task<long?> ReadNextAutoMoonHarvestMsAsync(
        Npgsql.NpgsqlConnection connection,
        long structureId,
        CancellationToken cancellationToken)
    {
        var modules = await structureStore.ServiceModuleFuelCyclesAsync(connection, null, structureId, cancellationToken);
        var moduleNext = modules
            .Where(cycle => cycle.ServiceIds.Contains(AutoMoonMiningServiceId) && cycle.NextFuelCycleMs > 0)
            .Select(cycle => (long?)cycle.NextFuelCycleMs)
            .Min();
        if (moduleNext is > 0)
            return moduleNext;

        var service = (await structureStore.ServiceFuelCyclesAsync(connection, null, structureId, cancellationToken))
            .FirstOrDefault(cycle => cycle.ServiceId == AutoMoonMiningServiceId &&
                cycle.State == Eve.Structures.ServiceState.Online && cycle.NextFuelCycleMs > 0);
        return service?.NextFuelCycleMs > 0 ? service.NextFuelCycleMs : null;
    }

    private async Task<IReadOnlyDictionary<int, long>> CalculateAutoMoonCycleOutputAsync(
        Eve.Structures.MoonResourceProfile profile,
        CancellationToken cancellationToken)
    {
        var oreIds = profile.Resources.Keys.Where(id => id is > 0 and <= int.MaxValue).Select(id => (int)id).ToArray();
        if (oreIds.Length == 0)
            return new Dictionary<int, long>();

        var ores = await sde.ReadAsync("types", oreIds.Select(id => (long)id), cancellationToken);
        var materialRows = await sde.ReadAsync("typeMaterials", oreIds.Select(id => (long)id), cancellationToken);
        var materialsByOre = materialRows.ToDictionary(row => (int)row.Key, row =>
            row.Doc.TryGetProperty("materials", out var materials) && materials.ValueKind == JsonValueKind.Array
                ? materials.EnumerateArray().Select(material => (
                    TypeId: material.GetProperty("materialTypeID").GetInt32(),
                    Quantity: material.GetProperty("quantity").GetInt64())).ToArray()
                : Array.Empty<(int TypeId, long Quantity)>());
        var oreTypes = ores.ToDictionary(row => (int)row.Key, row => (
            Volume: Number(row.Doc, "volume", 10),
            PortionSize: Integer(row.Doc, "portionSize", 100)));
        var outputTypeIds = materialsByOre.Values.SelectMany(materials => materials)
            .Select(material => material.TypeId).Where(id => id > 0).Distinct().ToArray();
        var outputTypes = await itemTypes.GetAsync(outputTypeIds, cancellationToken);
        var outputTypeList = await ReadOutputMaterialTypeListAsync(cancellationToken);
        var totals = new Dictionary<int, double>();

        foreach (var (oreId, fraction) in profile.Resources)
        {
            if (oreId is <= 0 or > int.MaxValue || !(fraction > 0) || !double.IsFinite(fraction) ||
                !oreTypes.TryGetValue((int)oreId, out var ore))
                continue;
            var volume = Math.Max(0.000001, ore.Volume);
            var portionSize = Math.Max(1, ore.PortionSize);
            var batches = fraction * OreCubicMetersPerCycle / volume / portionSize;
            foreach (var material in materialsByOre.GetValueOrDefault((int)oreId, []))
            {
                if (material.TypeId <= 0 || material.Quantity <= 0 ||
                    !outputTypes.TryGetValue(material.TypeId, out var outputType) ||
                    !IsAutoMoonOutput(material.TypeId, outputType, outputTypeList))
                    continue;
                totals[material.TypeId] = totals.GetValueOrDefault(material.TypeId) +
                    batches * material.Quantity * ReprocessingEfficiency;
            }
        }

        return totals.OrderBy(pair => pair.Key)
            .Select(pair => (pair.Key, Quantity: (long)Math.Floor(pair.Value + 1e-9)))
            .Where(pair => pair.Quantity > 0)
            .ToDictionary(pair => pair.Key, pair => pair.Quantity);
    }

    private async Task<IReadOnlyDictionary<int, double>> ReadTypeVolumesAsync(
        IReadOnlyCollection<int> typeIds,
        CancellationToken cancellationToken)
    {
        if (typeIds.Count == 0)
            return new Dictionary<int, double>();
        var rows = await sde.ReadAsync("types", typeIds.Select(id => (long)id), cancellationToken);
        return rows.ToDictionary(row => (int)row.Key, row => Number(row.Doc, "volume", 0));
    }

    private async Task<JsonElement?> ReadOutputMaterialTypeListAsync(CancellationToken cancellationToken) =>
        (await sde.ReadAsync("typeLists", [OutputMaterialTypeListId], cancellationToken))
            .SingleOrDefault()?.Doc;

    private static Dictionary<int, long> SumItemsByType(
        IReadOnlyList<Eve.Inventory.Item> items,
        IReadOnlyDictionary<int, Eve.Inventory.ItemType> types,
        int flag,
        Func<int, Eve.Inventory.ItemType, bool> predicate)
    {
        var totals = new Dictionary<int, long>();
        foreach (var item in items.Where(item => item.Flag == flag))
        {
            if (!types.TryGetValue(item.TypeId, out var type) || !predicate(item.TypeId, type))
                continue;
            var quantity = ItemQuantity(item);
            if (quantity > 0)
                totals[item.TypeId] = checked(totals.GetValueOrDefault(item.TypeId) + quantity);
        }
        return totals;
    }

    private static bool IsAutoMoonOutput(int typeId, Eve.Inventory.ItemType type, JsonElement? typeList = null)
    {
        if (type.GroupId == MoonMaterialGroupId)
            return true;
        if (typeList is not JsonElement list || list.ValueKind != JsonValueKind.Object)
            return false;
        var included = Contains(list, "includedTypeIDs", typeId) || Contains(list, "includedGroupIDs", type.GroupId) ||
            Contains(list, "includedCategoryIDs", type.CategoryId);
        var excluded = Contains(list, "excludedTypeIDs", typeId) || Contains(list, "excludedGroupIDs", type.GroupId) ||
            Contains(list, "excludedCategoryIDs", type.CategoryId);
        return included && !excluded;
    }

    private static bool Contains(JsonElement row, string field, int id) =>
        row.TryGetProperty(field, out var values) && values.ValueKind == JsonValueKind.Array &&
        values.EnumerateArray().Any(value => value.TryGetInt32(out var candidate) && candidate == id);

    private static long ItemQuantity(Eve.Inventory.Item item) => item.Singleton ? 1 : Math.Max(0, item.Quantity);

    private static double Number(JsonElement row, string field, double fallback) =>
        row.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
            ? number
            : fallback;

    private static int Integer(JsonElement row, string field, int fallback) =>
        row.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : fallback;

    private static MachoWrappedException AutoMoonAccessDenied(string structureName) => MachoErrors.ThrowUserError(
        "AccessToAutoMoonMinerDenied", ("structureName", new PyStr(structureName)));

    private static KeyValuePair<PyValue, PyValue> Entry(string key, PyValue value) => new(new PyStr(key), value);

    private static PyValue NumericDictionary(IReadOnlyDictionary<int, long> values) => new PyDict(
        [.. values.OrderBy(pair => pair.Key).Select(pair => new KeyValuePair<PyValue, PyValue>(
            WireId(pair.Key), WireId(pair.Value)))]);

    private static PyValue WireId(long value) => value is >= int.MinValue and <= int.MaxValue
        ? new PyInt((int)value)
        : new PyLong(value);
}

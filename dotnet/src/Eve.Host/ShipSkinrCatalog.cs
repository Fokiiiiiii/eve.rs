using System.Globalization;
using System.Text.Json;
using Eve.Sde;
using Eve.Host.PublicGateway;

namespace Eve.Host;

/// <summary>SKINR slot, component, rarity, binder, hull and tier authority from the mission SDE build.</summary>
internal sealed class ShipSkinrCatalog
{
    internal sealed record Component(
        int Id,
        int CategoryId,
        int RarityId,
        int Points,
        int BinderTypeId,
        int BinderCount,
        IReadOnlyDictionary<int, int> LicenseUsesByTypeId,
        IReadOnlySet<int> DisallowedSlots,
        bool Published);

    internal sealed record ItemTypeInfo(int GroupId, int CategoryId);

    private readonly IReadOnlyDictionary<int, Component> _components;
    private readonly IReadOnlyDictionary<int, IReadOnlySet<int>> _slotsByShipType;
    private readonly IReadOnlyDictionary<int, IReadOnlySet<int>> _categoriesBySlot;
    private readonly IReadOnlyDictionary<int, int> _groupByShipType;
    private readonly IReadOnlyDictionary<int, int> _categoryByGroup;
    private readonly IReadOnlyDictionary<int, IReadOnlyDictionary<int, int>> _thresholdsByGroup;

    private ShipSkinrCatalog(
        IReadOnlyDictionary<int, Component> components,
        IReadOnlyDictionary<int, IReadOnlySet<int>> slotsByShipType,
        IReadOnlyDictionary<int, IReadOnlySet<int>> categoriesBySlot,
        IReadOnlyDictionary<int, int> groupByShipType,
        IReadOnlyDictionary<int, int> categoryByGroup,
        IReadOnlyDictionary<int, IReadOnlyDictionary<int, int>> thresholdsByGroup)
    {
        _components = components;
        _slotsByShipType = slotsByShipType;
        _categoriesBySlot = categoriesBySlot;
        _groupByShipType = groupByShipType;
        _categoryByGroup = categoryByGroup;
        _thresholdsByGroup = thresholdsByGroup;
    }

    internal Component? ComponentFor(int id) => _components.GetValueOrDefault(id);

    internal ItemTypeInfo? ItemInfoFor(int typeId) =>
        _groupByShipType.TryGetValue(typeId, out var groupId) &&
        _categoryByGroup.TryGetValue(groupId, out var categoryId)
            ? new ItemTypeInfo(groupId, categoryId)
            : null;

    internal (Component Component, int Uses)? ComponentForLicenseType(int typeId)
    {
        foreach (var component in _components.Values)
            if (component.Published && component.LicenseUsesByTypeId.TryGetValue(typeId, out var uses))
                return (component, uses);
        return null;
    }

    public static async Task<ShipSkinrCatalog> LoadAsync(SdeReader sde, CancellationToken cancellationToken = default)
    {
        var pointsByCategoryAndRarity = new Dictionary<int, Dictionary<int, int>>();
        foreach (var row in await sde.ReadAsync("skinrComponentPointValues", cancellationToken: cancellationToken))
        {
            if (row.Key is <= 0 or > int.MaxValue) continue;
            var points = ReadKeyedIntMap(Property(row.Doc, "values", "_value"));
            if (points.Count > 0) pointsByCategoryAndRarity[(int)row.Key] = points;
        }

        var components = new Dictionary<int, Component>();
        foreach (var row in await sde.ReadAsync("skinrComponents", cancellationToken: cancellationToken))
        {
            if (row.Key <= 0 || row.Key > int.MaxValue) continue;
            var categoryId = Int(Property(row.Doc, "category"));
            var rarityId = Int(Property(row.Doc, "rarity"));
            var usesByType = ReadLicenseUsesByType(Property(row.Doc, "associatedTypeIDs", "associatedTypeIds"));
            var binder = Property(row.Doc, "sequenceBinder", "sequence_binder");
            var binderTypeId = Int(Property(binder, "itemTypeID", "itemTypeId"));
            var binderCount = Int(Property(binder, "count"));
            var disallowed = Array(Property(row.Doc, "disallowedSlots", "disallowed_slots"))
                .Select(Int).Where(id => id > 0).ToHashSet();
            var published = Bool(Property(row.Doc, "published"), false);
            if (categoryId <= 0 || rarityId <= 0 || usesByType.Count == 0 ||
                !pointsByCategoryAndRarity.TryGetValue(categoryId, out var points) ||
                !points.TryGetValue(rarityId, out var pointValue))
                continue;
            components[(int)row.Key] = new Component((int)row.Key, categoryId, rarityId, pointValue,
                binderTypeId, binderCount, usesByType, disallowed, published);
        }

        var categoriesBySlot = new Dictionary<int, IReadOnlySet<int>>();
        foreach (var row in await sde.ReadAsync("skinrSlots", cancellationToken: cancellationToken))
        {
            var slotId = Int(Property(row.Doc, "id", "slotID", "slotId"));
            if (slotId == 0 && row.Key is > 0 and <= int.MaxValue) slotId = (int)row.Key;
            var categories = Array(Property(row.Doc, "allowedDesignComponentCategories", "allowed_design_component_categories"))
                .Select(Int).Where(id => id > 0).ToHashSet();
            if (slotId > 0 && categories.Count > 0) categoriesBySlot[slotId] = categories;
        }

        var categoryByGroup = new Dictionary<int, int>();
        foreach (var group in await sde.ReadAsync("groups", cancellationToken: cancellationToken))
        {
            var groupId = group.Key is > 0 and <= int.MaxValue ? (int)group.Key :
                Int(Property(group.Doc, "groupID", "groupId", "id"));
            var categoryId = Int(Property(group.Doc, "categoryID", "categoryId"));
            if (groupId > 0 && categoryId > 0) categoryByGroup[groupId] = categoryId;
        }

        var groupByShipType = new Dictionary<int, int>();
        foreach (var type in await sde.ReadAsync("types", cancellationToken: cancellationToken))
        {
            if (type.Key is > 0 and <= int.MaxValue &&
                TryInt(Property(type.Doc, "groupID", "groupId"), out var groupId) && groupId > 0)
                groupByShipType[(int)type.Key] = groupId;
        }

        var shipCategoryIds = (await sde.ReadAsync("categories", cancellationToken: cancellationToken))
            .Where(row => SdeReader.English(Property(row.Doc, "name")) == "Ship")
            .Where(row => row.Key is > 0 and <= int.MaxValue)
            .Select(row => (int)row.Key)
            .Distinct()
            .ToArray();
        if (shipCategoryIds.Length != 1)
            throw new InvalidOperationException("the mission SDE must identify exactly one Ship category");
        var slotsByShipType = BuildSlotsByShipType(
            await sde.ReadAsync("skinrSlotConfigurations", cancellationToken: cancellationToken),
            groupByShipType,
            categoryByGroup,
            shipCategoryIds[0]);

        var thresholdsByGroup = new Dictionary<int, IReadOnlyDictionary<int, int>>();
        foreach (var row in await sde.ReadAsync("skinrTierThresholds", cancellationToken: cancellationToken))
        {
            var groupId = Int(Property(row.Doc, "groupID", "groupId"));
            if (groupId == 0 && row.Key is > 0 and <= int.MaxValue) groupId = (int)row.Key;
            var thresholds = ReadKeyedIntMap(Property(row.Doc, "thresholds", "tierThresholds", "tier_thresholds", "_value"))
                .Where(pair => pair.Key > 0)
                .ToDictionary(pair => pair.Key, pair => pair.Value);
            if (groupId > 0 && thresholds.Count > 0) thresholdsByGroup[groupId] = thresholds;
        }

        if (components.Count == 0 || slotsByShipType.Count == 0 || categoriesBySlot.Count == 0 ||
            thresholdsByGroup.Count == 0 || categoryByGroup.Count == 0)
        {
            throw new InvalidOperationException(
                $"mission SDE has incomplete SKINR authority data (components={components.Count}, " +
                $"ship configurations={slotsByShipType.Count}, slots={categoriesBySlot.Count}, " +
                $"tier groups={thresholdsByGroup.Count}, groups={categoryByGroup.Count})");
        }

        return new ShipSkinrCatalog(components, slotsByShipType, categoriesBySlot, groupByShipType,
            categoryByGroup, thresholdsByGroup);
    }

    internal static Dictionary<int, int> ReadKeyedIntMap(JsonElement value)
    {
        var entries = new Dictionary<int, int>();
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var item in value.EnumerateObject())
                if (int.TryParse(item.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var key) &&
                    TryInt(item.Value, out var mappedValue))
                    entries[key] = mappedValue;
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
                if (TryInt(Property(item, "_key"), out var key) &&
                    TryInt(Property(item, "_value"), out var mappedValue))
                    entries[key] = mappedValue;
        }
        return entries;
    }

    internal static Dictionary<int, int> ReadLicenseUsesByType(JsonElement associatedTypes)
    {
        var usesByType = new Dictionary<int, int>();
        foreach (var associated in Array(associatedTypes))
        {
            var typeId = Int(Property(associated, "typeID", "typeId"));
            var uses = Int(Property(associated, "licenseUsesGranted", "license_uses_granted"));
            // The SDE uses -1 for unlimited; the runtime uses zero as its unlimited sentinel.
            if (typeId > 0 && uses >= -1) usesByType[typeId] = Math.Max(0, uses);
        }
        return usesByType;
    }

    internal static IReadOnlyDictionary<int, IReadOnlySet<int>> BuildSlotsByShipType(
        IReadOnlyList<SdeRow> configurations,
        IReadOnlyDictionary<int, int> groupByType,
        IReadOnlyDictionary<int, int> categoryByGroup,
        int shipCategoryId)
    {
        var slotsByShipType = new Dictionary<int, IReadOnlySet<int>>();
        var resolvedTypes = new HashSet<int>();
        foreach (var row in configurations
            .OrderBy(row => Int(Property(row.Doc, "priority")))
            .ThenBy(row => row.Key))
        {
            var slotIds = Array(Property(row.Doc, "config", "slotIDs", "slotIds", "slots"))
                .Select(Int).Where(id => id > 0).ToHashSet();
            var ships = Array(Property(row.Doc, "ships")).Select(Int).Where(id => id > 0);
            foreach (var shipTypeId in ships)
            {
                if (resolvedTypes.Add(shipTypeId) && slotIds.Count > 0)
                    slotsByShipType[shipTypeId] = slotIds;
            }

            if (!Bool(Property(row.Doc, "allowAllShips"), false) || slotIds.Count == 0) continue;
            foreach (var (typeId, groupId) in groupByType)
            {
                if (categoryByGroup.TryGetValue(groupId, out var categoryId) &&
                    categoryId == shipCategoryId && resolvedTypes.Add(typeId))
                    slotsByShipType[typeId] = slotIds;
            }
        }
        return slotsByShipType;
    }

    public bool TryValidateLayout(ReadOnlyMemory<byte> layoutBytes, out int shipTypeId, out int tier, out string error)
    {
        shipTypeId = 0;
        tier = 0;
        error = "INVALID_DESIGN";
        IReadOnlySet<int>? allowedSlots = null;
        var slotIds = new HashSet<int>();
        var selected = new List<Component>();
        foreach (var field in ProtoReader.Fields(layoutBytes))
        {
            if (field.Number == 2 && field.WireType == 2)
            {
                shipTypeId = PositiveSequential(field.Bytes);
                if (shipTypeId <= 0 || !_slotsByShipType.TryGetValue(shipTypeId, out allowedSlots)) return false;
            }
            else if (field.Number == 1 && field.WireType == 2)
            {
                var (slotId, componentId, _) = ReadSlot(field.Bytes);
                if (slotId <= 0 || componentId <= 0 || !slotIds.Add(slotId)) return false;
                if (!_components.TryGetValue(componentId, out var component) || !component.Published ||
                    component.DisallowedSlots.Contains(slotId) ||
                    !_categoriesBySlot.TryGetValue(slotId, out var categories) ||
                    !categories.Contains(component.CategoryId))
                    return false;
                selected.Add(component);
            }
        }

        if (shipTypeId <= 0 || allowedSlots is null || slotIds.Any(slot => !allowedSlots.Contains(slot)) ||
            !_groupByShipType.TryGetValue(shipTypeId, out var groupId) ||
            !_thresholdsByGroup.TryGetValue(groupId, out var thresholds))
            return false;

        var points = selected.Sum(component => component.Points);
        foreach (var (level, limit) in thresholds.OrderBy(item => item.Key))
            if (points <= limit)
            {
                tier = level;
                return true;
            }
        tier = thresholds.Keys.Max();
        return true;
    }

    private static int PositiveSequential(ReadOnlyMemory<byte> identifier)
    {
        foreach (var field in ProtoReader.Fields(identifier))
            if (field.Number == 1 && field.WireType == 0 && field.Scalar is > 0 and <= int.MaxValue)
                return (int)field.Scalar;
        return 0;
    }

    internal static (int SlotId, int ComponentId, int ComponentType) ReadSlot(ReadOnlyMemory<byte> slot)
    {
        var slotId = 0;
        ReadOnlyMemory<byte>? configuration = null;
        foreach (var field in ProtoReader.Fields(slot))
        {
            if (field.Number == 1 && field.WireType == 2) slotId = PositiveSequential(field.Bytes);
            if (field.Number == 2 && field.WireType == 2) configuration = field.Bytes;
        }
        if (configuration is not { } config) return (slotId, 0, 0);
        foreach (var field in ProtoReader.Fields(config))
        {
            if (field.Number is not (1 or 2) || field.WireType != 2) continue;
            var component = 0;
            foreach (var nested in ProtoReader.Fields(field.Bytes))
            {
                if (nested.Number != 1 || nested.WireType != 2) continue;
                component = PositiveSequential(nested.Bytes);
                break;
            }
            return (slotId, component, field.Number);
        }
        return (slotId, 0, 0);
    }

    private static JsonElement Property(JsonElement value, params string[] names)
    {
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var name in names)
                if (value.TryGetProperty(name, out var property))
                    return property;
        return default;
    }

    private static IEnumerable<JsonElement> Array(JsonElement value) =>
        value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : Enumerable.Empty<JsonElement>();

    private static int Int(JsonElement value) => TryInt(value, out var result) ? result : 0;

    private static bool TryInt(JsonElement value, out int result)
    {
        if (value.ValueKind == JsonValueKind.Number) return value.TryGetInt32(out result);
        if (value.ValueKind == JsonValueKind.String)
            return int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
        result = 0;
        return false;
    }

    private static bool Bool(JsonElement value, bool fallback) =>
        value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : fallback;
}

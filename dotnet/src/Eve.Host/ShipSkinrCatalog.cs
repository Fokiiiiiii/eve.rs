using System.Globalization;
using System.Text.Json;
using Eve.Sde;
using Eve.Host.PublicGateway;

namespace Eve.Host;

/// <summary>SKINR slot, component, rarity, binder, hull and tier authority from the pinned SDE.</summary>
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
            var values = Property(row.Doc, "values");
            if (values.ValueKind != JsonValueKind.Object) continue;
            var points = new Dictionary<int, int>();
            foreach (var value in values.EnumerateObject())
                if (int.TryParse(value.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rarityId) &&
                    TryInt(value.Value, out var pointValue))
                    points[rarityId] = pointValue;
            if (points.Count > 0) pointsByCategoryAndRarity[checked((int)row.Key)] = points;
        }

        var components = new Dictionary<int, Component>();
        foreach (var row in await sde.ReadAsync("skinrComponents", cancellationToken: cancellationToken))
        {
            if (row.Key <= 0 || row.Key > int.MaxValue) continue;
            var categoryId = Int(Property(row.Doc, "category"));
            var rarityId = Int(Property(row.Doc, "rarity"));
            var usesByType = new Dictionary<int, int>();
            foreach (var associated in Array(Property(row.Doc, "associatedTypeIDs", "associatedTypeIds")))
            {
                var typeId = Int(Property(associated, "typeID", "typeId"));
                var uses = Int(Property(associated, "licenseUsesGranted", "license_uses_granted"));
                if (typeId > 0 && uses >= 0) usesByType[typeId] = uses;
            }
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

        var slotsByShipType = new Dictionary<int, IReadOnlySet<int>>();
        foreach (var row in await sde.ReadAsync("skinrSlotConfigurations", cancellationToken: cancellationToken))
        {
            var shipTypeId = Int(Property(row.Doc, "shipTypeID", "shipTypeId", "typeID", "typeId"));
            if (shipTypeId == 0 && row.Key is > 0 and <= int.MaxValue) shipTypeId = (int)row.Key;
            var slotIds = Array(Property(row.Doc, "slotIDs", "slotIds", "slots")).Select(Int).Where(id => id > 0).ToHashSet();
            if (shipTypeId > 0 && slotIds.Count > 0) slotsByShipType[shipTypeId] = slotIds;
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

        var thresholdsByGroup = new Dictionary<int, IReadOnlyDictionary<int, int>>();
        foreach (var row in await sde.ReadAsync("skinrTierThresholds", cancellationToken: cancellationToken))
        {
            var groupId = Int(Property(row.Doc, "groupID", "groupId"));
            if (groupId == 0 && row.Key is > 0 and <= int.MaxValue) groupId = (int)row.Key;
            var thresholdObject = Property(row.Doc, "thresholds", "tierThresholds", "tier_thresholds");
            if (thresholdObject.ValueKind != JsonValueKind.Object) continue;
            var thresholds = new Dictionary<int, int>();
            foreach (var threshold in thresholdObject.EnumerateObject())
                if (int.TryParse(threshold.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tier) &&
                    TryInt(threshold.Value, out var limit) && tier > 0)
                    thresholds[tier] = limit;
            if (groupId > 0 && thresholds.Count > 0) thresholdsByGroup[groupId] = thresholds;
        }

        if (components.Count == 0 || slotsByShipType.Count == 0 || categoriesBySlot.Count == 0 || thresholdsByGroup.Count == 0 || categoryByGroup.Count == 0)
            throw new InvalidOperationException("the pinned SDE does not contain the SKINR component and hull authority tables");
        return new ShipSkinrCatalog(components, slotsByShipType, categoriesBySlot, groupByShipType, categoryByGroup, thresholdsByGroup);
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

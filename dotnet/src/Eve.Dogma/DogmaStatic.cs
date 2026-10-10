using System.Collections.Concurrent;
using System.Text.Json;
using Eve.Sde;

namespace Eve.Dogma;

/// <summary>
/// The static data dogma works from: the attribute and effect definitions (loaded whole, they are small) and the types
/// (loaded when first needed). Read from the SDE.
/// </summary>
public sealed class DogmaStatic
{
    /// <summary>The attributes every type has from its own record, not from its dogma: mass, capacity, volume, radius.</summary>
    public static readonly (int Attribute, string Field)[] IntrinsicAttributes = [(4, "mass"), (38, "capacity"), (161, "volume"), (162, "radius")];

    private readonly SdeReader? _sde;
    /// <summary>The compressed gas types by name, read once: a cloud's yield is looked up by name when its material is not in the SDE.</summary>
    private Dictionary<string, int>? _compressedGasTypeIds;
    private readonly ConcurrentDictionary<int, TypeDef> _types = new();
    private readonly Dictionary<int, AttributeDef> _attributes;
    private readonly Dictionary<int, EffectDef> _effects;
    private readonly Dictionary<int, int> _categoryOfGroup;
    private readonly Dictionary<int, string> _groupNames;
    private readonly Dictionary<int, string> _categoryNames;
    private readonly Dictionary<int, string> _factionNames;
    private readonly Dictionary<string, int> _attributeIdsByName;

    public DogmaStatic(IEnumerable<AttributeDef> attributes, IEnumerable<EffectDef> effects, IEnumerable<TypeDef> types, SdeReader? sde = null, IReadOnlyDictionary<int, int>? categoryOfGroup = null, IReadOnlyDictionary<int, string>? groupNames = null, IReadOnlyDictionary<int, string>? categoryNames = null, IReadOnlyDictionary<int, string>? factionNames = null, IReadOnlySet<int>? missileLauncherGroups = null)
    {
        _attributes = attributes.ToDictionary(a => a.Id);
        _effects = effects.ToDictionary(e => e.Id);
        foreach (var type in types)
        {
            _types[type.TypeId] = type;
        }
        _sde = sde;
        _categoryOfGroup = categoryOfGroup is null ? [] : new Dictionary<int, int>(categoryOfGroup);
        _groupNames = groupNames is null ? [] : new Dictionary<int, string>(groupNames);
        _categoryNames = categoryNames is null ? [] : new Dictionary<int, string>(categoryNames);
        _factionNames = factionNames is null ? [] : new Dictionary<int, string>(factionNames);
        MissileLauncherGroups = missileLauncherGroups ?? new HashSet<int>();
        _attributeIdsByName = _attributes.Values
            .Where(a => a.Name.Length > 0)
            .GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Returns this catalog with extra in-memory definitions for deterministic fixtures.</summary>
    public DogmaStatic WithFixtures(
        IEnumerable<TypeDef> types,
        IReadOnlyDictionary<int, int>? categoryOfGroup = null,
        IReadOnlyDictionary<int, string>? groupNames = null)
    {
        var categories = new Dictionary<int, int>(_categoryOfGroup);
        if (categoryOfGroup is not null)
            foreach (var (groupId, categoryId) in categoryOfGroup)
                categories[groupId] = categoryId;
        var names = new Dictionary<int, string>(_groupNames);
        if (groupNames is not null)
            foreach (var (groupId, name) in groupNames)
                names[groupId] = name;
        return new DogmaStatic(
            _attributes.Values,
            _effects.Values,
            _types.Values.Concat(types),
            _sde,
            categories,
            names,
            _categoryNames,
            _factionNames,
            MissileLauncherGroups);
    }

    /// <summary>
    /// The launcher groups of the missile family (<see cref="LauncherFamilies"/>), derived from the static data when it is loaded from the SDE. Empty for a catalog
    /// built in memory without them.
    /// </summary>
    public IReadOnlySet<int> MissileLauncherGroups { get; }

    /// <summary>The timed buff collections of the static data (<c>dbuffCollections</c>), by id. Empty when the static data has none.</summary>
    public IReadOnlyDictionary<int, BuffCollection> BuffCollections { get; init; } = new Dictionary<int, BuffCollection>();

    public AttributeDef? Attribute(int id) => _attributes.GetValueOrDefault(id);

    public EffectDef? Effect(int id) => _effects.GetValueOrDefault(id);

    public TypeDef? Type(int typeId) => _types.GetValueOrDefault(typeId);

    /// <summary>The English name of a group (an empty string when the data has none).</summary>
    public string GroupName(int groupId) => _groupNames.GetValueOrDefault(groupId, "");

    /// <summary>The SDE category name for a group category identifier.</summary>
    public string CategoryName(int categoryId) => _categoryNames.GetValueOrDefault(categoryId, "");

    public string FactionName(int factionId) => _factionNames.GetValueOrDefault(factionId, "");

    /// <summary>The SDE identifier for a category name.</summary>
    public int? CategoryId(string name) => _categoryNames
        .Where(pair => pair.Value.Equals(name, StringComparison.OrdinalIgnoreCase))
        .Select(pair => (int?)pair.Key)
        .FirstOrDefault();

    /// <summary>The SDE identifier for a dogma attribute name.</summary>
    public int? AttributeId(string name) => _attributeIdsByName.TryGetValue(name, out var id) ? id : null;

    public int AttributeCount => _attributes.Count;

    public int EffectCount => _effects.Count;

    /// <summary>Every effect the static data and the built-in effects define.</summary>
    public IEnumerable<EffectDef> Effects => _effects.Values;

    /// <summary>Makes sure these types are known (reads them from the SDE when they are not).</summary>
    public async Task EnsureTypesAsync(IEnumerable<int> typeIds, CancellationToken cancellationToken = default)
    {
        var missing = typeIds.Distinct().Where(id => !_types.ContainsKey(id)).ToArray();
        if (missing.Length == 0 || _sde is null)
        {
            return;
        }
        var keys = missing.Select(id => (long)id).ToArray();
        var types = await _sde.ReadAsync("types", keys, cancellationToken);
        var dogma = (await _sde.ReadAsync("typeDogma", keys, cancellationToken)).ToDictionary(r => r.Key, r => r.Doc);
        foreach (var row in types)
        {
            var group = row.Doc.GetProperty("groupID").GetInt32();
            var attributes = new Dictionary<int, double>();
            foreach (var (attribute, field) in IntrinsicAttributes)
            {
                if (row.Doc.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.Number)
                {
                    attributes[attribute] = v.GetDouble();
                }
            }
            var effects = new List<int>();
            if (dogma.TryGetValue(row.Key, out var doc))
            {
                // a type may have attributes and no effects, or effects and no attributes
                if (doc.TryGetProperty("dogmaAttributes", out var dogmaAttributes))
                {
                    foreach (var a in dogmaAttributes.EnumerateArray())
                    {
                        attributes[a.GetProperty("attributeID").GetInt32()] = a.GetProperty("value").GetDouble();
                    }
                }
                if (doc.TryGetProperty("dogmaEffects", out var dogmaEffects))
                {
                    foreach (var e in dogmaEffects.EnumerateArray())
                    {
                        effects.Add(e.GetProperty("effectID").GetInt32());
                    }
                }
            }
            var name = row.Doc.TryGetProperty("name", out var names) ? SdeReader.English(names) : "";
            var graphic = row.Doc.TryGetProperty("graphicID", out var graphicId) && graphicId.ValueKind == JsonValueKind.Number ? graphicId.GetInt32() : 0;
            _types[(int)row.Key] = new TypeDef((int)row.Key, group, _categoryOfGroup.GetValueOrDefault(group), attributes, effects, name, graphic);
        }
    }

    /// <summary>
    /// Whether the SDE type list <paramref name="listId"/> names the type: included by its id, group or category, and not excluded by any of them (the
    /// <c>typeLists</c> of the SDE; the Gas Compressor's list names the gas groups and excludes some types). An unknown list names nothing, and so does a
    /// type that is not loaded (<see cref="EnsureTypesAsync"/> first).
    /// </summary>
    public async Task<bool> TypeListNamesAsync(int listId, int typeId, CancellationToken cancellationToken = default)
    {
        if (_sde is null || listId <= 0 || Type(typeId) is not { } type)
        {
            return false;
        }
        static bool Names(System.Text.Json.JsonElement list, string property, int id) =>
            id > 0 && list.TryGetProperty(property, out var values) && values.ValueKind == System.Text.Json.JsonValueKind.Array
            && values.EnumerateArray().Any(value => value.TryGetInt32(out var candidate) && candidate == id);
        foreach (var list in await _sde.ReadAsync("typeLists", [(long)listId], cancellationToken))
        {
            var included = Names(list.Doc, "includedTypeIDs", typeId) || Names(list.Doc, "includedGroupIDs", type.GroupId) || Names(list.Doc, "includedCategoryIDs", type.CategoryId);
            var excluded = Names(list.Doc, "excludedTypeIDs", typeId) || Names(list.Doc, "excludedGroupIDs", type.GroupId) || Names(list.Doc, "excludedCategoryIDs", type.CategoryId);
            if (included && !excluded)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>The type a source type compresses into (the SDE's <c>compressibleTypes</c>: ore, ice, gas and moon ore), or null when the type cannot be compressed.</summary>
    public async Task<int?> CompressedTypeOfAsync(int typeId, CancellationToken cancellationToken = default)
    {
        if (_sde is null || typeId <= 0)
        {
            return null;
        }
        foreach (var row in await _sde.ReadAsync("compressibleTypes", [(long)typeId], cancellationToken))
        {
            if (row.Doc.TryGetProperty("compressedTypeID", out var compressed) && compressed.TryGetInt32(out var compressedTypeId) && compressedTypeId > 0)
            {
                return compressedTypeId;
            }
        }
        return null;
    }

    /// <summary>
    /// The type a harvestable gas cloud yields: its material in the SDE (<c>typeMaterials</c>), else the compressed gas it is named for (<c>Compressed &lt;name&gt;</c>
    /// in the Compressed Gas group). Null when neither exists. A cloud type must be loaded (<see cref="EnsureTypesAsync"/> first).
    /// </summary>
    public async Task<int?> GasYieldTypeAsync(int cloudTypeId, CancellationToken cancellationToken = default)
    {
        if (_sde is null || Type(cloudTypeId) is not { } cloud)
        {
            return null;
        }
        foreach (var row in await _sde.ReadAsync("typeMaterials", [(long)cloudTypeId], cancellationToken))
        {
            if (row.Doc.TryGetProperty("materials", out var materials) && materials.ValueKind == System.Text.Json.JsonValueKind.Array
                && materials.EnumerateArray().FirstOrDefault().TryGetProperty("materialTypeID", out var material)
                && material.TryGetInt32(out var materialTypeId) && materialTypeId > 0)
            {
                return materialTypeId;
            }
        }
        _compressedGasTypeIds ??= (await TypesInGroupsAsync(["Compressed Gas"], cancellationToken))
            .Where(candidate => candidate.Name is not null)
            .GroupBy(candidate => candidate.Name!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().TypeId, StringComparer.OrdinalIgnoreCase);
        return _compressedGasTypeIds.TryGetValue($"Compressed {cloud.Name}", out var compressed) ? compressed : null;
    }

    /// <summary>Published SDE types in the named groups, with their dogma loaded.</summary>
    public async Task<IReadOnlyList<TypeDef>> TypesInGroupsAsync(IEnumerable<string> groupNames, CancellationToken cancellationToken = default)
    {
        if (_sde is null)
        {
            return [];
        }
        var names = groupNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (names.Count == 0)
        {
            return [];
        }
        var groups = (await _sde.ReadAsync("groups", cancellationToken: cancellationToken))
            .Where(row => row.Doc.TryGetProperty("name", out var n)
                && names.Contains(SdeReader.English(n))
                && row.Doc.TryGetProperty("published", out var published)
                && published.ValueKind == JsonValueKind.True)
            .Select(row => row.Key)
            .ToArray();
        if (groups.Length == 0)
        {
            return [];
        }
        var types = (await _sde.ReadByFieldAsync("types", "groupID", groups, cancellationToken))
            .Where(row => !row.Doc.TryGetProperty("published", out var published)
                || published.ValueKind != JsonValueKind.False)
            .Select(row => (int)row.Key)
            .ToArray();
        await EnsureTypesAsync(types, cancellationToken);
        return types.Select(Type).OfType<TypeDef>().ToArray();
    }

    /// <summary>Published SDE types in a named category, with dogma loaded.</summary>
    public async Task<IReadOnlyList<TypeDef>> TypesInCategoryAsync(string categoryName, CancellationToken cancellationToken = default)
    {
        if (_sde is null || CategoryId(categoryName) is not { } categoryId)
        {
            return [];
        }
        var groups = _categoryOfGroup.Where(pair => pair.Value == categoryId).Select(pair => (long)pair.Key).ToArray();
        if (groups.Length == 0)
        {
            return [];
        }
        var types = (await _sde.ReadByFieldAsync("types", "groupID", groups, cancellationToken))
            .Where(row => !row.Doc.TryGetProperty("published", out var published)
                || published.ValueKind != JsonValueKind.False)
            .Select(row => (int)row.Key)
            .ToArray();
        await EnsureTypesAsync(types, cancellationToken);
        return types.Select(Type).OfType<TypeDef>().ToArray();
    }

    public static async Task<DogmaStatic> LoadAsync(SdeReader sde, CancellationToken cancellationToken = default)
    {
        var attributes = (await sde.ReadAsync("dogmaAttributes", cancellationToken: cancellationToken)).Select(row =>
        {
            var d = row.Doc;
            return new AttributeDef(
                (int)row.Key,
                d.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                d.TryGetProperty("defaultValue", out var def) && def.ValueKind == JsonValueKind.Number ? def.GetDouble() : 0d,
                !d.TryGetProperty("stackable", out var st) || st.ValueKind != JsonValueKind.False,
                d.TryGetProperty("highIsGood", out var hg) && hg.ValueKind == JsonValueKind.True,
                d.TryGetProperty("dataType", out var dt) && dt.ValueKind == JsonValueKind.Number ? dt.GetInt32() : 0,
                d.TryGetProperty("maxAttributeID", out var max) && max.ValueKind == JsonValueKind.Number ? max.GetInt32() : 0,
                d.TryGetProperty("minAttributeID", out var min) && min.ValueKind == JsonValueKind.Number ? min.GetInt32() : 0);
        }).ToList();

        var effects = (await sde.ReadAsync("dogmaEffects", cancellationToken: cancellationToken)).Select(row =>
        {
            var modifiers = new List<Modifier>();
            if (row.Doc.TryGetProperty("modifierInfo", out var info) && info.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in info.EnumerateArray())
                {
                    var func = m.GetProperty("func").GetString() switch
                    {
                        "ItemModifier" => ModifierFunc.Item,
                        "LocationModifier" => ModifierFunc.Location,
                        "LocationGroupModifier" => ModifierFunc.LocationGroup,
                        "LocationRequiredSkillModifier" => ModifierFunc.LocationRequiredSkill,
                        "OwnerRequiredSkillModifier" => ModifierFunc.OwnerRequiredSkill,
                        _ => (ModifierFunc?)null, // a kind the engine does not know does nothing
                    };
                    if (func is null)
                    {
                        continue;
                    }
                    modifiers.Add(new Modifier(
                        func.Value,
                        m.TryGetProperty("domain", out var domain) && domain.ValueKind == JsonValueKind.String ? domain.GetString() : null,
                        (Operation)m.GetProperty("operation").GetInt32(),
                        m.GetProperty("modifiedAttributeID").GetInt32(),
                        m.GetProperty("modifyingAttributeID").GetInt32(),
                        m.TryGetProperty("groupID", out var g) ? g.GetInt32() : 0,
                        m.TryGetProperty("skillTypeID", out var s) ? s.GetInt32() : 0));
                }
            }
            modifiers.AddRange(BuiltInEffects.ClientModifiers((int)row.Key));
            return new EffectDef(
                (int)row.Key,
                row.Doc.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                row.Doc.TryGetProperty("effectCategoryID", out var c) ? c.GetInt32() : 0,
                modifiers)
            {
                Guid = row.Doc.TryGetProperty("guid", out var guid) && guid.ValueKind == JsonValueKind.String ? guid.GetString() ?? "" : "",
                RangeAttributeId = row.Doc.TryGetProperty("rangeAttributeID", out var range) && range.ValueKind == JsonValueKind.Number ? range.GetInt32() : 0,
                DurationAttributeId = row.Doc.TryGetProperty("durationAttributeID", out var duration) && duration.ValueKind == JsonValueKind.Number ? duration.GetInt32() : 0,
                FalloffAttributeId = row.Doc.TryGetProperty("falloffAttributeID", out var falloff) && falloff.ValueKind == JsonValueKind.Number ? falloff.GetInt32() : 0,
                DischargeAttributeId = row.Doc.TryGetProperty("dischargeAttributeID", out var discharge) && discharge.ValueKind == JsonValueKind.Number ? discharge.GetInt32() : 0,
                ChanceAttributeId = row.Doc.TryGetProperty("chanceAttributeID", out var chance) && chance.ValueKind == JsonValueKind.Number ? chance.GetInt32() : 0,
            };
        }).ToList();

        var groupRows = await sde.ReadAsync("groups", cancellationToken: cancellationToken);
        var groups = groupRows.ToDictionary(g => (int)g.Key, g => g.Doc.GetProperty("categoryID").GetInt32());
        var groupNames = groupRows.ToDictionary(g => (int)g.Key, g => g.Doc.TryGetProperty("name", out var n) ? SdeReader.English(n) : "");
        var categoryNames = (await sde.ReadAsync("categories", cancellationToken: cancellationToken))
            .ToDictionary(c => (int)c.Key, c => c.Doc.TryGetProperty("name", out var n) ? SdeReader.English(n) : "");
        var factionNames = (await sde.ReadAsync("factions", cancellationToken: cancellationToken))
            .ToDictionary(f => (int)f.Key, f => f.Doc.TryGetProperty("name", out var n) ? SdeReader.English(n) : "");
        var effectNames = effects.ToDictionary(e => e.Id, e => e.Name);
        var missileLaunchers = await MissileLauncherGroupsAsync(sde, effectNames, groups, cancellationToken);
        var buffs = (await sde.ReadAsync("dbuffCollections", cancellationToken: cancellationToken))
            .Select(row => BuffCollection.From((int)row.Key, row.Doc))
            .OfType<BuffCollection>()
            .ToDictionary(collection => collection.Id);
        return new DogmaStatic(attributes, BuiltInEffects.Merge(effects), [], sde, groups, groupNames, categoryNames, factionNames, missileLaunchers)
        {
            BuffCollections = buffs,
        };
    }

    /// <summary>
    /// The launcher groups of the missile family (<see cref="LauncherFamilies.MissileGroups"/>): the types carrying <c>useMissiles</c>, their charge groups, and the
    /// default firing effect of each published charge of those groups. Four reads in all; the static data does not change while the server runs.
    /// </summary>
    private static async Task<IReadOnlySet<int>> MissileLauncherGroupsAsync(SdeReader sde, IReadOnlyDictionary<int, string> effectNames, IReadOnlyDictionary<int, int> categoryOfGroup, CancellationToken cancellationToken)
    {
        var launcherEffect = effectNames.Where(pair => pair.Value.Equals(LauncherFamilies.LauncherEffectName, StringComparison.OrdinalIgnoreCase)).Select(pair => pair.Key).ToArray();
        if (launcherEffect.Length == 0)
        {
            return new HashSet<int>();
        }
        var containment = JsonSerializer.Serialize(new { dogmaEffects = new[] { new { effectID = launcherEffect[0] } } });
        var carrying = await sde.ReadContainingAsync("typeDogma", containment, cancellationToken);
        var typeDocs = (await sde.ReadAsync("types", carrying.Select(row => row.Key), cancellationToken)).ToDictionary(row => row.Key, row => row.Doc);
        var launchers = new List<LauncherCandidate>();
        foreach (var row in carrying)
        {
            if (!typeDocs.TryGetValue(row.Key, out var type))
            {
                continue;
            }
            var groupId = type.GetProperty("groupID").GetInt32();
            var chargeGroups = new List<int>();
            if (row.Doc.TryGetProperty("dogmaAttributes", out var attributes) && attributes.ValueKind == JsonValueKind.Array)
            {
                foreach (var attribute in attributes.EnumerateArray())
                {
                    if (LauncherFamilies.ChargeGroupAttributes.Contains(attribute.GetProperty("attributeID").GetInt32()))
                    {
                        chargeGroups.Add((int)attribute.GetProperty("value").GetDouble());
                    }
                }
            }
            launchers.Add(new LauncherCandidate((int)row.Key, groupId, IsPublished(type), categoryOfGroup.GetValueOrDefault(groupId), chargeGroups));
        }
        var chargeGroupIds = launchers.SelectMany(launcher => launcher.ChargeGroups).Where(group => group > 0).Distinct().Select(group => (long)group).ToArray();
        IReadOnlyList<SdeRow> chargeTypes = chargeGroupIds.Length == 0
            ? new List<SdeRow>()
            : await sde.ReadByFieldAsync("types", "groupID", chargeGroupIds, cancellationToken);
        var chargeDogma = (await sde.ReadAsync("typeDogma", chargeTypes.Select(row => row.Key), cancellationToken)).ToDictionary(row => row.Key, row => row.Doc);
        var charges = chargeTypes.Select(row => new ChargeCandidate(
            (int)row.Key,
            row.Doc.GetProperty("groupID").GetInt32(),
            IsPublished(row.Doc),
            chargeDogma.TryGetValue(row.Key, out var dogma) ? DefaultEffectName(dogma, effectNames) : null)).ToList();
        return LauncherFamilies.MissileGroups(launchers, charges).ToHashSet();
    }

    /// <summary>A type is published unless its record says it is not (the SDE leaves the flag out of a few records).</summary>
    private static bool IsPublished(JsonElement type) =>
        !type.TryGetProperty("published", out var published) || published.ValueKind != JsonValueKind.False;

    /// <summary>The name of the effect a type fires by default (<c>isDefault</c> in its dogma), or null when it has none.</summary>
    private static string? DefaultEffectName(JsonElement doc, IReadOnlyDictionary<int, string> effectNames)
    {
        if (doc.ValueKind != JsonValueKind.Object || !doc.TryGetProperty("dogmaEffects", out var effects) || effects.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        foreach (var effect in effects.EnumerateArray())
        {
            if (effect.TryGetProperty("isDefault", out var isDefault) && isDefault.ValueKind == JsonValueKind.True
                && effectNames.TryGetValue(effect.GetProperty("effectID").GetInt32(), out var name))
            {
                return name;
            }
        }
        return null;
    }
}

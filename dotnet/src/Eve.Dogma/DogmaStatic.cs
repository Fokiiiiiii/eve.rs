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
    private readonly ConcurrentDictionary<int, TypeDef> _types = new();
    private readonly Dictionary<int, AttributeDef> _attributes;
    private readonly Dictionary<int, EffectDef> _effects;
    private readonly Dictionary<int, int> _categoryOfGroup;
    private readonly Dictionary<int, string> _groupNames;
    private readonly Dictionary<int, string> _categoryNames;
    private readonly Dictionary<int, string> _factionNames;
    private readonly Dictionary<string, int> _attributeIdsByName;

    public DogmaStatic(IEnumerable<AttributeDef> attributes, IEnumerable<EffectDef> effects, IEnumerable<TypeDef> types, SdeReader? sde = null, IReadOnlyDictionary<int, int>? categoryOfGroup = null, IReadOnlyDictionary<int, string>? groupNames = null, IReadOnlyDictionary<int, string>? categoryNames = null, IReadOnlyDictionary<int, string>? factionNames = null)
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
            _factionNames);
    }

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
            return new EffectDef(
                (int)row.Key,
                row.Doc.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                row.Doc.TryGetProperty("effectCategoryID", out var c) ? c.GetInt32() : 0,
                modifiers)
            {
                Guid = row.Doc.TryGetProperty("guid", out var guid) && guid.ValueKind == JsonValueKind.String ? guid.GetString() ?? "" : "",
                RangeAttributeId = row.Doc.TryGetProperty("rangeAttributeID", out var range) && range.ValueKind == JsonValueKind.Number ? range.GetInt32() : 0,
                DurationAttributeId = row.Doc.TryGetProperty("durationAttributeID", out var duration) && duration.ValueKind == JsonValueKind.Number ? duration.GetInt32() : 0,
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
        return new DogmaStatic(attributes, BuiltInEffects.Merge(effects), [], sde, groups, groupNames, categoryNames, factionNames);
    }
}

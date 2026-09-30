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

    public DogmaStatic(IEnumerable<AttributeDef> attributes, IEnumerable<EffectDef> effects, IEnumerable<TypeDef> types, SdeReader? sde = null, IReadOnlyDictionary<int, int>? categoryOfGroup = null, IReadOnlyDictionary<int, string>? groupNames = null)
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
    }

    public AttributeDef? Attribute(int id) => _attributes.GetValueOrDefault(id);

    public EffectDef? Effect(int id) => _effects.GetValueOrDefault(id);

    public TypeDef? Type(int typeId) => _types.GetValueOrDefault(typeId);

    /// <summary>The English name of a group (an empty string when the data has none).</summary>
    public string GroupName(int groupId) => _groupNames.GetValueOrDefault(groupId, "");

    public int AttributeCount => _attributes.Count;

    public int EffectCount => _effects.Count;

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
                DurationAttributeId = row.Doc.TryGetProperty("durationAttributeID", out var duration) && duration.ValueKind == JsonValueKind.Number ? duration.GetInt32() : 0,
                DischargeAttributeId = row.Doc.TryGetProperty("dischargeAttributeID", out var discharge) && discharge.ValueKind == JsonValueKind.Number ? discharge.GetInt32() : 0,
            };
        }).ToList();

        var groupRows = await sde.ReadAsync("groups", cancellationToken: cancellationToken);
        var groups = groupRows.ToDictionary(g => (int)g.Key, g => g.Doc.GetProperty("categoryID").GetInt32());
        var groupNames = groupRows.ToDictionary(g => (int)g.Key, g => g.Doc.TryGetProperty("name", out var n) ? SdeReader.English(n) : "");
        return new DogmaStatic(attributes, BuiltInEffects.Merge(effects), [], sde, groups, groupNames);
    }
}

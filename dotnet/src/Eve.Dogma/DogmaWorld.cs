namespace Eve.Dogma;

/// <summary>An item as dogma sees it: what it is, where it is, whose it is, how far it is switched on, and the values it carries of its own.</summary>
public sealed class DogmaItem
{
    internal readonly Dictionary<int, double> Own = [];

    public DogmaItem(long id, int typeId, long ownerId, long locationId, int flagId = 0, ItemState state = ItemState.Offline)
    {
        Id = id;
        TypeId = typeId;
        OwnerId = ownerId;
        LocationId = locationId;
        FlagId = flagId;
        State = state;
    }

    public long Id { get; }

    public int TypeId { get; }

    public long OwnerId { get; }

    public long LocationId { get; }

    public int FlagId { get; }

    public ItemState State { get; internal set; }

    /// <summary>The values this item carries of its own, over its type's: a skill's level, the damage a ship has taken.</summary>
    public IReadOnlyDictionary<int, double> OwnValues => Own;
}

/// <summary>
/// The items whose attributes are being worked out (a character, the skills and implants it has, the ship it flies and what is fitted to it)
/// and the modifiers between them. Attribute values are computed on demand and remembered until the world changes.
/// Not thread-safe: one world belongs to one owner (a session's fitting, a ship in a scene).
/// </summary>
/// <remarks>
/// The rules are the client's (<c>dogma/const.py</c>, <c>dogma/attributes</c>): a base value, then the modifiers in operation order, with the
/// stacking penalty on attributes that are not stackable, cut to the attribute's cap and floor. See docs/adr/0004-dogma-is-a-pure-library.md.
/// </remarks>
public sealed class DogmaWorld
{
    /// <summary>The level of a skill. The level is given, so no modifier changes it.</summary>
    public const int SkillLevel = 280;

    private const int MaxPenalisedPerOperation = 8;

    private static readonly double[] Denominators = [.. Enumerable.Range(0, MaxPenalisedPerOperation).Select(i => Math.Exp(Math.Pow(i / 2.67, 2.0)))];

    private static readonly HashSet<int> UnpenalisedCategories = [16 /* skill */, 20 /* implant */, 6 /* ship */, 8 /* charge */, 32 /* subsystem */];

    /// <summary>The power and processing attributes are kept to two decimals.</summary>
    private static readonly HashSet<int> RoundedAttributes = [49, 15, 11, 48, 50, 30];

    private readonly record struct Source(DogmaItem Item, EffectDef Effect, Modifier Modifier, long DomainItemId);

    private readonly DogmaStatic _static;
    private readonly Dictionary<long, DogmaItem> _items = [];
    private readonly Dictionary<(long Item, int Attribute), double> _values = [];
    private readonly HashSet<(long Item, int Attribute)> _evaluating = [];
    private Dictionary<int, List<Source>>? _sources;

    public DogmaWorld(DogmaStatic statics, long characterId = 0, long shipId = 0)
    {
        _static = statics;
        CharacterId = characterId;
        ShipId = shipId;
    }

    /// <summary>The character whose skills and implants apply (0 when the world has none, as for a ship flown by the game).</summary>
    public long CharacterId { get; private set; }

    /// <summary>The ship the character flies: the ship the skills act on.</summary>
    public long ShipId { get; private set; }

    public IEnumerable<DogmaItem> Items => _items.Values;

    public DogmaItem? Item(long id) => _items.GetValueOrDefault(id);

    public DogmaItem Add(DogmaItem item)
    {
        _items[item.Id] = item;
        Changed();
        return item;
    }

    public bool Remove(long id)
    {
        var removed = _items.Remove(id);
        if (removed)
        {
            Changed();
        }
        return removed;
    }

    public void SetShip(long shipId)
    {
        ShipId = shipId;
        Changed();
    }

    public void SetState(long id, ItemState state)
    {
        if (_items.TryGetValue(id, out var item) && item.State != state)
        {
            item.State = state;
            Changed();
        }
    }

    /// <summary>Sets a value the item carries of its own, over its type's.</summary>
    public void SetValue(long id, int attributeId, double value)
    {
        if (_items.TryGetValue(id, out var item))
        {
            item.Own[attributeId] = value;
            Changed();
        }
    }

    /// <summary>The value of an attribute of an item, after every modifier that reaches it.</summary>
    public double Get(long itemId, int attributeId)
    {
        if (_values.TryGetValue((itemId, attributeId), out var known))
        {
            return known;
        }
        if (!_items.TryGetValue(itemId, out var item))
        {
            return _static.Attribute(attributeId)?.DefaultValue ?? 0d;
        }
        var value = Compute(item, attributeId);
        _values[(itemId, attributeId)] = value;
        return value;
    }

    /// <summary>
    /// Every attribute the item has, each with its value now: its type's, its own, and the ones a modifier reaches (a skill's bonus to
    /// an attribute the type never had gives the item that attribute).
    /// </summary>
    public IReadOnlyDictionary<int, double> Attributes(long itemId)
    {
        var result = new SortedDictionary<int, double>();
        if (!_items.TryGetValue(itemId, out var item))
        {
            return result;
        }
        var ids = new HashSet<int>(item.Own.Keys);
        if (_static.Type(item.TypeId) is { } type)
        {
            ids.UnionWith(type.Attributes.Keys);
        }
        foreach (var (attribute, sources) in Sources())
        {
            if (!ids.Contains(attribute) && sources.Any(s => Reaches(s, item)))
            {
                ids.Add(attribute);
            }
        }
        foreach (var id in ids)
        {
            result[id] = Get(itemId, id);
        }
        return result;
    }

    /// <summary>The effects of an item that are running given how far it is switched on.</summary>
    public IEnumerable<EffectDef> RunningEffects(DogmaItem item)
    {
        if (_static.Type(item.TypeId) is not { } type)
        {
            yield break;
        }
        foreach (var effectId in type.EffectIds)
        {
            if (_static.Effect(effectId) is { } effect && Runs(effect, item.State))
            {
                yield return effect;
            }
        }
    }

    private static bool Runs(EffectDef effect, ItemState state) => effect.Category switch
    {
        EffectDef.Passive => true,
        EffectDef.Online => state >= ItemState.Online,
        EffectDef.Activation => state >= ItemState.Active,
        EffectDef.Overload => state >= ItemState.Overloaded,
        _ => false, // an effect on a target or on an area needs a target or an area
    };

    private void Changed()
    {
        _values.Clear();
        _sources = null;
    }

    private Dictionary<int, List<Source>> Sources()
    {
        if (_sources is not null)
        {
            return _sources;
        }
        var index = new Dictionary<int, List<Source>>();
        foreach (var item in _items.Values.OrderBy(i => i.Id))
        {
            foreach (var effect in RunningEffects(item))
            {
                foreach (var modifier in effect.Modifiers)
                {
                    var domain = Resolve(item, modifier.Domain);
                    if (domain < 0)
                    {
                        continue;
                    }
                    if (!index.TryGetValue(modifier.ModifiedAttributeId, out var list))
                    {
                        index[modifier.ModifiedAttributeId] = list = [];
                    }
                    list.Add(new Source(item, effect, modifier, domain));
                }
            }
        }
        return _sources = index;
    }

    /// <summary>The item a modifier's domain names, counted from the item the effect is on; -1 when there is none.</summary>
    private long Resolve(DogmaItem source, string? domain)
    {
        switch (domain)
        {
            case "itemID":
                return source.Id;
            case "charID":
                return _items.ContainsKey(source.OwnerId) ? source.OwnerId : -1;
            case "shipID":
                if (ShipId == 0 || !_items.ContainsKey(ShipId))
                {
                    return -1;
                }
                // the ship itself, what is fitted in it, or what the pilot has (the pilot's skills act on the ship he flies)
                for (var at = source; ; )
                {
                    if (at.Id == ShipId)
                    {
                        return ShipId;
                    }
                    if (!_items.TryGetValue(at.LocationId, out var up) || up == at)
                    {
                        break;
                    }
                    at = up;
                }
                // what the pilot carries (skills, implants) acts on the ship he flies, in the ship or not; a module lying in a hangar does not
                return CharacterId != 0 && (source.Id == CharacterId || source.LocationId == CharacterId) ? ShipId : -1;
            default:
                return -1; // targets, other items, structures: nothing in a station reaches them
        }
    }

    private bool Reaches(in Source s, DogmaItem target)
    {
        var m = s.Modifier;
        return m.Func switch
        {
            ModifierFunc.Item => target.Id == s.DomainItemId,
            ModifierFunc.Location => target.LocationId == s.DomainItemId,
            ModifierFunc.LocationGroup => target.LocationId == s.DomainItemId && GroupOf(target) == m.GroupId,
            ModifierFunc.LocationRequiredSkill => target.LocationId == s.DomainItemId && Requires(target, SkillOf(s)),
            ModifierFunc.OwnerRequiredSkill => target.OwnerId == s.DomainItemId && Requires(target, SkillOf(s)),
            _ => false,
        };
    }

    private static int SkillOf(in Source s) => s.Modifier.SkillTypeId == Modifier.OwnSkill ? s.Item.TypeId : s.Modifier.SkillTypeId;

    private int GroupOf(DogmaItem item) => _static.Type(item.TypeId)?.GroupId ?? 0;

    private bool Requires(DogmaItem item, int skillTypeId) => _static.Type(item.TypeId)?.RequiredSkills.Contains(skillTypeId) ?? false;

    private double BaseValue(DogmaItem item, int attributeId)
    {
        if (item.Own.TryGetValue(attributeId, out var own))
        {
            return own;
        }
        if (attributeId == BuiltInEffects.IsOnline)
        {
            return item.State >= ItemState.Online ? 1d : 0d; // whether a module is online is its state, not something its type says
        }
        if (_static.Type(item.TypeId) is { } type && type.Attributes.TryGetValue(attributeId, out var typed))
        {
            return typed;
        }
        return _static.Attribute(attributeId)?.DefaultValue ?? 0d;
    }

    private double Compute(DogmaItem item, int attributeId)
    {
        var baseValue = BaseValue(item, attributeId);
        if (attributeId == SkillLevel)
        {
            return baseValue;
        }
        var key = (item.Id, attributeId);
        if (!_evaluating.Add(key))
        {
            return baseValue; // an attribute that depends on itself keeps its base value
        }
        try
        {
            var def = _static.Attribute(attributeId);
            double? cap = null;
            double? floor = null;
            if (def is { MaxAttributeId: > 0 })
            {
                cap = Get(item.Id, def.MaxAttributeId);
                baseValue = Math.Min(baseValue, cap.Value);
            }
            if (def is { MinAttributeId: > 0 })
            {
                floor = Get(item.Id, def.MinAttributeId);
                baseValue = Math.Max(baseValue, floor.Value);
            }

            var value = ApplyModifiers(item, attributeId, baseValue, def is null or { Stackable: true });
            if (cap is not null)
            {
                value = Math.Min(value, cap.Value);
            }
            if (floor is not null)
            {
                value = Math.Max(value, floor.Value);
            }
            if (def?.DataType is 10 or 11 or 12)
            {
                value = Math.Truncate(value);
            }
            if (RoundedAttributes.Contains(attributeId))
            {
                value = Math.Round(value, 2, MidpointRounding.AwayFromZero);
            }
            return value;
        }
        finally
        {
            _evaluating.Remove(key);
        }
    }

    private double ApplyModifiers(DogmaItem item, int attributeId, double value, bool stackable)
    {
        if (!Sources().TryGetValue(attributeId, out var candidates))
        {
            return value;
        }
        var incoming = new List<(Operation Op, double Value, bool Penalised)>();
        foreach (var source in candidates)
        {
            if (!Reaches(source, item))
            {
                continue;
            }
            var op = source.Modifier.Operation;
            var penalised = !stackable && Operations.IsPenalisable(op) && !UnpenalisedCategories.Contains(_static.Type(source.Item.TypeId)?.CategoryId ?? 0);
            incoming.Add((op, Get(source.Item.Id, source.Modifier.ModifyingAttributeId), penalised));
        }
        if (incoming.Count == 0)
        {
            return value;
        }

        foreach (var op in Operations.Precedence)
        {
            var factors = new List<double>();
            foreach (var (o, v, penalised) in incoming)
            {
                if (o != op)
                {
                    continue;
                }
                if (penalised)
                {
                    factors.Add(Operations.Apply(op, 1d, v));
                }
                else
                {
                    value = Operations.Apply(op, value, v);
                }
            }
            if (factors.Count > 0)
            {
                value *= PenalisedFactor(factors);
            }
        }
        return value;
    }

    /// <summary>
    /// The factor of the modifiers that are penalised: the ones that weaken come strongest first, then the ones that strengthen strongest first,
    /// each group on its own row of the penalty table, and no more than eight of each count.
    /// </summary>
    internal static double PenalisedFactor(List<double> factors)
    {
        factors.Sort();
        var split = factors.Count(f => f <= 1d);
        var ordered = factors.Take(split).Take(MaxPenalisedPerOperation).Select((f, i) => Step(f, i))
            .Concat(Enumerable.Reverse(factors.Skip(split)).Take(MaxPenalisedPerOperation).Select((f, i) => Step(f, i)));
        var product = 0d;
        var first = true;
        foreach (var step in ordered)
        {
            product = first ? step : product * step;
            first = false;
        }
        return product;

        static double Step(double factor, int index) => (factor - 1d) * (1d / Denominators[index]) + 1d;
    }
}

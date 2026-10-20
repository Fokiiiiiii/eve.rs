namespace Eve.Dogma;

/// <summary>An item as dogma sees it: what it is, where it is, whose it is, how far it is switched on, and the values it carries of its own.</summary>
public sealed class DogmaItem
{
    internal readonly Dictionary<int, double> Own = [];

    /// <param name="baseTypeId">
    /// The type the item's base attribute values come from, when that is not its own type: a mutated module is typed as its result, but its
    /// values are those of the input it was made from. The skill requirements always come from <paramref name="typeId"/>. Null: the item's own type.
    /// </param>
    /// <param name="ownValues">The values the item carries of its own from the start (a mutated module's rolled values), over its base. Null: none.</param>
    public DogmaItem(long id, int typeId, long ownerId, long locationId, int flagId = 0, ItemState state = ItemState.Offline, IReadOnlySet<int>? activeEffectIds = null,
        int? baseTypeId = null, IReadOnlyDictionary<int, double>? ownValues = null)
    {
        Id = id;
        TypeId = typeId;
        BaseTypeId = baseTypeId;
        OwnerId = ownerId;
        LocationId = locationId;
        FlagId = flagId;
        State = state;
        ActiveEffectIds = activeEffectIds;
        if (ownValues is not null)
        {
            foreach (var (attributeId, value) in ownValues)
            {
                Own[attributeId] = value;
            }
        }
    }

    public long Id { get; }

    public int TypeId { get; }

    /// <summary>The type whose attribute values this item takes as its base, when it is not <see cref="TypeId"/> (null: it takes them from <see cref="TypeId"/>).</summary>
    public int? BaseTypeId { get; }

    public long OwnerId { get; }

    public long LocationId { get; }

    public int FlagId { get; }

    public ItemState State { get; internal set; }

    /// <summary>When supplied, limits an active item to effects the server actually activated.</summary>
    public IReadOnlySet<int>? ActiveEffectIds { get; }

    /// <summary>The values this item carries of its own, over its type's: a skill's level, the damage a ship has taken.</summary>
    public IReadOnlyDictionary<int, double> OwnValues => Own;
}

/// <summary>
/// The items whose attributes are being worked out (a character, the skills and implants it has, the ship it flies and what is fitted to it)
/// and the modifiers between them. Attribute values are computed on demand and remembered until the world changes.
/// </summary>
/// <remarks>
/// The rules are the client's (<c>dogma/const.py</c>, <c>dogma/attributes</c>): a base value, then the modifiers in operation order, with the
/// </remarks>
public sealed class DogmaWorld
{
    /// <summary>The level of a skill. The level is given, so no modifier changes it.</summary>
    public const int SkillLevel = 280;

    private const int ChargeCategory = 8;

    private const int MaxPenalisedPerOperation = 8;

    private static readonly double[] Denominators = [.. Enumerable.Range(0, MaxPenalisedPerOperation).Select(i => Math.Exp(Math.Pow(i / 2.67, 2.0)))];

    private static readonly HashSet<int> UnpenalisedCategories = [16 /* skill */, 20 /* implant */, 6 /* ship */, 8 /* charge */, 32 /* subsystem */];

    /// <summary>The power and processing attributes are kept to two decimals.</summary>
    private static readonly HashSet<int> RoundedAttributes = [49, 15, 11, 48, 50, 30];

    /// <param name="Buff">A buff's modifier (<see cref="SetBuffs"/>): its stacking penalty follows its attribute, as EVE.js does for a buff, whatever its source.</param>
    private readonly record struct Source(DogmaItem Item, EffectDef Effect, Modifier Modifier, long DomainItemId, bool Buff = false);

    private readonly DogmaStatic _static;
    private readonly Dictionary<long, DogmaItem> _items = [];
    private readonly Dictionary<(long Item, int Attribute), double> _values = [];
    private readonly HashSet<(long Item, int Attribute)> _evaluating = [];
    private Dictionary<int, List<Source>>? _sources;
    /// <summary>The modifiers an assumption (<see cref="Assume"/>) adds, each with the item it is on.</summary>
    private readonly List<(DogmaItem Item, EffectDef Effect)> _assumed = [];
    /// <summary>The buffs each item carries (<see cref="SetBuffs"/>): passive effects that run on the item whatever its state.</summary>
    private readonly Dictionary<long, IReadOnlyList<EffectDef>> _buffs = [];

    public DogmaWorld(DogmaStatic statics, long characterId = 0, long shipId = 0, long structureId = 0)
    {
        _static = statics;
        CharacterId = characterId;
        ShipId = shipId;
        StructureId = structureId;
    }

    /// <summary>The character whose skills and implants apply (0 when the world has none, as for a ship flown by the game).</summary>
    public long CharacterId { get; private set; }

    /// <summary>The ship the character flies: the ship the skills act on.</summary>
    public long ShipId { get; private set; }

    /// <summary>The structure the modules and skills of a structure act on, as the ship is for a ship (0 when the world has no structure).</summary>
    public long StructureId { get; private set; }

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

    public void SetStructure(long structureId)
    {
        StructureId = structureId;
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

    /// <summary>
    /// Works out the world as if an item were in another state and ran more modifiers, until the returned scope is disposed; the world is the same
    /// after it. An effect uses it to work out the numbers it is going to tell (before it starts), so they follow the rules of the world: precedence,
    /// the stacking penalty and a pinned value (a PostAssign) all apply. Scopes end in the reverse order they began. An unknown item assumes nothing.
    /// </summary>
    public IDisposable Assume(long itemId, ItemState state, IReadOnlyList<Modifier>? modifiers = null)
    {
        if (!_items.TryGetValue(itemId, out var item))
        {
            return new Scope(() => { });
        }
        var previous = item.State;
        var assumed = modifiers is { Count: > 0 } ? new EffectDef(0, "assumed", EffectDef.Passive, modifiers) : null;
        item.State = state;
        if (assumed is not null)
        {
            _assumed.Add((item, assumed));
        }
        Changed();
        return new Scope(() =>
        {
            item.State = previous;
            if (assumed is not null)
            {
                _assumed.RemoveAt(_assumed.FindLastIndex(a => ReferenceEquals(a.Effect, assumed)));
            }
            Changed();
        });
    }

    /// <summary>Ends an assumption (<see cref="Assume"/>) when disposed; a second dispose does nothing.</summary>
    private sealed class Scope(Action end) : IDisposable
    {
        private Action? _end = end;

        public void Dispose()
        {
            var end = _end;
            _end = null;
            end?.Invoke();
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
        if (item.BaseTypeId is { } baseTypeId)
        {
            if (_static.Type(baseTypeId) is { } baseType)
            {
                ids.UnionWith(baseType.Attributes.Keys.Where(attribute => !TypeDef.IsRequiredSkillAttribute(attribute)));
            }
            if (_static.Type(item.TypeId) is { } resultType)
            {
                ids.UnionWith(resultType.Attributes.Keys.Where(TypeDef.IsRequiredSkillAttribute));
            }
        }
        else if (_static.Type(item.TypeId) is { } type)
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
            if ((item.ActiveEffectIds is null || item.ActiveEffectIds.Contains(effectId)) &&
                _static.Effect(effectId) is { } effect && Runs(effect, item.State))
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
        _ => false, // an effect on a target or an area needs a target or an area
    };

    /// <summary>
    /// Replaces the buffs an item carries (the modifiers of the timed buff collections, <see cref="BuffRegistry"/>): each effect runs on the item as a passive
    /// effect, and its modifiers carry their own values (<see cref="Modifier.Literal"/>). An empty list takes the item's buffs away.
    /// </summary>
    public void SetBuffs(long itemId, IReadOnlyList<EffectDef> effects)
    {
        if (effects.Count == 0)
        {
            _buffs.Remove(itemId);
        }
        else
        {
            _buffs[itemId] = effects;
        }
        Changed();
    }

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
        void Index(DogmaItem item, EffectDef effect, bool buff = false)
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
                list.Add(new Source(item, effect, modifier, domain, buff));
            }
        }
        foreach (var item in _items.Values.OrderBy(i => i.Id))
        {
            foreach (var effect in RunningEffects(item))
            {
                Index(item, effect);
            }
        }
        foreach (var (item, effect) in _assumed)
        {
            Index(item, effect);
        }
        foreach (var (itemId, effects) in _buffs)
        {
            if (_items.TryGetValue(itemId, out var buffed))
            {
                foreach (var effect in effects)
                {
                    Index(buffed, effect, buff: true);
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
                return ResolveRoot(source, ShipId);
            case "structureID":
                return ResolveRoot(source, StructureId);
            case "otherID":
                // the charge in a module, from the module's side, and the module a charge is loaded in, from the charge's: the item that shares its
                // location and its slot (a charge is an item of the ship whose flag is the flag of the module it sits in)
                return Partner(source)?.Id ?? -1;
            default:
                // targetID and target name the target of a projected module. The static world has no target, so they stay unresolved here, and
                // nothing applies them yet: ModuleEffects skips them (its DomainItemId has no case for them) and ProjectedEffect reads only the
                // module's own attributes. Other items (a drone, a fleet member) are not reached by any domain in this world either.
                return -1;
        }
    }

    /// <summary>
    /// The root a modifier's domain names, the ship or the structure: the root itself, what is fitted in it, or what the pilot carries (his skills
    /// and implants act on the root he flies or sits in, in it or not). -1 when there is no such root in the world.
    /// </summary>
    private long ResolveRoot(DogmaItem source, long rootId)
    {
        if (rootId == 0 || !_items.ContainsKey(rootId))
        {
            return -1;
        }
        for (var at = source; ; )
        {
            if (at.Id == rootId)
            {
                return rootId;
            }
            if (!_items.TryGetValue(at.LocationId, out var up) || up == at)
            {
                break;
            }
            at = up;
        }
        // what the pilot carries (skills, implants) acts on the root, in it or not; a module lying in a hangar does not
        return CharacterId != 0 && (source.Id == CharacterId || source.LocationId == CharacterId) ? rootId : -1;
    }

    /// <summary>Whether an item is a charge (ammunition, a crystal, a missile: the items that are loaded into a module).</summary>
    public bool IsCharge(DogmaItem item) => _static.Type(item.TypeId)?.CategoryId == ChargeCategory;

    /// <summary>The item a module has loaded, or the module a charge is loaded in: the other item of the same location and slot; null when there is none.</summary>
    public DogmaItem? Partner(DogmaItem item) =>
        item.FlagId <= 0
            ? null
            : _items.Values.OrderBy(other => other.Id).FirstOrDefault(other => other.Id != item.Id && other.LocationId == item.LocationId && other.FlagId == item.FlagId && IsCharge(other) != IsCharge(item));

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
        if (TypeAttribute(item, attributeId) is { } typed)
        {
            return typed;
        }
        return _static.Attribute(attributeId)?.DefaultValue ?? 0d;
    }

    /// <summary>
    /// The value the item's type gives an attribute, or null when it gives none. An item with a base type takes every attribute from it, except the
    /// skill requirements (and their levels), which come from its own type only: a mutated module needs the skills of its result (T2), whatever its
    /// base type required, so a skill the result does not name is no requirement at all.
    /// </summary>
    private double? TypeAttribute(DogmaItem item, int attributeId)
    {
        if (item.BaseTypeId is not { } baseTypeId)
        {
            return _static.Type(item.TypeId) is { } type && type.Attributes.TryGetValue(attributeId, out var typed) ? typed : null;
        }
        if (TypeDef.IsRequiredSkillAttribute(attributeId))
        {
            return _static.Type(item.TypeId) is { } resultType && resultType.Attributes.TryGetValue(attributeId, out var skill) ? skill : null;
        }
        return _static.Type(baseTypeId) is { } baseType && baseType.Attributes.TryGetValue(attributeId, out var fromBase) ? fromBase : null;
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
            var penalised = !stackable && Operations.IsPenalisable(op)
                && (source.Buff || !UnpenalisedCategories.Contains(_static.Type(source.Item.TypeId)?.CategoryId ?? 0));
            incoming.Add((op, source.Modifier.Literal ?? Get(source.Item.Id, source.Modifier.ModifyingAttributeId), penalised));
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

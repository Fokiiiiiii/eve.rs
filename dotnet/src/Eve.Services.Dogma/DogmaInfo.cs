using Eve.Dogma;
using Eve.Marshal;
using Eve.Ships;
using Eve.Services.Ships;

namespace Eve.Services.Dogma;

/// <summary>
/// Builds what <c>dogmaIM.GetAllInfo</c> answers: for the ship, what is fitted to it and the character, their attributes and the
/// effects running, and the brain of the character. The attribute values are the engine's; which attributes are listed follows the
/// reference (docs/compat/DEVIATIONS.md #11 and #12).
/// </summary>
public sealed class DogmaInfo(DogmaStatic statics)
{
    private const int CharacterGroup = 1;

    /// <summary>The reference files the character under category 3.</summary>
    private const int CharacterCategory = 3;

    private const int Mass = 4;
    private const int Capacity = 38;
    private const int Volume = 161;
    private const int Radius = 162;
    private const int Damage = 3;
    private const int IsOnline = 2;
    private const int Charge = 18;
    private const int ShieldCharge = 264;
    private const int ArmorDamage = 266;
    private const int HullPoints = 9;
    private const int ShieldCapacity = 263;
    private const int ArmorHitPoints = 265;
    private const int CapacitorCapacity = 482;
    private const int Quantity = 805;
    private const int PilotSecurityStatus = 2610;

    /// <summary>Every item is told about these, with these values when its type has none of its own.</summary>
    private static readonly (int Attribute, double Default)[] Intrinsic = [(Mass, 0), (Capacity, 0), (Volume, 0), (Radius, 1)];

    /// <summary>What every ship is told about its fitting.</summary>
    private static readonly int[] FittingLoads = [15, 49, 1152];

    /// <summary>What the character is always told about itself: how it learns, its limits, what it can build, its clones and its standing.</summary>
    public static readonly int[] CharacterInfoAttributes =
        [164, 165, 166, 167, 168, 192, 196, 219, 385, 387, 398, 467, 979, 1174, 1267, 1277, 1921, 1959, 2610, 2662, 2664];

    /// <summary>What the character's attributes as the ship changes them always include, whatever the type says.</summary>
    private static readonly int[] CharacterAlways = [979, 1921, 2610, 2662];

    /// <summary>The character's security status: 0 until standings come with the module that keeps them.</summary>
    public const double SecurityStatus = 0;

    public static ItemRow RowOf(Eve.Inventory.Item item, TypeDef? type) =>
        new(item.Id, item.TypeId, item.OwnerId, item.LocationId, item.Flag, item.Singleton ? -1 : item.Quantity, type?.GroupId ?? 0, type?.CategoryId ?? 0);

    private ItemRow CharacterRow(ShipSnapshot s) =>
        new(s.Character.Id, s.Character.TypeId, s.Character.Id, s.Ship.Id, ShipSnapshot.PilotFlag, -1, CharacterGroup, CharacterCategory);

    private static PyValue Row(ItemRow row, bool character) => InventoryWire.PackedRow(row, character ? new PyStr("") : PyNone.Instance);

    private Dictionary<int, double> WithIntrinsics(ShipSnapshot s, long itemId, bool withDefaults = true)
    {
        var attributes = s.World.Attributes(itemId).ToDictionary(a => a.Key, a => a.Value);
        if (withDefaults)
        {
            foreach (var (attribute, value) in Intrinsic)
            {
                attributes.TryAdd(attribute, value);
            }
        }
        return attributes;
    }

    /// <summary>The ship's attributes: the engine's, the ones every ship reports, and what it has taken (as fractions of what it can).</summary>
    public IReadOnlyDictionary<int, double> ShipAttributes(ShipSnapshot s)
    {
        var attributes = WithIntrinsics(s, s.Ship.Id);
        foreach (var load in FittingLoads)
        {
            attributes.TryAdd(load, s.World.Get(s.Ship.Id, load));
        }
        attributes[Damage] = s.Condition.Damage;
        var capacitor = s.World.Get(s.Ship.Id, CapacitorCapacity);
        if (capacitor > 0)
        {
            attributes[Charge] = Round6(capacitor * s.Condition.Charge);
        }
        attributes[ShieldCharge] = Round6(s.World.Get(s.Ship.Id, ShieldCapacity) * s.Condition.ShieldCharge);
        attributes[ArmorDamage] = Round6(s.World.Get(s.Ship.Id, ArmorHitPoints) * s.Condition.ArmorDamage);
        attributes[PilotSecurityStatus] = SecurityStatus;
        return attributes;
    }

    /// <summary>A fitted module's attributes: the engine's, its own state, and the ones every item reports.</summary>
    public IReadOnlyDictionary<int, double> ModuleAttributes(ShipSnapshot s, long moduleId)
    {
        var attributes = WithIntrinsics(s, moduleId);
        attributes[IsOnline] = s.World.Item(moduleId)?.State >= ItemState.Online ? 1 : 0;
        // what the module has taken: its hull in points, its shield and armor as the fractions the module keeps them as
        var condition = s.ConditionOf(moduleId);
        attributes[Damage] = Round6(s.World.Get(moduleId, HullPoints) * condition.Damage);
        attributes[ShieldCharge] = condition.ShieldCharge;
        attributes[ArmorDamage] = condition.ArmorDamage;
        attributes[Quantity] = 1;
        return attributes;
    }

    /// <summary>The character's attributes with the ship's changes to them (its fitting and the rookie ship's bonus among them).</summary>
    public IReadOnlyDictionary<int, double> ShipModifiedCharacterAttributes(ShipSnapshot s)
    {
        var attributes = WithIntrinsics(s, s.Character.Id, withDefaults: false);
        attributes.Remove(Volume);
        foreach (var attribute in CharacterAlways)
        {
            attributes.TryAdd(attribute, s.World.Get(s.Character.Id, attribute));
        }
        return attributes;
    }

    /// <summary>The character's attributes as it is told them: the fixed list.</summary>
    public IReadOnlyDictionary<int, double> CharacterAttributes(ShipSnapshot s) =>
        CharacterInfoAttributes.ToDictionary(a => a, a => s.World.Get(s.Character.Id, a));

    /// <summary>The character's attributes that make up the first change events: what it can build, then how it learns.</summary>
    private static readonly int[][] CharacterEventGroups = [[196, 219, 385, 387, 398, 467, 1959, 2662, 2664], [164, 165, 166, 167, 168]];

    /// <summary>
    /// What the client is told about the character's attributes after <c>GetAllInfo</c>: two batches of changes from nothing to the value now
    /// (the client's own copy of the character starts from these).
    /// </summary>
    public IEnumerable<PyValue> CharacterAttributeEvents(ShipSnapshot s, long fileTime)
    {
        var attributes = CharacterAttributes(s);
        foreach (var group in CharacterEventGroups)
        {
            yield return new PyList([.. group.Select(a => ShipNotifications.AttributeChange(s.Character.Id, s.Character.Id, a, fileTime, null, attributes[a]))]);
        }
    }

    /// <summary>The brain as the client is told it changed: its version and the effects, marshalled (a list each for the character, the ship and the structure).</summary>
    public PyValue BrainUpdate(ShipSnapshot s, int version)
    {
        var brain = BrainBuilder.Build(s.World, statics);
        PyValue List(IEnumerable<BrainEffect> effects, long target) => new PyList([.. effects.Select(e => DogmaWire.BrainEffect(e, target))]);
        var grayMatter = MarshalCodec.Encode(Py.Tuple(List(brain.Character, s.Character.Id), List(brain.Ship, s.Ship.Id), List(brain.Structure, s.Character.Id)));
        return Py.Tuple(new PyInt(version), new PyBuffer(grayMatter));
    }

    private static double Round6(double value) => Math.Round(value, 6, MidpointRounding.AwayFromZero);

    private PyValue ModuleInfo(ShipSnapshot s, Eve.Inventory.Item module, long now)
    {
        var running = s.World.Item(module.Id) is { State: >= ItemState.Online }
            ? new PyDict([new(new PyInt(BuiltInEffects.Online), DogmaWire.RunningEffect(module.Id, s.Character.Id, s.Ship.Id, BuiltInEffects.Online, now))])
            : new PyDict([]);
        return DogmaWire.ItemInfo(module.Id, Row(RowOf(module, statics.Type(module.TypeId)), false), running, DogmaWire.Attributes(ModuleAttributes(s, module.Id)), now);
    }

    private PyValue ShipInfo(ShipSnapshot s, long now)
    {
        var entries = new List<KeyValuePair<PyValue, PyValue>>
        {
            new(InventoryWire.Id(s.Ship.Id), DogmaWire.ItemInfo(
                s.Ship.Id, Row(RowOf(s.Ship, statics.Type(s.Ship.TypeId)), false), new PyDict([]), DogmaWire.Attributes(ShipAttributes(s)), now)),
        };
        entries.AddRange(s.Fitted.Select(m => new KeyValuePair<PyValue, PyValue>(InventoryWire.Id(m.Id), ModuleInfo(s, m, now))));
        return new PyDict(entries);
    }

    private PyValue CharacterInfo(ShipSnapshot s, IReadOnlyDictionary<int, double> attributes, long now) =>
        DogmaWire.ItemInfo(s.Character.Id, Row(CharacterRow(s), true), new PyDict([]), DogmaWire.Attributes(attributes), now);

    /// <summary>The state of everything the client keeps a state for: the ship, the character and each module, and the heat of the slots.</summary>
    private PyValue ShipState(ShipSnapshot s, long now)
    {
        var rows = new List<KeyValuePair<PyValue, PyValue>>
        {
            new(InventoryWire.Id(s.Ship.Id), DogmaWire.InstanceRow(
                s.Ship.Id, damage: s.Condition.Damage, charge: s.Condition.Charge, armorDamage: s.Condition.ArmorDamage, shieldCharge: s.Condition.ShieldCharge, incapacitated: s.Condition.Incapacitated)),
            new(InventoryWire.Id(s.Character.Id), DogmaWire.InstanceRow(s.Character.Id, online: true, skillPoints: (int)s.TotalSkillPoints)),
        };
        rows.AddRange(s.Fitted.Select(m =>
        {
            var condition = s.ConditionOf(m.Id);
            return new KeyValuePair<PyValue, PyValue>(
                InventoryWire.Id(m.Id),
                DogmaWire.InstanceRow(
                    m.Id, online: s.World.Item(m.Id)?.State >= ItemState.Online, damage: condition.Damage, armorDamage: condition.ArmorDamage, shieldCharge: condition.ShieldCharge,
                    incapacitated: condition.Incapacitated));
        }));
        return Py.Tuple(new PyDict(rows), new PyDict([]), new PyDict([]), DogmaWire.HeatState(now));
    }

    /// <summary>
    /// <c>GetAllInfo</c> for a character in its ship in a station. What is left out follows the reference: the character and
    /// ship info asked for; a docked ship asked for on its own brings the character with it.
    /// </summary>
    public PyValue GetAllInfo(ShipSnapshot s, bool getCharInfo, bool getShipInfo, bool getStructureInfo, long fileTime)
    {
        _ = getStructureInfo; // a character docked in a station has no structure to tell about
        var dockedShipOnly = getShipInfo && !getCharInfo;
        var includeCharacter = getCharInfo || dockedShipOnly;
        var brain = BrainBuilder.Build(s.World, statics);
        return Py.KeyVal(
            ("systemWideEffectsOnShip", new PyDict([])),
            ("shipModifiedCharAttribs", getShipInfo && getCharInfo ? CharacterInfo(s, ShipModifiedCharacterAttributes(s), fileTime) : PyNone.Instance),
            ("structureInfo", new PyDict([])),
            ("locationInfo", new PyDict([])),
            ("shipInfo", getShipInfo ? ShipInfo(s, fileTime) : new PyDict([])),
            ("activeShipID", InventoryWire.Id(s.Ship.Id)),
            ("shipState", getShipInfo ? ShipState(s, fileTime) : PyNone.Instance),
            ("charInfo", includeCharacter
                ? Py.Tuple(
                    new PyDict([new(InventoryWire.Id(s.Character.Id), CharacterInfo(s, CharacterAttributes(s), fileTime))]),
                    DogmaWire.Brain(brain, s.Character.Id, s.Ship.Id, s.Character.Id))
                : PyNone.Instance));
    }
}

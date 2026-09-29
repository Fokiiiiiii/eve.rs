using Eve.Marshal;

namespace Eve.Services.Dogma;

/// <summary>How dogma's answers look on the wire: attribute values, item info, instance rows, brain effects.</summary>
public static class DogmaWire
{
    private const int DbTypeI8 = 0x14;
    private const int DbTypeI4 = 0x03;
    private const int DbTypeR8 = 0x05;
    private const int DbTypeBool = 0x0b;

    private static readonly (string Name, int Type)[] InstanceColumns =
    [
        ("instanceID", DbTypeI8), ("online", DbTypeBool), ("damage", DbTypeR8), ("charge", DbTypeR8),
        ("skillPoints", DbTypeI4), ("armorDamage", DbTypeR8), ("shieldCharge", DbTypeR8), ("incapacitated", DbTypeBool),
    ];

    private static readonly Descriptor InstanceDescriptor = Descriptor.FromHeader(new PyObjectEx(
        ObjectExKind.Ex1,
        Py.Tuple(new PyToken("blue.DBRowDescriptor"), Py.Tuple(Py.Tuple([.. InstanceColumns.Select(c => (PyValue)Py.Tuple(new PyStr(c.Name), new PyInt(c.Type)))]))),
        [],
        []));

    /// <summary>The attributes the client reads as real numbers even when they are whole: the duration, the rate of fire, the recharge time and the capacitor size.</summary>
    private static readonly HashSet<int> AlwaysReal = [73, 51, 55, 482];

    /// <summary>The heat of each kind of slot, as the client is told it when it starts.</summary>
    public static readonly int[] HeatAttributes = [1176, 1177, 1175];

    public static PyValue AttributeValue(int attributeId, double value) =>
        AlwaysReal.Contains(attributeId) ? (value < 0 ? new PyInt((long)Math.Truncate(value)) : new PyReal(value)) : Py.Number(value);

    /// <summary>A dictionary of attribute id to value, in the order of the ids.</summary>
    public static PyValue Attributes(IReadOnlyDictionary<int, double> attributes) =>
        new PyDict([.. attributes.OrderBy(a => a.Key).Select(a => new KeyValuePair<PyValue, PyValue>(new PyInt(a.Key), AttributeValue(a.Key, a.Value)))]);

    /// <summary>What dogma tells the client about one item: the item, the effects running on it, and its attributes.</summary>
    public static PyValue ItemInfo(long itemId, PyValue invItem, PyValue activeEffects, PyValue attributes, long fileTime) => Py.KeyVal(
        ("itemID", InventoryWire.Id(itemId)),
        ("invItem", invItem),
        ("activeEffects", activeEffects),
        ("time", new PyLong(fileTime)),
        ("attributes", attributes),
        ("wallclockTime", new PyLong(fileTime - 1)));

    /// <summary>The effect running on a module that is online: who and what it is on, and since when. No end, one repeat.</summary>
    public static PyValue RunningEffect(long itemId, long characterId, long shipId, int effectId, long startFileTime) => Py.Tuple(
        InventoryWire.Id(itemId), InventoryWire.Id(characterId), InventoryWire.Id(shipId), PyNone.Instance, PyNone.Instance, Py.Tuple(),
        new PyInt(effectId), new PyLong(startFileTime), new PyInt(-1), new PyInt(1));

    public static PyValue InstanceRow(long itemId, bool online = false, double damage = 0, double charge = 0, int skillPoints = 0, double armorDamage = 0, double shieldCharge = 0, bool incapacitated = false) =>
        new PyPackedRow(InstanceDescriptor,
        [
            new PyLong(itemId), online ? PyBool.True : PyBool.False, new PyReal(damage), new PyReal(charge), new PyInt(skillPoints),
            new PyReal(armorDamage), new PyReal(shieldCharge), incapacitated ? PyBool.True : PyBool.False,
        ]);

    /// <summary>The state of the heat of the slots: nothing built up, room for a hundred, at a rate of a hundredth.</summary>
    public static PyValue HeatState(long fileTime) => new PyDict([.. HeatAttributes.Select(a => new KeyValuePair<PyValue, PyValue>(
        new PyInt(a), Py.Tuple(new PyInt(0), new PyInt(100), new PyInt(0), new PyInt(1), new PyReal(0.01), new PyLong(fileTime))))]);

    /// <summary>One effect of the brain, as an object the client builds a <c>BrainEffect</c> from.</summary>
    public static PyValue BrainEffect(Eve.Dogma.BrainEffect effect, long targetItemId)
    {
        var extras = Py.Tuple([.. effect.Extras.Select(e => (PyValue)new PyInt(e))]);
        var source = Py.KeyVal(("invItem", Py.KeyVal(("typeID", new PyInt(effect.SkillTypeId)))));
        return new PyObjectEx(
            ObjectExKind.Ex1,
            Py.Tuple(
                new PyToken("eve.common.script.dogma.effect.BrainEffect"),
                Py.Tuple(source, InventoryWire.Id(targetItemId), new PyStr(effect.ModifierType), new PyInt(effect.TargetAttributeId), new PyInt(effect.Operation), extras),
                Py.Dict(
                    ("fromAttrib", PyNone.Instance),
                    ("value", Py.Number(effect.Value)),
                    ("toItemID", InventoryWire.Id(targetItemId)),
                    ("modifierType", new PyStr(effect.ModifierType)),
                    ("toAttribID", new PyInt(effect.TargetAttributeId)),
                    ("operation", new PyInt(effect.Operation)),
                    ("extras", extras),
                    ("skills", new PyList([new PyInt(effect.SkillTypeId)])))),
            [],
            []);
    }

    /// <summary>The brain as the client is told it: a version, then the effects on the character, on the ship and on the structure.</summary>
    public static PyValue Brain(Eve.Dogma.Brain brain, long characterId, long shipId, long structureId, int version = 0) => Py.Tuple(
        new PyInt(version),
        Py.Tuple([.. brain.Character.Select(e => BrainEffect(e, characterId))]),
        Py.Tuple([.. brain.Ship.Select(e => BrainEffect(e, shipId))]),
        Py.Tuple([.. brain.Structure.Select(e => BrainEffect(e, structureId))]));
}

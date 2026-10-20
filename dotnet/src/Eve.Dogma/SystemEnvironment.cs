namespace Eve.Dogma;

/// <summary>
/// Triglavian ones later). The effect beacon's attributes are the static data's: "&lt;name&gt;Multiplier" multiplies the attribute "&lt;name&gt;"
/// (PostMul, the beacon's value is the multiplier), and "&lt;name&gt;Bonus" adds its percentage to "&lt;name&gt;" (PostPercent). They reach the ship and
/// the items located in it (its modules), as a buff of the same shape does. An attribute whose base name has no dogma attribute is not applied and
/// is listed in <c>NotApplied</c>, so the caller can count it.
/// </summary>
public static class SystemEnvironment
{
    /// <summary>The passive effect of a beacon, as its modifiers on the ship and on what is located in it.</summary>
    public static (EffectDef? Effect, IReadOnlyList<string> NotApplied) EffectOf(int beaconTypeId, string beaconName,
        IReadOnlyDictionary<string, double> beaconAttributes, Func<string, int?> attributeId)
    {
        var modifiers = new List<Modifier>();
        var notApplied = new List<string>();
        foreach (var (name, value) in beaconAttributes.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            Operation operation;
            string attribute;
            var amount = value;
            if (name.EndsWith("Multiplier", StringComparison.Ordinal))
            {
                attribute = name[..^"Multiplier".Length];
                operation = Operation.PostMul;
            }
            else if (name.EndsWith("ResistanceBonus", StringComparison.Ordinal))
            {
                // the dogma keeps a resistance as a resonance (the damage taken is the resonance times the damage): a resistance that grows by
                // the bonus makes the resonance smaller by it, so the percentage is negated
                attribute = name[..^"ResistanceBonus".Length] + "Resonance";
                operation = Operation.PostPercent;
                amount = -value;
            }
            else if (name.EndsWith("Bonus", StringComparison.Ordinal))
            {
                attribute = name[..^"Bonus".Length];
                operation = Operation.PostPercent;
            }
            else
            {
                notApplied.Add(name);
                continue;
            }
            if (attributeId(attribute) is not { } id)
            {
                notApplied.Add(name);
                continue;
            }
            modifiers.Add(new Modifier(ModifierFunc.Item, "itemID", operation, id, 0, Literal: amount));
            modifiers.Add(new Modifier(ModifierFunc.Location, "itemID", operation, id, 0, Literal: amount));
        }
        var effect = modifiers.Count == 0 ? null : new EffectDef(beaconTypeId, "environment:" + beaconName, EffectDef.Passive, modifiers);
        return (effect, notApplied);
    }
}

namespace Eve.Inventory.Mutation;

/// <summary>
/// One attribute a mutaplasmid may move: the multiplier interval printed on it and the attribute's direction.
/// <see cref="Min"/> and <see cref="Max"/> are kept as the data states them; six rows of the data have Min above Max, which
/// is the same interval read backwards, so <see cref="Low"/> and <see cref="High"/> are the interval itself.
/// </summary>
public readonly record struct AttributeRange(int AttributeId, double Min, double Max, bool HighIsGood)
{
    public double Low => Math.Min(Min, Max);

    public double High => Math.Max(Min, Max);
}

/// <summary>Why a mutation is refused. Expected refusals are values, not exceptions.</summary>
public enum MutationRefusal
{
    /// <summary>The type is not a mutaplasmid of the static data (or it is not published).</summary>
    UnknownMutator,

    /// <summary>The mutaplasmid does not take this type as input (not applicable, unpublished, or already an output).</summary>
    InputNotMappable,

    /// <summary>A mutated attribute has no roll.</summary>
    RollMissing,

    /// <summary>A roll names an attribute the mutaplasmid does not move.</summary>
    RollNotMutated,

    /// <summary>A roll is not a number or lies outside its interval (the interval's ends are allowed).</summary>
    RollOutOfRange,

    /// <summary>The source type has no value for a mutated attribute.</summary>
    AttributeMissingInSource,
}

/// <summary>The outcome of a mutation step: accepted with a value, or refused with a typed reason.</summary>
public abstract record MutationOutcome<T>;

public sealed record Accepted<T>(T Value) : MutationOutcome<T>;

public sealed record Refused<T>(MutationRefusal Reason) : MutationOutcome<T>;

/// <summary>
/// The pure rules of an abyssal mutation. Rolls are explicit inputs: the functions never draw a random number.
/// A roll is the multiplier applied to the source base value, and it must lie in its interval (inclusive).
/// </summary>
public static class MutationMath
{
    /// <summary>
    /// The multiplier for a uniform draw <paramref name="fraction"/> in [0, 1] over an interval, by the client's formula
    /// (<c>min + fraction x (max - min)</c>, eve-logic dynamicitemattributes/__init__.py:9-14). For an inverted row the
    /// result stays inside the interval; fraction 0 gives the printed Min.
    /// </summary>
    /// <remarks>A fraction outside [0, 1] is a bug in the caller's random source, so it throws rather than refusing.</remarks>
    public static double MultiplierAt(AttributeRange range, double fraction)
    {
        if (!(fraction >= 0 && fraction <= 1))
        {
            throw new ArgumentOutOfRangeException(nameof(fraction), fraction, "a roll fraction must be in [0, 1]");
        }
        return range.Min + fraction * (range.Max - range.Min);
    }

    /// <summary>Checks a roll vector against the mutaplasmid's intervals: every mutated attribute has a roll, no other attribute does, and each roll is inside its interval (inclusive).</summary>
    public static MutationOutcome<IReadOnlyDictionary<int, double>> ValidateRolls(
        IReadOnlyList<AttributeRange> ranges, IReadOnlyDictionary<int, double> rolls)
    {
        var moved = ranges.ToDictionary(range => range.AttributeId);
        foreach (var range in ranges)
        {
            if (!rolls.ContainsKey(range.AttributeId))
            {
                return new Refused<IReadOnlyDictionary<int, double>>(MutationRefusal.RollMissing);
            }
        }
        foreach (var attributeId in rolls.Keys)
        {
            if (!moved.ContainsKey(attributeId))
            {
                return new Refused<IReadOnlyDictionary<int, double>>(MutationRefusal.RollNotMutated);
            }
        }
        foreach (var range in ranges)
        {
            var roll = rolls[range.AttributeId];
            if (double.IsNaN(roll) || roll < range.Low || roll > range.High)
            {
                return new Refused<IReadOnlyDictionary<int, double>>(MutationRefusal.RollOutOfRange);
            }
        }
        return new Accepted<IReadOnlyDictionary<int, double>>(new Dictionary<int, double>(rolls));
    }

    /// <summary>The mutated values: each mutated attribute is the source base value multiplied by its roll, with no rounding.</summary>
    public static MutationOutcome<IReadOnlyDictionary<int, double>> MutatedValues(
        IReadOnlyDictionary<int, double> sourceAttributes,
        IReadOnlyList<AttributeRange> ranges,
        IReadOnlyDictionary<int, double> rolls)
    {
        var validation = ValidateRolls(ranges, rolls);
        if (validation is not Accepted<IReadOnlyDictionary<int, double>> validated)
        {
            return validation;
        }
        var values = new Dictionary<int, double>();
        foreach (var range in ranges)
        {
            if (!sourceAttributes.TryGetValue(range.AttributeId, out var baseValue))
            {
                return new Refused<IReadOnlyDictionary<int, double>>(MutationRefusal.AttributeMissingInSource);
            }
            values[range.AttributeId] = baseValue * validated.Value[range.AttributeId];
        }
        return new Accepted<IReadOnlyDictionary<int, double>>(values);
    }

    /// <summary>
    /// The effective attributes of a mutated item (decision D1, D2): the source type's attributes, with the output type's
    /// skill requirements replacing the source's skill requirements, and each mutated attribute set to its source base value
    /// times its roll. Nothing else is taken from the output type.
    /// </summary>
    public static MutationOutcome<IReadOnlyDictionary<int, double>> EffectiveAttributes(
        IReadOnlyDictionary<int, double> sourceAttributes,
        IReadOnlyDictionary<int, double> outputTypeAttributes,
        IReadOnlySet<int> skillAttributeIds,
        IReadOnlyList<AttributeRange> ranges,
        IReadOnlyDictionary<int, double> rolls)
    {
        var outcome = MutatedValues(sourceAttributes, ranges, rolls);
        if (outcome is not Accepted<IReadOnlyDictionary<int, double>> accepted)
        {
            return outcome;
        }
        var effective = sourceAttributes
            .Where(pair => !skillAttributeIds.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        foreach (var pair in outputTypeAttributes)
        {
            if (skillAttributeIds.Contains(pair.Key))
            {
                effective[pair.Key] = pair.Value;
            }
        }
        foreach (var pair in accepted.Value)
        {
            effective[pair.Key] = pair.Value;
        }
        return new Accepted<IReadOnlyDictionary<int, double>>(effective);
    }
}

using System.Text.Json;

namespace Eve.Dogma;

/// <summary>
/// A timed buff collection (the static data's <c>dbuffCollections</c>, e.g. Shield Resistance Bonus): the attributes a buff of it changes on the items it
/// reaches, the operation it applies, and how the buffs active at once combine: the strongest (a Maximum collection) or the weakest (a Minimum one). The
/// numbers are the static data's; the values a buff carries come from the burst that applies it.
/// </summary>
public sealed record BuffCollection(int Id, string Name, bool Weakest, IReadOnlyList<Modifier> Shape)
{
    /// <summary>The modifiers of a buff of this collection that carries <paramref name="value"/>: the collection's shape, each with the value.</summary>
    public IReadOnlyList<Modifier> ModifiersFor(double value) => [.. Shape.Select(modifier => modifier with { Literal = value })];

    /// <summary>The passive effect that a buff of this collection runs on the item it reaches (the ship, whose modules and ship attributes it changes).</summary>
    public EffectDef EffectFor(double value) => new(Id, $"buff:{Id}", EffectDef.Passive, ModifiersFor(value));

    /// <summary>
    /// The collection of one row of the static data. The modifiers reach the item the buff is on (<c>itemID</c>): its attributes (<c>itemModifiers</c>), all
    /// items located in it (<c>locationModifiers</c>), the items located in it of a group (<c>locationGroupModifiers</c>), and the ones located in it that need a skill
    /// (<c>locationRequiredSkillModifiers</c>).
    /// Null when the row applies an operation the engine does not know, so no buff of it is made.
    /// </summary>
    public static BuffCollection? From(int id, JsonElement doc)
    {
        var operation = doc.TryGetProperty("operationName", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() : null;
        if (OperationOf(operation) is not { } op)
        {
            return null;
        }
        var shape = new List<Modifier>();
        if (doc.TryGetProperty("itemModifiers", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in items.EnumerateArray())
            {
                shape.Add(new Modifier(ModifierFunc.Item, "itemID", op, row.GetProperty("dogmaAttributeID").GetInt32(), 0));
            }
        }
        if (doc.TryGetProperty("locationModifiers", out var locations) && locations.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in locations.EnumerateArray())
            {
                shape.Add(new Modifier(ModifierFunc.Location, "itemID", op, row.GetProperty("dogmaAttributeID").GetInt32(), 0));
            }
        }
        if (doc.TryGetProperty("locationGroupModifiers", out var groups) && groups.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in groups.EnumerateArray())
            {
                shape.Add(new Modifier(ModifierFunc.LocationGroup, "itemID", op, row.GetProperty("dogmaAttributeID").GetInt32(), 0,
                    row.GetProperty("groupID").GetInt32()));
            }
        }
        if (doc.TryGetProperty("locationRequiredSkillModifiers", out var skills) && skills.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in skills.EnumerateArray())
            {
                shape.Add(new Modifier(ModifierFunc.LocationRequiredSkill, "itemID", op, row.GetProperty("dogmaAttributeID").GetInt32(), 0, 0,
                    row.GetProperty("skillID").GetInt32()));
            }
        }
        var weakest = doc.TryGetProperty("aggregateMode", out var mode) && mode.ValueKind == JsonValueKind.String && mode.GetString() == "Minimum";
        var description = doc.TryGetProperty("developerDescription", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() ?? "" : "";
        return new BuffCollection(id, description, weakest, shape);
    }

    /// <summary>The operation the static data names (the client's dgmAss* codes, eve-logic dogma/const.py: PreAssignment -1, PostAssignment 7).</summary>
    private static Operation? OperationOf(string? name) => name switch
    {
        "PreAssignment" => Operation.PreAssign,
        "PostAssignment" => Operation.PostAssign,
        "ModAdd" => Operation.ModAdd,
        "PostMul" => Operation.PostMul,
        "PostPercent" => Operation.PostPercent,
        _ => null,
    };
}

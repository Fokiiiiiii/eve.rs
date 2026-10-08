using Eve.Dogma;

namespace Eve.Services.Space.Effects;

/// <summary>
/// The cynosural field types a field generator makes, with the group and radius the static data gives them. EveJS (<c>cynosuralField.js</c>) falls back to group 2020 or
/// 897 and radius 250 when a value is missing; here the group comes from <c>sde.types.groupID</c> and the radius from the type's <c>radius</c> (dogma attribute 162,
/// <see cref="DogmaStatic"/>), so a fallback is never used for a type the static data has. Nothing spawns a field yet (the cyno body is not written; see
/// docs/project/TODO.md section 12), and the spawn radius of the generator (attribute 2455) is read by that body, not here.
/// </summary>
internal static class CynoFieldTypes
{
    /// <summary>The attribute of a type's radius, as <see cref="DogmaStatic"/> loads it from <c>sde.types</c>.</summary>
    private const int Radius = 162;

    /// <summary>Cynosural Field (21094), Covert Cynosural Field I (28650) and Industrial Cynosural Field (52696): the fields the generators make.</summary>
    public static readonly IReadOnlyList<int> FieldTypes = [21094, 28650, 52696];

    /// <summary>A field type as the static data has it: its type, the group it is in and its radius in metres.</summary>
    public readonly record struct Field(int TypeId, int GroupId, double Radius);

    /// <summary>The field of a type, or null when the static data has no such type or no radius for it.</summary>
    public static Field? Of(DogmaStatic statics, int fieldTypeId) =>
        statics.Type(fieldTypeId) is { } type && type.Attributes.TryGetValue(Radius, out var radius) && radius > 0
            ? new Field(type.TypeId, type.GroupId, radius)
            : null;
}

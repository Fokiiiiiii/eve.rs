using Eve.Dogma;

namespace Eve.Services.Space.Effects;

/// <summary>
/// 897 and radius 250 when a value is missing; here the group comes from <c>sde.types.groupID</c> and the radius from the type's <c>radius</c> (dogma attribute 162,
/// <see cref="DogmaStatic"/>), so a fallback is never used for a type the static data has. Nothing spawns a field yet (the cyno body is not written; see
/// </summary>
internal static class CynoFieldTypes
{
    /// <summary>The attribute of a type's radius, as <see cref="DogmaStatic"/> loads it from <c>sde.types</c>.</summary>
    private const int Radius = 162;

    /// <summary>Cynosural Field (21094), Covert Cynosural Field I (28650) and Industrial Cynosural Field (52696): the fields the generators make.</summary>
    public static readonly IReadOnlyList<int> FieldTypes = [21094, 28650, 52696];

    /// <summary>The covert field (28650): a jammer of standard fields does not stop it (SDE 35914: "Covert Cynosural Field Generators are immune").</summary>
    public const int CovertFieldType = 28650;

    /// <summary>The group of the field generators (Cynosural Field Generator I 21096, Covert 28646, Industrial 52694).</summary>
    public const int GeneratorGroup = 658;

    /// <summary>The attribute of a field type's lifetime: explosionDelay (281), 600000 ms on all three types; the same rule the bombs use.</summary>
    public const int Lifetime = 281;

    /// <summary>The field a generator makes: the covert generator makes the covert field, the industrial one the industrial field, the others the field.</summary>
    public static int FieldTypeOf(int generatorTypeId) => generatorTypeId switch
    {
        28646 => 28650,
        52694 => 52696,
        _ => 21094,
    };

    /// <summary>A field type as the static data has it: its type, the group it is in and its radius in metres.</summary>
    public readonly record struct Field(int TypeId, int GroupId, double Radius);

    /// <summary>The field of a type, or null when the static data has no such type or no radius for it.</summary>
    public static Field? Of(DogmaStatic statics, int fieldTypeId) =>
        statics.Type(fieldTypeId) is { } type && type.Attributes.TryGetValue(Radius, out var radius) && radius > 0
            ? new Field(type.TypeId, type.GroupId, radius)
            : null;
}

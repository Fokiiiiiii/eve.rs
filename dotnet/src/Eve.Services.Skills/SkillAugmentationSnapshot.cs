using Eve.Macho;
using Eve.Marshal;
using Eve.Characters;
using Eve.Skills;
using Npgsql;

namespace Eve.Services.Skills;

/// <summary>Current client-visible booster and character attribute modifier state.</summary>
public sealed record SkillAugmentationSnapshot(
    IReadOnlyList<SkillActiveBoosterSnapshot> ActiveBoosters,
    IReadOnlyList<SkillAttributeModifier> AttributeModifiers)
{
    public static SkillAugmentationSnapshot Empty { get; } = new([], []);
}

/// <summary>Reads the character's current active boosters and exact client attribute-modifier wire list.</summary>
public delegate ValueTask<SkillAugmentationSnapshot> SkillAugmentationReader(long characterId, CancellationToken cancellationToken);

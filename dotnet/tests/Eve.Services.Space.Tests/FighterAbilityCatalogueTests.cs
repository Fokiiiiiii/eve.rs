using Eve.Services.Space.Effects;

namespace Eve.Services.Space.Tests;

/// <summary>
/// The fighter ability slot table (the EVE.js table, embedded): the 94 published fighter types, the slots of each effect family as the SDE counts them, and the
/// slot facts the table gives for a few types.
/// </summary>
public class FighterAbilityCatalogueTests
{
    private static readonly FighterAbilityCatalogue Table = FighterAbilityCatalogue.Default;

    /// <summary>The slot count of each effect family across the fighter types (the SDE typeDogma counts of the families).</summary>
    private static readonly (string Family, int Slots)[] SdeFamilyCounts =
    [
        ("fighterAbilityMissiles", 56),
        ("fighterAbilityAttackMissile", 54),
        ("fighterAbilityMicroWarpDrive", 51),
        ("fighterAbilityTackle", 20),
        ("fighterAbilityEvasiveManeuvers", 20),
        ("fighterAbilityMicroJumpDrive", 16),
        ("fighterAbilityLaunchBomb", 16),
        ("fighterAbilityAfterburner", 7),
        ("fighterAbilityEnergyNeutralizer", 5),
        ("fighterAbilityECM", 5),
        ("fighterAbilityWarpDisruption", 5),
        ("fighterAbilityStasisWebifier", 5),
        ("fighterAbilityKamikaze", 2),
    ];

    [Fact]
    public void The_table_has_the_94_published_fighter_types_and_36_abilities()
    {
        Assert.Equal(94, Table.TypeIds.Count);
        Assert.Equal(36, Table.Abilities.Count);
    }

    [Fact]
    public void Each_effect_family_fills_the_slots_the_SDE_counts_for_it()
    {
        var slotsByFamily = Table.TypeIds
            .SelectMany(Table.SlotsOf)
            .GroupBy(slot => Table.Ability(slot.AbilityId)!.EffectFamily)
            .ToDictionary(group => group.Key, group => group.Count());

        Assert.Equal(SdeFamilyCounts.Length, slotsByFamily.Count);
        foreach (var (family, slots) in SdeFamilyCounts)
        {
            Assert.Equal(slots, slotsByFamily[family]);
        }
    }

    [Fact]
    public void The_abilities_no_fighter_type_carries_are_not_in_any_slot()
    {
        var carried = Table.TypeIds.SelectMany(Table.SlotsOf).Select(slot => slot.AbilityId).ToHashSet();

        // turret, target paint, damage multiply, missile family 14 and the unused attack variant 17
        Assert.Empty(carried.Intersect(new[] { 1, 3, 6, 14, 17 }));
    }

    [Fact]
    public void A_light_squadron_has_the_microwarpdrive_with_its_cooldown_and_the_missiles_with_their_charges()
    {
        Assert.Equal(new FighterAbilitySlot(1, 4, 60, null), Table.Slot(23055, 1));
        Assert.Equal(new FighterAbilitySlot(2, 33, null, new FighterCharges(18, 4)), Table.Slot(23055, 2));
        Assert.Equal(new FighterAbilitySlot(0, 22, null, null), Table.Slot(23055, 0));
        Assert.Null(Table.Slot(23055, 3));
    }

    [Fact]
    public void A_heavy_squadron_has_the_afterburner_and_the_kamikaze_in_its_slots()
    {
        Assert.Equal(new[] { new FighterAbilitySlot(0, 22, null, null), new FighterAbilitySlot(1, 9, 60, null), new FighterAbilitySlot(2, 38, null, null) },
            Table.SlotsOf(2948));
        Assert.Equal("fighterAbilityKamikaze", Table.Ability(38)!.EffectFamily);
    }

    [Fact]
    public void An_ability_names_its_effect_and_the_attribute_its_duration_is_read_from()
    {
        var microwarpdrive = Table.Ability(4)!;

        Assert.Equal(("fighterAbilityMicroWarpDrive", 2157, false), (microwarpdrive.EffectFamily, microwarpdrive.DurationAttributeId, microwarpdrive.IsOffensive));
        // the AttackM family is item targeted; EVE.js flags it not offensive while the SDE effect is offensive (DEVIATIONS, Player fighters)
        Assert.Equal("itemTargeted", Table.Ability(22)!.TargetMode);
    }

    [Fact]
    public void A_type_the_table_does_not_have_has_no_slots()
    {
        Assert.Empty(Table.SlotsOf(1));
        Assert.Null(Table.Ability(999));
    }
}

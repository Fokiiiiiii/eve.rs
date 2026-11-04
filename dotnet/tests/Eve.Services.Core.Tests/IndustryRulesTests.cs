using Eve.Services.Core;
using Xunit;

namespace Eve.Services.Core.Tests;

public sealed class IndustryRulesTests
{
    [Theory]
    [InlineData(86, 0.9, 1, 1, 78)]
    [InlineData(1, 0.9, 10, 10, 10)]
    [InlineData(100, 1.0, 10, 10, 1000)]
    [InlineData(10, 1.015, 1, 1, 11)]
    [InlineData(1, 1.0, 20, 2, 20)]
    public void Material_quantity_matches_client_round_then_ceil_and_per_run_floor(
        long baseQuantity, double modifier, long materialRuns, long runs, long expected)
    {
        // industry/job.py Job._get_materials rounds to MATERIAL_ROUND_PRECISION, then ceil, with runs as the floor.
        Assert.Equal(expected, IndustryRules.MaterialQuantity(baseQuantity, modifier, materialRuns, runs));
    }

    [Theory]
    [InlineData(0, 0, 1.0, 1.0)]
    [InlineData(10, 20, 0.9, 0.8)]
    [InlineData(5, 10, 0.95, 0.9)]
    public void Blueprint_efficiency_modifiers_match_client_percentage_rules(
        int materialEfficiency, int timeEfficiency, double expectedMaterial, double expectedTime)
    {
        Assert.Equal(expectedMaterial, IndustryRules.MaterialEfficiencyModifier(materialEfficiency), 12);
        Assert.Equal(expectedTime, IndustryRules.TimeEfficiencyModifier(timeEfficiency), 12);
    }

    [Theory]
    [InlineData(-4, 5, 0.8)]
    [InlineData(-2, 5, 0.9)]
    public void Skill_modifiers_match_client_percent_per_level_rule(
        double attributePerLevelPercent, int level, double expected)
    {
        Assert.Equal(expected, IndustryRules.SkillModifier(attributePerLevelPercent, level), 12);
    }

    [Theory]
    [InlineData(1_000_000, 0.05, 0.10, true, 1, 50_000, 100_000, 40_000, 50_000)]
    [InlineData(1_000_000, 0.0, 0.0, false, 3, 0, 0, 20_000, 0)]
    [InlineData(10_000, 0.01505, 0.00505, true, 1, 151, 51, 400, 500)]
    [InlineData(100.6, 0.5, 0.0, false, 1, 50, 0, 4, 0)]
    public void Install_cost_matches_client_rounded_base_shares(
        double baseCost, double costIndex, double facilityTax, bool highSecurity, int activity,
        long expectedCost, long expectedTax, long expectedScc, long expectedSecurityTax)
    {
        var parts = IndustryRules.CostParts(baseCost, costIndex, facilityTax, highSecurity, activity);
        Assert.Equal(new InstallCostParts(expectedCost, expectedTax, expectedScc, expectedSecurityTax), parts);
    }

    [Fact]
    public void Alpha_clone_industry_tax_adds_quarter_percent_of_unrounded_base_cost()
    {
        var omega = IndustryRules.CostParts(100_002.5, 0.2, 0.1, false, IndustryRules.Manufacturing);
        var alpha = IndustryRules.CostParts(100_002.5, 0.2, 0.1, false, IndustryRules.Manufacturing, alphaClone: true);
        Assert.Equal(20001, omega.Cost);
        Assert.Equal(20251, alpha.Cost);
        Assert.Equal(250, alpha.Cost - omega.Cost);
        Assert.Equal(omega.Tax, alpha.Tax);
    }

    [Theory]
    [InlineData(2.5, 3)]
    [InlineData(100.5, 101)]
    [InlineData(100.4, 100)]
    public void Client_round_breaks_ties_away_from_zero_as_python_2_does(double value, long expected)
    {
        Assert.Equal(expected, IndustryRules.Round(value));
    }

    [Fact]
    public void Two_industry_skills_multiply_as_dogma_post_percent_modifiers()
    {
        // Industry 5 (-20%) and Advanced Industry 5 (-15%) stack multiplicatively: 0.8 x 0.85, not 0.65.
        Assert.Equal(680, IndustryRules.DurationSeconds(1000, [IndustryRules.SkillModifier(-4, 5), IndustryRules.SkillModifier(-3, 5)]));
    }

    [Fact]
    public void Research_scc_is_halved_and_null_security_tax_outside_high_security()
    {
        var parts = IndustryRules.CostParts(1_000_000, 0.0, 0.0, highSecurity: false, IndustryRules.ResearchTime);
        Assert.Equal(new InstallCostParts(0, 0, 20_000, 0), parts);
    }

    [Theory]
    [InlineData(1200, 0.9, 0.8, 864)]
    [InlineData(100, 1.0, 0.0, 0)]
    [InlineData(201, 0.5, 1.0, 101)]
    public void Duration_matches_client_multiplier_order_and_rounding(
        double baseSeconds, double firstModifier, double secondModifier, long expected)
    {
        // industry/job.py Job._get_time multiplies each applicable modifier and rounds the final seconds.
        Assert.Equal(expected, IndustryRules.DurationSeconds(baseSeconds, [firstModifier, secondModifier]));
    }

    [Fact]
    public void Research_time_steps_through_the_level_ladder_and_caps_at_the_last_level()
    {
        Assert.Equal(238, Math.Round(IndustryRules.ResearchJobSeconds(100, 0, 2)));
        Assert.Equal(IndustryRules.ResearchTimes[9] * 100 * 3, IndustryRules.ResearchJobSeconds(100, 9, 3), 6);
    }

    [Fact]
    public void Research_levels_left_respect_the_step_and_the_maximum()
    {
        Assert.Equal(0, IndustryRules.ResearchRunsLeft(10, IndustryRules.MaxMaterialEfficiency, 1));
        Assert.Equal(1, IndustryRules.ResearchRunsLeft(18, IndustryRules.MaxTimeEfficiency, IndustryRules.StepTimeEfficiency));
    }

    [Fact]
    public void Invention_probability_multiplies_base_skills_and_decryptor_and_clamps()
    {
        Assert.Equal(0.48, IndustryRules.InventionProbability([0.3], 10.0 / 30.0, 1.2), 9);
        Assert.Equal(1.0, IndustryRules.InventionProbability([0.9], 1.0, 2.0), 9);
    }

    [Fact]
    public void Invention_skill_bonus_counts_1_30_per_level_and_1_40_for_the_lower_list()
    {
        Assert.Equal(2.0 / 30.0 + 1.0 / 40.0, IndustryRules.InventionSkillBonus([(2, false), (1, true)]), 12);
    }

    [Fact]
    public void Jump_allowance_is_five_per_distance_level_up_to_twenty_five()
    {
        Assert.Equal(0, IndustryRules.MaxDistanceJumps(0));
        Assert.Equal(5, IndustryRules.MaxDistanceJumps(1));
        Assert.Equal(25, IndustryRules.MaxDistanceJumps(5));
    }

    [Fact]
    public void Successful_attempts_are_deterministic_for_a_seed_and_bounded_by_the_probability()
    {
        Assert.Equal(0, IndustryRules.SuccessfulAttempts(100, 0.0, 7));
        Assert.Equal(100, IndustryRules.SuccessfulAttempts(100, 1.0, 7));
        var first = IndustryRules.SuccessfulAttempts(100, 0.5, 7);
        Assert.Equal(first, IndustryRules.SuccessfulAttempts(100, 0.5, 7));
        Assert.InRange(first, 30, 70);
    }

    [Fact]
    public void Activity_keys_and_pricing_activities_match_the_sde_and_the_client()
    {
        Assert.Equal("reaction", IndustryRules.ActivityKey(IndustryRules.Reaction));
        Assert.Equal("research_time", IndustryRules.ActivityKey(IndustryRules.ResearchTime));
        Assert.Equal(IndustryRules.Manufacturing, IndustryRules.PricingActivity(IndustryRules.Copying));
        Assert.Equal(IndustryRules.Reaction, IndustryRules.PricingActivity(IndustryRules.Reaction));
    }

    [Fact]
    public void A_structure_runs_the_activities_its_online_services_allow()
    {
        Assert.Empty(IndustryRules.StructureActivities([]));
        Assert.Equal(new HashSet<int> { IndustryRules.Manufacturing }, IndustryRules.StructureActivities([21]));
        Assert.Equal(new HashSet<int> { IndustryRules.ResearchTime, IndustryRules.ResearchMaterial, IndustryRules.Copying, IndustryRules.Invention },
            IndustryRules.StructureActivities([30]));
        Assert.Equal(new HashSet<int> { IndustryRules.ResearchMaterial }, IndustryRules.StructureActivities([32]));
        Assert.Equal(new HashSet<int> { IndustryRules.Reaction }, IndustryRules.StructureActivities([42]));
    }

    private static System.Text.Json.JsonElement Json(string text) => System.Text.Json.JsonDocument.Parse(text).RootElement.Clone();

    [Fact]
    public void A_rig_changes_its_structure_attribute_targets_by_one_plus_its_percentage()
    {
        // The basic medium ship material rig (type 37146): 2594 = -2 % on the structure attribute 2546 (medium ship groups).
        var rig = IndustryBonuses.ParseRig(37146,
            Json("""{"dogmaAttributes":[{"attributeID":2594,"value":-2.0},{"attributeID":2355,"value":1.0}]}"""),
            [Json("""{"modifierInfo":[{"func":"ItemModifier","domain":"structureID","operation":6,"modifiedAttributeID":2546,"modifyingAttributeID":2594}]}""")]);
        var bonuses = IndustryBonuses.RigBonuses(rig, 0.9);

        Assert.Equal(0.98, IndustryBonuses.Multiplier(bonuses, IndustryBonusKind.Material, IndustryRules.Manufacturing, 6, 26), 10);
        Assert.Equal(1.0, IndustryBonuses.Multiplier(bonuses, IndustryBonusKind.Material, IndustryRules.Manufacturing, 6, 25));
        Assert.Equal(1.0, IndustryBonuses.Multiplier(bonuses, IndustryBonusKind.Time, IndustryRules.Manufacturing, 6, 26));
        Assert.Equal(1.0, IndustryBonuses.Multiplier(bonuses, IndustryBonusKind.Material, IndustryRules.Reaction, null, 26));
    }

    [Fact]
    public void A_rig_percentage_is_scaled_by_the_security_band_of_the_system()
    {
        // A time rig (2593 = -20 %) with the low-security multiplier 1.1; the high and null multipliers are absent (default 1).
        var rig = IndustryBonuses.ParseRig(1,
            Json("""{"dogmaAttributes":[{"attributeID":2593,"value":-20.0},{"attributeID":2356,"value":1.1}]}"""),
            [Json("""{"modifierInfo":[{"func":"ItemModifier","domain":"structureID","operation":6,"modifiedAttributeID":2545,"modifyingAttributeID":2593}]}""")]);

        Assert.Equal(0.78, IndustryBonuses.Multiplier(IndustryBonuses.RigBonuses(rig, 0.3), IndustryBonusKind.Time, IndustryRules.Manufacturing, 6, 25), 10);
        Assert.Equal(0.8, IndustryBonuses.Multiplier(IndustryBonuses.RigBonuses(rig, 0.9), IndustryBonusKind.Time, IndustryRules.Manufacturing, 6, 25), 10);
        Assert.Equal(0.8, IndustryBonuses.Multiplier(IndustryBonuses.RigBonuses(rig, 0.0), IndustryBonusKind.Time, IndustryRules.Manufacturing, 6, 25), 10);
        Assert.Equal(2355, IndustryBonuses.SecurityBandAttribute(0.45));
        Assert.Equal(2356, IndustryBonuses.SecurityBandAttribute(0.44));
        Assert.Equal(2357, IndustryBonuses.SecurityBandAttribute(-0.5));
    }

    [Fact]
    public void Role_bonuses_of_an_engineering_complex_and_a_refinery_follow_the_structure_type()
    {
        var engineering = IndustryBonuses.HullBonuses(new Dictionary<int, double> { [2600] = 0.99, [2601] = 0.97, [2602] = 0.85 });

        Assert.Equal(0.99, IndustryBonuses.Multiplier(engineering, IndustryBonusKind.Material, IndustryRules.Manufacturing, null, null), 10);
        Assert.Equal(0.85, IndustryBonuses.Multiplier(engineering, IndustryBonusKind.Time, IndustryRules.Manufacturing, null, null), 10);
        Assert.Equal(0.85, IndustryBonuses.Multiplier(engineering, IndustryBonusKind.Time, IndustryRules.ResearchTime, null, null), 10);
        Assert.Equal(0.97, IndustryBonuses.Multiplier(engineering, IndustryBonusKind.Cost, IndustryRules.Invention, null, null), 10);
        Assert.Equal(1.0, IndustryBonuses.Multiplier(engineering, IndustryBonusKind.Time, IndustryRules.Reaction, null, null));
        Assert.Equal(1.0, IndustryBonuses.Multiplier(engineering, IndustryBonusKind.Material, IndustryRules.ResearchMaterial, null, null));

        var refinery = IndustryBonuses.HullBonuses(new Dictionary<int, double> { [2721] = 0.75 });
        Assert.Equal(0.75, IndustryBonuses.Multiplier(refinery, IndustryBonusKind.Time, IndustryRules.Reaction, null, null), 10);
        Assert.Equal(1.0, IndustryBonuses.Multiplier(refinery, IndustryBonusKind.Time, IndustryRules.Manufacturing, null, null));
    }

    [Fact]
    public void Category_targets_apply_only_to_their_product_category_and_only_structure_effects_are_kept()
    {
        // Ammunition material (2540, category 8) with -5 %; the item-domain effect is not a structure bonus.
        var rig = IndustryBonuses.ParseRig(2,
            Json("""{"dogmaAttributes":[{"attributeID":2594,"value":-5.0}]}"""),
            [Json("""{"modifierInfo":[{"func":"ItemModifier","domain":"structureID","operation":6,"modifiedAttributeID":2540,"modifyingAttributeID":2594},{"func":"ItemModifier","domain":"itemID","operation":4,"modifiedAttributeID":2540,"modifyingAttributeID":2594}]}""")]);
        var bonuses = IndustryBonuses.RigBonuses(rig, 0.9);

        Assert.Single(rig.Modifiers);
        Assert.Equal(0.95, IndustryBonuses.Multiplier(bonuses, IndustryBonusKind.Material, IndustryRules.Manufacturing, 8, null), 10);
        Assert.Equal(1.0, IndustryBonuses.Multiplier(bonuses, IndustryBonusKind.Material, IndustryRules.Manufacturing, 7, null));
        Assert.Equal(1.0, IndustryBonuses.Multiplier(bonuses, IndustryBonusKind.Material, IndustryRules.Manufacturing, null, null));
    }

    [Fact]
    public void Rig_slots_are_flags_92_to_99_and_each_rig_lists_the_activities_it_changes()
    {
        Assert.False(IndustryBonuses.IsRigSlot(91));
        Assert.True(IndustryBonuses.IsRigSlot(92));
        Assert.True(IndustryBonuses.IsRigSlot(99));
        Assert.False(IndustryBonuses.IsRigSlot(100));

        var bonuses = new[]
        {
            new IndustryBonus(IndustryBonusKind.Material, IndustryRules.Manufacturing, 0.98, IndustryBonuses.SourceRig, GroupId: 26, RigTypeId: 37146),
            new IndustryBonus(IndustryBonusKind.Time, IndustryRules.Manufacturing, 0.8, IndustryBonuses.SourceRig, GroupId: 26, RigTypeId: 37146),
            new IndustryBonus(IndustryBonusKind.Cost, IndustryRules.Invention, 0.9, IndustryBonuses.SourceRig, RigTypeId: 37147),
            new IndustryBonus(IndustryBonusKind.Time, IndustryRules.Manufacturing, 0.85, IndustryBonuses.SourceHull),
        };
        var rigs = IndustryBonuses.RigActivities(bonuses);

        Assert.Equal(2, rigs.Count);
        Assert.Equal(new[] { IndustryRules.Manufacturing }, rigs[37146]);
        Assert.Equal(new[] { IndustryRules.Invention }, rigs[37147]);
    }
}

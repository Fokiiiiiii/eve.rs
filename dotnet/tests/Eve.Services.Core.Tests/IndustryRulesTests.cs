using Eve.Services.Core;
using Xunit;

namespace Eve.Services.Core.Tests;

public sealed class IndustryRulesTests
{
    [Fact]
    public void Material_quantity_rounds_up_after_rounding_to_two_decimals_and_is_at_least_one_per_run()
    {
        Assert.Equal(78, IndustryRules.MaterialQuantity(86, IndustryRules.MaterialEfficiencyModifier(10), 1, 1));
        Assert.Equal(10, IndustryRules.MaterialQuantity(1, IndustryRules.MaterialEfficiencyModifier(10), 10, 10));
        Assert.Equal(1000, IndustryRules.MaterialQuantity(100, 1.0, 10, 10));
    }

    [Fact]
    public void Efficiency_modifiers_follow_the_client_percentages()
    {
        Assert.Equal(0.9, IndustryRules.MaterialEfficiencyModifier(10), 12);
        Assert.Equal(1.0, IndustryRules.MaterialEfficiencyModifier(0), 12);
        Assert.Equal(0.8, IndustryRules.TimeEfficiencyModifier(20), 12);
        Assert.Equal(0.8, IndustryRules.SkillModifier(-4, 5), 12);
    }

    [Fact]
    public void Install_cost_is_the_index_tax_scc_and_security_shares_of_the_base_cost()
    {
        var parts = IndustryRules.CostParts(1_000_000, 0.05, 0.10, highSecurity: true, IndustryRules.Manufacturing);
        Assert.Equal(new InstallCostParts(50_000, 100_000, 40_000, 50_000), parts);
        Assert.Equal(240_000, IndustryRules.Total(parts));
    }

    [Fact]
    public void Research_scc_is_halved_and_null_security_tax_outside_high_security()
    {
        var parts = IndustryRules.CostParts(1_000_000, 0.0, 0.0, highSecurity: false, IndustryRules.ResearchTime);
        Assert.Equal(new InstallCostParts(0, 0, 20_000, 0), parts);
    }

    [Fact]
    public void Duration_multiplies_the_base_seconds_by_every_modifier()
    {
        Assert.Equal(864, IndustryRules.DurationSeconds(1200, [0.9, 0.8]));
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
}

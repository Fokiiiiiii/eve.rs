using Eve.Services.Space.Pochven;

namespace Eve.Services.Space.Tests;

/// <summary>The Pochven rules (the region, the capital stargate refusal, the faction hostility and the NPC standing rule): pure, no PostgreSQL.</summary>
public sealed class PochvenRulesTests
{
    private const int TitanGroup = 30, FrigateGroup = 25, BlackOpsGroup = 898, JumpFreighterGroup = 902;
    private const int TheForgeRegion = 10_000_002;
    private const int TriglavianFaction = 500_026, EdencomFaction = 500_027, RogueDroneFaction = 500_025, DrifterFaction = 500_024;
    private const int ConcordFaction = 500_006, SerpentisFaction = 500_020;

    [Fact]
    public void The_capital_groups_are_the_six_the_sde_names_and_no_other()
    {
        var capitals = Enumerable.Range(0, 2_000).Where(PochvenRules.IsCapitalGroup).ToArray();
        Assert.Equal([30, 485, 547, 659, 883, 1538], capitals);
    }

    [Fact]
    public void A_capital_is_refused_a_stargate_in_pochven_and_no_other_ship_or_region_is()
    {
        Assert.Equal(PochvenRules.CapitalStargateRefusal, PochvenRules.StargateRefusal(PochvenRules.RegionId, TitanGroup));
        Assert.Null(PochvenRules.StargateRefusal(PochvenRules.RegionId, FrigateGroup));
        Assert.Null(PochvenRules.StargateRefusal(PochvenRules.RegionId, BlackOpsGroup));
        Assert.Null(PochvenRules.StargateRefusal(PochvenRules.RegionId, JumpFreighterGroup));
        Assert.Null(PochvenRules.StargateRefusal(TheForgeRegion, TitanGroup));
    }

    [Fact]
    public void The_four_pochven_factions_attack_each_other_and_no_other_faction_is_part_of_the_rule()
    {
        int[] four = [TriglavianFaction, EdencomFaction, RogueDroneFaction, DrifterFaction];
        foreach (var a in four)
        {
            foreach (var b in four)
            {
                Assert.Equal(a != b, PochvenRules.FactionsAreHostile(a, b));
            }
        }
        Assert.False(PochvenRules.FactionsAreHostile(TriglavianFaction, ConcordFaction));
        Assert.False(PochvenRules.FactionsAreHostile(EdencomFaction, SerpentisFaction));
        Assert.False(PochvenRules.FactionsAreHostile(TriglavianFaction, 0));
    }

    [Fact]
    public void Triglavian_rats_are_hostile_at_zero_and_below_and_edencom_rats_only_below_zero()
    {
        Assert.True(PochvenRules.NpcHostileToPilot(TriglavianFaction, 0.0));
        Assert.False(PochvenRules.NpcHostileToPilot(TriglavianFaction, 0.01));
        Assert.True(PochvenRules.NpcHostileToPilot(TriglavianFaction, -0.5));
        Assert.False(PochvenRules.NpcHostileToPilot(EdencomFaction, 0.0));
        Assert.True(PochvenRules.NpcHostileToPilot(EdencomFaction, -0.01));
        Assert.True(PochvenRules.NpcHostileToPilot(RogueDroneFaction, 10.0));
        Assert.True(PochvenRules.NpcHostileToPilot(DrifterFaction, 10.0));
        Assert.False(PochvenRules.NpcHostileToPilot(ConcordFaction, -10.0));
    }
}

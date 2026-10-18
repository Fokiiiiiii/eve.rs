namespace Eve.Skills.Tests;

/// <summary>The canonical order and levels of a plan (EveJS skillPlanState.js canonicalizeSkillRequirements): prerequisites absorbed and listed first.</summary>
public class SkillPlanCanonicalTests
{
    private const int Gunnery = 1, Turrets = 2, Engineering = 3, Hidden = 4;

    private static SkillCatalog Catalog() => new(
    [
        new SkillType(Gunnery, "Gunnery", 1, "g", true, 1),
        new SkillType(Turrets, "Turrets", 1, "g", true, 2, Requirements: [new SkillRequirement(Gunnery, 2)]),
        new SkillType(Engineering, "Engineering", 1, "g", true, 1, Requirements: [new SkillRequirement(Turrets, 1)]),
        new SkillType(Hidden, "Unpublished", 1, "g", false, 1),
    ], new Dictionary<int, IReadOnlyList<StartingSkill>>());

    [Fact]
    public void Prerequisites_are_absorbed_at_the_level_they_need_and_listed_before_the_skills_that_need_them()
    {
        var plan = SkillPlanCanonical.Canonicalize([new SkillPlanRequirement(Engineering, 2)], Catalog());
        Assert.Equal(
            [(Gunnery, 1), (Gunnery, 2), (Turrets, 1), (Engineering, 1), (Engineering, 2)],
            plan.Select(r => (r.SkillTypeId, r.Level)));
    }

    [Fact]
    public void A_skill_named_twice_keeps_its_highest_level_once_and_levels_above_5_or_unpublished_skills_are_dropped()
    {
        var plan = SkillPlanCanonical.Canonicalize(
        [
            new SkillPlanRequirement(Gunnery, 2), new SkillPlanRequirement(Gunnery, 1),
            new SkillPlanRequirement(Hidden, 3), new SkillPlanRequirement(Turrets, 9),
        ], Catalog());
        Assert.Equal(
            [(Gunnery, 1), (Gunnery, 2), (Turrets, 1), (Turrets, 2), (Turrets, 3), (Turrets, 4), (Turrets, 5)],
            plan.Select(r => (r.SkillTypeId, r.Level)));
    }
}

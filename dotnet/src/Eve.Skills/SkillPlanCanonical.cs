namespace Eve.Skills;

/// <summary>
/// The requirements of a skill plan as the reference keeps them (EveJS <c>canonicalizeSkillRequirements</c>, skillPlanState.js): each skill
/// once at its highest level, the published prerequisites it needs absorbed at the level they need (up to level 5), and every level of
/// every skill listed after the skills it needs. Skills that are not published, or levels that are not 1 to 5, are dropped.
/// </summary>
public static class SkillPlanCanonical
{
    public static IReadOnlyList<SkillPlanRequirement> Canonicalize(IEnumerable<SkillPlanRequirement> requirements, SkillCatalog catalog)
    {
        var target = new Dictionary<int, int>();
        var rootOrder = new List<int>();
        foreach (var requirement in requirements)
        {
            var level = Math.Clamp(requirement.Level, 0, 5);
            if (requirement.SkillTypeId <= 0 || level <= 0 || !IsPublished(catalog, requirement.SkillTypeId))
                continue;
            if (!target.ContainsKey(requirement.SkillTypeId))
            {
                rootOrder.Add(requirement.SkillTypeId);
                target[requirement.SkillTypeId] = level;
            }
            else
            {
                target[requirement.SkillTypeId] = Math.Max(target[requirement.SkillTypeId], level);
            }
        }

        var visiting = new HashSet<int>();
        void Absorb(int skillTypeId)
        {
            if (!visiting.Add(skillTypeId))
                return;
            foreach (var need in catalog.Get(skillTypeId)?.Needs ?? [])
            {
                var level = Math.Clamp(need.Level, 0, 5);
                if (need.TypeId <= 0 || level <= 0 || !IsPublished(catalog, need.TypeId))
                    continue;
                if (target.GetValueOrDefault(need.TypeId) < level)
                    target[need.TypeId] = level;
                Absorb(need.TypeId);
            }
            visiting.Remove(skillTypeId);
        }
        foreach (var skillTypeId in rootOrder)
            Absorb(skillTypeId);

        var ordered = new List<SkillPlanRequirement>();
        var emitted = new HashSet<int>();
        var emitting = new HashSet<int>();
        void Emit(int skillTypeId)
        {
            if (emitted.Contains(skillTypeId) || !emitting.Add(skillTypeId))
                return;
            foreach (var need in catalog.Get(skillTypeId)?.Needs ?? [])
            {
                if (target.GetValueOrDefault(need.TypeId) > 0)
                    Emit(need.TypeId);
            }
            for (var level = 1; level <= target.GetValueOrDefault(skillTypeId); level++)
                ordered.Add(new SkillPlanRequirement(skillTypeId, level));
            emitted.Add(skillTypeId);
            emitting.Remove(skillTypeId);
        }
        foreach (var skillTypeId in rootOrder)
            Emit(skillTypeId);
        foreach (var skillTypeId in target.Keys.ToList())
            Emit(skillTypeId);
        return ordered;
    }

    private static bool IsPublished(SkillCatalog catalog, int skillTypeId) => catalog.Get(skillTypeId) is { Published: true };
}

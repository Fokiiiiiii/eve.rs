using System.Text.Json;
using Eve.Sde;
using Eve.Skills;

namespace Eve.Corporations;

internal sealed class CorporationMemberLimitRules(SdeReader sde)
{
    private const int MaximumSkillLevel = 5;
    private static readonly string[] CorporationSkills =
    [
        "Corporation Management",
        "Megacorp Management",
        "Empire Control",
        "Sovereignty",
    ];

    private readonly object _gate = new();
    private Task<IReadOnlyDictionary<int, SkillRule>>? _rules;

    public async Task<int> MemberLimitAsync(IReadOnlyList<CharacterSkill> skills, CancellationToken cancellationToken)
    {
        var rules = await RulesAsync(cancellationToken);
        var levels = skills.GroupBy(skill => skill.TypeId)
            .ToDictionary(group => group.Key, group => Math.Clamp(group.Max(skill => skill.Level), 0, MaximumSkillLevel));
        var resolved = new Dictionary<int, int>();
        var resolving = new HashSet<int>();
        return checked(rules.Values.Sum(rule => rule.MemberBonus * EffectiveLevel(rule.TypeId, rules, levels, resolved, resolving)));
    }

    public async Task<bool> CanFoundCorporationAsync(IReadOnlyList<CharacterSkill> skills, CancellationToken cancellationToken)
    {
        var rules = await RulesAsync(cancellationToken);
        var levels = skills.GroupBy(skill => skill.TypeId)
            .ToDictionary(group => group.Key, group => Math.Clamp(group.Max(skill => skill.Level), 0, MaximumSkillLevel));
        var corporationManagement = rules.Values.Single(rule => rule.Name == CorporationSkills[0]);
        return EffectiveLevel(corporationManagement.TypeId, rules, levels, new Dictionary<int, int>(), new HashSet<int>()) >= 1;
    }

    private async Task<IReadOnlyDictionary<int, SkillRule>> RulesAsync(CancellationToken cancellationToken)
    {
        Task<IReadOnlyDictionary<int, SkillRule>> task;
        lock (_gate)
        {
            _rules ??= LoadRulesAsync();
            task = _rules;
        }

        try
        {
            return await task.WaitAsync(cancellationToken);
        }
        catch
        {
            if (task.IsFaulted || task.IsCanceled)
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_rules, task))
                    {
                        _rules = null;
                    }
                }
            }
            throw;
        }
    }

    private async Task<IReadOnlyDictionary<int, SkillRule>> LoadRulesAsync()
    {
        if (await sde.BuildAsync() is null)
            throw new InvalidDataException("The pinned SDE build is not loaded.");

        var types = await sde.ReadTypeSummariesAsync();
        var skillTypes = CorporationSkills.ToDictionary(
            name => name,
            name => types.SingleOrDefault(type => type.Published && type.Name == name)
                ?? throw new InvalidDataException($"The SDE does not define the published skill '{name}'."),
            StringComparer.Ordinal);
        var neededAttributes = new HashSet<string>(StringComparer.Ordinal) { "corporationMemberBonus" };
        for (var slot = 1; slot <= 6; slot++)
        {
            neededAttributes.Add($"requiredSkill{slot}");
            neededAttributes.Add($"requiredSkill{slot}Level");
        }

        var attributeRows = await sde.ReadAsync("dogmaAttributes");
        var attributeIds = attributeRows
            .Where(row => row.Doc.TryGetProperty("name", out var name)
                && name.ValueKind == JsonValueKind.String
                && neededAttributes.Contains(name.GetString()!))
            .ToDictionary(row => row.Doc.GetProperty("name").GetString()!, row => checked((int)row.Key), StringComparer.Ordinal);
        foreach (var name in neededAttributes)
        {
            if (!attributeIds.ContainsKey(name))
                throw new InvalidDataException($"The SDE does not define dogma attribute '{name}'.");
        }

        var dogmaRows = await sde.ReadAsync("typeDogma", skillTypes.Values.Select(type => (long)type.TypeId));
        var dogmaByType = dogmaRows.ToDictionary(row => checked((int)row.Key));
        var rules = new Dictionary<int, SkillRule>();
        foreach (var (name, type) in skillTypes)
        {
            if (!dogmaByType.TryGetValue(type.TypeId, out var dogma)
                || !dogma.Doc.TryGetProperty("dogmaAttributes", out var attributes)
                || attributes.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException($"The SDE has no dogma attributes for '{name}'.");

            var values = attributes.EnumerateArray().ToDictionary(
                attribute => ReadInteger(attribute.GetProperty("attributeID"), $"attribute ID for {name}"),
                attribute => attribute.GetProperty("value"));
            if (!values.TryGetValue(attributeIds["corporationMemberBonus"], out var bonusValue))
                throw new InvalidDataException($"The SDE has no corporation member bonus for '{name}'.");
            var bonus = ReadInteger(bonusValue, $"corporation member bonus for {name}");
            if (bonus <= 0)
                throw new InvalidDataException($"The SDE has an invalid corporation member bonus for '{name}'.");

            var prerequisites = new List<SkillRequirement>();
            for (var slot = 1; slot <= 6; slot++)
            {
                var skillAttribute = attributeIds[$"requiredSkill{slot}"];
                var levelAttribute = attributeIds[$"requiredSkill{slot}Level"];
                if (!values.TryGetValue(skillAttribute, out var requiredSkillValue))
                    continue;
                var requiredSkill = ReadInteger(requiredSkillValue, $"required skill {slot} for {name}");
                if (requiredSkill <= 0)
                    continue;
                if (!values.TryGetValue(levelAttribute, out var requiredLevelValue))
                    throw new InvalidDataException($"The SDE has no required level for skill {slot} of '{name}'.");
                var requiredLevel = ReadInteger(requiredLevelValue, $"required level for skill {slot} of {name}");
                if (requiredLevel is < 1 or > MaximumSkillLevel)
                    throw new InvalidDataException($"The SDE has an invalid required level for skill {slot} of '{name}'.");
                prerequisites.Add(new SkillRequirement(requiredSkill, requiredLevel));
            }
            rules.Add(type.TypeId, new SkillRule(type.TypeId, name, bonus, prerequisites));
        }

        return rules;
    }

    private static int EffectiveLevel(
        int typeId,
        IReadOnlyDictionary<int, SkillRule> rules,
        IReadOnlyDictionary<int, int> levels,
        IDictionary<int, int> resolved,
        ISet<int> resolving)
    {
        if (resolved.TryGetValue(typeId, out var cached))
            return cached;
        if (!rules.TryGetValue(typeId, out var rule))
            return levels.GetValueOrDefault(typeId);
        if (!resolving.Add(typeId))
            throw new InvalidDataException($"The SDE contains a circular skill prerequisite at type {typeId}.");

        var level = levels.GetValueOrDefault(typeId);
        if (rule.Prerequisites.Any(requirement =>
            EffectiveLevel(requirement.TypeId, rules, levels, resolved, resolving) < requirement.Level))
            level = 0;

        resolving.Remove(typeId);
        resolved[typeId] = level;
        return level;
    }

    private static int ReadInteger(JsonElement value, string description)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var number)
            || number != decimal.Truncate(number) || number is < int.MinValue or > int.MaxValue)
            throw new InvalidDataException($"The SDE has a non-integer {description}.");
        return decimal.ToInt32(number);
    }

    private sealed record SkillRule(int TypeId, string Name, int MemberBonus, IReadOnlyList<SkillRequirement> Prerequisites);
    private sealed record SkillRequirement(int TypeId, int Level);
}

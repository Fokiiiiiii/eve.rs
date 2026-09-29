using System.Text.Json;
using Eve.Sde;

namespace Eve.Skills;

/// <summary>A skill as the static data describes it.</summary>
public sealed record SkillType(int TypeId, string Name, int GroupId, string GroupName, bool Published, int Rank);

/// <summary>A skill at a level a new character of a race starts with.</summary>
public sealed record StartingSkill(int TypeId, int Level);

/// <summary>
/// The skills of the game (the types of the skill category, with their rank, group and whether they are published)
/// and the skills each playable race starts with, read from the static data.
/// </summary>
public sealed class SkillCatalog
{
    /// <summary>The category the skills are in.</summary>
    public const int SkillCategoryId = 16;

    /// <summary>The dogma attribute that holds a skill's rank (<c>skillTimeConstant</c>).</summary>
    public const int RankAttributeId = 275;

    private readonly Dictionary<int, SkillType> _skills;
    private readonly Dictionary<int, StartingSkill[]> _starting;

    public SkillCatalog(IEnumerable<SkillType> skills, IReadOnlyDictionary<int, IReadOnlyList<StartingSkill>> starting)
    {
        _skills = skills.ToDictionary(s => s.TypeId);
        _starting = starting.ToDictionary(e => e.Key, e => e.Value.ToArray());
    }

    public IReadOnlyCollection<SkillType> Skills => _skills.Values;

    public SkillType? Get(int typeId) => _skills.GetValueOrDefault(typeId);

    /// <summary>The skills a new character of <paramref name="raceId"/> has, at the levels it has them (some at 0).</summary>
    public IReadOnlyList<StartingSkill> StartingSkills(int raceId) => _starting.GetValueOrDefault(raceId) ?? [];

    /// <summary>The starting skills with their points, or null when one of them is not a skill of this catalog.</summary>
    public IReadOnlyList<CharacterSkill>? StartingSet(int raceId)
    {
        var set = new List<CharacterSkill>();
        foreach (var start in StartingSkills(raceId))
        {
            if (Get(start.TypeId) is not { } skill)
            {
                return null;
            }
            set.Add(new CharacterSkill(skill.TypeId, start.Level, SkillPoints.At(skill.Rank, start.Level)));
        }
        return set;
    }

    public static async Task<SkillCatalog> LoadAsync(SdeReader sde, CancellationToken cancellationToken = default)
    {
        var groups = (await sde.ReadByFieldAsync("groups", "categoryID", [SkillCategoryId], cancellationToken))
            .ToDictionary(g => g.Key, g => SdeReader.English(g.Doc.GetProperty("name")));
        var types = await sde.ReadByFieldAsync("types", "groupID", groups.Keys, cancellationToken);
        var dogma = await sde.ReadAsync("typeDogma", types.Select(t => t.Key), cancellationToken);

        var ranks = new Dictionary<long, int>();
        foreach (var row in dogma)
        {
            foreach (var attribute in row.Doc.GetProperty("dogmaAttributes").EnumerateArray())
            {
                if (attribute.GetProperty("attributeID").GetInt32() == RankAttributeId)
                {
                    ranks[row.Key] = (int)Math.Round(attribute.GetProperty("value").GetDouble());
                }
            }
        }

        var skills = new List<SkillType>();
        foreach (var type in types)
        {
            if (!ranks.TryGetValue(type.Key, out var rank))
            {
                continue; // in a skill group but with no rank: not a trainable skill
            }
            var group = type.Doc.GetProperty("groupID").GetInt32();
            var published = type.Doc.TryGetProperty("published", out var flag) && flag.ValueKind == JsonValueKind.True;
            skills.Add(new SkillType((int)type.Key, SdeReader.English(type.Doc.GetProperty("name")), group, groups[group], published, rank));
        }

        var starting = new Dictionary<int, IReadOnlyList<StartingSkill>>();
        foreach (var race in await sde.ReadAsync("races", cancellationToken: cancellationToken))
        {
            if (race.Doc.TryGetProperty("skills", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                starting[(int)race.Key] = [.. list.EnumerateArray().Select(e => new StartingSkill(e.GetProperty("_key").GetInt32(), e.GetProperty("_value").GetInt32()))];
            }
        }
        return new SkillCatalog(skills, starting);
    }
}

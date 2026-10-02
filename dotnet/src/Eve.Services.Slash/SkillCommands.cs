using Eve.Marshal;
using Eve.Services.Skills;
using Eve.Skills;
using Npgsql;

namespace Eve.Services.Slash;

/// <summary>
/// The game master's skill command: <c>/giveskill me &lt;skill name|typeID|all|super&gt; [level]</c> sets a skill of the pilot (all the published ones, or every one) to a level
/// (5 when none is given; 0 to 5). Its words are the reference's (<c>services/chat/commands/skills.js</c>, ADR 0009). The reference also serves another character, and
/// mends the training queue and the skill session of a character that is online: here the target is the pilot, and the client is told what training tells it when a level
/// is reached (<c>OnServerSkillsChanged</c>, <c>OnSkillLevelsTrained</c>, <c>OnSkillsChanged</c> and the ship and character after it) (<c>docs/compat/DEVIATIONS.md</c>).
/// </summary>
public sealed class SkillCommands(SkillCatalog catalog, PgSkillStore store, NpgsqlDataSource source, ICharacterDogmaSync? dogma, Func<long> nowUnixMs)
{
    private const string Usage = "Usage: /giveskill <target> <skill name|typeID|all|super> [level]";
    private static readonly string[] LevelLabels = ["0", "I", "II", "III", "IV", "V"];

    private static string Normalize(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c) && c is not ('_' or '-'))).ToLowerInvariant();

    private static string Suggestions(IEnumerable<string> names) => names.Any() ? $" Suggestions: {string.Join(", ", names)}" : "";

    public async Task<string> GiveAsync(ClientSession session, string argument, CancellationToken cancellationToken)
    {
        var tokens = argument.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim('"', '\'')).Where(t => t.Length > 0).ToList();
        if (tokens.Count < 2)
        {
            return Usage;
        }
        var level = 5;
        int? requested = null;
        var skillTokens = tokens.Skip(1).ToList();
        if (tokens.Count >= 3 && int.TryParse(tokens[^1], out var trailing))
        {
            requested = trailing;
            level = Math.Clamp(trailing, 0, SkillPoints.MaxLevel);
            skillTokens = [.. tokens.Skip(1).Take(tokens.Count - 2)];
        }
        var skillToken = string.Join(' ', skillTokens).Trim();
        if (skillToken.Length == 0)
        {
            return Usage;
        }
        if (session.CharacterId is not { } character)
        {
            return "Select a character before using /giveskill me.";
        }
        if (!tokens[0].Equals("me", StringComparison.OrdinalIgnoreCase) && tokens[0] != character.ToString(System.Globalization.CultureInfo.InvariantCulture))
        {
            return $"Character not found: {tokens[0]}. Use me, a character ID, or an exact character name.";
        }

        var query = Normalize(skillToken);
        var all = query == "all";
        var super = query == "super";
        SkillType? single = null;
        if (!all && !super)
        {
            var skills = catalog.Skills;
            if (int.TryParse(skillToken, out var typeId) && typeId > 0)
            {
                single = catalog.Get(typeId);
            }
            else
            {
                var exact = skills.Where(skill => Normalize(skill.Name) == query).ToList();
                if (exact.Count > 1)
                {
                    return $"Skill is ambiguous: {skillToken}.{Suggestions(exact.Take(5).Select(skill => $"{skill.Name}({skill.TypeId})"))}";
                }
                single = exact.SingleOrDefault();
            }
            if (single is null)
            {
                var near = skills.Where(skill => Normalize(skill.Name).Contains(query, StringComparison.Ordinal)).OrderBy(skill => skill.Name).Take(5).Select(skill => $"{skill.Name}({skill.TypeId})");
                return $"Skill not found: {skillToken}.{Suggestions(near)}";
            }
        }

        var before = await store.ForCharacterAsync(character, cancellationToken);
        var have = before.ToDictionary(skill => skill.TypeId);
        var targets = single is not null ? [single] : catalog.Skills.Where(skill => super || skill.Published).ToList();
        var changed = new List<CharacterSkill>();
        foreach (var skill in targets)
        {
            if (have.TryGetValue(skill.TypeId, out var owned) && owned.Level == level)
            {
                continue;
            }
            changed.Add(new CharacterSkill(skill.TypeId, level, SkillPoints.At(skill.Rank, level)));
        }
        if (changed.Count > 0)
        {
            await using var connection = await source.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await store.GrantAsync(connection, transaction, character, changed, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            var skills = SkillWire.EntryDictionary(character, catalog, changed);
            SkillNotifications.ServerSkillsChanged(session, skills, null, Py.FileTimeFromUnixMs(nowUnixMs()));
            SkillNotifications.LevelsTrained(session, changed);
            SkillNotifications.SkillsChanged(session, skills);
            if (dogma is not null)
            {
                await dogma.SkillsChangedAsync(session, character, before, cancellationToken);
            }
        }

        var target = $"character({character})";
        var label = LevelLabels[level];
        var clamped = requested is { } asked && asked != level ? $" Requested level {asked} was clamped to {level}." : "";
        if (all || super)
        {
            var which = all ? "all published skills" : "all published and unpublished skills";
            return $"Set {which} for {target} to level {label}. " +
                (changed.Count > 0 ? $"Updated {changed.Count} skills." : all ? "All published skills already matched the requested level." : "All skills already matched the requested level.") + clamped;
        }
        return (changed.Count > 0 ? $"Set {single!.Name}({single.TypeId}) for {target} to level {label}." : $"{target} already has {single!.Name}({single.TypeId}) at level {label}.") + clamped;
    }
}

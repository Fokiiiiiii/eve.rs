using Eve.Marshal;
using Eve.Services.Skills;
using Eve.Skills;
using Npgsql;

namespace Eve.Services.Slash;

/// <summary>
/// The game master's skill command: <c>/giveskill me &lt;skill name|typeID|all|super&gt; [level]</c> sets a skill of the pilot (all the published ones, or every one) to a level
/// (5 when none is given; 0 to 5). Its words are the reference's (<c>services/chat/commands/skills.js</c>, ADR 0009). The reference also serves another character, and
/// mends the training queue and the skill session of a character that is online: here the target is the pilot, and the client is told what training tells it when a level
/// </summary>
public sealed class SkillCommands(SkillCatalog catalog, PgSkillStore store, NpgsqlDataSource source, ICharacterDogmaSync? dogma, Func<long> nowUnixMs)
{
    private const string Usage = "Usage: /giveskill <target> <skill name|typeID|all|super> [level]";
    private const string RemoveUsage = "Usage: /removeskill <target> <skill name|typeID|all>";
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
        if (!all && !super && Resolve(skillToken, out single) is { } error) return error;

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

    /// <summary>The skill a token names (a name, or a type ID); the message when there is none or the name is ambiguous.</summary>
    private static int SuggestionDistance(string query, string name)
    {
        if (query.Length == 0) return int.MaxValue;
        if (name.StartsWith(query, StringComparison.Ordinal)) return 0;
        if (name.Contains(query, StringComparison.Ordinal)) return 1;

        var previous = Enumerable.Range(0, name.Length + 1).ToArray();
        var current = new int[name.Length + 1];
        for (var i = 1; i <= query.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= name.Length; j++)
            {
                var substitution = query[i - 1] == name[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + substitution);
            }
            (previous, current) = (current, previous);
        }
        return previous[name.Length];
    }

    private string? Resolve(string skillToken, out SkillType? single)
    {
        var query = Normalize(skillToken);
        var skills = catalog.Skills;
        single = null;
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
            var near = skills
                .Select(skill => (Skill: skill, Distance: SuggestionDistance(query, Normalize(skill.Name))))
                .Where(candidate => candidate.Distance <= 6)
                .OrderBy(candidate => candidate.Distance)
                .ThenBy(candidate => candidate.Skill.Name, StringComparer.Ordinal)
                .Take(5)
                .Select(candidate => $"{candidate.Skill.Name}({candidate.Skill.TypeId})");
            return $"Skill not found: {skillToken}.{Suggestions(near)}";
        }
        return null;
    }

    /// <summary>
    /// the skills are gone with the zeroed records EveJS sends. shortcut: the skill queue is not cleared (EveJS clears it), upgrade when a queue row survives a removal.
    /// </summary>
    public async Task<string> RemoveAsync(ClientSession session, string argument, CancellationToken cancellationToken)
    {
        var tokens = argument.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim('"', '\'')).Where(t => t.Length > 0).ToList();
        if (tokens.Count < 2) return RemoveUsage;
        var skillToken = string.Join(' ', tokens.Skip(1)).Trim();
        if (session.CharacterId is not { } character) return "Select a character before using /removeskill me.";
        if (!tokens[0].Equals("me", StringComparison.OrdinalIgnoreCase) && tokens[0] != character.ToString(System.Globalization.CultureInfo.InvariantCulture))
            return $"Character not found: {tokens[0]}. Use me, a character ID, or an exact character name.";

        var all = Normalize(skillToken) == "all";
        SkillType? single = null;
        if (!all && Resolve(skillToken, out single) is { } error) return error;

        var before = await store.ForCharacterAsync(character, cancellationToken);
        var removed = before.Where(skill => all || skill.TypeId == single!.TypeId).Select(skill => new CharacterSkill(skill.TypeId, 0, 0)).ToList();
        if (removed.Count > 0)
        {
            await using var connection = await source.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await store.DeleteAsync(connection, transaction, character, all ? null : [single!.TypeId], cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            var skills = SkillWire.EntryDictionary(character, catalog, removed);
            SkillNotifications.ServerSkillsChanged(session, skills, null, Py.FileTimeFromUnixMs(nowUnixMs()));
            SkillNotifications.SkillsChanged(session, skills);
            if (dogma is not null) await dogma.SkillsChangedAsync(session, character, before, cancellationToken);
        }

        var target = $"character({character})";
        if (all)
            return removed.Count > 0 ? $"Removed all skills from {target}. Deleted {removed.Count} skill records." : $"{target} already has no skills.";
        return removed.Count > 0 ? $"Removed {single!.Name}({single.TypeId}) from {target}." : $"{target} does not have {single!.Name}({single.TypeId}).";
    }
}

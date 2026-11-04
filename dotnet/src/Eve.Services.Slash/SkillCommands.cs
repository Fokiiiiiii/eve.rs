using Eve.Characters;
using Eve.Services;
using Eve.Services.Skills;
using Eve.Skills;

namespace Eve.Services.Slash;

/// <summary>
/// The game master's skill command: <c>/giveskill &lt;target&gt; &lt;skill name|typeID|all|super&gt; [level]</c> sets a target's skill level
/// (5 when none is given; 0 to 5). It also reconciles that character's queue and tells its active client what changed.
/// </summary>
public sealed class SkillCommands(SkillCatalog catalog, PgCharacterStore characters, CharacterTraining training, TrainingAnnouncer announcer, Func<long, ClientSession?> sessionOf)
{
    private const string Usage = "Usage: /giveskill <target> <skill name|typeID|all|super> [level]";
    private const string RemoveUsage = "Usage: /removeskill <target> <skill name|typeID|all>";
    private static readonly string[] LevelLabels = ["0", "I", "II", "III", "IV", "V"];

    private static string Normalize(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c) && c is not ('_' or '-'))).ToLowerInvariant();

    private static string Suggestions(IEnumerable<string> names) => names.Any() ? $" Suggestions: {string.Join(", ", names)}" : "";

    private async Task<(Character? Pilot, string? Error)> ResolveTargetAsync(ClientSession session, string token, string command, CancellationToken cancellationToken)
    {
        var (id, error) = await PilotTokens.ResolveAsync(characters, session, token, cancellationToken);
        if (id is null)
        {
            return (null, error == "Select a character before using me." ? $"Select a character before using {command} me." : error);
        }
        var pilot = await characters.GetAsync(id.Value, cancellationToken);
        return pilot is null
            ? (null, $"Character not found: {token}. Use me, a character ID, or an exact character name.")
            : (pilot, null);
    }

    public async Task<string> GiveAsync(ClientSession session, string argument, CancellationToken cancellationToken)
    {
        var tokens = PilotTokens.Operands(argument);
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
        var (pilot, targetError) = await ResolveTargetAsync(session, tokens[0], "/giveskill", cancellationToken);
        if (pilot is null) return targetError!;
        var character = pilot.Id;

        var query = Normalize(skillToken);
        var all = query == "all";
        var super = query == "super";
        SkillType? single = null;
        if (!all && !super && Resolve(skillToken, out single) is { } error) return error;

        var targets = single is not null ? [single] : catalog.Skills.Where(skill => super || skill.Published).ToList();
        var update = await training.ApplyAdminMutationAsync(character, pilot.AccountId, targets
            .Select(skill => new CharacterSkill(skill.TypeId, level, SkillPoints.At(skill.Rank, level))).ToList(), [],
            deleteUnwanted: false, clearQueue: false, cancellationToken);
        await announcer.AnnounceAdminMutationAsync(character == session.CharacterId ? session : sessionOf(character), update);
        var changed = update.ChangedSkills;

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

    /// <summary>Removes a target character's skill rows and clears its persisted training queue.</summary>
    public async Task<string> RemoveAsync(ClientSession session, string argument, CancellationToken cancellationToken)
    {
        var tokens = PilotTokens.Operands(argument);
        if (tokens.Count < 2) return RemoveUsage;
        var skillToken = string.Join(' ', tokens.Skip(1)).Trim();
        var (pilot, targetError) = await ResolveTargetAsync(session, tokens[0], "/removeskill", cancellationToken);
        if (pilot is null) return targetError!;
        var character = pilot.Id;

        var all = Normalize(skillToken) == "all";
        SkillType? single = null;
        if (!all && Resolve(skillToken, out single) is { } error) return error;

        var update = await training.ApplyAdminMutationAsync(character, pilot.AccountId, [],
            all ? null : [single!.TypeId], deleteUnwanted: all, clearQueue: true, cancellationToken);
        await announcer.AnnounceAdminMutationAsync(character == session.CharacterId ? session : sessionOf(character), update);
        var removed = update.RemovedSkills;

        var target = $"character({character})";
        if (all)
            return removed.Count > 0 ? $"Removed all skills from {target}. Deleted {removed.Count} skill records." : $"{target} already has no skills.";
        return removed.Count > 0 ? $"Removed {single!.Name}({single.TypeId}) from {target}." : $"{target} does not have {single!.Name}({single.TypeId}).";
    }
}

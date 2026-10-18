using System.Globalization;
using System.Text.RegularExpressions;
using Eve.Characters;
using Eve.Marshal;
using Eve.Services.Skills;
using Eve.Skills;
using Npgsql;

namespace Eve.Services.Slash;

/// <summary>
/// The game master's bulk skill commands. <c>/givestarterskills [me|characterID|name]</c> sets the skills of the pilot's race to the starting set the SDE gives
/// character creation (the levels of <see cref="SkillCatalog.StartingSet"/>, the other skills are kept). <c>/randomizeskills</c> sets every published skill of the
/// pilot to a random level from 0 to 5. <c>/skillscopy "from" "to"</c> makes the second character's skills the first's, levels and points, and removes the rest.
/// A pilot who is online is told the change the way training tells it; an offline pilot reads it at the next login.
/// </summary>
public sealed class SkillBulkCommands(
    SkillCatalog catalog, PgSkillStore store, NpgsqlDataSource source, PgCharacterStore characters, ICharacterDogmaSync? dogma,
    Func<long> nowUnixMs, Func<long, ClientSession?> sessionOf, Random? random = null)
{
    public const string SkillCopyUsage = "Usage: /skillscopy \"<from character>\" \"<to character>\"";
    private static readonly Regex Operand = new("\"([^\"]*)\"|'([^']*)'|(\\S+)", RegexOptions.Compiled);
    private readonly Random _random = random ?? Random.Shared;

    public async Task<string> GiveStarterAsync(ClientSession session, string argument, CancellationToken cancellationToken)
    {
        var token = argument.Trim().Trim('"', '\'');
        var (id, error) = await ResolveAsync(session, token.Length == 0 ? "me" : token, cancellationToken);
        if (id is not { } character) return error!;
        var pilot = await characters.GetAsync(character, cancellationToken);
        if (pilot is null) return $"Character not found: {token}.";
        var starter = catalog.StartingSet(pilot.RaceId);
        if (starter is null) return $"The starting skill set of race {pilot.RaceId} names a skill the catalog does not have.";
        if (starter.Count == 0) return $"Race {pilot.RaceId} has no starting skills.";
        var (before, rows) = await WriteAsync(character, starter, clearFirst: false, cancellationToken);
        await NotifyAsync(Online(session, character), character, before, rows, cancellationToken);
        return $"Gave the starting skills of race {pilot.RaceId} to character({character}): {rows.Count} changed.";
    }

    public async Task<string> RandomizeAsync(ClientSession session, CancellationToken cancellationToken)
    {
        if (session.CharacterId is not { } character) return "Select a character before using /randomizeskills.";
        var wanted = catalog.Skills.Where(skill => skill.Published).Select(skill =>
        {
            var level = _random.Next(0, SkillPoints.MaxLevel + 1);
            return new CharacterSkill(skill.TypeId, level, SkillPoints.At(skill.Rank, level));
        }).ToList();
        var (before, rows) = await WriteAsync(character, wanted, clearFirst: false, cancellationToken);
        await NotifyAsync(session, character, before, rows, cancellationToken);
        return $"Randomized the published skills of character({character}): {rows.Count} changed.";
    }

    public async Task<string> CopyAsync(ClientSession session, string argument, CancellationToken cancellationToken)
    {
        var operands = Operand.Matches(argument).Select(match => match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value)
            .Where(operand => operand.Length > 0).ToList();
        if (operands.Count != 2) return SkillCopyUsage;
        var (fromId, fromError) = await ResolveAsync(session, operands[0], cancellationToken);
        if (fromId is not { } from) return fromError!;
        var (toId, toError) = await ResolveAsync(session, operands[1], cancellationToken);
        if (toId is not { } to) return toError!;
        if (from == to) return "Source and target are the same character.";
        var donor = await store.ForCharacterAsync(from, cancellationToken);
        var (before, rows) = await WriteAsync(to, donor, clearFirst: true, cancellationToken);
        await NotifyAsync(Online(session, to), to, before, rows, cancellationToken);
        return $"Copied {donor.Count} skills from character({from}) to character({to}): {rows.Count} changed.";
    }

    /// <summary>The pilot's session when it is online: the caller's own session for the caller's character.</summary>
    private ClientSession? Online(ClientSession caller, long character) => character == caller.CharacterId ? caller : sessionOf(character);

    /// <summary>A character by <c>me</c>, its ID or its exact name; the message when there is none.</summary>
    private async Task<(long? Id, string? Error)> ResolveAsync(ClientSession session, string token, CancellationToken cancellationToken)
    {
        if (token.Equals("me", StringComparison.OrdinalIgnoreCase))
            return session.CharacterId is { } self ? (self, null) : (null, "Select a character before using me.");
        if (long.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0)
            return await characters.GetAsync(id, cancellationToken) is null ? (null, $"Character not found: {token}.") : (id, null);
        return await characters.FindIdByNameAsync(token, cancellationToken) is { } named
            ? (named, null)
            : (null, $"Character not found: {token}. Use me, a character ID, or an exact character name.");
    }

    /// <summary>
    /// Writes the wanted skills in one transaction. Only the rows that differ are written; with <paramref name="clearFirst"/> the skills not wanted are
    /// deleted. Returns the skills before the write and the rows the pilot must be told about (the written ones and the removed ones at level 0).
    /// </summary>
    private async Task<(IReadOnlyList<CharacterSkill> Before, IReadOnlyList<CharacterSkill> Rows)> WriteAsync(
        long character, IReadOnlyList<CharacterSkill> wanted, bool clearFirst, CancellationToken cancellationToken)
    {
        var before = await store.ForCharacterAsync(character, cancellationToken);
        var have = before.ToDictionary(skill => skill.TypeId);
        var grant = wanted.Where(skill => !have.TryGetValue(skill.TypeId, out var owned) || owned.Level != skill.Level || owned.SkillPoints != skill.SkillPoints).ToList();
        var gone = clearFirst ? before.Where(skill => wanted.All(want => want.TypeId != skill.TypeId)).ToList() : [];
        if (grant.Count == 0 && gone.All(skill => skill.Level == 0)) return (before, []);
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (gone.Count > 0) await store.DeleteAsync(connection, transaction, character, gone.Select(skill => skill.TypeId).ToList(), cancellationToken);
        await store.GrantAsync(connection, transaction, character, grant, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        var rows = grant.Concat(gone.Where(skill => skill.Level > 0).Select(skill => new CharacterSkill(skill.TypeId, 0, 0))).ToList();
        return (before, rows);
    }

    /// <summary>The client is told the skills changed, as training tells it (no message when the pilot is offline or nothing changed).</summary>
    private async Task NotifyAsync(ClientSession? online, long character, IReadOnlyList<CharacterSkill> before, IReadOnlyList<CharacterSkill> rows, CancellationToken cancellationToken)
    {
        if (online is null || rows.Count == 0) return;
        var skills = SkillWire.EntryDictionary(character, catalog, rows);
        SkillNotifications.ServerSkillsChanged(online, skills, null, Py.FileTimeFromUnixMs(nowUnixMs()));
        SkillNotifications.LevelsTrained(online, rows.Where(skill => skill.Level > 0));
        SkillNotifications.SkillsChanged(online, skills);
        if (dogma is not null) await dogma.SkillsChangedAsync(online, character, before, cancellationToken);
    }
}

using Eve.Characters;
using Eve.Services;
using Eve.Services.Skills;
using Eve.Skills;

namespace Eve.Services.Slash;

/// <summary>
/// The game master's bulk skill commands. <c>/givestarterskills [me|characterID|name]</c> sets the skills of the pilot's race to the starting set the SDE gives
/// character creation (the levels of <see cref="SkillCatalog.StartingSet"/>, the other skills are kept). <c>/randomizeskills</c> sets every published skill of the
/// pilot to a random level from 0 to 5. <c>/skillscopy "from" "to"</c> makes the second character's skills the first's, levels and points, and removes the rest.
/// A pilot who is online is told the change the way training tells it; an offline pilot reads it at the next login.
/// </summary>
public sealed class SkillBulkCommands(
    SkillCatalog catalog, PgSkillStore store, PgCharacterStore characters, CharacterTraining training, TrainingAnnouncer announcer,
    Func<long, ClientSession?> sessionOf, Random? random = null)
{
    public const string SkillCopyUsage = "Usage: /skillscopy \"<from character>\" \"<to character>\"";
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
        var update = await WriteAsync(character, pilot.AccountId, starter, clearFirst: false, cancellationToken);
        await announcer.AnnounceAdminMutationAsync(Online(session, character), update);
        return $"Gave the starting skills of race {pilot.RaceId} to character({character}): {ChangedCount(update)} changed.";
    }

    public async Task<string> RandomizeAsync(ClientSession session, CancellationToken cancellationToken)
    {
        if (session.CharacterId is not { } character) return "Select a character before using /randomizeskills.";
        var pilot = await characters.GetAsync(character, cancellationToken);
        if (pilot is null) return $"Character not found: {character}.";
        var wanted = catalog.Skills.Where(skill => skill.Published).Select(skill =>
        {
            var level = _random.Next(0, SkillPoints.MaxLevel + 1);
            return new CharacterSkill(skill.TypeId, level, SkillPoints.At(skill.Rank, level));
        }).ToList();
        var update = await WriteAsync(character, pilot.AccountId, wanted, clearFirst: false, cancellationToken);
        await announcer.AnnounceAdminMutationAsync(session, update);
        return $"Randomized the published skills of character({character}): {ChangedCount(update)} changed.";
    }

    public async Task<string> CopyAsync(ClientSession session, string argument, CancellationToken cancellationToken)
    {
        var operands = PilotTokens.Operands(argument);
        if (operands.Count != 2) return SkillCopyUsage;
        var (fromId, fromError) = await ResolveAsync(session, operands[0], cancellationToken);
        if (fromId is not { } from) return fromError!;
        var (toId, toError) = await ResolveAsync(session, operands[1], cancellationToken);
        if (toId is not { } to) return toError!;
        if (from == to) return "Source and target are the same character.";
        var pilot = await characters.GetAsync(to, cancellationToken);
        if (pilot is null) return $"Character not found: {operands[1]}.";
        var donor = await store.ForCharacterAsync(from, cancellationToken);
        var update = await WriteAsync(to, pilot.AccountId, donor, clearFirst: true, cancellationToken);
        await announcer.AnnounceAdminMutationAsync(Online(session, to), update);
        return $"Copied {donor.Count} skills from character({from}) to character({to}): {ChangedCount(update)} changed.";
    }

    /// <summary>The pilot's session when it is online: the caller's own session for the caller's character.</summary>
    private ClientSession? Online(ClientSession caller, long character) => character == caller.CharacterId ? caller : sessionOf(character);

    private Task<(long? Id, string? Error)> ResolveAsync(ClientSession session, string token, CancellationToken cancellationToken) =>
        PilotTokens.ResolveAsync(characters, session, token, cancellationToken);

    /// <summary>
    /// Writes the wanted skills and reconciles the target's queue in one transaction.
    /// </summary>
    private Task<SkillMutationUpdate> WriteAsync(long character, int account, IReadOnlyList<CharacterSkill> wanted, bool clearFirst, CancellationToken cancellationToken) =>
        training.ApplyAdminMutationAsync(character, account, wanted, [], deleteUnwanted: clearFirst, clearQueue: false, cancellationToken);

    private static int ChangedCount(SkillMutationUpdate update) => update.ChangedSkills.Count + update.RemovedSkills.Count;
}

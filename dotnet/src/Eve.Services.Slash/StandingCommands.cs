using System.Globalization;
using System.Text.RegularExpressions;
using Eve.Relations;

namespace Eve.Services.Slash;

/// <summary>An owner a standing is held toward: a corporation, a faction or an agent. The name is empty when the directory does not know it.</summary>
public sealed record StandingOwner(long Id, string Name, bool IsFaction = false, bool IsCorporation = false);

/// <summary>What the standing commands look up: the owner a token names, the agents' owners, and a character by its ID or exact name.</summary>
public interface IStandingDirectory
{
    Task<StandingOwner?> FindOwnerAsync(string token, CancellationToken cancellationToken);

    Task<IReadOnlyList<StandingOwner>> AgentOwnersAsync(CancellationToken cancellationToken);

    Task<long?> FindCharacterAsync(string token, CancellationToken cancellationToken);
}

/// <summary>
/// The game master's standing commands (EveJS <c>standings.js</c>): <c>/setstanding &lt;value&gt; &lt;owner name|id&gt; [target]</c> sets a pilot's standing toward one owner
/// (-10 to 10), and <c>/fullstandings</c> (<c>/maxagentstandings</c>) sets it to 10 toward every agent owner. The target is the pilot unless a trailing token names a
/// character. A standing is the relation row subject = owner, object = character (the mission rewards' direction, <see cref="RelationKinds.Standing"/>). The pilot is
/// told each standing that changed, as <c>OnStandingSet</c>, after the commit.
/// </summary>
public sealed class StandingCommands(IStandingDirectory directory, RelationStore relations, Action<long, long, double>? notify = null)
{
    public const string Usage = "Usage: /setstanding <value> <owner name|id> [target]";
    private const double Maximum = 10;
    private const string Reason = "GM /setstanding";

    public async Task<string> SetAsync(ClientSession session, string argument, CancellationToken cancellationToken)
    {
        var tokens = Tokens(argument);
        if (tokens.Count < 2) return Usage;
        if (!decimal.TryParse(tokens[0], NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var asked)
            || asked < -10m || asked > 10m)
            return "Standing value must be a number between -10.0 and 10.0.";
        var standing = Math.Round((double)asked, 2, MidpointRounding.AwayFromZero);

        var ownerTokens = tokens.Skip(1).ToList();
        var character = session.CharacterId;
        if (ownerTokens.Count >= 2 && await CharacterAsync(session, ownerTokens[^1], cancellationToken) is { } named)
        {
            character = named;
            ownerTokens.RemoveAt(ownerTokens.Count - 1);
        }
        if (character is not { } target) return "Select a character before using /setstanding.";
        var ownerToken = string.Join(' ', ownerTokens);
        if (await directory.FindOwnerAsync(ownerToken, cancellationToken) is not { } owner)
            return $"Could not resolve a standing owner from \"{ownerToken}\".";

        await SetOneAsync(owner, target, standing, cancellationToken);
        return $"Set standing with {Label(owner)} to {standing.ToString("F2", CultureInfo.InvariantCulture)} for character({target}).";
    }

    /// <summary>Sets the standing toward every agent owner to 10 for the named pilot (the pilot, when no name is given).</summary>
    public async Task<string> MaxAgentAsync(ClientSession session, string argument, CancellationToken cancellationToken)
    {
        var token = string.Join(' ', Tokens(argument));
        if (token.Length == 0) token = "me";
        if (await CharacterAsync(session, token, cancellationToken) is not { } target)
            return token.Equals("me", StringComparison.OrdinalIgnoreCase)
                ? "Select a character before using /maxagentstandings."
                : $"Character not found: {token}.";
        var owners = await directory.AgentOwnersAsync(cancellationToken);
        if (owners.Count == 0) return "No agent corporations or factions were available to update.";
        foreach (var owner in owners) await SetOneAsync(owner, target, Maximum, cancellationToken);
        var factions = owners.Count(owner => owner.IsFaction);
        return $"Set {owners.Count - factions} agent corporation standings and {factions} agent faction standings to 10.00 for character({target}).";
    }

    private async Task SetOneAsync(StandingOwner owner, long character, double standing, CancellationToken cancellationToken)
    {
        var kind = RelationKinds.Standing.Name;
        var current = await relations.ValueAsync(kind, owner.Id, character, 0, cancellationToken: cancellationToken);
        if (Math.Abs(current - standing) < 0.005) return;
        await relations.SetAsync(kind, owner.Id, character, standing, $"{Reason} {standing.ToString("F2", CultureInfo.InvariantCulture)}", cancellationToken);
        notify?.Invoke(character, owner.Id, standing);
    }

    /// <summary>The pilot a token names: <c>me</c> is the session's character, anything else is the directory's (an ID or an exact name).</summary>
    private async Task<long?> CharacterAsync(ClientSession session, string token, CancellationToken cancellationToken) =>
        token.Equals("me", StringComparison.OrdinalIgnoreCase) ? session.CharacterId : await directory.FindCharacterAsync(token, cancellationToken);

    private static string Label(StandingOwner owner) => owner.Name.Length == 0 ? $"owner {owner.Id}" : $"{owner.Name}({owner.Id})";

    /// <summary>The words of an argument; a quoted run (<c>"Caldari State"</c>) is one word.</summary>
    private static List<string> Tokens(string argument) =>
        [.. Regex.Matches(argument, "\"([^\"]*)\"|'([^']*)'|(\\S+)").Select(match => match.Groups.Cast<Group>().Skip(1).First(group => group.Success).Value).Where(token => token.Length > 0)];
}

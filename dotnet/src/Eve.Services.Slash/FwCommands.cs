using System.Globalization;

namespace Eve.Services.Slash;

/// <summary>
/// What a faction warfare change answers: the words of a refusal (null when it was made), the faction it concerns, and the attacker points of a system after a victory
/// point was given.
/// </summary>
public sealed record GmFwAnswer(string? Refusal, string? Faction = null, long? Points = null);

/// <summary>What the faction warfare commands need from the host: the faction a token names, and the three changes the game master may make.</summary>
public interface IFwControl
{
    /// <summary>Enlists the corporation in the faction the token names (a faction's name or its ID), now.</summary>
    Task<GmFwAnswer> EnlistAsync(long corporationId, string factionToken, CancellationToken cancellationToken);

    /// <summary>Takes the corporation out of its faction, now.</summary>
    Task<GmFwAnswer> LeaveAsync(long corporationId, CancellationToken cancellationToken);

    /// <summary>Gives victory points to the attacking side of an occupied system.</summary>
    Task<GmFwAnswer> AddVictoryPointsAsync(long solarSystemId, long points, CancellationToken cancellationToken);
}

/// <summary>
/// The game master's faction warfare commands (<c>/fw</c>): <c>enlist &lt;faction&gt;</c> and <c>leave</c> put the caller's corporation in an occupier faction, or out of
/// it, at once (the client's join waits for the downtime; a game master's does not); <c>vp &lt;points&gt;</c> gives victory points to the system the caller is in, which
/// must be occupied. The corporation is the caller's own; a refusal is the rule the answer names (not an occupier, already in a faction, not occupied).
/// </summary>
public sealed class FwCommands(IFwControl fw)
{
    public const string Usage = "Usage: /fw enlist <faction name|id> | leave | vp <points>";

    public async Task<string> RunAsync(ClientSession session, string argument, CancellationToken cancellationToken)
    {
        var words = argument.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return Usage;
        var verb = words[0].ToLowerInvariant();
        switch (verb)
        {
            case "enlist" when words.Length >= 2:
                if (session.GetInt("corpid") is not { } corporation) return "Character has no corporation to enlist.";
                var faction = string.Join(' ', words.Skip(1));
                var enlisted = await fw.EnlistAsync(corporation, faction, cancellationToken);
                return enlisted.Refusal ?? $"Corporation {corporation} is in {enlisted.Faction} now.";
            case "leave" when words.Length == 1:
                if (session.GetInt("corpid") is not { } left) return "Character has no corporation to leave a faction.";
                var withdrawn = await fw.LeaveAsync(left, cancellationToken);
                return withdrawn.Refusal ?? $"Corporation {left} left its faction now.";
            case "vp" when words.Length == 2:
                if (!long.TryParse(words[1], NumberStyles.None, CultureInfo.InvariantCulture, out var points) || points <= 0) return Usage;
                if ((session.GetInt("solarsystemid2") ?? session.GetInt("solarsystemid")) is not { } system) return "Character is in no solar system.";
                var given = await fw.AddVictoryPointsAsync(system, points, cancellationToken);
                return given.Refusal ?? $"Added {points} victory points to system {system}; its attacker points are {given.Points} now.";
            default:
                return Usage;
        }
    }
}

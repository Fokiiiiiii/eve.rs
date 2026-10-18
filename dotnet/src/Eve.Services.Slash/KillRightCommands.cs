using Eve.Characters;

namespace Eve.Services.Slash;

/// <summary>
/// <c>/killright &lt;target&gt; [owner]</c>: the owner (the caller when none is named) may take the target's kill in the owner's solar system, as the pilot an
/// illegal aggression wronged may. The grant is the crimewatch store's, and the owner is told as the aggression tells it (<c>CrimewatchRuntime.GrantKillRightAsync</c>).
/// The client's Insider sends the words as typed; a pilot is named as <see cref="PilotTokens"/> names one.
/// </summary>
public sealed class KillRightCommands(PgCharacterStore characters, Func<long, long, long, long, CancellationToken, Task<long?>> grant, Func<long> nowUnixMs)
{
    public const string Usage = "Usage: /killright <target character> [owner character]";

    public async Task<string> RunAsync(ClientSession session, string argument, CancellationToken cancellationToken)
    {
        var tokens = PilotTokens.Operands(argument);
        if (tokens.Count is < 1 or > 2) return Usage;
        var (targetId, targetError) = await PilotTokens.ResolveAsync(characters, session, tokens[0], cancellationToken);
        if (targetId is not { } target) return targetError!;
        var (ownerId, ownerError) = await PilotTokens.ResolveAsync(characters, session, tokens.Count == 2 ? tokens[1] : "me", cancellationToken);
        if (ownerId is not { } owner) return ownerError!;
        var ownerPilot = await characters.GetAsync(owner, cancellationToken);
        if (ownerPilot is null) return $"Character not found: {owner}.";
        var killRightId = await grant(owner, target, ownerPilot.SolarSystemId, nowUnixMs(), cancellationToken);
        return killRightId is { } id
            ? $"Made kill right {id}: character({owner}) may take character({target}) in solar system {ownerPilot.SolarSystemId}."
            : $"No kill right was made: character({owner}) has one open against character({target}), or the two are the same pilot.";
    }
}

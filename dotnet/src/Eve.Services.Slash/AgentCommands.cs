using System.Globalization;

namespace Eve.Services.Slash;

/// <summary>One of a character's agent missions as the game master sees it: the agent, the state, the mission and its title.</summary>
public sealed record GmAgentMission(long AgentId, string State, long MissionId, string Title);

/// <summary>
/// What a change of an agent mission answers: the mission it concerns (null when none), and the words of a refusal (null when it was made). A change that the
/// state does not allow is refused with the reason the store gives, and nothing is written.
/// </summary>
public sealed record GmAgentAnswer(GmAgentMission? Mission, string? Refusal);

/// <summary>What the agent commands need from the host: a character's agent missions, and the changes the game master may make to them.</summary>
public interface IAgentControl
{
    /// <summary>The character's missions that are offered or accepted, one per agent.</summary>
    Task<IReadOnlyList<GmAgentMission>> HeldAsync(long characterId, CancellationToken cancellationToken);

    /// <summary>Gives the character the offer of the agent, the agent's next mission (an agent that makes no offer, or an unknown agent, is refused).</summary>
    Task<GmAgentAnswer> OfferAsync(long characterId, long agentId, CancellationToken cancellationToken);

    /// <summary>Accepts the character's offer from the agent.</summary>
    Task<GmAgentAnswer> AcceptAsync(long characterId, long agentId, CancellationToken cancellationToken);

    /// <summary>Quits the character's accepted mission with the agent.</summary>
    Task<GmAgentAnswer> QuitAsync(long characterId, long agentId, CancellationToken cancellationToken);

    /// <summary>Completes the character's accepted mission with the agent, as its objectives were met (the rewards are paid).</summary>
    Task<GmAgentAnswer> CompleteAsync(long characterId, long agentId, CancellationToken cancellationToken);

    /// <summary>Resets the character's mission with the agent to none (no cooldown); the answer names the mission it held, if any.</summary>
    Task<GmAgentAnswer> ResetAsync(long characterId, long agentId, CancellationToken cancellationToken);

    /// <summary>Resets every mission the character holds with an agent; the answer is how many were reset.</summary>
    Task<int> ResetAllAsync(long characterId, CancellationToken cancellationToken);
}

/// <summary>
/// The game master's agent commands (<c>/agent</c>): <c>offers</c> lists the character's offered and accepted missions; <c>offer</c>, <c>accept</c>, <c>quit</c>,
/// <c>complete</c> and <c>reset</c> take one agent (<c>reset all</c> takes every mission the character holds). They act on the caller's own character, through the
/// same agent missions the client's agent window uses (the store's transitions and the mission runtime's hooks), so the client is told each change as it is told
/// of a change it made itself. The words of a refusal are the store's (the state the change needs) or a usage check.
/// </summary>
public sealed class AgentCommands(IAgentControl agents)
{
    public const string Usage = "Usage: /agent offers | offer <agentId> | accept <agentId> | quit <agentId> | complete <agentId> | reset <agentId|all>";

    public async Task<string> RunAsync(ClientSession session, string argument, CancellationToken cancellationToken)
    {
        if (session.CharacterId is not { } character) return "Select a character before using /agent.";
        var words = argument.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return Usage;
        var verb = words[0].ToLowerInvariant();
        if (verb == "offers")
            return words.Length == 1 ? await OffersAsync(character, cancellationToken) : Usage;
        if (words.Length != 2) return Usage;
        if (verb == "reset" && words[1].Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            var reset = await agents.ResetAllAsync(character, cancellationToken);
            return reset == 0
                ? $"No agent mission to reset for character {character}."
                : $"Reset {reset} agent mission{(reset == 1 ? "" : "s")} for character {character}.";
        }
        if (!long.TryParse(words[1], NumberStyles.None, CultureInfo.InvariantCulture, out var agent) || agent <= 0) return Usage;
        return verb switch
        {
            "offer" => Answer(await agents.OfferAsync(character, agent, cancellationToken), mission =>
                $"Offered mission {mission.MissionId} ({mission.Title}) from agent {agent} to character {character}."),
            "accept" => Answer(await agents.AcceptAsync(character, agent, cancellationToken), mission =>
                $"Accepted mission {mission.MissionId} from agent {agent} for character {character}."),
            "quit" => Answer(await agents.QuitAsync(character, agent, cancellationToken), mission =>
                $"Quit mission {mission.MissionId} with agent {agent} for character {character}."),
            "complete" => Answer(await agents.CompleteAsync(character, agent, cancellationToken), mission =>
                $"Completed mission {mission.MissionId} with agent {agent} for character {character}."),
            "reset" => Answer(await agents.ResetAsync(character, agent, cancellationToken), mission =>
                $"Reset mission {mission.MissionId} with agent {agent} for character {character}."),
            _ => Usage,
        };
    }

    private async Task<string> OffersAsync(long character, CancellationToken cancellationToken)
    {
        var held = await agents.HeldAsync(character, cancellationToken);
        if (held.Count == 0) return $"No offered or accepted agent mission for character {character}.";
        return string.Join("\n", held.Select(mission => $"Agent {mission.AgentId}: {mission.State} mission {mission.MissionId} ({mission.Title})."));
    }

    private static string Answer(GmAgentAnswer answer, Func<GmAgentMission, string> done) =>
        answer.Refusal ?? (answer.Mission is { } mission ? done(mission) : "Nothing was changed.");
}

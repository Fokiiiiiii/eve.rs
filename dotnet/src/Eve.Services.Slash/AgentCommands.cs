using System.Globalization;

namespace Eve.Services.Slash;

public sealed record GmAgentMission(long AgentId, string State, long MissionId, string Title);

/// <summary>
/// state does not allow is refused with the reason the store gives, and nothing is written.
/// </summary>
public sealed record GmAgentAnswer(GmAgentMission? Mission, string? Refusal);

public interface IAgentControl
{

    Task<IReadOnlyList<GmAgentMission>> HeldAsync(long characterId, CancellationToken cancellationToken);

    Task<GmAgentAnswer> OfferAsync(long characterId, long agentId, CancellationToken cancellationToken);

    Task<GmAgentAnswer> AcceptAsync(long characterId, long agentId, CancellationToken cancellationToken);

    Task<GmAgentAnswer> QuitAsync(long characterId, long agentId, CancellationToken cancellationToken);

    Task<GmAgentAnswer> CompleteAsync(long characterId, long agentId, CancellationToken cancellationToken);

    Task<GmAgentAnswer> ResetAsync(long characterId, long agentId, CancellationToken cancellationToken);

    Task<int> ResetAllAsync(long characterId, CancellationToken cancellationToken);
}

/// <summary>
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

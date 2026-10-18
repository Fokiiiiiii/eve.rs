using Eve.Agents;
using Eve.Services.Core;
using Eve.Services.Slash;

namespace Eve.Host;

/// <summary>
/// The game master's agent missions on this host (<see cref="IAgentControl"/>): the store's transitions (offer, accept, quit, reset) and the mission runtime's
/// hooks on accept and quit, as the client's agent window runs them; a completion is the runtime's <c>GmCompleteAsync</c> (the rewards a completion pays).
/// The offer is the composition's picker (<paramref name="pickOffer"/>), the same the agent's conversation uses.
/// </summary>
internal sealed class HostAgentControl(
    AgentMissionStore store,
    Func<long, AgentMissionAgentData?> agentOf,
    Func<AgentMissionAgentData, long, CancellationToken, ValueTask<AgentMissionContent?>> pickOffer,
    MissionDungeonRuntime runtime,
    Func<DateTimeOffset> now) : IAgentControl
{
    public async Task<IReadOnlyList<GmAgentMission>> HeldAsync(long characterId, CancellationToken cancellationToken) =>
        (await store.ReadAsync(characterId, cancellationToken))
            .Where(record => record.State is AgentMissionState.Offered or AgentMissionState.Accepted)
            .Select(Mission)
            .ToArray();

    public async Task<GmAgentAnswer> OfferAsync(long characterId, long agentId, CancellationToken cancellationToken)
    {
        if (agentOf(agentId) is not { } agent) return new GmAgentAnswer(null, $"Agent {agentId} is not known.");
        if (await pickOffer(agent, characterId, cancellationToken) is not { } content)
            return new GmAgentAnswer(null, $"Agent {agentId} has no mission to offer character {characterId}.");
        try
        {
            if (await store.OfferAsync(characterId, agentId, content, now(), Reference("offer", characterId, agentId), cancellationToken) is null)
                return new GmAgentAnswer(null, null);
        }
        catch (AgentMissionRefusedException refused)
        {
            return new GmAgentAnswer(null, refused.Message);
        }
        return new GmAgentAnswer(new GmAgentMission(agentId, AgentMissionState.Offered.ToString(), content.MissionId, content.Title), null);
    }

    public async Task<GmAgentAnswer> AcceptAsync(long characterId, long agentId, CancellationToken cancellationToken)
    {
        try
        {
            if (await store.AcceptAsync(characterId, agentId, now(), Reference("accept", characterId, agentId), cancellationToken) is null)
                return new GmAgentAnswer(null, null);
        }
        catch (AgentMissionRefusedException refused)
        {
            return new GmAgentAnswer(null, refused.Message);
        }
        await runtime.OnAcceptedAsync(characterId, agentId, cancellationToken);
        return new GmAgentAnswer(await MissionOfAsync(characterId, agentId, cancellationToken), null);
    }

    public async Task<GmAgentAnswer> QuitAsync(long characterId, long agentId, CancellationToken cancellationToken)
    {
        var held = await MissionOfAsync(characterId, agentId, cancellationToken);
        try
        {
            if (await store.QuitAsync(characterId, agentId, now(), Reference("quit", characterId, agentId), cancellationToken) is null)
                return new GmAgentAnswer(null, null);
        }
        catch (AgentMissionRefusedException refused)
        {
            return new GmAgentAnswer(null, refused.Message);
        }
        await runtime.OnQuitAsync(characterId, agentId, cancellationToken);
        return new GmAgentAnswer(held is null ? null : held with { State = AgentMissionState.Quit.ToString() }, null);
    }

    public async Task<GmAgentAnswer> CompleteAsync(long characterId, long agentId, CancellationToken cancellationToken)
    {
        var held = await MissionOfAsync(characterId, agentId, cancellationToken);
        if (held is null || held.State != AgentMissionState.Accepted.ToString() || !await runtime.GmCompleteAsync(characterId, agentId, cancellationToken))
            return new GmAgentAnswer(null, "there is no accepted mission with this agent to complete");
        return new GmAgentAnswer(held with { State = AgentMissionState.Completed.ToString() }, null);
    }

    public async Task<GmAgentAnswer> ResetAsync(long characterId, long agentId, CancellationToken cancellationToken)
    {
        var held = await MissionOfAsync(characterId, agentId, cancellationToken);
        try
        {
            if (await store.ResetAsync(characterId, agentId, now(), Reference("reset", characterId, agentId), cancellationToken) is null)
                return new GmAgentAnswer(null, null);
        }
        catch (AgentMissionRefusedException refused)
        {
            return new GmAgentAnswer(null, refused.Message);
        }
        return new GmAgentAnswer(held, null);
    }

    public async Task<int> ResetAllAsync(long characterId, CancellationToken cancellationToken)
    {
        var reset = 0;
        foreach (var record in (await store.ReadAsync(characterId, cancellationToken)).Where(record => record.State != AgentMissionState.Allocated))
        {
            if (await store.ResetAsync(characterId, record.AgentId, now(), Reference("reset-all", characterId, record.AgentId), cancellationToken) is not null)
                reset++;
        }
        return reset;
    }

    private async Task<GmAgentMission?> MissionOfAsync(long characterId, long agentId, CancellationToken cancellationToken) =>
        await store.ReadAsync(characterId, agentId, cancellationToken) is { } record ? Mission(record) : null;

    private static GmAgentMission Mission(AgentMissionRecord record) =>
        new(record.AgentId, record.State.ToString(), record.Content.MissionId, record.Content.Title);

    private static string Reference(string action, long characterId, long agentId) => $"gm-agent:{action}:{characterId}:{agentId}:{Guid.NewGuid():N}";
}

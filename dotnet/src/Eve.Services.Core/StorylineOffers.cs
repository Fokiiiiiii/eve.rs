using Eve.Agents;

namespace Eve.Services.Core;

/// <summary>
/// The storyline offer (EVE University, Storyline missions): a storyline agent (types 6 and 7) of a faction with a pending offer for a level from 1 to 5
/// offers the lowest storyline trade mission the character has not completed. The offer is marked made when the character accepts it; a declined or
/// abandoned offer keeps the pending count. The trade mission is handed in at its agent (the fetch of X13b). DEVIATIONS X15 and X16.
/// </summary>
public sealed class StorylineOffers(AgentMissionStore store, StorylineCounters counters, MissionStaticCatalog catalog, Func<long, bool> isTrade,
    IReadOnlyDictionary<long, long> factionOfCorporation)
{
    public static bool IsStorylineAgent(AgentMissionAgentData agent) =>
        agent.AgentTypeId == AgentMissionKindRules.TypeGenericStorylineMissionAgent || agent.AgentTypeId == AgentMissionKindRules.TypeStorylineMissionAgent;

    /// <summary>The storyline mission the agent offers, or null when the agent is no storyline agent, its faction has no pending offer, or no mission is left.</summary>
    public async Task<AgentMissionContent?> PickAsync(AgentMissionAgentData agent, long characterId, CancellationToken cancellationToken = default)
    {
        if (!IsStorylineAgent(agent) || await DueAsync(agent, characterId, cancellationToken) is null) return null;
        var completed = (await store.ReadCompletedAsync(characterId, cancellationToken)).Select(item => item.MissionId).ToHashSet();
        var mission = catalog.Missions
            .Where(candidate => candidate.Courier is not null && isTrade(candidate.MissionId) && !completed.Contains(candidate.MissionId)
                && catalog.MissionAvailability(candidate.MissionId).ArcId is null)
            .OrderBy(candidate => candidate.MissionId)
            .FirstOrDefault();
        if (mission is null) return null;
        return new AgentMissionContent(mission.MissionId, mission.NameId, mission.NameEn, agent.MissionKind, agent.ImportantMission);
    }

    /// <summary>When the character accepts a storyline trade offered by a storyline agent: the lowest due level of the faction is counted as offered.</summary>
    public async Task MarkAcceptedAsync(AgentMissionAgentData agent, long characterId, CancellationToken cancellationToken = default)
    {
        if (!IsStorylineAgent(agent)) return;
        var record = await store.ReadAsync(characterId, agent.AgentId, cancellationToken);
        if (record is null || record.State != AgentMissionState.Accepted || !isTrade(record.Content.MissionId)) return;
        if (await DueAsync(agent, characterId, cancellationToken) is not { } due) return;
        await counters.MarkOfferedAsync(characterId, due.FactionId, due.Level, cancellationToken);
    }

    /// <summary>The faction and the lowest level (1 to 5) whose storyline offer is pending for the agent's faction, or null.</summary>
    private async Task<(long FactionId, long Level)?> DueAsync(AgentMissionAgentData agent, long characterId, CancellationToken cancellationToken)
    {
        if (!factionOfCorporation.TryGetValue(agent.CorporationId, out var faction) || faction <= 0) return null;
        for (long level = 1; level <= 5; level++)
        {
            var (completed, offered) = await counters.ReadAsync(characterId, faction, level, cancellationToken);
            if (StorylineCounters.PendingOffers(completed, offered) > 0) return (faction, level);
        }
        return null;
    }
}

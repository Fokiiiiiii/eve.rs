using Eve.Agents;

namespace Eve.Services.Core;

/// <summary>
/// The basic kill missions of the security agents (A-X2): a basic security agent of level 4 or 5 offers the lowest kill mission of its own level
/// that the character has not completed within the replay delay. A mission qualifies when its committed level tag equals the agent's level, it
/// has a kill objective whose dungeon can start (the wave director's check), and it is no epic arc step. The composition of these dungeons is the
/// catalog's wave data (the owner's rule). The official data gives no reward for them (DEVIATIONS "Mission execution X2").
/// </summary>
public static class BasicKillOffers
{
    /// <summary>The levels of the first cut (level 3 and lower are not offered).</summary>
    public const long MinLevel = 4;

    public const long MaxLevel = 5;

    /// <summary>
    /// shortcut: the replay delay after a completion is EveJS's REPLAY_DELAY_MS (4 h, missionRuntimeState.js:26), not read from the client's
    /// replay rule; upgrade when the client's rule is read.
    /// </summary>
    public static readonly TimeSpan ReplayDelay = AgentMissionStore.DeclineReplayDelay;

    /// <summary>The mission to offer, or null when the agent has none.</summary>
    public static long? Pick(
        MissionStaticCatalog catalog,
        AgentMissionAgentData agent,
        IReadOnlyDictionary<long, DateTimeOffset> completedAt,
        DateTimeOffset now,
        Func<long, bool> canStartDungeon)
    {
        if (!catalog.IsLoaded) return null;
        if (agent.AgentTypeId != AgentMissionKindRules.TypeBasicAgent || agent.MissionKind != "encounter") return null;
        if (agent.Level is < MinLevel or > MaxLevel) return null;

        long? best = null;
        foreach (var mission in catalog.Missions)
        {
            if (mission.Kill?.DungeonId is not { } dungeonId) continue;
            if (catalog.Level(mission.MissionId).Level is not { } level || level != agent.Level) continue;
            if (catalog.MissionAvailability(mission.MissionId).ArcId is not null) continue;
            if (completedAt.TryGetValue(mission.MissionId, out var at) && at + ReplayDelay > now) continue;
            if (!canStartDungeon(dungeonId)) continue;
            best = best is null || mission.MissionId < best ? mission.MissionId : best;
        }
        return best;
    }

    /// <summary>
    /// The basic courier offer of a Distribution agent (MissionKind "courier", basic type): the lowest courier mission of the agent's own level that
    /// is not an epic arc step and that the character has not completed within the replay delay. The level comes from the committed level tags
    /// (241 of the 1155 courier records carry one, levels 1 to 4); a level the destination rule does not state gets no offer.
    /// </summary>
    public static long? PickCourier(MissionStaticCatalog catalog, AgentMissionAgentData agent, IReadOnlyDictionary<long, DateTimeOffset> completedAt, DateTimeOffset now)
    {
        if (!catalog.IsLoaded || agent.AgentTypeId != AgentMissionKindRules.TypeBasicAgent || agent.MissionKind != "courier") return null;
        if (MissionCourierRules.JumpRange(agent.Level) is null) return null;

        long? best = null;
        foreach (var mission in catalog.Missions)
        {
            if (mission.Courier is null) continue;
            if (catalog.Level(mission.MissionId).Level is not { } level || level != agent.Level) continue;
            if (catalog.MissionAvailability(mission.MissionId).ArcId is not null) continue;
            if (completedAt.TryGetValue(mission.MissionId, out var at) && at + ReplayDelay > now) continue;
            best = best is null || mission.MissionId < best ? mission.MissionId : best;
        }
        return best;
    }
}

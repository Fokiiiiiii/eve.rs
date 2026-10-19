using Eve.Agents;

namespace Eve.Services.Core;

/// <summary>




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



    /// </summary>
    public static long? PickMining(MissionStaticCatalog catalog, AgentMissionAgentData agent, IReadOnlyDictionary<long, DateTimeOffset> completedAt,
        DateTimeOffset now, Func<long, bool> canStartDungeon, Func<long, bool> isMining)
    {
        if (!catalog.IsLoaded || agent.AgentTypeId != AgentMissionKindRules.TypeBasicAgent || agent.MissionKind != "mining") return null;
        if (MissionCourierRules.JumpRange(agent.Level) is null) return null;

        long? best = null;
        foreach (var mission in catalog.Missions)
        {
            if (mission.Kill?.DungeonId is not { } dungeonId || !isMining(mission.MissionId)) continue;
            if (catalog.Level(mission.MissionId).Level is not { } level || level != agent.Level) continue;
            if (catalog.MissionAvailability(mission.MissionId).ArcId is not null) continue;
            if (completedAt.TryGetValue(mission.MissionId, out var at) && at + ReplayDelay > now) continue;
            if (!canStartDungeon(dungeonId)) continue;
            best = best is null || mission.MissionId < best ? mission.MissionId : best;
        }
        return best;
    }

    /// <summary>


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

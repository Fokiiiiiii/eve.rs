namespace Eve.Services.Core;

/// <summary>
/// The basic kill missions of the security agents (A-X2): a basic security agent of level 4 or 5 offers the lowest kill mission of its own level
/// that the character has not completed. A mission qualifies when its committed level tag equals the agent's level, it has a kill objective whose
/// dungeon can start (the wave director's check), and it is no epic arc step. The composition of these dungeons is the catalog's wave data (the
/// owner's rule). The official data gives no reward for them (DEVIATIONS "Mission execution X2").
/// </summary>
public static class BasicKillOffers
{
    /// <summary>The levels of the first cut (level 3 and lower are not offered).</summary>
    public const long MinLevel = 4;

    public const long MaxLevel = 5;

    /// <summary>The mission to offer, or null when the agent has none.</summary>
    public static long? Pick(
        MissionStaticCatalog catalog,
        AgentMissionAgentData agent,
        IReadOnlySet<long> completed,
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
            if (completed.Contains(mission.MissionId) || !canStartDungeon(dungeonId)) continue;
            best = best is null || mission.MissionId < best ? mission.MissionId : best;
        }
        return best;
    }
}

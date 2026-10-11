namespace Eve.Services.Core;

/// <summary>The mission kind and the importance of an agent, as the rule table gives them.</summary>
/// <param name="MissionKind">
/// The kind of the missions the agent gives: <c>encounter</c>, <c>courier</c>, <c>mining</c>, <c>research</c>,
/// <c>epicArc</c>, <c>heraldry</c>, or empty when the rule does not classify the agent.
/// </param>
/// <param name="ImportantMission">True for storyline and epic arc agents (the journal marks their missions as important).</param>
public sealed record AgentMissionClass(string MissionKind, bool ImportantMission);

/// <summary>
/// eve.rs's own rule for an agent's mission kind and importance. The SDE has neither field (checked: the agent object of
/// <c>npcCharacters</c> carries <c>agentTypeID</c>, <c>divisionID</c>, <c>isLocator</c> and <c>level</c> only), so the
/// server decides them here, from two official tables and nothing else:
/// <list type="bullet">
/// <item>the agent type's name in <c>agentTypes</c> (build 3396210), keyed by <c>agentTypeID</c>;</item>
/// <item>the division's name in <c>npcCorporationDivisions</c> (build 3396210), keyed by <c>divisionID</c>.</item>
/// </list>
/// The mission kind of a division is what the EVE University wiki's Agent page states (last modified 28 April 2024):
/// Security gives encounter missions, Distribution courier, Mining mining. The page gives R&amp;D only two missions, a
/// trade and a courier, so R&amp;D is not a division rule here; its research agents are classified by their agent type.
/// Agent types the wiki does not describe get no kind (empty), or the division rule where the wiki states it. The rule
/// is not copied from EveJS; where the wiki and EveJS differ, DEVIATIONS records it.
/// </summary>
public static class AgentMissionKindRules
{
    // npcCorporationDivisions (build 3396210): the names the rule keys on.
    public const long DivisionResearchAndDevelopment = 18;
    public const long DivisionDistribution = 22;
    public const long DivisionMining = 23;
    public const long DivisionSecurity = 24;

    // agentTypes (build 3396210): the names the rule keys on.
    public const long TypeNonAgent = 1;
    public const long TypeBasicAgent = 2;
    public const long TypeTutorialAgent = 3;
    public const long TypeResearchAgent = 4;
    public const long TypeConcordAgent = 5;
    public const long TypeGenericStorylineMissionAgent = 6;
    public const long TypeStorylineMissionAgent = 7;
    public const long TypeEventMissionAgent = 8;
    public const long TypeFactionalWarfareAgent = 9;
    public const long TypeEpicArcAgent = 10;
    public const long TypeAuraAgent = 11;
    public const long TypeCareerAgent = 12;
    public const long TypeHeraldryAgent = 13;

    /// <summary>The mission kind of a division's agents, by the wiki's Agent page; null for a division the rule does not name.</summary>
    public static string? DivisionMissionKind(long divisionId) => divisionId switch
    {
        DivisionSecurity => "encounter",
        DivisionDistribution => "courier",
        DivisionMining => "mining",
        _ => null,
    };

    /// <summary>
    /// The mission kind and importance of an agent with the given type and division. An agent whose type or division the
    /// rule does not name gets an empty kind: the empty kind is the honest answer, not a default.
    /// </summary>
    public static AgentMissionClass Classify(long agentTypeId, long divisionId)
    {
        var division = DivisionMissionKind(divisionId) ?? "";
        return agentTypeId switch
        {
            // Basic agents give the kind of their division's missions (wiki: "courier, mining, or encounter, depending mostly on the division").
            TypeBasicAgent => new AgentMissionClass(division, false),
            // Tutorial agents: the division rule (the wiki does not describe them; 14 agents in the SDE).
            TypeTutorialAgent => new AgentMissionClass(division, false),
            // Storyline agents give the division's kinds (wiki: courier, mining, encounter or trade); their missions are important.
            TypeGenericStorylineMissionAgent or TypeStorylineMissionAgent => new AgentMissionClass(division, true),
            // Epic arc agents: the arc's own steps decide the kinds, which the official data does not state; important.
            TypeEpicArcAgent => new AgentMissionClass("epicArc", true),
            // Research agents: research missions.
            TypeResearchAgent => new AgentMissionClass("research", false),
            // Factional warfare agents give encounter missions only (wiki).
            TypeFactionalWarfareAgent => new AgentMissionClass("encounter", false),
            // Heraldry agents: a kind of their own; no other kind is stated for them.
            TypeHeraldryAgent => new AgentMissionClass("heraldry", false),
            // Event, aura, CONCORD and career agents, and non-agents: the wiki does not describe their kinds; none is assumed.
            _ => new AgentMissionClass("", false),
        };
    }
}

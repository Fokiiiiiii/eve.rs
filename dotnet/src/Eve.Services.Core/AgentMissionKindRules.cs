namespace Eve.Services.Core;


/// <param name="MissionKind">


/// </param>
/// <param name="ImportantMission">True for storyline and epic arc agents (the journal marks their missions as important).</param>
public sealed record AgentMissionClass(string MissionKind, bool ImportantMission);

/// <summary>

/// <c>npcCharacters</c> carries <c>agentTypeID</c>, <c>divisionID</c>, <c>isLocator</c> and <c>level</c> only), so the
/// server decides them here, from two official tables and nothing else:
/// <list type="bullet">

/// <item>the division's name in <c>npcCorporationDivisions</c> (build 3396210), keyed by <c>divisionID</c>.</item>
/// </list>

/// Security gives encounter missions, Distribution courier, Mining mining. The page gives R&amp;D only two missions, a



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


    public static string? DivisionMissionKind(long divisionId) => divisionId switch
    {
        DivisionSecurity => "encounter",
        DivisionDistribution => "courier",
        DivisionMining => "mining",
        _ => null,
    };

    /// <summary>

    /// rule does not name gets an empty kind: the empty kind is the honest answer, not a default.
    /// </summary>
    public static AgentMissionClass Classify(long agentTypeId, long divisionId)
    {
        var division = DivisionMissionKind(divisionId) ?? "";
        return agentTypeId switch
        {

            TypeBasicAgent => new AgentMissionClass(division, false),

            TypeTutorialAgent => new AgentMissionClass(division, false),

            TypeGenericStorylineMissionAgent or TypeStorylineMissionAgent => new AgentMissionClass(division, true),
            // Epic arc agents: the arc's own steps decide the kinds, which the official data does not state; important.
            TypeEpicArcAgent => new AgentMissionClass("epicArc", true),
            // Research agents: research missions.
            TypeResearchAgent => new AgentMissionClass("research", false),

            TypeFactionalWarfareAgent => new AgentMissionClass("encounter", false),
            // Heraldry agents: a kind of their own; no other kind is stated for them.
            TypeHeraldryAgent => new AgentMissionClass("heraldry", false),

            _ => new AgentMissionClass("", false),
        };
    }
}

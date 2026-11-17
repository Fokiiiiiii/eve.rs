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

    /// <summary>
    /// The V24 journal sends this value to localization.GetByLabel. SDE mission kinds are
    /// concise internal values, so translate them at the wire boundary.
    /// </summary>
    public static string ClientMissionTypeLabel(long agentTypeId, string missionKind, string? configuredLabel = null)
    {
        var configured = configuredLabel?.Trim() ?? "";
        if (configured.StartsWith("UI/Agents/MissionTypes/", StringComparison.Ordinal)) return configured;

        var kind = MissionKindOf(configured.Length > 0 ? configured : missionKind);
        if (agentTypeId is TypeGenericStorylineMissionAgent or TypeStorylineMissionAgent)
            return kind switch
            {
                "courier" => "UI/Agents/MissionTypes/StorylineCourier",
                "mining" => "UI/Agents/MissionTypes/StorylineMining",
                "trade" => "UI/Agents/MissionTypes/StorylineTrade",
                "encounter" => "UI/Agents/MissionTypes/StorylineEncounter",
                _ => "UI/Agents/MissionTypes/Storyline",
            };

        if (agentTypeId == TypeEpicArcAgent)
            return kind switch
            {
                "courier" => "UI/Agents/MissionTypes/EpicArcCourier",
                "mining" => "UI/Agents/MissionTypes/EpicArcMining",
                "trade" => "UI/Agents/MissionTypes/EpicArcTrade",
                "encounter" => "UI/Agents/MissionTypes/EpicArcEncounter",
                _ => "UI/Agents/MissionTypes/EpicArc",
            };

        if (agentTypeId == TypeResearchAgent)
            return kind switch
            {
                "courier" => "UI/Agents/MissionTypes/ResearchCourier",
                "trade" => "UI/Agents/MissionTypes/ResearchTrade",
                "encounter" => "UI/Agents/MissionTypes/ResearchEncounter",
                _ => "UI/Agents/MissionTypes/Research",
            };

        if (agentTypeId == TypeHeraldryAgent || kind == "heraldry") return "UI/Agents/MissionTypes/Heraldry";

        return kind switch
        {
            "courier" => "UI/Agents/MissionTypes/Courier",
            "mining" => "UI/Agents/MissionTypes/Mining",
            "trade" => "UI/Agents/MissionTypes/Trade",
            "encounter" => "UI/Agents/MissionTypes/Encounter",
            "epicArc" => "UI/Agents/MissionTypes/EpicArc",
            "storyline" => "UI/Agents/MissionTypes/Storyline",
            "research" => "UI/Agents/MissionTypes/Research",
            _ => configured,
        };
    }

    public static string MissionKindOf(string missionKindOrLabel)
    {
        var value = missionKindOrLabel.Trim();
        const string standardMissionPrefix = "UI/Agents/StandardMission/";
        if (value.StartsWith(standardMissionPrefix, StringComparison.Ordinal))
            value = value[standardMissionPrefix.Length..];

        var normalized = value.ToLowerInvariant();
        if (normalized is "courier" or "distribution" || normalized.EndsWith("courier", StringComparison.Ordinal)) return "courier";
        if (normalized is "mining" || normalized.EndsWith("mining", StringComparison.Ordinal)) return "mining";
        if (normalized is "encounter" or "security" || normalized.EndsWith("encounter", StringComparison.Ordinal)) return "encounter";
        if (normalized is "trade" || normalized.EndsWith("trade", StringComparison.Ordinal)) return "trade";
        if (normalized is "research" || normalized.EndsWith("research", StringComparison.Ordinal)) return "research";
        if (normalized is "epicArc" or "epicarc" || normalized.StartsWith("epicarc", StringComparison.Ordinal)) return "epicArc";
        if (normalized is "storyline" || normalized.StartsWith("storyline", StringComparison.Ordinal)) return "storyline";
        if (normalized is "heraldry") return "heraldry";
        return normalized;
    }


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

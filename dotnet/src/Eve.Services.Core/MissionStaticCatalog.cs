using System.Text.Json;
using System.Text.Json.Nodes;
using Eve.Sde;

namespace Eve.Services.Core;

/// <summary>The bounty-free reward terms of a mission record (CCP <c>missions</c>.<c>missionRewards</c>).</summary>
public sealed record MissionStaticReward(
    long? RewardTypeId,
    long RewardQuantity,
    long? BonusRewardTypeId,
    long BonusRewardQuantity,
    long? BonusIntervalSeconds);

/// <summary>A kill mission's objective (CCP <c>missions</c>.<c>killMission</c>). The dungeon is a client dungeon ID, not a row of <c>dungeons</c>.
/// <c>DropItemTypeId</c> is the type of an item the mission container drops (<c>dropItemInMissionContainer</c>), when there is one.</summary>
public sealed record MissionStaticKill(long? DungeonId, long? ObjectiveTypeId, long ObjectiveQuantity, long? DropItemTypeId);

/// <summary>A courier mission's objective (CCP <c>missions</c>.<c>courierMission</c>).</summary>
public sealed record MissionStaticCourier(long? ObjectiveTypeId, long ObjectiveQuantity, bool Singleton);

/// <summary>One record of CCP's <c>missions</c> table, as the server reads it. Fields the table does not carry are absent, never guessed.</summary>
public sealed record MissionStatic(
    long MissionId,
    string NameEn,
    bool HasStandingRewards,
    long? ExpirationMinutes,
    long? FactionId,
    long? CorporationId,
    long? AgentTypeId,
    MissionStaticKill? Kill,
    MissionStaticCourier? Courier,
    MissionStaticReward? Reward,
    long? InitialAgentGiftTypeId,
    long InitialAgentGiftQuantity,
    IReadOnlyList<(long Id, double Value)> ExtraStandings,
    IReadOnlyDictionary<string, string> EnglishMessages,
    long NameId);

/// <summary>The dungeon answer of a kill mission: its official dungeon ID, and the content (never available here) with its reason.</summary>
public sealed record MissionDungeonAnswer(long MissionId, long? DungeonId, bool OfficialContent, string Reason);

/// <summary>The availability of a mission: its epic arc (when it is a step of one), that arc's availability, or unknown with the reason.</summary>
public sealed record MissionAvailabilityAnswer(long MissionId, long? ArcId, string Availability, string? Reason);

/// <summary>One step of an epic arc: the mission, the agent who gives it and the steps that follow it.</summary>
public sealed record EpicArcStep(long MissionId, long AgentId, IReadOnlyList<long> NextMissionIds, long? FailMissionId);

/// <summary>
/// One record of CCP's <c>epicArcs</c> table. <c>arcRestartInterval</c> is in minutes (129600 is the wiki's ninety days).
/// </summary>
public sealed record EpicArcStatic(long ArcId, string NameEn, long RestartIntervalMinutes, long? FactionId, long? IconId, IReadOnlyList<EpicArcStep> Steps);

/// <summary>
/// The official mission data (CCP SDE build 3405148, schema <c>sde_mission</c>): the mission records and the epic arcs.
/// Every question the build cannot answer is answered with a named reason (see <see cref="MissionDataGaps"/>), and the
/// startup log prints them, so an empty answer is never silent. Nothing here is read from the EveJS authority tables.
/// </summary>
public sealed class MissionStaticCatalog
{
    /// <summary>The schema <c>eve-sde ensure --missions</c> loads the build into (<c>eve_sde::ensure::MISSION_SCHEMA</c>).</summary>
    public const string DefaultSchema = "sde_mission";

    private static readonly IReadOnlyList<long> NoIds = Array.Empty<long>();

    private readonly Dictionary<long, MissionStatic> _missions;
    private readonly IReadOnlyList<EpicArcStatic> _arcs;
    private readonly Dictionary<long, List<(long ArcId, EpicArcStep Step)>> _stepsByAgent;
    private readonly Dictionary<long, long> _arcByMission;
    private readonly Dictionary<long, EpicArcStep> _stepByMission = new();
    private readonly EpicArcAvailability _availability;
    private readonly bool _includeUnavailableArcs;
    private readonly IReadOnlyDictionary<long, string> _siteStatus;
    private readonly bool _includeGenericArcs;

    private readonly MissionLevelTags _levels;

    private MissionStaticCatalog(
        long? build,
        string? unavailableReason,
        IEnumerable<MissionStatic> missions,
        IEnumerable<EpicArcStatic> arcs,
        MissionLevelTags levels,
        EpicArcAvailability availability,
        bool includeUnavailableArcs,
        IReadOnlyDictionary<long, string> siteStatus,
        bool includeGenericArcs)
    {
        Build = build;
        UnavailableReason = unavailableReason;
        _levels = levels;
        _availability = availability;
        _includeUnavailableArcs = includeUnavailableArcs;
        _siteStatus = siteStatus;
        _includeGenericArcs = includeGenericArcs;
        _missions = missions.ToDictionary(mission => mission.MissionId);
        _arcs = arcs.OrderBy(arc => arc.ArcId).ToArray();
        _stepsByAgent = new Dictionary<long, List<(long ArcId, EpicArcStep Step)>>();
        _arcByMission = new Dictionary<long, long>();
        foreach (var arc in _arcs)
        {
            foreach (var step in arc.Steps)
            {
                if (!_stepsByAgent.TryGetValue(step.AgentId, out var steps))
                    _stepsByAgent[step.AgentId] = steps = [];
                steps.Add((arc.ArcId, step));
                _arcByMission.TryAdd(step.MissionId, arc.ArcId);
                _stepByMission.TryAdd(step.MissionId, step);
            }
        }
    }

    /// <summary>The loaded build number; null when no mission build is loaded.</summary>
    public long? Build { get; }

    /// <summary>Why the catalog holds nothing, when no mission build is loaded; null otherwise.</summary>
    public string? UnavailableReason { get; }

    public bool IsLoaded => Build is not null;

    public IReadOnlyCollection<MissionStatic> Missions => _missions.Values;

    public IReadOnlyList<EpicArcStatic> EpicArcs => _arcs;

    public MissionStatic? Mission(long missionId) => _missions.GetValueOrDefault(missionId);

    public EpicArcStatic? EpicArc(long arcId) => _arcs.FirstOrDefault(arc => arc.ArcId == arcId);

    /// <summary>
    /// The missions an agent gives as steps of an epic arc, ascending. The official build links an agent to a mission
    /// only through <c>epicArcs</c>, so this is the whole of what can be answered for an agent. Empty for every other
    /// agent (see <see cref="MissionDataGaps.BasicPools"/>) and for every agent when no build is loaded.
    /// </summary>
    public IReadOnlyList<long> PlausibleMissionIds(long agentId) =>
        !IsLoaded || !_stepsByAgent.TryGetValue(agentId, out var steps)
            ? NoIds
            : steps.Where(step => IsOfferable(step.ArcId)).Select(step => step.Step.MissionId).Distinct().Order().ToArray();

    /// <summary>Whether the mission is a step of an epic arc (the official build's <c>epicArcs</c>).</summary>
    public bool IsEpicArcStep(long missionId) => _stepByMission.ContainsKey(missionId);

    /// <summary>
    /// A talk step: an epic arc step with neither a kill nor a courier objective, completed by talking to the agent of the step that follows it.
    /// </summary>
    public bool IsTalkStep(long missionId) =>
        _stepByMission.ContainsKey(missionId) && Mission(missionId) is { Kill: null, Courier: null };

    /// <summary>The agents of the steps that follow an arc step (the agent a talk step is completed with).</summary>
    public IReadOnlyList<long> NextAgentsOf(long missionId) =>
        _stepByMission.TryGetValue(missionId, out var step)
            ? step.NextMissionIds.Where(_stepByMission.ContainsKey).Select(next => _stepByMission[next].AgentId).Distinct().ToArray()
            : NoIds;

    /// <summary>
    /// The mission IDs the server disables. eve.rs disables none; see <see cref="MissionDataGaps.DisabledMissions"/>.
    /// </summary>
    public IReadOnlyList<long> DisabledMissionIds => NoIds;

    /// <summary>
    /// The journal text IDs of the epic arc missions, by message type. The build has no such IDs (see
    /// <see cref="MissionDataGaps.EpicJournalText"/>), so each type maps to an empty object: the client finds no
    /// text for a mission and shows none, instead of an invented ID.
    /// </summary>
    public JsonObject EpicArcMessageMaps
    {
        get
        {
            var maps = new JsonObject();
            foreach (var messageType in MissionDataGaps.EpicJournalMessageTypes)
                maps[messageType] = new JsonObject();
            return maps;
        }
    }

    /// <summary>The level tags this catalog answers from.</summary>
    public MissionLevelTags LevelTags => _levels;

    /// <summary>The gaps this catalog reports at startup: one line each, the area and the reason.</summary>
    public IReadOnlyList<(string Area, string Reason)> Gaps() => MissionDataGaps.All;

    /// <summary>
    /// Reads the mission build from <paramref name="reader"/> (schema <see cref="DefaultSchema"/>). Loads nothing when the
    /// build is not there. The level tags are <paramref name="levels"/>, or the committed tags plus the owner's file.
    /// </summary>
    public static async Task<MissionStaticCatalog> LoadAsync(SdeReader reader, MissionLevelTags? levels = null, CancellationToken cancellationToken = default)
    {
        var build = await reader.BuildAsync(cancellationToken);
        if (build is null)
        {
            return Unavailable(
                "no mission build is loaded in schema " + DefaultSchema +
                ": run `cargo run -p eve-sde -- ensure --missions --pg $EVE_PG_URL`",
                levels ?? MissionLevelTags.LoadDefault());
        }
        var missionRows = await reader.ReadAsync("missions", cancellationToken: cancellationToken);
        var arcRows = await reader.ReadAsync("epicArcs", cancellationToken: cancellationToken);
        return FromRows(build, missionRows, arcRows, levels ?? MissionLevelTags.LoadDefault(), EpicArcAvailability.Committed, IncludeUnavailableFromEnvironment(),
            MissionSiteStatus.Committed, IncludeGenericFromEnvironment());
    }

    /// <summary>A catalog that holds no mission and says why. Its level tags are still answered.</summary>
    public static MissionStaticCatalog Unavailable(string reason, MissionLevelTags? levels = null) =>
        new(null, reason, [], [], levels ?? MissionLevelTags.Committed, EpicArcAvailability.Committed, IncludeUnavailableFromEnvironment(),
            new Dictionary<long, string>(), IncludeGenericFromEnvironment());

    /// <summary>The catalog of already-read rows of the two tables (the tests build their fixtures this way).</summary>
    public static MissionStaticCatalog FromRows(
        long? build,
        IReadOnlyList<SdeRow> missions,
        IReadOnlyList<SdeRow> arcs,
        MissionLevelTags? levels = null,
        EpicArcAvailability? availability = null,
        bool includeUnavailableArcs = false,
        IReadOnlyDictionary<long, string>? siteStatus = null,
        bool includeGenericArcs = false) =>
        new(
            build,
            null,
            missions.Select(row => ParseMission(row.Key, row.Doc)),
            arcs.Select(row => ParseArc(row.Key, row.Doc)),
            levels ?? MissionLevelTags.Committed,
            availability ?? EpicArcAvailability.Committed,
            includeUnavailableArcs,
            siteStatus ?? new Dictionary<long, string>(),
            includeGenericArcs);

    /// <summary>
    /// The switch that offers the epic arc steps whose kill sites are generic (a family rule, not client data). Off by default
    /// (owner decision: held back until the content is complete). It is read from <c>EVE_MISSION_INCLUDE_GENERIC_ARCS</c> (<c>1</c> or <c>true</c>).
    /// A step whose site is missing is never offered.
    /// </summary>
    public const string IncludeGenericVariable = "EVE_MISSION_INCLUDE_GENERIC_ARCS";

    public static bool IncludeGenericFromEnvironment() =>
        Environment.GetEnvironmentVariable(IncludeGenericVariable) is "1" or "true";

    /// <summary>True when the switch offers the arc steps with generic sites.</summary>
    public bool IncludesGenericArcs => _includeGenericArcs;

    /// <summary>The site status of a step with a kill objective (<see cref="MissionSiteStatus"/>), or null when the step has none recorded.</summary>
    public string? SiteStatusOf(long missionId) => _siteStatus.GetValueOrDefault(missionId);

    /// <summary>
    /// Whether an arc step is held back from offers: a missing site always; a generic site unless the switch is on.
    /// A step with no recorded site status is not held back.
    /// </summary>
    public bool IsHeldBack(long missionId) => SiteStatusOf(missionId) switch
    {
        MissionSiteStatus.Missing => true,
        MissionSiteStatus.Generic => !_includeGenericArcs && !(_arcByMission.TryGetValue(missionId, out var arcId) && ArcsOnByDefault.Contains(arcId)),
        _ => false,
    };

    /// <summary>
    /// The epic arcs whose generic kill sites are offered by default: the arcs whose whole offer path was walked live with the client's test ship
    /// (tools/headless-client/tests/test_arc29_walk.py, DEVIATIONS "Mission execution X8"). Every other arc stays held back unless the
    /// switch <see cref="IncludeGenericVariable"/> is set. Owner decision: an arc is added here only after its walk passes.
    /// </summary>
    public static readonly IReadOnlySet<long> ArcsOnByDefault = new HashSet<long>
    {
        29, 40, 48, 52, 53, 55, 56, 128,
        64, 67, 68, 69, 70, 71, 72, 73, 74, 75, 76, 77,
    };

    /// <summary>
    /// The switch that offers the arcs not known to be in the client build. Off by default: only <c>in-client-build</c> arcs
    /// are offered. It is read from the environment variable <c>EVE_MISSION_INCLUDE_UNAVAILABLE_ARCS</c> (<c>1</c> or <c>true</c>).
    /// </summary>
    public const string IncludeUnavailableVariable = "EVE_MISSION_INCLUDE_UNAVAILABLE_ARCS";

    public static bool IncludeUnavailableFromEnvironment() =>
        Environment.GetEnvironmentVariable(IncludeUnavailableVariable) is "1" or "true";

    /// <summary>The availability table this catalog answers from.</summary>
    public EpicArcAvailability Availability => _availability;

    /// <summary>True when the switch offers arcs that are not known to be in the client build.</summary>
    public bool IncludesUnavailableArcs => _includeUnavailableArcs;

    /// <summary>The availability of an epic arc: <c>in-client-build</c>, <c>newer-than-client</c> or <c>unknown</c>.</summary>
    public string ArcAvailability(long arcId) => _availability.Availability(arcId);

    /// <summary>Whether the server offers an arc to the client: in-client-build, or any arc when the switch is on.</summary>
    public bool IsOfferable(long arcId) => ArcAvailability(arcId) == EpicArcAvailability.InClientBuild || _includeUnavailableArcs;

    /// <summary>The arcs the server offers by default (the in-client-build arcs), or all arcs when the switch is on.</summary>
    public IReadOnlyList<EpicArcStatic> OfferableEpicArcs => _arcs.Where(arc => IsOfferable(arc.ArcId)).ToArray();

    /// <summary>
    /// The availability of a mission. A mission that is a step of an epic arc takes its arc's availability. A mission that is
    /// not a step of an epic arc has no evidence in this table and is <c>unknown</c>, with the reason.
    /// </summary>
    public MissionAvailabilityAnswer MissionAvailability(long missionId)
    {
        if (_arcByMission.TryGetValue(missionId, out var arcId))
            return new MissionAvailabilityAnswer(missionId, arcId, ArcAvailability(arcId), null);
        return new MissionAvailabilityAnswer(
            missionId,
            null,
            EpicArcAvailability.Unknown,
            $"mission {missionId} is not a step of an epic arc, so this table has no evidence for it; its availability is not answered");
    }

    /// <summary>
    /// The level of a mission with its provenance: <c>rule</c>, <c>wiki</c> or <c>owner</c> when it has a tag, and
    /// <c>unknown</c> with the reason when it has none. The official build holds no level (see <see cref="MissionDataGaps.Levels"/>).
    /// </summary>
    public MissionLevelAnswer Level(long missionId) => _levels.Answer(missionId);

    /// <summary>
    /// The dungeon content of a kill mission. The official <c>dungeonID</c> is kept as the reference; its content is not
    /// available from official data, and the answer says why. No mapping to another dungeon is made.
    /// </summary>
    public MissionDungeonAnswer KillDungeon(long missionId)
    {
        var mission = Mission(missionId);
        if (mission is null)
            return new MissionDungeonAnswer(missionId, null, false, $"mission {missionId} is not in the official build");
        if (mission.Kill is null)
            return new MissionDungeonAnswer(missionId, null, false, $"mission {missionId} has no kill objective, so it names no dungeon");
        if (mission.Kill.DungeonId is not { } dungeonId)
            return new MissionDungeonAnswer(missionId, null, false, $"mission {missionId}'s kill objective names no dungeon");
        return new MissionDungeonAnswer(missionId, dungeonId, false, MissionDataGaps.DungeonContent(dungeonId));
    }

    private static MissionStatic ParseMission(long missionId, JsonElement doc)
    {
        var kill = Object(doc, "killMission") is { } killDoc
            ? new MissionStaticKill(
                Optional(killDoc, "dungeonID"),
                Optional(killDoc, "objectiveTypeID"),
                Integer(killDoc, "objectiveQuantity"),
                Optional(killDoc, "dropItemInMissionContainer"))
            : null;
        var courier = Object(doc, "courierMission") is { } courierDoc
            ? new MissionStaticCourier(
                Optional(courierDoc, "objectiveTypeID"),
                Integer(courierDoc, "objectiveQuantity"),
                Boolean(courierDoc, "objectiveSingleton"))
            : null;
        var reward = Object(doc, "missionRewards") is { } rewardDoc
            ? new MissionStaticReward(
                Optional(Object(rewardDoc, "reward"), "rewardTypeID"),
                Integer(Object(rewardDoc, "reward"), "rewardQuantity"),
                Optional(Object(rewardDoc, "bonusReward"), "rewardTypeID"),
                Integer(Object(rewardDoc, "bonusReward"), "rewardQuantity"),
                Optional(rewardDoc, "bonusTimeInterval"))
            : null;
        var extra = doc.TryGetProperty("extraStandings", out var extraDoc) && extraDoc.ValueKind == JsonValueKind.Array
            ? extraDoc.EnumerateArray()
                .Select(item => (Id: Integer(item, "_key"), Value: Number(item, "_value")))
                .Where(item => item.Id > 0)
                .ToArray()
            : Array.Empty<(long Id, double Value)>();
        var messages = new Dictionary<string, string>();
        if (doc.TryGetProperty("messages", out var messageDoc) && messageDoc.ValueKind == JsonValueKind.Array)
        {
            // Each message is {"_key": "messages.mission.briefing", "de": ..., "en": ...}; only the English text is kept.
            foreach (var item in messageDoc.EnumerateArray())
            {
                if (item.TryGetProperty("_key", out var key) && key.ValueKind == JsonValueKind.String &&
                    item.TryGetProperty("en", out var en) && en.ValueKind == JsonValueKind.String)
                    messages[key.GetString()!] = en.GetString()!;
            }
        }
        return new MissionStatic(
            missionId,
            SdeReader.English(doc.TryGetProperty("name", out var name) ? name : default),
            Boolean(doc, "hasStandingRewards"),
            Optional(doc, "expirationTime"),
            Optional(doc, "factionID"),
            Optional(doc, "corporationID"),
            Optional(doc, "agentTypeID"),
            kill,
            courier,
            reward,
            Optional(doc, "initialAgentGiftTypeID"),
            Integer(doc, "initialAgentGiftQuantity"),
            extra,
            messages.AsReadOnly(),
            Integer(doc, "nameID"));
    }

    private static EpicArcStatic ParseArc(long arcId, JsonElement doc)
    {
        var steps = doc.TryGetProperty("missions", out var stepDoc) && stepDoc.ValueKind == JsonValueKind.Array
            ? stepDoc.EnumerateArray()
                .Select(step => new EpicArcStep(
                    Integer(step, "_key"),
                    Integer(step, "agentID"),
                    step.TryGetProperty("nextMissions", out var next) && next.ValueKind == JsonValueKind.Array
                        ? next.EnumerateArray().Select(id => id.GetInt64()).ToArray()
                        : Array.Empty<long>(),
                    Optional(step, "failMissionID")))
                .ToArray()
            : Array.Empty<EpicArcStep>();
        return new EpicArcStatic(
            arcId,
            SdeReader.English(doc.TryGetProperty("name", out var name) ? name : default),
            Integer(doc, "arcRestartInterval"),
            Optional(doc, "factionID"),
            Optional(doc, "iconID"),
            steps);
    }

    private static JsonElement? Object(JsonElement doc, string name) =>
        doc.ValueKind == JsonValueKind.Object && doc.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : null;

    private static long? Optional(JsonElement? doc, string name) =>
        doc is { } element && element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? number
            : null;

    private static long? Optional(JsonElement doc, string name) => Optional((JsonElement?)doc, name);

    private static long Integer(JsonElement doc, string name) => Optional(doc, name) ?? 0;

    private static long Integer(JsonElement? doc, string name) => Optional(doc, name) ?? 0;

    private static bool Boolean(JsonElement doc, string name) =>
        doc.ValueKind == JsonValueKind.Object && doc.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static double Number(JsonElement doc, string name) =>
        doc.ValueKind == JsonValueKind.Object && doc.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
            ? number
            : 0;
}

/// <summary>
/// What the official data cannot answer, with the reason. Each entry is printed at startup. The server's answers that
/// depend on these are empty or absent, and never invented.
/// </summary>
public static class MissionDataGaps
{
    public const string Levels =
        "The official build has no mission level. The level tags are the repository owner's: 1,429 basic missions have a tag (client 1,019, tq-log 13, wiki 346, rule 51), committed in Data/mission-level-tags.v1.json under the owner's approval for four fields only. Other missions have no level: they are answered unknown, never defaulted.";

    public const string BasicPools =
        "The official build links no basic, storyline, research, factional-warfare, heraldry, career, tutorial or event agent to a mission. Its only agent-to-mission table is epicArcs, so these agents answer no plausible missions.";

    public const string EpicJournalText =
        "The official build has no journal text IDs for epic arcs (the reference's messages.epicMission.journalText.* keys are not in it). The client shows no chapter titles.";

    public const string Standings =
        "The official build has no LP reward per mission and no LP store standing requirement. The LP store offers come from the loyalty-store source (the agents track), not from the mission data.";

    public const string DisabledMissions =
        "eve.rs disables no mission. The reference keeps one disabled entry (mission 4743, a template of its own content policy); that policy is not an official fact and is not carried.";

    public const string KillDungeonContent =
        "A kill mission's dungeonID refers to a client dungeon template. Only 3 of the 1,460 distinct IDs are rows of the SDE dungeons table, so the rooms and spawns of a mission dungeon are client-only and no official source gives them.";

    public static readonly string[] EpicJournalMessageTypes =
    [
        "messages.epicMission.journalText.chapterTitle",
        "messages.epicMission.journalText.inProgressMessage",
        "messages.epicMission.journalText.completedMessage",
    ];

    public static IReadOnlyList<(string Area, string Reason)> All { get; } =
    [
        ("levels", Levels),
        ("basic pools", BasicPools),
        ("epic arc journal text", EpicJournalText),
        ("LP and standings", Standings),
        ("disabled missions", DisabledMissions),
        ("kill mission dungeon content", KillDungeonContent),
    ];

    /// <summary>The reason a mission's dungeon content is not answered, for a dungeon ID the SDE does not hold as a row.</summary>
    public static string DungeonContent(long dungeonId) =>
        $"mission dungeon {dungeonId} is not available from official data (its ID is a client dungeon ID; it is not a row of the SDE dungeons table): " + KillDungeonContent;

    /// <summary>The reason a mission has no level tag.</summary>
    public static string LevelUnknown(long missionId) =>
        $"mission {missionId} has no level tag: the official build has no level, and no committed or owner rule or wiki tag exists for it";
}

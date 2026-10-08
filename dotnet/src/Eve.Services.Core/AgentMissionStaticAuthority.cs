using System.Globalization;
using System.Text.Json.Nodes;

namespace Eve.Services.Core;

/// <summary>Projects the same checked-in EveJS static authority JSON tables used by agentMgr.</summary>
public static class AgentMissionStaticAuthority
{
    public static readonly string[] EpicArcMessageTypes =
    [
        "messages.epicMission.journalText.chapterTitle",
        "messages.epicMission.journalText.inProgressMessage",
        "messages.epicMission.journalText.completedMessage",
    ];

    public static IReadOnlyList<long> DisabledMissionIds(JsonObject? productionMissionPolicy)
    {
        if (productionMissionPolicy?["disabledMissions"] is not JsonArray disabled)
            return Array.Empty<long>();
        return disabled.OfType<JsonObject>()
            .Select(record => Positive(record["missionID"]))
            .Where(id => id > 0)
            .Distinct()
            .ToArray();
    }

    public static IReadOnlyList<long> PlausibleMissionIds(
        JsonObject? agentAuthority,
        JsonObject? missionAuthority,
        long agentId)
    {
        if (agentId <= 0)
            return Array.Empty<long>();
        var key = agentId.ToString(CultureInfo.InvariantCulture);
        var agent = (agentAuthority?["agentsByID"] as JsonObject)?[key] as JsonObject;
        if (agent is null)
            return Array.Empty<long>();

        var indexes = missionAuthority?["indexes"] as JsonObject;
        var explicitIds = (indexes?["agentIDToMissionIDs"] as JsonObject)?[key] as JsonArray;
        var agentSpecific = explicitIds is { Count: > 0 };
        var route = ResolveRoute(agent, agentSpecific);
        var ids = agentSpecific
            ? explicitIds!.Select(Positive).Where(id => id > 0)
            : TemplateBoundIds(agent, indexes, missionAuthority);

        var missionRecords = missionAuthority?["missionsByID"] as JsonObject;
        return ids
            .Distinct()
            .Where(id => IsClientMissionAvailable(missionRecords, id))
            .Where(id => missionRecords is not null &&
                         HasValidMissionContent(missionRecords[id.ToString(CultureInfo.InvariantCulture)] as JsonObject, route))
            .Where(id => agentSpecific ||
                         ConstraintsMatch(agent, missionRecords![id.ToString(CultureInfo.InvariantCulture)] as JsonObject) &&
                         MissionMatchesAgent(agent, missionRecords[id.ToString(CultureInfo.InvariantCulture)] as JsonObject, agentId, route) &&
                         LevelMatches(agent, missionRecords[id.ToString(CultureInfo.InvariantCulture)] as JsonObject, route))
            .ToArray();
    }

    public static JsonObject EpicArcMessageMaps(JsonObject? missionAuthority)
    {
        var maps = new JsonObject();
        foreach (var messageType in EpicArcMessageTypes)
            maps[messageType] = new JsonObject();

        if (missionAuthority?["missionsByID"] is not JsonObject missions)
            return maps;

        foreach (var (key, node) in missions)
        {
            if (node is not JsonObject mission ||
                !IsTrue(mission["isEpicArc"]) &&
                !string.Equals(Text(mission["missionFlavor"]), "epicArc", StringComparison.OrdinalIgnoreCase))
                continue;

            var missionId = Positive(mission["missionID"]);
            if (missionId <= 0 && !long.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out missionId))
                continue;
            if (missionId <= 0 || mission["messages"] is not JsonObject messages)
                continue;

            var missionKey = missionId.ToString(CultureInfo.InvariantCulture);
            foreach (var messageType in EpicArcMessageTypes)
            {
                var messageId = Positive(messages[messageType]);
                if (messageId > 0)
                    ((JsonObject)maps[messageType]!)[missionKey] = messageId;
            }
        }
        return maps;
    }

    private static IEnumerable<long> TemplateBoundIds(JsonObject agent, JsonObject? indexes, JsonObject? authority)
    {
        var byTemplate = indexes?["missionTemplateToMissionIDs"] as JsonObject;
        if (byTemplate is not null &&
            agent["missionTemplateIDs"] is JsonArray templates)
        {
            foreach (var template in templates)
            {
                var templateId = Text(template);
                if (byTemplate[templateId] is not JsonArray missionIds)
                    continue;
                foreach (var missionId in missionIds)
                    if (Positive(missionId) is > 0 and var id)
                        yield return id;
            }
        }

        if (indexes?["preferredMissionIDs"] is not JsonObject preferred)
            yield break;
        foreach (var preference in PreferenceKeys(agent))
        {
            var preferredMissionId = Positive(preferred[preference]);
            if (preferredMissionId <= 0)
                continue;
            var preferredMission = (authority?["missionsByID"] as JsonObject)?[
                preferredMissionId.ToString(CultureInfo.InvariantCulture)] as JsonObject;
            var contentTemplate = Text(preferredMission?["contentTemplate"]);
            if (contentTemplate.Length > 0 && byTemplate?[contentTemplate] is JsonArray templateMissionIds)
            {
                foreach (var missionId in templateMissionIds)
                    if (Positive(missionId) is > 0 and var id)
                        yield return id;
            }
            else
            {
                yield return preferredMissionId;
            }
        }
    }

    private static IEnumerable<string> PreferenceKeys(JsonObject agent)
    {
        var kind = Text(agent["missionKind"]).ToLowerInvariant();
        if (kind == "distribution") kind = "courier";
        var typeId = Positive(agent["agentTypeID"]);
        var label = Text(agent["missionTypeLabel"]).ToLowerInvariant();
        if (typeId == 13 || Text(agent["ownerName"]).StartsWith("iris", StringComparison.OrdinalIgnoreCase))
        {
            yield return "heraldry";
            yield break;
        }
        if (kind == "research" || label.Contains("research", StringComparison.Ordinal) || typeId == 4)
        {
            yield return "researchTrade";
            yield return "researchCourier";
            yield break;
        }
        if (typeId == 10)
        {
            var suffix = kind == "courier" ? "Courier" : kind == "trade" ? "Trade" : "Encounter";
            yield return "epicArc" + suffix;
            yield return "epicArcAgentInteraction";
            yield return "epicArcTalkToAgent";
            yield break;
        }
        if (IsTrue(agent["importantMission"]) || label.Contains("storyline", StringComparison.Ordinal))
        {
            var suffix = kind == "courier" ? "Courier" : kind == "trade" ? "Trade" : "Encounter";
            yield return "storyline" + suffix;
            yield return "genericStoryline" + suffix;
            yield break;
        }
        var basic = kind switch
        {
            "encounter" => "basicEncounter",
            "courier" => "basicCourier",
            "mining" => "basicMining",
            "trade" => "basicTrade",
            _ => "",
        };
        if (basic.Length > 0) yield return basic;
    }

    private static string ResolveRoute(JsonObject agent, bool hasAgentSpecificIds)
    {
        var agentKind = Text(agent["missionKind"]).ToLowerInvariant();
        if (agentKind == "distribution") agentKind = "courier";
        var typeId = Positive(agent["agentTypeID"]);
        var label = Text(agent["missionTypeLabel"]).ToLowerInvariant();
        if (typeId == 13 || Text(agent["ownerName"]).StartsWith("iris", StringComparison.OrdinalIgnoreCase)) return "heraldry";
        if (agentKind == "research" || label.Contains("research", StringComparison.Ordinal) || typeId == 4) return "research";
        if (typeId == 10) return "epicArc";
        if (hasAgentSpecificIds) return "agent-specific";
        if (IsTrue(agent["importantMission"]) || label.Contains("storyline", StringComparison.Ordinal)) return "storyline";
        return agentKind switch
        {
            "encounter" => "security",
            "courier" => "courier",
            "mining" => "mining",
            "trade" => "trade",
            _ => "misrouted",
        };
    }

    private static bool MissionMatchesAgent(JsonObject agent, JsonObject? mission, long agentId, string route)
    {
        if (mission is null) return false;
        var missionKind = Text(mission["missionKind"]).ToLowerInvariant();
        var flavor = Text(mission["missionFlavor"]).ToLowerInvariant();
        var agentKind = Text(agent["missionKind"]).ToLowerInvariant();
        if (agentKind == "distribution") agentKind = "courier";
        return route switch
        {
            "agent-specific" => true,
            "epicArc" => (IsTrue(mission["isEpicArc"]) || flavor == "epicarc") &&
                         Positive(mission["sourceAgentID"]) == agentId,
            "heraldry" => missionKind == "trade" && (IsTrue(mission["isHeraldry"]) || flavor == "heraldry"),
            "research" => (missionKind is "trade" or "courier") && (IsTrue(mission["isResearch"]) || flavor == "research"),
            "storyline" => missionKind == agentKind &&
                           (IsTrue(mission["isStoryline"]) || IsTrue(mission["isGenericStoryline"]) ||
                            (flavor is "storyline" or "genericstoryline")),
            "security" => missionKind == "encounter" && flavor == "basic" && !IsSpecial(mission),
            "courier" or "mining" or "trade" => missionKind == route && flavor == "basic" && !IsSpecial(mission),
            _ => false,
        };
    }

    private static bool HasValidMissionContent(JsonObject? mission, string route)
    {
        if (mission is null || IsFalse(mission["validContent"]) || IsFalse(mission["isContentValid"])) return false;
        if (Text(mission["contentTemplate"] ?? mission["missionTemplateID"] ?? mission["dungeonTemplateID"]).Length == 0)
            return false;
        if ((route is "security" or "mining") && Positive((mission["killMission"] as JsonObject)?["dungeonID"]) <= 0)
            return false;
        if (string.Equals(Text(mission["missionKind"]), "courier", StringComparison.OrdinalIgnoreCase))
        {
            var courier = mission["courierMission"] as JsonObject;
            if (Positive(courier?["objectiveTypeID"] ?? courier?["typeID"]) <= 0 ||
                Positive(courier?["objectiveQuantity"] ?? courier?["quantity"]) <= 0)
                return false;
        }
        return true;
    }

    private static bool ConstraintsMatch(JsonObject agent, JsonObject? mission)
    {
        if (mission is null) return false;
        foreach (var field in new[] { "agentTypeID", "corporationID", "factionID", "divisionID" })
        {
            var missionValue = Positive(mission[field] ?? (field == "divisionID" ? mission["division"] : null));
            var agentValue = Positive(agent[field] ?? (field == "divisionID" ? agent["division"] : null));
            if (missionValue > 0 && agentValue > 0 && missionValue != agentValue) return false;
        }
        return true;
    }

    private static bool LevelMatches(JsonObject agent, JsonObject? mission, string route)
    {
        if (route is not ("security" or "courier" or "mining" or "trade")) return true;
        var agentLevel = Positive(agent["level"]);
        var missionLevel = Positive(mission?["missionLevel"]);
        return missionLevel > 0 && missionLevel == agentLevel;
    }

    private static bool IsSpecial(JsonObject mission)
    {
        var flavor = Text(mission["missionFlavor"]).ToLowerInvariant();
        return flavor is "storyline" or "genericstoryline" or "epicarc" or "research" or "heraldry" ||
               IsTrue(mission["isEpicArc"]) || IsTrue(mission["isHeraldry"]) ||
               IsTrue(mission["isResearch"]) || IsTrue(mission["isStoryline"]) ||
               IsTrue(mission["isGenericStoryline"]) || IsTrue(mission["isAgentInteraction"]) ||
               IsTrue(mission["isTalkToAgent"]);
    }

    private static bool IsClientMissionAvailable(JsonObject? missions, long missionId)
    {
        if (missions is null || missions[missionId.ToString(CultureInfo.InvariantCulture)] is not JsonObject mission)
            return false;
        return mission["clientDungeonAvailable"] is not JsonValue available ||
               !available.TryGetValue<bool>(out var hasClientDungeon) || hasClientDungeon;
    }

    private static long Positive(JsonNode? node)
    {
        if (node is JsonObject wrapper && wrapper["value"] is { } value)
            node = value;
        if (node is JsonValue scalar)
        {
            if (scalar.TryGetValue<long>(out var integer)) return Math.Max(0, integer);
            if (scalar.TryGetValue<string>(out var text) &&
                long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out integer))
                return Math.Max(0, integer);
        }
        return 0;
    }

    private static bool IsTrue(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<bool>(out var result) && result;

    private static bool IsFalse(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<bool>(out var result) && !result;

    private static string Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : node?.ToString() ?? "";
}

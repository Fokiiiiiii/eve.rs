using System.Text.Json;
using Eve.Services.Core;
using Eve.Services.CharacterInfo;
using Eve.Sde;

namespace Eve.Host;


public readonly record struct AgentStationPlace(long TypeId, long? SolarSystemId);

/// <summary>

/// its in-space placement (<c>agentsInSpace</c>), its station and its solar system. It is a pure function of its inputs so that a test
/// can run it on the pinned SDE and assert the catalog's counts and agents (tests: <c>AgentCatalogSdeTests</c>).
/// </summary>
public static class AgentCatalog
{
    public static IReadOnlyList<AgentMissionAgentData> Build(
        IReadOnlyList<SdeRow> npcCharacters,
        IEnumerable<SdeRow> agentsInSpace,
        Func<long, AgentStationPlace?> station,
        Func<long, bool> solarSystemExists)
    {
        ArgumentNullException.ThrowIfNull(npcCharacters);
        ArgumentNullException.ThrowIfNull(station);
        ArgumentNullException.ThrowIfNull(solarSystemExists);
        var inSpaceById = agentsInSpace.ToDictionary(row => row.Key);
        return npcCharacters
            .Where(row => row.Key > 0 &&
                row.Doc.TryGetProperty("agent", out var agent) &&
                agent.ValueKind == JsonValueKind.Object)
            .Select(row =>
            {
                var agent = row.Doc.GetProperty("agent");

                var missionClass = AgentMissionKindRules.Classify(AgentSdeInteger(agent, "agentTypeID"), AgentSdeInteger(agent, "divisionID"));
                var locationId = AgentSdeInteger(row.Doc, "locationID");
                var place = locationId > 0 ? station(locationId) : null;
                JsonElement? inSpace = inSpaceById.TryGetValue(row.Key, out var inSpaceRow)
                    ? inSpaceRow.Doc
                    : null;
                var inSpaceSolarSystemId = inSpace is { } inSpaceDoc
                    ? AgentSdeInteger(inSpaceDoc, "solarSystemID")
                    : 0;
                var isInSpace = inSpaceSolarSystemId is > 0 and <= int.MaxValue &&
                    solarSystemExists(inSpaceSolarSystemId);
                long? solarSystemId = isInSpace
                    ? inSpaceSolarSystemId
                    : place?.SolarSystemId;
                if (solarSystemId is null &&
                    locationId is > 0 and <= int.MaxValue &&
                    solarSystemExists(locationId))
                {
                    solarSystemId = locationId;
                }

                return new AgentMissionAgentData(
                    row.Key,
                    AgentSdeInteger(agent, "agentTypeID"),
                    AgentSdeInteger(agent, "divisionID"),
                    AgentSdeInteger(agent, "level") is > 0 and var level ? level : 1,
                    isInSpace ? null : AgentSdeOptionalInteger(row.Doc, "locationID"),
                    AgentSdeInteger(row.Doc, "corporationID"),
                    AgentSdeBoolean(agent, "isLocator"),
                    isInSpace,
                    AgentSdeOptionalInteger(row.Doc, "careerID"),
                    AgentSdeBoolean(row.Doc, "importantMission") || AgentSdeBoolean(agent, "importantMission") || missionClass.ImportantMission,
                    AgentSdeText(row.Doc, "missionKind") is { Length: > 0 } missionKind
                        ? missionKind
                        : AgentSdeText(agent, "missionKind") is { Length: > 0 } agentKind
                            ? agentKind
                            : missionClass.MissionKind,
                    AgentSdeText(row.Doc, "missionTypeLabel") is { Length: > 0 } missionType
                        ? missionType
                        : AgentSdeText(agent, "missionTypeLabel"),
                    AgentSdeOptionalInteger(row.Doc, "specialityID"),
                    place is { TypeId: > 0 } ? place.Value.TypeId : AgentSdeOptionalInteger(row.Doc, "stationTypeID"),
                    AgentSdeOptionalInteger(row.Doc, "bloodlineID"),
                    solarSystemId,
                    isInSpace && inSpace is { } spaceDungeon ? AgentSdeOptionalInteger(spaceDungeon, "dungeonID") : null,
                    isInSpace && inSpace is { } spaceSpawn ? AgentSdeOptionalInteger(spaceSpawn, "spawnPointID") : null,
                    isInSpace && inSpace is { } spaceType ? AgentSdeOptionalInteger(spaceType, "typeID") : null,
                    AgentSdeOptionalInteger(row.Doc, "locationID"));
            })
            .OrderBy(agent => agent.AgentId)
            .ToArray();
    }

    public static IReadOnlyDictionary<long, AgentOwnerInfo> BuildOwners(IEnumerable<SdeRow> npcCharacters)
    {
        ArgumentNullException.ThrowIfNull(npcCharacters);
        return npcCharacters
            .Where(row => row.Key > 0 &&
                row.Doc.TryGetProperty("agent", out var agent) &&
                agent.ValueKind == JsonValueKind.Object)
            .Select(row => (
                row.Key,
                Name: row.Doc.TryGetProperty("name", out var name) ? AgentSdeText(name, "en") : "",
                Gender: AgentSdeGender(row.Doc)))
            .Where(owner => !string.IsNullOrWhiteSpace(owner.Name) && owner.Gender is not null)
            .ToDictionary(
                owner => owner.Key,
                owner => new AgentOwnerInfo(owner.Name, owner.Gender!.Value));
    }

    private static long AgentSdeInteger(JsonElement document, string name) =>
        document.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out var result)
            ? result
            : 0;

    private static long? AgentSdeOptionalInteger(JsonElement document, string name) =>
        AgentSdeInteger(document, name) is > 0 and var value ? value : null;

    private static string AgentSdeText(JsonElement document, string name) =>
        document.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static long? AgentSdeGender(JsonElement document) =>
        document.TryGetProperty("gender", out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.ValueKind == JsonValueKind.True ? 1 : 0
            : null;

    private static bool AgentSdeBoolean(JsonElement document, string name) =>
        document.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}

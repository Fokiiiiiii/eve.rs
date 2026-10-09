using System.Net;
using System.Net.Sockets;
using Eve.Characters;
using Eve.Data;
using Eve.Host;
using Eve.Identity;
using Eve.Dogma;
using Eve.Fitting;
using Eve.GameStore;
using System.Text.Json;
using System.Text.Json.Nodes;
using Eve.Inventory;
using Eve.Ships;
using Eve.Marshal;
using Eve.Npc;
using Eve.Sde;
using Eve.Skills;
using Eve.Universe;
using Eve.Wallet;
using Npgsql;
using Sentry;
using Eve.Services;
using Eve.Services.CharacterSelect;
using Eve.Services.Core;
using Eve.Services.Dogma;
using Eve.Services.Inventory;
using Eve.Services.CharacterInfo;
using Eve.Services.Skills;
using Eve.Services.Slash;
using Eve.Services.Space;
using Eve.Space;
using Eve.Services.Station;
using Eve.Services.Wallet;
using static Eve.Host.HostConfiguration;

namespace Eve.Host;

internal sealed partial class CharacterServicesComposition
{
    static long AgentSdeInteger(JsonElement document, string name) =>
        document.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out var result)
            ? result
            : 0;

    static long? AgentSdeOptionalInteger(JsonElement document, string name) =>
        AgentSdeInteger(document, name) is > 0 and var value ? value : null;

    static string AgentSdeText(JsonElement document, string name) =>
        document.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    static bool AgentSdeBoolean(JsonElement document, string name) =>
        document.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private async Task NpcAgents()
    {
        npcCharacterRows = await sde.ReadAsync("npcCharacters", cancellationToken: CancellationToken.None);
        agentsInSpaceById = (await sde.ReadAsync("agentsInSpace", cancellationToken: CancellationToken.None))
            .ToDictionary(row => row.Key);
        agentLocationById = new Dictionary<long, long>();
        foreach (var row in npcCharacterRows)
        {
            if (row.Key > 0 &&
                row.Doc.TryGetProperty("locationID", out var location) &&
                location.TryGetInt64(out var locationId) &&
                locationId > 0)
            {
                agentLocationById[row.Key] = locationId;
            }
        }

        agentMissionAgentRows = npcCharacterRows
            .Where(row => row.Key > 0 &&
                row.Doc.TryGetProperty("agent", out var agent) &&
                agent.ValueKind == JsonValueKind.Object)
            .Select(row =>
            {
                var agent = row.Doc.GetProperty("agent");
                var locationId = AgentSdeInteger(row.Doc, "locationID");
                var station = locationId > 0 ? stations.Get(locationId) : null;
                JsonElement? inSpace = agentsInSpaceById.TryGetValue(row.Key, out var inSpaceRow)
                    ? inSpaceRow.Doc
                    : null;
                var inSpaceSolarSystemId = inSpace is { } inSpaceDoc
                    ? AgentSdeInteger(inSpaceDoc, "solarSystemID")
                    : 0;
                var isInSpace = inSpaceSolarSystemId is > 0 and <= int.MaxValue &&
                    solarSystems.Get((int)inSpaceSolarSystemId) is not null;
                long? solarSystemId = isInSpace
                    ? inSpaceSolarSystemId
                    : station?.SolarSystemId;
                if (solarSystemId is null &&
                    locationId is > 0 and <= int.MaxValue &&
                    solarSystems.Get((int)locationId) is not null)
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
                    AgentSdeBoolean(row.Doc, "importantMission") || AgentSdeBoolean(agent, "importantMission"),
                    AgentSdeText(row.Doc, "missionKind") is { Length: > 0 } missionKind
                        ? missionKind
                        : AgentSdeText(agent, "missionKind"),
                    AgentSdeText(row.Doc, "missionTypeLabel") is { Length: > 0 } missionType
                        ? missionType
                        : AgentSdeText(agent, "missionTypeLabel"),
                    AgentSdeOptionalInteger(row.Doc, "specialityID"),
                    station?.TypeId > 0 ? station.TypeId : AgentSdeOptionalInteger(row.Doc, "stationTypeID"),
                    AgentSdeOptionalInteger(row.Doc, "bloodlineID"),
                    solarSystemId,
                    isInSpace && inSpace is { } spaceDungeon ? AgentSdeOptionalInteger(spaceDungeon, "dungeonID") : null,
                    isInSpace && inSpace is { } spaceSpawn ? AgentSdeOptionalInteger(spaceSpawn, "spawnPointID") : null,
                    isInSpace && inSpace is { } spaceType ? AgentSdeOptionalInteger(spaceType, "typeID") : null);
            })
            .OrderBy(agent => agent.AgentId)
            .ToArray();
    }
}

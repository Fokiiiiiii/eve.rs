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
    private async Task NpcAgents()
    {
        npcCharacterRows = await sde.ReadAsync("npcCharacters", cancellationToken: CancellationToken.None);
        agentOwnerById = AgentCatalog.BuildOwners(npcCharacterRows);
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

        agentMissionAgentRows = AgentCatalog.Build(
            npcCharacterRows,
            agentsInSpaceById.Values,
            id => stations.Get(id) is { } station ? new AgentStationPlace(station.TypeId, station.SolarSystemId) : null,
            id => solarSystems.Get((int)id) is not null).ToArray();
    }
}

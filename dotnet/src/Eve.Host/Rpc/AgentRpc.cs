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
    /// <summary>Agent missions, alliances, the public QA slash service and the ship and beyonce services.</summary>
    private IReadOnlyList<IService> AgentRpc()
    {
        // the cynosural field generator's lighting needs a fleet: the fleet store answers it for the space sessions
        if (spaceSessions is not null)
        {
            spaceSessions.IsInFleetAsync = async (characterId, token) => await fleetStore.FleetIdForCharacterAsync(characterId, token) is not null;
            spaceSessions.FleetIdOfAsync = (characterId, token) => fleetStore.FleetIdForCharacterAsync(characterId, token);
        }
        return AgentRpcServices();
    }

    private IReadOnlyList<IService> AgentRpcServices() =>
    [
            agentMissionService,
            allianceRegistry,
            publicQaSlashService = new SlashService(new ShipHealthCommands(ships, dogma, now, spaceSessions), spaceSessions is null ? null : new DeathTestCommands(spaceSessions, new ShipTypeNames(sde)), spaceSessions is null ? null : new SuicideCommands(spaceSessions), spaceSessions is null ? null : new NpcCommands(spaceSessions), new ItemCommands(new ItemTypeNames(sde), ships, dogma, now), new SkillCommands(skillCatalog, skills, source, dogmaSync, now)),
            .. spaceSessions is null ? [] : new IService[] { new ShipService(spaceSessions, now, spaceContainers, spaceDrones), new BeyonceService(spaceSessions, now, fleetStore, FleetSessionForCharacter), new StructureTravelRpcService("structureCynoBeaconMgr"), new ShipcasterRpcService("shipCasterLandingPadMgr", solarSystems), new ShipcasterRpcService("shipCasterLauncherMgr", solarSystems), new ShipcasterRpcService("shipcasterTravelMgr", solarSystems), new ShipcasterRpcService("starterShipcasterTravelMgr", solarSystems), new EntityService(spaceDrones!, now, spaceContainers) },
    ];
}

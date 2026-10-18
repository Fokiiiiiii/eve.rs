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

    /// <summary>
    /// The Director rule of a corporation purchase: the character is a member of the corporation and its roles include Director
    /// (the member's roles, as the corporation service reads them; eve-logic lpStoreSvc.py:457-462 is the client's copy).
    /// </summary>
    private async ValueTask<bool> IsCorporationDirectorAsync(long characterId, long corporationId, CancellationToken token)
    {
        var corporation = await corporations.GetAsync(corporationId, token);
        var member = await corporations.MemberAsync(characterId, token);
        if (corporation is null || corporation.IsNpc || member?.CorporationId != corporationId) return false;
        var titles = await corporations.TitlesAsync(corporationId, token);
        var roles = Eve.Corporations.CorporationRoles.Session(corporation, member, null, titles);
        return (roles.AtAll & Eve.Corporations.CorporationRoles.Director) != 0;
    }

    private IReadOnlyList<IService> AgentRpcServices() =>
    [
            agentMissionService,
            new LoyaltyPointsService(loyaltyPoints),
            new Eve.Services.Wallet.LpStoreMgrService(lpStoreCache, lpStorePurchases, IsCorporationDirectorAsync),
            allianceRegistry,
            publicQaSlashService = new SlashService(new ShipHealthCommands(ships, dogma, now, spaceSessions), spaceSessions is null ? null : new DeathTestCommands(spaceSessions, new ShipTypeNames(sde)), spaceSessions is null ? null : new SuicideCommands(spaceSessions), spaceSessions is null ? null : new NpcCommands(spaceSessions), new ItemCommands(new ItemTypeNames(sde), ships, dogma, now, spaceSessions), new SkillCommands(skillCatalog, skills, source, dogmaSync, now), new IskCommands(economyWallets, source, now, wallets, loyaltyPoints),
                new LoyaltyCommands(loyaltyPoints, new HostStandingDirectory(dogma, corporations, agentMissionAgentById, characters), now),
                new StandingCommands(new HostStandingDirectory(dogma, corporations, agentMissionAgentById, characters), foundations.Relations,
                    (character, owner, standing) => characterSessions.Of(character)?.SendNotification("OnStandingSet", "clientID", new PyList(
                    [new PyInt(owner), new PyLong(character), new PyReal(standing)]))),
                spaceSessions is null ? null : new TeleportCommands(new HostGmRelocation(spaceSessions, solarSystems, new HostStandingDirectory(dogma, corporations, agentMissionAgentById, characters), characterSessions)),
                missionDungeons is null ? null : new AgentCommands(new HostAgentControl(agentMissionStore, agentId => agentMissionAgentById.GetValueOrDefault(agentId), PickOfferAsync, missionDungeons, () => DateTimeOffset.UtcNow)),
                new FwCommands(new HostFwControl(factionWarfare, new HostStandingDirectory(dogma, corporations, agentMissionAgentById, characters), () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())),
                planetMgr is null ? null : new PiCommands(new HostPiControl(planetMgr)),
                new IndustryCommands(new ItemTypeNames(sde), sde, new HostGmIndustry(ReadOwnedBlueprintsAsync, industryJobOperations.ReadJobsAsync)),
                spaceSessions is null ? null : new SpawnCommands(new ItemTypeNames(sde), spaceSessions),
                new SkillBulkCommands(skillCatalog, skills, source, characters, dogmaSync, now, characterSessions.Of),
                new DogmaUpdateCommands(dogma),
                new KillRightCommands(characters, (owner, target, system, when, token) => crimewatchRuntime.GrantKillRightAsync(owner, target, system, when, token), now),
                spaceSessions is null || spaceContainers is null ? null : new AsteroidCommands(async (session, token) => spaceSessions.SolarSystemOf(session) is { } system
                    ? (await spaceContainers.RocksInSystemAsync(system, token)).Select(rock => (rock.ItemId, rock.TypeId, rock.Name, rock.Units)).ToList()
                    : null)),
            .. spaceSessions is null ? [] : new IService[] { new ShipService(spaceSessions, now, spaceContainers, spaceDrones, getShipFittingInfo: ShipFittingInfoOf, ships: ships,
                rowOf: item => new Eve.Services.ItemRow(item.Id, item.TypeId, item.OwnerId, item.LocationId, item.Flag, item.Singleton ? -1 : item.Quantity,
                    dogma.Type(item.TypeId)?.GroupId ?? 0, dogma.Type(item.TypeId)?.CategoryId ?? 0)), new BeyonceService(spaceSessions, now, fleetStore, FleetSessionForCharacter) { CynoJammerOnlineSimTimeForSystem = CynoJammerOnlineSimTimeAsync, InitiateBeaconLink = AnalysisBeaconLink }, new StructureTravelRpcService("structureCynoBeaconMgr", new StructureBeaconTravel(StructureBeaconAsync, CynoJammedAsync, structureDirectoryReadModel.CharacterHasServiceAsync, JumpToStructureBeaconAsync).TravelAsync), new ShipcasterRpcService("shipCasterLandingPadMgr", solarSystems), new ShipcasterRpcService("shipCasterLauncherMgr", solarSystems), new ShipcasterRpcService("shipcasterTravelMgr", solarSystems), new ShipcasterRpcService("starterShipcasterTravelMgr", solarSystems), new EntityService(spaceDrones!, now, spaceContainers) },
    ];
}

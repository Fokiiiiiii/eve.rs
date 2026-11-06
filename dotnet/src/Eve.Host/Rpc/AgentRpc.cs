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

    internal static async Task<WarpCoordinates?> ResolveMoonMiningPointForSystemAsync(SdeReader sde, long moonId, int systemId, CancellationToken token)
    {
        var point = MoonMiningPointCatalog.Resolve(moonId, systemId);
        if (point is null)
            return null;

        var moons = await sde.ReadAsync("mapMoons", [moonId], token);
        if (moons.Count != 1
            || !moons[0].Doc.TryGetProperty("solarSystemID", out var moonSystem)
            || !moonSystem.TryGetInt32(out var moonSystemId) || moonSystemId != systemId
            || !moons[0].Doc.TryGetProperty("typeID", out var type)
            || !type.TryGetInt32(out var typeId))
            return null;

        var types = await sde.ReadAsync("types", [typeId], token);
        if (types.Count != 1
            || !types[0].Doc.TryGetProperty("groupID", out var group)
            || !group.TryGetInt32(out var groupId) || groupId != 8
            || !moons[0].Doc.TryGetProperty("position", out var position)
            || !position.TryGetProperty("x", out var x) || !x.TryGetDouble(out var moonX)
            || !position.TryGetProperty("y", out var y) || !y.TryGetDouble(out var moonY)
            || !position.TryGetProperty("z", out var z) || !z.TryGetDouble(out var moonZ)
            || !MoonMiningPointCatalog.IsAtMoonMiningPoint(moonId, systemId, moonX, moonY, moonZ))
            return null;

        return point;
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

    private async ValueTask<bool> HasPochvenLpStoreStandingAsync(long characterId, long stationId, CancellationToken token)
    {
        var station = stations.Get(stationId);
        if (station is null || !Eve.Services.Space.Pochven.PochvenRules.IsPochvenRegion(station.RegionId)) return true;
        if (characterId <= 0) return false;

        var standing = await foundations.Relations.ValueAsync(
            Eve.Relations.RelationKinds.Standing.Name,
            Eve.Services.Space.Pochven.PochvenRules.TriglavianFactionId,
            characterId,
            cancellationToken: token);
        return Eve.Services.Space.Pochven.PochvenRules.HasLoyaltyStoreStanding(standing);
    }

    private async Task<WarpCoordinates?> LaunchWarpTargetForCharacterAsync(
        long characterId, long launchId, int currentSystemId, CancellationToken cancellationToken)
    {
        if (planetMgr is null)
            return null;

        var target = await planetMgr.ResolveLaunchWarpTargetAsync(characterId, launchId, currentSystemId, cancellationToken);
        return target is { } point
            ? new WarpCoordinates(point.SolarSystemId, point.X, point.Y, point.Z)
            : null;
    }

    private IReadOnlyList<IService> AgentRpcServices() =>
    [
            agentMissionService,
            new LoyaltyPointsService(loyaltyPoints, async (characterId, token) =>
                (await characters.GetAsync(characterId, token))?.CorporationId,
                isDirector: IsCorporationDirectorAsync,
                isEligibleDestination: async (corporationId, token) =>
                {
                    var corporation = await corporations.GetAsync(corporationId, token);
                    return corporation is { IsNpc: false, Deleted: false } &&
                        (await corporations.MembersAsync(corporationId, token)).Count > 0;
                }),
            new Eve.Services.Wallet.LpStoreMgrService(lpStoreCache, lpStorePurchases, IsCorporationDirectorAsync,
                HasPochvenLpStoreStandingAsync,
                hasLoyaltyStoreService: (characterId, structureId, token) => structureDirectoryReadModel.CharacterHasServiceAsync(
                    characterId, structureId, Eve.Structures.StructureServiceIds.LoyaltyStore, token)),
            allianceRegistry,
            publicQaSlashService = new SlashService(new ShipHealthCommands(ships, dogma, now, spaceSessions), spaceSessions is null ? null : new DeathTestCommands(spaceSessions, new ShipTypeNames(sde)), spaceSessions is null ? null : new SuicideCommands(spaceSessions), spaceSessions is null ? null : new NpcCommands(spaceSessions), new ItemCommands(new ItemTypeNames(sde), ships, dogma, now, spaceSessions), new SkillCommands(skillCatalog, characters, training, announcer, characterSessions.Of), new IskCommands(economyWallets, source, now, wallets, loyaltyPoints),
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
                new SkillBulkCommands(skillCatalog, skills, characters, training, announcer, characterSessions.Of),
                new DogmaUpdateCommands(dogma, (session, character, token) => dogmaSync!.ShipAttributesChangedAsync(session, character, token)),
                new KillRightCommands(characters, (owner, target, system, when, token) => crimewatchRuntime.GrantKillRightAsync(owner, target, system, when, token), now),
                spaceSessions is null || spaceContainers is null ? null : new AsteroidCommands(async (session, token) => spaceSessions.SolarSystemOf(session) is { } system
                    ? (await spaceContainers.RocksInSystemAsync(system, token)).Select(rock => (rock.ItemId, rock.TypeId, rock.Name, rock.Units)).ToList()
                    : null),
                new SkyhookCommands(new PersistenceSkyhookStateWriter(
                    table => mailCache.Read(table).Data as JsonObject ?? new JsonObject(),
                    (table, path, data) => mailCache.Write(table, path, data),
                    (table, token) => mailCache.FlushTableAsync(table, token),
                    () => sovereigntyWriteGate).SetStateAsync),
                spaceSessions is null ? null : new EntityCommands(new ItemTypeNames(sde), spaceSessions),
                spaceSessions is null ? null : new EssCommands(new ShipTypeNames(sde), dogma, ships, (session, token) => EssMgr.AttemptLinkToMainBank(new ServiceCall { Method = "AttemptLinkToMainBank", Args = [], Session = session, Cancellation = token }))),
            .. spaceSessions is null
                ? new IService[] { new ShipFittingInfoOnlyService(new ShipService(null!, now, getShipFittingInfo: ShipFittingInfoOf)) }
                : new IService[] { new ShipService(spaceSessions, now, spaceContainers, spaceDrones, getShipFittingInfo: ShipFittingInfoOf, ships: ships,
                rowOf: item => new Eve.Services.ItemRow(item.Id, item.TypeId, item.OwnerId, item.LocationId, item.Flag, item.Singleton ? -1 : item.Quantity,
                    dogma.Type(item.TypeId)?.GroupId ?? 0, dogma.Type(item.TypeId)?.CategoryId ?? 0),
                shipConfigurations: new PgShipConfigurationStore(source,
                    string.IsNullOrEmpty(Environment.GetEnvironmentVariable("EVE_SPACE_SCHEMA")) ? PgSpaceItemStore.DefaultSchema : Environment.GetEnvironmentVariable("EVE_SPACE_SCHEMA")!)), new BeyonceService(spaceSessions, now, fleetStore, FleetSessionForCharacter)
                {
                    CynoJammerOnlineSimTimeForSystem = CynoJammerOnlineSimTimeAsync,
                    InitiateBeaconLink = AnalysisBeaconLink,
                    LaunchTargetForCharacter = LaunchWarpTargetForCharacterAsync,
                    MoonMiningPointForSystem = (moonId, systemId, token) => ResolveMoonMiningPointForSystemAsync(sde, moonId, systemId, token),
                    EpicInstanceForSystem = async (instanceId, systemId, token) =>
                    {
                        if (cosmicSites is not { } sites)
                            return null;
                        var site = (await sites.ActiveAsync(systemId, token)).FirstOrDefault(active => active.InstanceId == instanceId);
                        return site is null ? null : new WarpCoordinates(systemId, site.Position[0], site.Position[1], site.Position[2]);
                    },
                }, new StructureTravelRpcService("structureCynoBeaconMgr", new StructureBeaconTravel(StructureBeaconAsync, CynoJammedAsync, structureDirectoryReadModel.CharacterHasServiceAsync, JumpToStructureBeaconAsync, BridgeToStructureBeaconAsync, GroupJumpToStructureBeaconAsync).TravelAsync), new ShipcasterRpcService("shipCasterLandingPadMgr", solarSystems), new ShipcasterRpcService("shipCasterLauncherMgr", solarSystems), new ShipcasterRpcService("shipcasterTravelMgr", solarSystems), new ShipcasterRpcService("starterShipcasterTravelMgr", solarSystems), new EntityService(spaceDrones!, now, spaceContainers) },
    ];
}

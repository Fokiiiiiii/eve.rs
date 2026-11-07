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

public static class HostInventoryServiceFactory
{
    public static InvBrokerService CreateInvBroker(
        PgItemStore items,
        ItemTypeCatalog types,
        StationCatalog stations,
        CharacterShips ships,
        DogmaStatic statics,
        Func<long> nowUnixMs,
        Eve.Services.Ships.ISpaceContainers? space,
        Eve.Services.Ships.ISpaceDroneBays? droneBays,
        Eve.Corporations.CorporationStore corporations,
        IReadOnlyList<ICharacterEntryHook>? entryHooks = null,
        Func<Eve.Services.ClientSession, long, CancellationToken, ValueTask<ControlledStructureInventory?>>? controlledStructureInventory = null,
        Func<IReadOnlyCollection<long>, CancellationToken, Task<IReadOnlyDictionary<long, double>>>? crystalDamage = null,
        Func<long, CancellationToken, ValueTask<DockableAssetLocation?>>? dockableAssetLocation = null,
        Eve.Corporations.CorporationOfficeStore? officeStore = null,
        Func<long, long, CancellationToken, ValueTask<bool>>? capsuleerDeliveriesAccess = null,
        Eve.Services.CharacterSessions? characterSessions = null) =>
        new(
            items,
            types,
            stations,
            ships,
            statics,
            nowUnixMs,
            space,
            droneBays,
            corporationHangarAccess: HangarAccessOf(corporations),
            entryHooks: entryHooks,
            controlledStructureInventory: controlledStructureInventory,
            crystalDamage: crystalDamage,
            dockableAssetLocation: dockableAssetLocation,
            capsuleerDeliveriesAccess: capsuleerDeliveriesAccess,
            hasCorporationOffice: officeStore is null ? null : async (corporationId, locationId, token) =>
                (await officeStore.ForCorporationAsync(corporationId, token)).Any(office => office.StationId == locationId),
            characterSessions: characterSessions);

    /// <summary>Capsuleer Deliveries can be viewed and taken by Project Managers and Directors only.</summary>
    public static Func<long, long, CancellationToken, ValueTask<bool>> CapsuleerDeliveriesAccessOf(
        Eve.Corporations.CorporationStore corporations) => async (characterId, corporationId, token) =>
    {
        if (await corporations.GetAsync(corporationId, token) is not { IsNpc: false, Deleted: false } corporation
            || await corporations.MemberAsync(characterId, token) is not { } member
            || member.CorporationId != corporationId)
            return false;

        const long projectManager = 1L << 60; // V24.01 appConst.corpRoleProjectManager.
        var roles = Eve.Corporations.CorporationRoles.Session(
            corporation, member, null, await corporations.TitlesAsync(corporationId, token));
        return (roles.CorpRole & (Eve.Corporations.CorporationRoles.Director | projectManager)) != 0;
    };

    /// <summary>
    /// The access to a corporation division hangar: the Query, Take and Deposit operations. Deposit needs only membership (the existing rule); Query and Take read
    /// the character corporation roles at the station (CorporationRoles.HasHangarAccess). The structure compression source uses the same rule.
    /// </summary>
    public static Eve.Services.Inventory.CorporationHangarAccess HangarAccessOf(Eve.Corporations.CorporationStore corporations) =>
        async (characterId, corporationId, stationId, divisionFlag, operation, token) =>
        {
            if (await corporations.GetAsync(corporationId, token) is not { IsNpc: false } corporation
                || await corporations.MemberAsync(characterId, token) is not { } member
                || member.CorporationId != corporationId)
                return false;

            if (operation == Eve.Services.Inventory.CorporationHangarOperation.Deposit)
                return true;

            var roles = Eve.Corporations.CorporationRoles.Session(
                corporation,
                member,
                stationId,
                await corporations.TitlesAsync(corporationId, token));
            return Eve.Corporations.CorporationRoles.HasHangarAccess(
                roles,
                stationId,
                divisionFlag,
                take: operation == Eve.Services.Inventory.CorporationHangarOperation.Take);
        };

    public static Func<ClientSession, IndustryMonitorRequest, Item, CancellationToken, ValueTask<long?>> IndustryMonitorLocationOf(
        PgItemStore items, ItemTypeCatalog itemTypes, Eve.Corporations.CorporationStore corporations,
        Eve.Corporations.CorporationOfficeStore? offices) =>
        async (session, request, blueprint, token) =>
        {
            var input = request.InputLocation;
            var characterId = session.CharacterId.GetValueOrDefault();
            if (characterId <= 0 || request.FacilityId <= 0 || input is not { ItemId: > 0 })
                return null;

            if (blueprint.OwnerId == characterId)
                return await ResolveIndustryInputLocationAsync(items, itemTypes, input, blueprint.OwnerId,
                    ItemFlags.Hangar, request.FacilityId, request.FacilityId, token);

            if (offices is null || input.FlagId is < ItemFlags.CorpSAG1 or > ItemFlags.CorpSAG7)
                return null;

            var corporation = await corporations.GetAsync(blueprint.OwnerId, token);
            var member = await corporations.MemberAsync(characterId, token);
            if (corporation is not { IsNpc: false, Deleted: false } || member?.CorporationId != blueprint.OwnerId)
                return null;

            var office = (await offices.ForCorporationAsync(blueprint.OwnerId, token))
                .FirstOrDefault(row => row.StationId == request.FacilityId);
            if (office is null)
                return null;

            var folder = await items.GetAsync(office.OfficeFolderId, token);
            if (folder is not { TypeId: Eve.Corporations.CorporationOfficeStore.OfficeFolderTypeId }
                || folder.OwnerId != blueprint.OwnerId || folder.LocationId != request.FacilityId
                || folder.Flag != ItemFlags.Hangar)
                return null;

            var roles = Eve.Corporations.CorporationRoles.Session(
                corporation, member, request.FacilityId,
                await corporations.TitlesAsync(blueprint.OwnerId, token));
            // Manufacturing needs both the division's Query and Take roles, plus Factory Manager access.
            if ((roles.AtAll & Eve.Corporations.CorporationRoles.FactoryManager) == 0
                || !Eve.Corporations.CorporationRoles.HasHangarAccess(roles, request.FacilityId, input.FlagId, take: false)
                || !Eve.Corporations.CorporationRoles.HasHangarAccess(roles, request.FacilityId, input.FlagId, take: true))
                return null;

            return await ResolveIndustryInputLocationAsync(items, itemTypes, input, blueprint.OwnerId,
                input.FlagId, office.OfficeFolderId, office.OfficeId, token);
        };

    private static async ValueTask<long?> ResolveIndustryInputLocationAsync(
        PgItemStore items, ItemTypeCatalog itemTypes, IndustryMonitorLocation input, long ownerId, int flagId,
        long rootLocationId, long rootItemId, CancellationToken token)
    {
        if (input.FlagId != flagId || rootLocationId <= 0 || rootItemId <= 0)
            return null;
        if (input.ItemId == rootItemId || input.ItemId == rootLocationId)
            return rootLocationId;

        var selected = await items.GetAsync(input.ItemId, token);
        if (selected is null || selected.OwnerId != ownerId || selected.Flag != flagId
            || selected.LocationId != rootLocationId || !selected.Singleton)
            return null;
        var type = await itemTypes.GetAsync(selected.TypeId, token);
        return type?.GroupId is 12 or 340 or 448 or 649 ? selected.Id : null;
    }
}

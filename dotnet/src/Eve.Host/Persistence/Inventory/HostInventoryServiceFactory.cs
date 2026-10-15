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
        Func<Eve.Services.ClientSession, long, CancellationToken, ValueTask<ControlledStructureInventory?>>? controlledStructureInventory = null) =>
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
            controlledStructureInventory: controlledStructureInventory);

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
}

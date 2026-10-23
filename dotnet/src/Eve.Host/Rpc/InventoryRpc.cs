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
    /// <summary>Inventory broker, crates, ship cosmetics, dynamic items, energy, dogma and planets.</summary>
    private IReadOnlyList<IService> InventoryRpc()
    {
        var industryMonitor = new IndustryMonitorRpcService(mailCache, items, nowUnixMs: now, itemTypes: itemTypes);
        var inventoryBroker = new IndustryInventoryChangePublisher(
            WithCustoms(HostInventoryServiceFactory.CreateInvBroker(items, itemTypes, stations, ships, dogma, now, spaceContainers, spaceDrones, corporations, characterEntryHooks,
                controlledStructureInventory: structureControlReadModel.InventoryAsync)),
            industryMonitor);

        return
        [
            inventoryBroker,
            new Eve.Services.Core.CrateService(new PersistenceCrateServiceBackend(source, items, itemTypes, skills, skillCatalog, mailCache, savedFittings)),
            new Eve.Host.ShipCosmeticsMgrRpcService(mailCache, mailCache, items, source, skinCosmeticsCatalog, now,
                (characterId, shipId, skinId) =>
                {
                    if (characterSessions.Of(characterId) is not { } session) return;
                    session.SendNotification("OnShipCosmeticsChanged", "clientID", InventoryWire.Id(shipId), new PyDict([]));
                    session.SendNotification("OnCurrentShipSkinChange", "clientID", InventoryWire.Id(shipId), new PyInt(skinId ?? 0));
                },
                (characterId, skinId) =>
                {
                    if (characterSessions.Of(characterId) is { } session)
                        session.SendNotification("OnSkinLicenseActivated", "clientID",
                            new PyInt(skinId), new PyInt(characterId));
                },
                inventoryItemConsumed: async (characterId, before, remainingQuantity, ct) =>
                {
                    if (characterSessions.Of(characterId) is not { } session) return;
                    var type = await itemTypes.GetAsync(before.TypeId, ct);
                    var row = new ItemRow(before.Id, before.TypeId, before.OwnerId, before.LocationId, before.Flag, remainingQuantity, type?.GroupId ?? 0, type?.CategoryId ?? 0);
                    var changes = new PyDict([new KeyValuePair<PyValue, PyValue>(new PyInt(9), InventoryWire.Id(before.Quantity))]);
                    session.SendNotification("OnItemChange", "clientID", InventoryWire.PackedRow(row), changes, PyNone.Instance);
                    await industryMonitor.InventoryChangedAsync(session, ct);
                }),
            new Eve.Host.DynamicItemRpcService(items, itemMutations, dogma, itemTypes, ships),
            new Eve.Host.CharacterEnergyMgrRpcService(mailCache, now),
            new Eve.Host.PaperDollServerRpcService(characters),
            planetMgr,
            planetRegistry,
            industryMonitor,
            new DogmaIMService(
                dogma, ships, now,
                skillHandler.TryActivateBoosterAsync,
                sync: dogmaSync,
                space: spaceSessions,
                modules: spaceSessions,
                health: spaceSessions,
                rounds: spaceSessions,
                probes: spaceSessions,
                controlledStructureSnapshot: structureControlReadModel.DogmaAsync,
                log: message => Console.Error.WriteLine($"eve-host: ammo: {message}"),
                localTimers: foundations.LocalTimers,
                augmentations: new PilotAugmentationRpc(pilotImplants, skillHandler.UseBoosterAsync, announcer, dogma),
                prepareSpaceDogma: spaceSessions is null ? null : spaceSessions.PrepareSpaceDogmaAsync).ListeningForDryGuns(),
        ];
    }
}
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
using Eve.Services.Ships;
using static Eve.Host.HostConfiguration;

namespace Eve.Host;

internal sealed partial class CharacterServicesComposition
{
    /// <summary>Inventory broker, crates, ship cosmetics, dynamic items, energy, dogma and planets.</summary>
    private IReadOnlyList<IService> InventoryRpc()
    {
        var industryMonitor = new IndustryMonitorRpcService(mailCache, items, nowUnixMs: now, itemTypes: itemTypes,
            authorizedInputLocation: HostInventoryServiceFactory.IndustryMonitorLocationOf(
                items, itemTypes, corporations, corporationOfficeStore));
        marketInventoryBroker = WithCustoms(HostInventoryServiceFactory.CreateInvBroker(items, itemTypes, stations, ships, dogma, now,
            spaceContainers, spaceDrones, corporations, characterEntryHooks,
            controlledStructureInventory: structureControlReadModel.InventoryAsync,
            crystalDamage: async (itemIds, token) => (await modules.ModuleConditionsAsync(itemIds, token))
                .ToDictionary(entry => entry.Key, entry => entry.Value.Damage),
            officeStore: corporationOfficeStore,
            capsuleerDeliveriesAccess: HostInventoryServiceFactory.CapsuleerDeliveriesAccessOf(corporations),
            characterSessions: characterSessions));
        var inventoryBroker = new IndustryInventoryChangePublisher(marketInventoryBroker, industryMonitor);

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
                injectSkillBooks: skillHandler.InjectSkillBooksAsync,
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
                prepareSpaceDogma: spaceSessions is null ? null : spaceSessions.PrepareSpaceDogmaAsync,
                structureServiceModuleToggle: structureServiceModuleAuthority.ToggleAsync,
                updateDroneSettings: characters.UpdateDroneSettingsAsync,
                createNewbieShip: CreateNewbieShipAsync,
                unloadAmmoToContainer: UnloadAmmoToContainerAsync).ListeningForDryGuns(),
        ];
    }

    private async ValueTask CreateNewbieShipAsync(ClientSession session, CancellationToken cancellationToken)
    {
        try
        {
            var characterId = session.CharacterId;
            var locationId = session.GetInt("stationid") ?? session.GetInt("structureid");
            if (characterId is not > 0 || locationId is not > 0)
                throw MachoErrors.ThrowUserError("MustBeDocked");

            if (await characters.GetAsync(characterId.Value, cancellationToken) is not { ShipId: { } currentShipId } character
                || session.GetInt("shipid") != currentShipId
                || catalog.Race(character.RaceId) is not { } race)
                throw MachoErrors.ThrowUserError("ErrorCreatingNewbieShip");

            var prepared = await ships.PrepareNewbieShipAsync(
                characterId.Value, locationId.Value, currentShipId, race.ShipTypeId, cancellationToken);
            if (prepared.AlreadyInNewbieShip)
                throw MachoErrors.ThrowUserError("AlreadyInNewbieShip");
            if (prepared.Preparation is not { } shipPreparation)
                throw MachoErrors.ThrowUserError("ErrorCreatingNewbieShip");

            await dogma.EnsureTypesAsync(
                [shipPreparation.Ship.TypeId, .. shipPreparation.FittedModules.Select(change => change.After.TypeId)], cancellationToken);
            ItemRow RowOf(Eve.Inventory.Item item)
            {
                var type = dogma.Type(item.TypeId);
                return new ItemRow(item.Id, item.TypeId, item.OwnerId, item.LocationId, item.Flag,
                    item.Singleton ? -1 : item.Quantity, type?.GroupId ?? 0, type?.CategoryId ?? 0);
            }

            if (shipPreparation.Created)
            {
                var ship = shipPreparation.Ship;
                session.SendNotification("OnItemChange", "clientID", InventoryWire.PackedRow(RowOf(ship)), new PyDict([]), PyNone.Instance);
            }
            foreach (var fitted in shipPreparation.FittedModules)
                ShipNotifications.SendItemChange(session, fitted, RowOf);

            if (spaceSessions is { } space)
            {
                if (await space.BoardAsync(session, shipPreparation.Ship.Id, currentShipId, cancellationToken) is not PyTuple)
                    throw MachoErrors.ThrowUserError("ErrorCreatingNewbieShip");
            }
            else
            {
                if (!await ships.BoardAsync(characterId.Value, locationId.Value, currentShipId, shipPreparation.Ship.Id, cancellationToken))
                    throw MachoErrors.ThrowUserError("ErrorCreatingNewbieShip");
                session.Change(("shipid", new PyLong(shipPreparation.Ship.Id)));
            }

            await ships.HealAsync(characterId.Value, shipPreparation.Ship.Id, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (MachoWrappedException)
        {
            throw;
        }
        catch (Exception)
        {
            throw MachoErrors.ThrowUserError("ErrorCreatingNewbieShip");
        }
    }

    private async Task<ShipOutcome> UnloadAmmoToContainerAsync(
        ClientSession session, long characterId, long shipId, IReadOnlyList<long> moduleIds, long containerId,
        long? namedOwnerId, int? flag, long? quantity, CancellationToken cancellationToken)
    {
        if (session.DockedLocationId is not { } stationId
            || await items.GetAsync(containerId, cancellationToken) is not { } container)
            return new NothingHappened();

        if (namedOwnerId is { } named && named != container.OwnerId)
            return new NothingHappened();

        var destinationOwnerId = container.OwnerId;
        if (destinationOwnerId != characterId)
        {
            var corporationId = session.GetInt("corporationID") is { } corporation && corporation != 0
                ? corporation
                : session.GetInt("corpid") ?? 0;
            if (corporationId <= 0 || destinationOwnerId != corporationId
                || !await CanDepositIntoCorporationContainerAsync(characterId, corporationId, stationId, container, cancellationToken))
            {
                throw MachoErrors.ThrowUserError("CrpAccessDenied",
                    ("reason", Py.Tuple(new PyInt(101), new PyStr("UI/Corporations/AccessRestrictions/InsufficientRoles"))));
            }
        }

        return await ships.UnloadAmmoToContainerAsync(characterId, shipId, moduleIds, containerId,
            destinationOwnerId, flag, quantity, cancellationToken);
    }

    private async Task<bool> CanDepositIntoCorporationContainerAsync(
        long characterId, long corporationId, long stationId, Eve.Inventory.Item container, CancellationToken cancellationToken)
    {
        var current = container;
        var seen = new HashSet<long>();
        for (var depth = 0; depth < 32; depth++)
        {
            if (!seen.Add(current.Id) || current.OwnerId != corporationId)
                return false;
            if (current.LocationId == stationId)
            {
                return (current.Flag == ItemFlags.Hangar || ItemFlags.IsCorporationHangar(current.Flag))
                    && await HostInventoryServiceFactory.HangarAccessOf(corporations)(characterId, corporationId,
                        stationId, current.Flag, CorporationHangarOperation.Deposit, cancellationToken);
            }
            if (await items.GetAsync(current.LocationId, cancellationToken) is not { } parent)
                return false;
            current = parent;
        }
        return false;
    }
}

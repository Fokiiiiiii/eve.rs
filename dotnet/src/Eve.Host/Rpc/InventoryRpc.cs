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
            crystalDamage: async (itemIds, token) => (await items.GetManyAsync(itemIds, token))
                .ToDictionary(item => item.Id, Eve.Ships.CrystalDamageState.DamageOf),
            officeStore: corporationOfficeStore,
            capsuleerDeliveriesAccess: HostInventoryServiceFactory.CapsuleerDeliveriesAccessOf(corporations),
            characterSessions: characterSessions));
        var inventoryBroker = new IndustryInventoryChangePublisher(marketInventoryBroker, industryMonitor);
        var characterEnergy = new Eve.Services.Space.Beacons.AnalysisBeaconStore(source, spaceSchema);

        return
        [
            new I2Service(items, itemTypes, now),
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
                },
                inventorySchema: inventorySchema),
            new Eve.Host.DynamicItemRpcService(items, itemMutations, dogma, itemTypes, ships),
            new Eve.Host.CharacterEnergyMgrRpcService(mailCache, now,
                (characterId, template, token) => characterEnergy.ReadCharacterEnergyAsync(characterId, template, now(), token)),
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
                unloadAmmoToContainer: UnloadAmmoToContainerAsync,
                ammoContainerSourceOwner: AmmoContainerSourceOwnerAsync).ListeningForDryGuns(),
        ];
    }

    private async Task<long?> AmmoContainerSourceOwnerAsync(
        ClientSession session, long characterId, long shipId, long containerId, CancellationToken cancellationToken)
    {
        if (session.CharacterId != characterId || await items.GetAsync(containerId, cancellationToken) is not { } container)
            return null;

        var ownerId = container.OwnerId;
        if (ownerId != characterId)
        {
            var corporationId = session.GetInt("corporationID") is { } corporation && corporation != 0
                ? corporation
                : session.GetInt("corpid") ?? 0;
            if (corporationId <= 0 || corporationId != ownerId
                || await corporations.MemberAsync(characterId, cancellationToken) is not { CorporationId: var memberCorporation }
                || memberCorporation != corporationId)
                return null;
        }

        if (!await ships.CanReachAmmoContainerAsync(characterId, shipId, containerId, ownerId, cancellationToken))
            return null;

        if (ownerId == characterId)
            return ownerId;

        if (session.DockedLocationId is { } stationId)
        {
            if (await CorporationDivisionOfAmmoContainerAsync(container, ownerId, stationId, cancellationToken) is not { } divisionFlag
                || !await HostInventoryServiceFactory.HangarAccessOf(corporations)(characterId, ownerId, stationId, divisionFlag,
                    CorporationHangarOperation.Take, cancellationToken))
            {
                throw MachoErrors.ThrowUserError("CrpAccessDenied",
                    ("reason", Py.Tuple(new PyInt(101), new PyStr("UI/Corporations/AccessRestrictions/InsufficientRoles"))));
            }
        }

        return ownerId;
    }

    private async Task<int?> CorporationDivisionOfAmmoContainerAsync(
        Eve.Inventory.Item container, long corporationId, long stationId, CancellationToken cancellationToken)
    {
        var current = container;
        var seen = new HashSet<long>();
        for (var depth = 0; depth < 32; depth++)
        {
            if (!seen.Add(current.Id) || current.OwnerId != corporationId)
                return null;
            if (current.LocationId == stationId)
                return ItemFlags.IsCorporationHangar(current.Flag) ? current.Flag : null;
            if (await items.GetAsync(current.LocationId, cancellationToken) is not { } parent)
                return null;
            current = parent;
        }
        return null;
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
        if (tradeAuthority is not null && await tradeAuthority.ViewAsync(containerId, cancellationToken).ConfigureAwait(false) is { IsOpen: true } trade)
        {
            if (namedOwnerId is { } stagedNamedOwnerId && stagedNamedOwnerId != characterId)
                return new NothingHappened();
            if (flag is { } requestedFlag && requestedFlag != ItemFlags.Hangar)
                throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("Unload into the trade container by its inventory ID.")));
            if (quantity is > 0 && await ships.LoadAsync(characterId, cancellationToken).ConfigureAwait(false) is { } snapshot)
            {
                foreach (var moduleId in moduleIds.Distinct())
                    if (snapshot.Fitted.FirstOrDefault(module => module.Id == moduleId) is { } module
                        && snapshot.ChargeOf(module) is { } charge && quantity != charge.Quantity)
                        throw MachoErrors.ThrowUserError("CustomNotify",
                            ("notify", new PyStr("Unload the whole charge stack into trade; partial fitted-charge trade is not available yet.")));
            }

            return await ships.UnloadAmmoToTradeAsync(characterId, shipId, moduleIds, async (charges, token) =>
            {
                var change = await TradeMgrService.StageFittedChargesAsync(session, trade.TradeId, shipId, charges, token).ConfigureAwait(false);
                return change.Moves.Where(move => !move.Removed && move.After.LocationId == trade.TradeId)
                    .ToDictionary(move => move.After.Id, move => move.After);
            }, cancellationToken).ConfigureAwait(false);
        }

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

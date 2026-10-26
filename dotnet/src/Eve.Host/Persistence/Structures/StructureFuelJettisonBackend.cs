using Eve.Dogma;
using Eve.Events;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Ships;
using Eve.Space;
using Eve.Structures;
using Npgsql;

namespace Eve.Host;

/// <summary>Moves selected structure fuel-bay stacks into a corporation-owned space can.</summary>
public sealed class StructureFuelJettisonBackend(
    NpgsqlDataSource source,
    StructureStore structures,
    PgItemStore items,
    DogmaStatic dogma,
    ISimulation sim,
    PgSpaceItemStore spaceItems,
    EventBus events,
    Func<long, long, CancellationToken, ValueTask<bool>> stationManager,
    Func<long> nowUnixMs,
    Action<string>? log = null)
{
    private const int FuelBayFlag = 172;
    private const long CanLifetimeMs = 2 * 60 * 60 * 1000;
    private const double JettisonDistance = 275;

    public async ValueTask JettisonAsync(ServiceCall call, long structureId, IReadOnlyList<long> requestedIds)
    {
        var characterId = call.Session.CharacterId ?? 0;
        if (characterId <= 0 || structureId <= 0 ||
            call.Session.GetInt("shipid") != structureId ||
            call.Session.GetInt("structureid") != structureId)
            throw AccessDenied();

        var ids = requestedIds.Where(id => id > 0).Distinct().Order().ToArray();
        if (ids.Length == 0)
            return;

        await dogma.EnsureTypesAsync([CharacterShips.CargoContainerTypeId, .. await TypeIdsAsync(ids, call.Cancellation)], call.Cancellation);
        var containerType = dogma.Type(CharacterShips.CargoContainerTypeId)
            ?? throw new InvalidOperationException("The pinned SDE has no Cargo Container type.");

        await using var connection = await source.OpenConnectionAsync(call.Cancellation);
        await using var transaction = await connection.BeginTransactionAsync(call.Cancellation);
        var structure = await structures.GetAsync(connection, transaction, structureId, true, call.Cancellation);
        if (structure is null || structure.Snapshot.DestroyedMs > 0 ||
            structure.OwnerCorporationId <= 0 ||
            call.Session.GetInt("corpid") != structure.OwnerCorporationId ||
            !await stationManager(characterId, structure.OwnerCorporationId, call.Cancellation))
            throw AccessDenied();

        var systemId = structure.SolarSystemId;
        if (systemId <= 0 || call.Session.GetInt("solarsystemid2") != systemId)
            throw AccessDenied();

        var fuelItems = new List<Item>();
        foreach (var id in ids)
        {
            if (await items.LockAndGetAsync(connection, transaction, id, call.Cancellation) is not { } item ||
                item.OwnerId != structure.OwnerCorporationId || item.LocationId != structureId ||
                item.Flag != FuelBayFlag || item.Quantity <= 0 ||
                dogma.Type(item.TypeId) is not { } type ||
                (dogma.GroupName(type.GroupId) != "Fuel Block" && type.Name != "Liquid Ozone"))
                continue;
            fuelItems.Add(item);
        }

        if (fuelItems.Count == 0)
            return;

        var now = nowUnixMs();
        var containerId = await items.CreateAsync(connection, transaction,
            new NewItem(CharacterShips.CargoContainerTypeId, structure.OwnerCorporationId, systemId,
                CharacterShips.InSpaceFlag, 1, Singleton: true), call.Cancellation);
        var placed = false;
        try
        {
            var placement = await sim.PlaceContainerAsync(new SimPlaceContainer(
                (ulong)call.Session.SessionId,
                containerId,
                new SimWreckKind(CharacterShips.CargoContainerTypeId, containerType.Name, containerType.GraphicId),
                containerType.Attributes.GetValueOrDefault(162, 14),
                structureId,
                new SimCredit(characterId, structure.OwnerCorporationId, structure.AllianceId, 0),
                JettisonDistance)
            {
                ExpectedScene = SimSceneKey.SolarSystem(systemId),
            }, call.Cancellation);
            if (!placement.Accepted)
                throw Unavailable();
            placed = true;

            var radius = containerType.Attributes.GetValueOrDefault(162, 14);
            await spaceItems.SaveAsync(connection, transaction, new SpaceItem(
                containerId, systemId, placement.Position, placement.Direction, radius, containerType.GroupId,
                structureId, characterId, structure.OwnerCorporationId, structure.AllianceId, 0,
                checked(now + CanLifetimeMs)), call.Cancellation);
            var changes = new List<ItemChanged>(fuelItems.Count);
            foreach (var item in fuelItems)
            {
                await items.MoveToOwnerAsync(connection, transaction, item.Id, structure.OwnerCorporationId,
                    containerId, CharacterShips.ContainerFlag, call.Cancellation);
                changes.Add(new ItemChanged(item with
                {
                    LocationId = containerId,
                    Flag = CharacterShips.ContainerFlag,
                }, structureId, item.Flag, item.Quantity));
            }
            await events.PublishInTransactionAsync(connection, transaction,
                StructureFuelBayNotifications.Topic,
                new StructureFuelBayItemsChanged(structureId, changes),
                $"structure:{structureId}", call.Cancellation);
            await transaction.CommitAsync(call.Cancellation);
        }
        catch
        {
            if (placed && await spaceItems.GetAsync(containerId, CancellationToken.None) is null)
                sim.RemoveWreck(containerId, 0);
            throw;
        }

        events.Wake();
        try
        {
            await sim.ChangeWreckAsync((ulong)call.Session.SessionId, containerId, isEmpty: false);
        }
        catch (SimUnavailableException error)
        {
            log?.Invoke($"the contents of structure-fuel can {containerId} were not sent to the scene: {error.Message}");
        }
    }

    private async Task<IReadOnlyList<int>> TypeIdsAsync(IReadOnlyList<long> ids, CancellationToken cancellationToken)
    {
        var typeIds = new List<int>(ids.Count);
        foreach (var id in ids)
            if (await items.GetAsync(id, cancellationToken) is { } item)
                typeIds.Add(item.TypeId);
        return typeIds;
    }

    private static Exception AccessDenied() => MachoErrors.ThrowUserError(
        "CrpAccessDenied", ("reason", Py.Tuple(new PyInt(101), new PyStr("UI/Corporations/AccessRestrictions/InsufficientRoles"))));

    private static Exception Unavailable() => MachoErrors.ThrowUserError(
        "CustomNotify", ("notify", new PyStr("The fuel container could not be placed in space.")));
}

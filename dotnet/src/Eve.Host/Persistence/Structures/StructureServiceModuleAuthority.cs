using Eve.Dogma;
using Eve.Fitting;
using Eve.Inventory;
using Eve.Services;
using Eve.Services.Dogma;
using Eve.Ships;
using Eve.Structures;
using Npgsql;

namespace Eve.Host;

/// <summary>Validates and commits DogmaIM service-module toggles against the structure, inventory, fitting and fuel stores.</summary>
internal sealed class StructureServiceModuleAuthority(
    NpgsqlDataSource source,
    StructureStore structures,
    StructurePilotStore pilots,
    StructureControlReadModel control,
    PgItemStore items,
    PgModuleStateStore modules,
    DogmaStatic dogma,
    StructureFuel fuel)
{
    private const long StationManagerRole = 1L << 11;

    public async ValueTask<StructureServiceModuleToggleResult?> ToggleAsync(
        ClientSession session,
        long boundLocationId,
        long locationId,
        long moduleId,
        bool online,
        CancellationToken cancellationToken)
    {
        if (dogma.CategoryId("Structure") is not { } structureCategory)
            return null;
        var bound = boundLocationId > 0 ? await structures.GetAsync(boundLocationId, cancellationToken) : null;
        var requested = locationId > 0 ? await structures.GetAsync(locationId, cancellationToken) : null;
        var boundIsStructure = bound is not null && dogma.Type(bound.TypeId)?.CategoryId == structureCategory;
        var requestedIsStructure = requested is not null && dogma.Type(requested.TypeId)?.CategoryId == structureCategory;
        if (!boundIsStructure && !requestedIsStructure)
            return null;

        if (!boundIsStructure || !requestedIsStructure || boundLocationId != locationId || moduleId <= 0
            || session.DockedLocationId != locationId
            || session.GetInt("shipid") != locationId || session.GetInt("structureid") != locationId)
            return Refused("DeniedShipChanged");
        if (session.CharacterId is not { } character
            || await control.InventoryAsync(session, locationId, cancellationToken) is null)
            return Refused("CrpAccessDenied");

        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var record = await structures.GetAsync(connection, transaction, locationId, true, cancellationToken);
        if (record is null || record.Snapshot.DestroyedMs > 0 || record.OwnerCorporationId <= 0
            || (session.GetInt("corpid") ?? session.GetInt("corporationID")) != record.OwnerCorporationId)
            return await RollbackAsync(transaction, Refused("CrpAccessDenied"), cancellationToken);

        var pilot = await pilots.GetBySessionAsync(connection, transaction, session.SessionId, cancellationToken, forUpdate: true);
        if (pilot is null || pilot.CharacterId != character || pilot.StructureId != locationId || pilot.SessionId != session.SessionId)
            return await RollbackAsync(transaction, Refused("CrpAccessDenied"), cancellationToken);

        var item = await items.LockAndGetAsync(connection, transaction, moduleId, cancellationToken);
        var serviceIds = item is null ? [] : fuel.ServicesOfModule(item.TypeId);
        if (item is null || item.LocationId != locationId
            || item.Flag < StructureServiceIds.ServiceSlotFirst || item.Flag > StructureServiceIds.ServiceSlotLast
            || serviceIds.Count == 0
            || item.OwnerId != character && item.OwnerId != record.OwnerCorporationId)
            return await RollbackAsync(transaction, Refused("DeniedShipChanged"), cancellationToken);

        var previous = await modules.StateAsync(connection, transaction, moduleId, cancellationToken);
        var wasOnline = previous >= ItemState.Online;
        var roles = session.GetInt("corprole") ?? session.GetInt("corpRole") ?? session.GetInt("rolesAtAll") ?? 0;
        if (!online && wasOnline && (item.OwnerId != record.OwnerCorporationId || (roles & StationManagerRole) != StationManagerRole))
            return await RollbackAsync(transaction, Refused("CrpAccessDenied"), cancellationToken);

        var outcome = online
            ? await fuel.OnlineModuleAsync(connection, transaction, locationId, moduleId, item.TypeId, serviceIds, cancellationToken)
            : await fuel.OfflineModuleAsync(connection, transaction, locationId, moduleId, cancellationToken);
        if (outcome == StructureServiceModuleOutcome.NotEnoughFuel)
            return await RollbackAsync(transaction, Refused("NotEnoughFuel"), cancellationToken);
        if (outcome is StructureServiceModuleOutcome.NoStructure or StructureServiceModuleOutcome.UnknownService)
            return await RollbackAsync(transaction, Refused("DeniedShipChanged"), cancellationToken);

        await modules.SetAsync(connection, transaction, moduleId, online ? ItemState.Online : ItemState.Offline, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var events = new List<DogmaEvent>();
        if (wasOnline != online && dogma.AttributeId("isOnline") is { } isOnlineAttribute)
            events.Add(new AttributeChanged(moduleId, isOnlineAttribute, wasOnline ? 1 : 0, online ? 1 : 0));
        events.Add(new EffectChanged(moduleId, 16, online));
        return new StructureServiceModuleToggleResult(null, events);
    }

    private static StructureServiceModuleToggleResult Refused(string error) => new(error, []);

    private static async ValueTask<StructureServiceModuleToggleResult> RollbackAsync(
        NpgsqlTransaction transaction,
        StructureServiceModuleToggleResult result,
        CancellationToken cancellationToken)
    {
        await transaction.RollbackAsync(cancellationToken);
        return result;
    }
}

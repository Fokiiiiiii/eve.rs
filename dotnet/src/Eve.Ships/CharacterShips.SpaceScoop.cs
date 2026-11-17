using Eve.Inventory;
using Npgsql;

namespace Eve.Ships;

/// <summary>How the direct contents of a Mobile Depot are retained when the depot itself is scooped.</summary>
public sealed record ScoopEjectedContainer(
    int TypeId, int GroupId, double Radius, long NowUnixMs, long ExpiresAtMs,
    long CharacterId, long CorporationId, long AllianceId, long WarFactionId);

public sealed partial class CharacterShips
{
    /// <summary>
    /// Takes a visible scoopable space item and all of its nested contents into one ship hold. The caller has checked
    /// scene/range/loot rights; the item move, ownership changes, deployed record removal and space-row removal share one transaction.
    /// </summary>
    public async Task<ShipOutcome> ScoopSpaceItemToShipHoldAsync(
        long characterId,
        long expectedShipId,
        int solarSystemId,
        long itemId,
        int destinationFlag,
        PgSpaceItemStore spaceItems,
        PgDeployedStore? deployed = null,
        string? expectedDeployedKind = null,
        Func<CancellationToken, Task<bool>>? removeFromScene = null,
        CancellationToken cancellationToken = default,
        ScoopEjectedContainer? ejectContentsTo = null,
        Func<Item, SpaceItem, CancellationToken, Task<bool>>? placeEjectedContainer = null,
        Func<long, CancellationToken, Task>? removeEjectedContainer = null,
        Func<CancellationToken, Task>? restoreToScene = null)
    {
        if (characterId <= 0 || expectedShipId <= 0 || solarSystemId <= 0 || itemId <= 0 || !IsShipHoldFlag(destinationFlag))
            return new NothingHappened();
        if (ejectContentsTo is not null && (ejectContentsTo.TypeId <= 0 || ejectContentsTo.GroupId <= 0 ||
            !double.IsFinite(ejectContentsTo.Radius) || ejectContentsTo.Radius <= 0 || ejectContentsTo.ExpiresAtMs <= 0 ||
            placeEjectedContainer is null || removeEjectedContainer is null))
            throw new ArgumentException("A Mobile Depot content ejection requires a valid container and simulation callbacks.", nameof(ejectContentsTo));

        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, characterId, cancellationToken);
        if (await LoadAsync(characterId, cancellationToken) is not { } snapshot ||
            snapshot.Ship.Id != expectedShipId || snapshot.Ship.Flag != InSpaceFlag || snapshot.Ship.LocationId != solarSystemId)
            return new NothingHappened();

        var item = await items.LockAndGetAsync(connection, transaction, itemId, cancellationToken);
        if (item is null || item.LocationId != solarSystemId || item.Flag != InSpaceFlag || !item.Singleton || item.Quantity != 1)
            return new NothingHappened();
        var spaceItem = await spaceItems.GetAsync(connection, transaction, itemId, cancellationToken);
        if (spaceItem is null || spaceItem.SolarSystemId != solarSystemId)
            return new NothingHappened();
        DeployedRecord? deployedRecord = null;
        if (expectedDeployedKind is { } kind)
        {
            deployedRecord = deployed is null ? null : await deployed.LockAndGetAsync(connection, transaction, itemId, cancellationToken);
            if (deployedRecord is null || deployedRecord.Kind != kind || deployedRecord.SolarSystemId != solarSystemId || deployedRecord.ExpiresAtMs <= 0)
                return new NothingHappened();
        }

        await statics.EnsureTypesAsync([item.TypeId], cancellationToken);
        var type = statics.Type(item.TypeId)
            ?? throw new InvalidOperationException($"SDE type {item.TypeId} for space item {itemId} is missing.");
        if (ejectContentsTo is { } mobileDepot &&
            (expectedDeployedKind != "mobile_depot" || item.OwnerId != characterId || mobileDepot.CharacterId != characterId ||
             deployedRecord is null || deployedRecord.OwnerId != characterId || deployedRecord.ExpiresAtMs <= mobileDepot.NowUnixMs ||
             deployedRecord.ActivateAtMs > mobileDepot.NowUnixMs || deployedRecord.TypeId != item.TypeId || deployedRecord.GroupId != type.GroupId ||
             deployedRecord.CorporationId != mobileDepot.CorporationId || deployedRecord.AllianceId != mobileDepot.AllianceId))
            return new NothingHappened();
        if (!CanAddToShipHold(destinationFlag, type, item.Singleton, 1))
            return new ShipRefused(ShipRefusal.NotAllowedInHold, ModuleTypeId: item.TypeId, ShipTypeId: snapshot.Ship.TypeId);
        if (await RefuseWhatDoesNotFitAsync(snapshot, item, 1, destinationFlag, cancellationToken) is { } full)
            return full;

        var contents = ejectContentsTo is null
            ? await LockScoopContentsAsync(connection, transaction, itemId, cancellationToken)
            : await items.LockContainerAsync(connection, transaction, itemId, cancellationToken);
        Item? ejectedContainer = null;
        SpaceItem? ejectedSpaceItem = null;
        var ejectedContents = new List<ItemMoved>(contents.Count);
        if (ejectContentsTo is { } ejection && contents.Count > 0)
        {
            if (ejection.CharacterId != characterId)
                return new NothingHappened();
            var containerId = await items.CreateAsync(connection, transaction,
                new NewItem(ejection.TypeId, characterId, solarSystemId, InSpaceFlag, 1, Singleton: true), cancellationToken);
            ejectedContainer = new Item(containerId, ejection.TypeId, characterId, solarSystemId, InSpaceFlag, 1, true, null);
            ejectedSpaceItem = new SpaceItem(
                containerId, solarSystemId, spaceItem.Position, spaceItem.Direction, ejection.Radius, ejection.GroupId,
                expectedShipId, ejection.CharacterId, ejection.CorporationId, ejection.AllianceId, ejection.WarFactionId,
                ejection.ExpiresAtMs);
            await spaceItems.SaveAsync(connection, transaction, ejectedSpaceItem, cancellationToken);
            foreach (var child in contents)
            {
                await items.MoveToOwnerAsync(connection, transaction, child.Id, child.OwnerId, containerId, ContainerFlag, cancellationToken);
                ejectedContents.Add(new ItemMoved(child with { LocationId = containerId, Flag = ContainerFlag }, itemId, child.Flag));
            }
        }
        else
        {
            var movedContents = new List<ItemMoved>(contents.Count);
            foreach (var child in contents)
            {
                if (child.OwnerId == characterId)
                    continue;
                await items.MoveToOwnerAsync(connection, transaction, child.Id, characterId, child.LocationId, child.Flag, cancellationToken);
                movedContents.Add(new ItemMoved(child with { OwnerId = characterId }, child.LocationId, child.Flag, child.OwnerId));
            }
            ejectedContents = movedContents;
        }

        await items.MoveToOwnerAsync(connection, transaction, item.Id, characterId, snapshot.Ship.Id, destinationFlag, cancellationToken);
        if (!await spaceItems.RemoveAsync(connection, transaction, item.Id, cancellationToken))
            return new NothingHappened();
        if (expectedDeployedKind is not null && !await deployed!.RemoveAsync(connection, transaction, item.Id, cancellationToken))
            return new NothingHappened();
        var attemptedEjectedPlacement = false;
        var placedEjectedContainer = false;
        var attemptedSceneRemoval = false;
        try
        {
            if (ejectedContainer is not null && ejectedSpaceItem is not null)
            {
                attemptedEjectedPlacement = true;
                placedEjectedContainer = await placeEjectedContainer!(ejectedContainer, ejectedSpaceItem, cancellationToken);
                if (!placedEjectedContainer)
                {
                    await removeEjectedContainer!(ejectedContainer.Id, CancellationToken.None);
                    return new NothingHappened();
                }
            }
            if (removeFromScene is not null)
            {
                attemptedSceneRemoval = true;
                if (!await removeFromScene(cancellationToken))
                {
                    if (placedEjectedContainer)
                        await removeEjectedContainer!(ejectedContainer!.Id, CancellationToken.None);
                    return new NothingHappened();
                }
            }
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            if (attemptedEjectedPlacement)
                await removeEjectedContainer!(ejectedContainer!.Id, CancellationToken.None);
            if (attemptedSceneRemoval && restoreToScene is not null)
                await restoreToScene(CancellationToken.None);
            throw;
        }
        var movedItem = item with { OwnerId = characterId, LocationId = snapshot.Ship.Id, Flag = destinationFlag };
        return new ShipChanged(item.Id, snapshot.Ship.Id, ejectedContents, [],
            [new ItemChanged(movedItem, item.LocationId, item.Flag, item.Quantity, item.OwnerId)],
            ejectedContainer is null ? null : [ejectedContainer]);
    }

    /// <summary>
    /// Takes a marked space artifact into the active ship's cargo, reassigns every contained item to the pilot, and forgets its space row in one transaction.
    /// The caller verifies the artifact marker, scene visibility, range and loot rights before entering this authority.
    /// </summary>
    public async Task<ShipOutcome> ScoopSpaceArtifactAsync(
        long characterId,
        long corporationId,
        bool fittings,
        long expectedShipId,
        int solarSystemId,
        long itemId,
        string expectedMarker,
        PgSpaceItemStore spaceItems,
        CancellationToken cancellationToken = default,
        int destinationFlag = CargoFlag)
    {
        if (characterId <= 0 || corporationId <= 0 || expectedShipId <= 0 || solarSystemId <= 0 || itemId <= 0 || string.IsNullOrEmpty(expectedMarker))
        {
            return new NothingHappened();
        }

        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, characterId, cancellationToken);

        if (await LoadAsync(characterId, cancellationToken) is not { } snapshot ||
            snapshot.Ship.Id != expectedShipId || snapshot.Ship.Flag != InSpaceFlag || snapshot.Ship.LocationId != solarSystemId)
        {
            return new NothingHappened();
        }

        var item = await items.LockAndGetAsync(connection, transaction, itemId, cancellationToken);
        if (item is null || item.LocationId != solarSystemId || item.Flag != InSpaceFlag ||
            !item.Singleton || item.Quantity != 1 || !StringComparer.Ordinal.Equals(item.CustomInfo, expectedMarker))
        {
            return new NothingHappened();
        }

        var spaceItem = await spaceItems.GetAsync(connection, transaction, itemId, cancellationToken);
        if (spaceItem is null || spaceItem.ItemId != item.Id || spaceItem.SolarSystemId != solarSystemId)
        {
            return new NothingHappened();
        }

        await statics.EnsureTypesAsync([item.TypeId], cancellationToken);
        var type = statics.Type(item.TypeId)
            ?? throw new InvalidOperationException($"SDE type {item.TypeId} for space artifact {itemId} is missing.");
        if (!CanAddToShipHold(destinationFlag, type, item.Singleton, 1))
        {
            return new ShipRefused(ShipRefusal.NotAllowedInHold, ModuleTypeId: item.TypeId, ShipTypeId: snapshot.Ship.TypeId);
        }
        var category = statics.CategoryName(type.CategoryId);
        var custodyMatches = fittings
            ? item.OwnerId == spaceItem.CharacterId || item.OwnerId == spaceItem.CorporationId
            : category == "Structure"
                ? item.OwnerId == spaceItem.CorporationId
                : category == "Starbase" && (item.OwnerId == characterId ||
                    item.OwnerId == corporationId && spaceItem.CorporationId == corporationId);
        if (spaceItem.GroupId != type.GroupId || spaceItem.LauncherId <= 0 || !custodyMatches)
        {
            throw new InvalidOperationException($"Space artifact {itemId} disagrees with its SDE identity or custody record.");
        }

        if (await RefuseWhatDoesNotFitAsync(snapshot, item, 1, destinationFlag, cancellationToken) is { } full)
        {
            return full;
        }

        var contents = await LockScoopContentsAsync(connection, transaction, itemId, cancellationToken);
        var movedContents = new List<ItemMoved>(contents.Count);
        foreach (var child in contents)
        {
            if (child.OwnerId == characterId)
            {
                continue;
            }

            await items.MoveToOwnerAsync(connection, transaction, child.Id, characterId, child.LocationId, child.Flag, cancellationToken);
            movedContents.Add(new ItemMoved(child with { OwnerId = characterId }, child.LocationId, child.Flag));
        }

        await items.MoveToOwnerAsync(connection, transaction, item.Id, characterId, snapshot.Ship.Id, destinationFlag, cancellationToken);
        if (!await spaceItems.RemoveAsync(connection, transaction, item.Id, cancellationToken))
        {
            return new NothingHappened();
        }

        await transaction.CommitAsync(cancellationToken);

        var movedItem = item with { OwnerId = characterId, LocationId = snapshot.Ship.Id, Flag = destinationFlag };
        var stacks = new[] { new ItemChanged(movedItem, item.LocationId, item.Flag, item.Quantity) };
        return new ShipChanged(item.Id, snapshot.Ship.Id, movedContents, [], stacks);
    }

    private async Task<IReadOnlyList<Item>> LockScoopContentsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long rootItemId,
        CancellationToken cancellationToken)
    {
        var contents = new Dictionary<long, Item>();
        var pendingContainers = new Queue<long>();
        pendingContainers.Enqueue(rootItemId);

        while (pendingContainers.TryDequeue(out var containerId))
        {
            foreach (var child in await items.LockContainerAsync(connection, transaction, containerId, cancellationToken))
            {
                if (!contents.TryAdd(child.Id, child))
                {
                    throw new InvalidOperationException($"Space artifact {rootItemId} contains a cycle or duplicate item {child.Id}.");
                }
                pendingContainers.Enqueue(child.Id);
            }
        }

        return contents.Values.OrderBy(item => item.Id).ToArray();
    }
}

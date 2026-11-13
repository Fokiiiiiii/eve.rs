using Eve.Dogma;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services.Ships;
using Eve.Ships;
using Eve.Space;
using Npgsql;

namespace Eve.Services.Space;

public sealed partial class SpaceContainers
{
    private const double MaintenanceBayUseRange = 5_000;
    private const double MaintenanceBayLaunchOffset = 275;

    /// <summary>Launches one assembled ship from a visible ship maintenance bay and records its space pose with the inventory move.</summary>
    public async Task LaunchFromMaintenanceBayAsync(
        ClientSession session,
        long sourceId,
        long itemId,
        CancellationToken cancellationToken)
    {
        if (sourceId <= 0 || itemId <= 0 || sourceId == itemId ||
            session.CharacterId is not { } characterId ||
            session.GetInt("shipid") is not { } activeShipId || activeShipId <= 0 ||
            !_space.IsInSpace(session) || _space.SolarSystemOf(session) is not { } systemId)
            throw MaintenanceBayRefuse("Unable to launch this ship from the maintenance bay.");
        if (_space.IsWarping(session))
            throw MachoErrors.ThrowUserError("ShipInWarp");
        if (_space.IsInAbyssalRun(session))
            throw MaintenanceBayRefuse("You cannot eject, board, launch, or store ships inside Abyssal Deadspace.");
        if (_source is null)
            throw Unavailable();

        var gate = _launchItemGates[(int)((ulong)itemId % (uint)_launchItemGates.Length)];
        await gate.WaitAsync(cancellationToken);
        try
        {
            var current = await _items.GetAsync(itemId, cancellationToken);
            if (current is { LocationId: var launchedLocation, Flag: CharacterShips.InSpaceFlag } launched &&
                launchedLocation == systemId &&
                await _spaceItems.GetAsync(itemId, cancellationToken) is
                    { LauncherId: var launcherId, Kind: SpaceItemKind.MaintenanceBayLaunchedShip, SourceContainerId: var previousSource } existing &&
                launcherId == activeShipId && previousSource == sourceId)
            {
                await _statics.EnsureTypesAsync([launched.TypeId], cancellationToken);
                if (_statics.Type(launched.TypeId) is not { } retryType ||
                    !await EnsureLaunchedItemPlacedAsync(launched, existing, retryType, cancellationToken))
                    throw MaintenanceBayRefuse("Unable to launch this ship from the maintenance bay.");
                ShipNotifications.SendItemChange(session,
                    new ItemChanged(launched, sourceId, 0, launched.Quantity), RowOf);
                return;
            }

            var source = await _items.GetAsync(sourceId, cancellationToken);
            if (source is not { LocationId: var sourceLocation, Flag: CharacterShips.InSpaceFlag } || sourceLocation != systemId)
                throw MaintenanceBayRefuse("That container is not a ship maintenance bay.");

            await _statics.EnsureTypesAsync([source.TypeId], cancellationToken);
            var sourceType = _statics.Type(source.TypeId);
            var maintenanceCapacity = _statics.AttributeId("shipMaintenanceBayCapacity");
            if (sourceType is null || _statics.CategoryName(sourceType.CategoryId) != "Ship" ||
                maintenanceCapacity is not { } capacityAttribute ||
                sourceType.Attributes.GetValueOrDefault(capacityAttribute) <= 0)
                throw MaintenanceBayRefuse("That container is not a ship maintenance bay.");

            if (!await MayUseMaintenanceBayAsync(characterId, source, cancellationToken))
                throw MaintenanceBayRefuse("You do not have access to this ship maintenance bay.");

            CommandReply measure;
            try
            {
                measure = await _sim.CommandAsync((ulong)session.SessionId, SimCommands.Measure, [sourceId], cancellationToken);
            }
            catch (SimUnavailableException)
            {
                throw Unavailable();
            }
            if (!measure.Accepted || measure.Distance is not { } distance)
                throw MaintenanceBayRefuse("That container is not a ship maintenance bay.");
            if (distance > MaintenanceBayUseRange)
                throw MaintenanceBayRefuse("You are too far away from the ship maintenance bay.");

            if (await _items.GetAsync(itemId, cancellationToken) is not { } before || before.LocationId != sourceId)
                throw MaintenanceBayRefuse("That ship is not in the ship maintenance bay.");
            await _statics.EnsureTypesAsync([before.TypeId], cancellationToken);
            var type = _statics.Type(before.TypeId)
                ?? throw new InvalidOperationException($"SDE type {before.TypeId} for ship {itemId} is missing.");
            if (_statics.CategoryName(type.CategoryId) != "Ship")
                throw MaintenanceBayRefuse("Unable to launch this ship from the maintenance bay.");
            if (!before.Singleton || before.Quantity != 1)
                throw MaintenanceBayRefuse("Only assembled ships can be launched from a maintenance bay.");
            if (before.Flag != CharacterShips.ShipMaintenanceBayFlag)
                throw MaintenanceBayRefuse("That ship is not in the ship maintenance bay.");

            CommandReply pose;
            try
            {
                pose = await _sim.CommandAsync((ulong)session.SessionId, SimCommands.PositionOf, [sourceId], cancellationToken);
            }
            catch (SimUnavailableException)
            {
                throw Unavailable();
            }
            if (!pose.Accepted || pose.Position.Count < 3 || pose.Direction.Count < 3 ||
                pose.Position.Take(3).Any(value => !double.IsFinite(value)))
                throw MaintenanceBayRefuse("That container is not a ship maintenance bay.");

            var direction = pose.Direction.Take(3).ToArray();
            var directionLength = Math.Sqrt(direction.Sum(value => value * value));
            if (!double.IsFinite(directionLength) || directionLength <= 0)
                direction = [1, 0, 0];
            else
                direction = direction.Select(value => value / directionLength).ToArray();
            var radius = Math.Max(0, sourceType.Attributes.GetValueOrDefault(162, 0));
            var offset = radius + Math.Max(50, MaintenanceBayLaunchOffset);
            var position = new[]
            {
                pose.Position[0] + direction[0] * offset,
                pose.Position[1] + direction[1] * offset,
                pose.Position[2] + direction[2] * offset,
            };

            var owner = await _ships.CharacterAsync(before.OwnerId, cancellationToken);
            var spaceItem = new SpaceItem(
                before.Id, systemId, position, direction, Math.Max(1, type.Attributes.GetValueOrDefault(162, 0)),
                type.GroupId, activeShipId, owner?.Id ?? 0, owner?.CorporationId ?? 0,
                owner?.AllianceId ?? 0, owner?.FactionId ?? 0, long.MaxValue,
                Kind: SpaceItemKind.MaintenanceBayLaunchedShip,
                SourceContainerId: sourceId);

            await using (var connection = await _source.OpenConnectionAsync(cancellationToken))
            await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
            {
                var lockedSource = await _items.LockAndGetAsync(connection, transaction, sourceId, cancellationToken);
                if (lockedSource is null || lockedSource.LocationId != systemId ||
                    lockedSource.Flag != CharacterShips.InSpaceFlag || lockedSource.TypeId != source.TypeId)
                    throw MaintenanceBayRefuse("That container is not a ship maintenance bay.");
                await _items.LockContainerAsync(connection, transaction, sourceId, cancellationToken);
                var locked = await _items.LockAndGetAsync(connection, transaction, itemId, cancellationToken);
                if (locked is null || locked.LocationId != sourceId || locked.OwnerId != before.OwnerId ||
                    locked.TypeId != before.TypeId || locked.Quantity != 1 || !locked.Singleton ||
                    locked.Flag != CharacterShips.ShipMaintenanceBayFlag)
                    throw MaintenanceBayRefuse("That ship is not in the ship maintenance bay.");

                await _items.MoveAsync(connection, transaction, itemId, systemId, CharacterShips.InSpaceFlag, cancellationToken);
                await _spaceItems.SaveAsync(connection, transaction, spaceItem, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }

            var launchedItem = before with { LocationId = systemId, Flag = CharacterShips.InSpaceFlag };
            if (!await EnsureLaunchedItemPlacedAsync(launchedItem, spaceItem, type, cancellationToken))
                throw MaintenanceBayRefuse("Unable to launch this ship from the maintenance bay.");

            ShipNotifications.SendItemChange(session,
                new ItemChanged(launchedItem, sourceId, 0, launchedItem.Quantity), RowOf);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<bool> MayUseMaintenanceBayAsync(long characterId, Item source, CancellationToken cancellationToken)
    {
        if (source.OwnerId == characterId)
            return true;
        if (_shipConfigurations is null || await _ships.CharacterAsync(characterId, cancellationToken) is not { } caller)
            return false;

        var owner = await _ships.CharacterAsync(source.OwnerId, cancellationToken);
        var sameCorporation = caller.CorporationId > 0 &&
            (owner?.CorporationId == caller.CorporationId || source.OwnerId == caller.CorporationId);
        var sameFleet = false;
        if (owner is not null && FleetIdForCharacterAsync is { } fleetIdForCharacter)
        {
            var callerFleet = await fleetIdForCharacter(characterId, cancellationToken);
            var ownerFleet = await fleetIdForCharacter(owner.Id, cancellationToken);
            sameFleet = callerFleet is > 0 && callerFleet == ownerFleet;
        }
        var configuration = await _shipConfigurations.GetAsync(source.Id, cancellationToken);
        return (sameCorporation && configuration.SmbCorpAccess) ||
               (sameFleet && configuration.SmbFleetAccess);
    }

    /// <summary>Fleet membership is wired after fleet persistence is composed; a missing reader fails closed for non-owner users.</summary>
    public Func<long, CancellationToken, Task<long?>>? FleetIdForCharacterAsync { get; set; }

    private static MachoWrappedException MaintenanceBayRefuse(string message) =>
        MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr(message)));
}

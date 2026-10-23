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
    private readonly SemaphoreSlim[] _launchItemGates = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    /// <summary>
    /// Launches an assembled ship or a single container item from a visible wreck or container. Inventory and its durable space pose commit together;
    /// simulation placement follows the commit and can be retried from the persisted pose.
    /// </summary>
    public async Task LaunchShipOrContainerFromWreckOrContainerAsync(
        ClientSession session,
        long sourceId,
        long itemId,
        CancellationToken cancellationToken)
    {
        if (sourceId <= 0 || itemId <= 0 || sourceId == itemId ||
            session.CharacterId is not { } characterId ||
            session.GetInt("shipid") is not { } shipId || shipId <= 0 ||
            !_space.IsInSpace(session) ||
            _space.SolarSystemOf(session) is not { } systemId)
        {
            throw MachoErrors.ThrowUserError("FakeItemNotFound");
        }
        if (_space.IsInAbyssalRun(session))
        {
            throw LaunchRefuse("Ship or container launch is not available in Abyssal Deadspace.");
        }
        if (_source is null)
        {
            throw Unavailable();
        }

        var gate = _launchItemGates[(int)((ulong)itemId % (uint)_launchItemGates.Length)];
        await gate.WaitAsync(cancellationToken);
        try
        {
            var current = await _items.GetAsync(itemId, cancellationToken);
            if (current is { LocationId: var currentLocation, Flag: CharacterShips.InSpaceFlag } &&
                currentLocation == systemId &&
                await _spaceItems.GetAsync(itemId, cancellationToken) is { LauncherId: var launchedBy, Kind: var kind } existing &&
                launchedBy == shipId && existing.SourceContainerId == sourceId &&
                kind is SpaceItemKind.LaunchedShip or SpaceItemKind.ContainerOrWreck)
            {
                await _statics.EnsureTypesAsync([current.TypeId], cancellationToken);
                if (_statics.Type(current.TypeId) is not { } retryType ||
                    !await EnsureLaunchedItemPlacedAsync(current, existing, retryType, cancellationToken))
                {
                    throw LaunchRefuse("The item could not be placed in space. Try again.");
                }
                await NotifyLaunchedItemAsync(session, current, sourceId, cancellationToken);
                return;
            }

            var source = await OpenAsync(session, sourceId, cancellationToken)
                ?? throw MachoErrors.ThrowUserError("FakeItemNotFound");
            if (await _items.GetAsync(itemId, cancellationToken) is not { LocationId: var itemLocation } before ||
                itemLocation != sourceId)
            {
                throw MachoErrors.ThrowUserError("FakeItemNotFound");
            }

            await _statics.EnsureTypesAsync([before.TypeId, source.TypeId], cancellationToken);
            var type = _statics.Type(before.TypeId)
                ?? throw new InvalidOperationException($"SDE type {before.TypeId} for launchable item {itemId} is missing.");
            var sourceType = _statics.Type(source.TypeId)
                ?? throw new InvalidOperationException($"SDE type {source.TypeId} for source container {sourceId} is missing.");
            var isShip = string.Equals(_statics.CategoryName(type.CategoryId), "Ship", StringComparison.Ordinal);
            var isContainer = _statics.GroupName(type.GroupId).Contains("container", StringComparison.OrdinalIgnoreCase);
            if (!(isShip && before.Singleton && before.Quantity == 1) &&
                !(isContainer && before.Quantity == 1))
            {
                throw LaunchRefuse("Only an assembled ship or one container can be launched.");
            }
            var sourceRights = await _spaceItems.GetAsync(sourceId, cancellationToken);
            if (!await MayTakeFromAsync(characterId, source, cancellationToken, sourceRights, [itemId]))
            {
                throw MachoErrors.ThrowUserError("SafetyActivated");
            }

            CommandReply pose;
            try
            {
                pose = await _sim.CommandAsync(
                    (ulong)session.SessionId, SimCommands.PositionOf, [sourceId], cancellationToken);
            }
            catch (SimUnavailableException)
            {
                throw Unavailable();
            }
            if (!pose.Accepted || pose.Position.Count < 3 || pose.Direction.Count < 3 ||
                pose.Position.Take(3).Any(value => !double.IsFinite(value)) ||
                pose.Direction.Take(3).Any(value => !double.IsFinite(value)))
            {
                throw MachoErrors.ThrowUserError("FakeItemNotFound");
            }
            var directionLength = Math.Sqrt(pose.Direction.Take(3).Sum(value => value * value));
            if (!double.IsFinite(directionLength) || directionLength <= 0)
            {
                throw MachoErrors.ThrowUserError("FakeItemNotFound");
            }
            var direction = pose.Direction.Take(3).Select(value => value / directionLength).ToArray();
            var sourceRadius = Math.Max(0, sourceType.Attributes.GetValueOrDefault(162, 0));
            var offset = sourceRadius + 50;
            var position = new[]
            {
                pose.Position[0] + direction[0] * offset,
                pose.Position[1] + direction[1] * offset,
                pose.Position[2] + direction[2] * offset,
            };

            var owner = await _ships.CharacterAsync(before.OwnerId, cancellationToken);
            var rightsCharacter = owner?.Id ?? 0;
            var rightsCorporation = owner?.CorporationId ?? (before.OwnerId == characterId ? 0 : before.OwnerId);
            var spaceItem = new SpaceItem(
                before.Id, systemId, position, direction, Math.Max(1, type.Attributes.GetValueOrDefault(162, 0)),
                type.GroupId, shipId, rightsCharacter, rightsCorporation, owner?.AllianceId ?? 0, owner?.FactionId ?? 0, long.MaxValue,
                Kind: isShip ? SpaceItemKind.LaunchedShip : SpaceItemKind.ContainerOrWreck,
                SourceContainerId: sourceId);

            await using (var connection = await _source.OpenConnectionAsync(cancellationToken))
            await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
            {
                var lockedSource = await _items.LockAndGetAsync(connection, transaction, sourceId, cancellationToken);
                if (lockedSource is null || lockedSource.LocationId != systemId ||
                    lockedSource.Flag != CharacterShips.InSpaceFlag || lockedSource.TypeId != source.TypeId)
                {
                    throw MachoErrors.ThrowUserError("FakeItemNotFound");
                }
                await _items.LockContainerAsync(connection, transaction, sourceId, cancellationToken);
                var locked = await _items.LockAndGetAsync(connection, transaction, itemId, cancellationToken);
                if (locked is null || locked.LocationId != sourceId || locked.OwnerId != before.OwnerId ||
                    locked.TypeId != before.TypeId || locked.Quantity != 1 ||
                    (isShip && !locked.Singleton))
                {
                    throw MachoErrors.ThrowUserError("FakeItemNotFound");
                }
                await _items.MoveAsync(connection, transaction, itemId, systemId, CharacterShips.InSpaceFlag, cancellationToken);
                await _spaceItems.SaveAsync(connection, transaction, spaceItem, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }

            if (!await EnsureLaunchedItemPlacedAsync(
                    before with { LocationId = systemId, Flag = CharacterShips.InSpaceFlag }, spaceItem, type, cancellationToken))
            {
                throw LaunchRefuse("The item could not be placed in space. Try again.");
            }

            await NotifyLaunchedItemAsync(
                session, before with { LocationId = systemId, Flag = CharacterShips.InSpaceFlag },
                sourceId, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task NotifyLaunchedItemAsync(
        ClientSession session,
        Item launched,
        long sourceId,
        CancellationToken cancellationToken)
    {
        ShipNotifications.SendItemChange(
            session,
            new ItemChanged(launched, sourceId, 0, launched.Quantity),
            RowOf);
        if (await _items.GetAsync(sourceId, cancellationToken) is not { } source)
            return;

        var sourceEmpty = (await _items.InContainerAsync(sourceId, cancellationToken)).Count == 0;
        await TellChangeAsync((ulong)session.SessionId, sourceId, sourceEmpty);
        if (sourceEmpty && IsCan(source))
            await RemoveEmptyCanAsync(session, source, cancellationToken);
    }

    private async Task<bool> EnsureLaunchedItemPlacedAsync(
        Item item,
        SpaceItem spaceItem,
        TypeDef type,
        CancellationToken cancellationToken)
    {
        if (!_placed.TryAdd(item.Id, 0))
        {
            return true;
        }
        try
        {
            if (spaceItem.Kind == SpaceItemKind.LaunchedShip)
            {
                if (!await _space.PlaceLaunchedShipAsync(item.Id, item.OwnerId, spaceItem.SolarSystemId, type,
                        spaceItem.Position, spaceItem.Direction, cancellationToken))
                {
                    _placed.TryRemove(item.Id, out _);
                    return false;
                }
                return true;
            }

            var empty = (await _items.InContainerAsync(item.Id, cancellationToken)).Count == 0;
            _sim.PlaceWreck(new SimPlaceWreck(
                SimSceneKey.SolarSystem(spaceItem.SolarSystemId), item.Id, spaceItem.GroupId,
                new SimWreckKind(item.TypeId, item.CustomName ?? type.Name ?? "Cargo Container", type.GraphicId),
                spaceItem.Position, spaceItem.Direction, spaceItem.Radius, spaceItem.LauncherId,
                new SimCredit(spaceItem.CharacterId, spaceItem.CorporationId, spaceItem.AllianceId, spaceItem.WarFactionId), empty));
            return true;
        }
        catch (SimUnavailableException)
        {
            _placed.TryRemove(item.Id, out _);
            throw Unavailable();
        }
        catch
        {
            _placed.TryRemove(item.Id, out _);
            throw;
        }
    }

    private static MachoWrappedException LaunchRefuse(string message) =>
        MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr(message)));
}

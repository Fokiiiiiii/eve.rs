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
    private const int ShipMaintenanceArrayGroup = 363;
    private const int AssemblyArrayGroup = 397;
    private const long StarbaseCaretakerRole = 1L << 58;
    private const string PosMaintenanceArrayAccessDenied = "Your corporation roles or this Ship Maintenance Array's Use setting do not permit this operation.";
    private const string PosMaintenanceArrayOffline = "The Ship Maintenance Array must be anchored and online before it can be used.";
    private const string PosMaintenanceTowerUnavailable = "The Ship Maintenance Array requires its control tower to be anchored and online.";
    private const string PosMaintenanceArrayTooFar = "Move within 5,000 meters of the Ship Maintenance Array, or inside its control tower's shield, to use it.";

    /// <summary>Launches one assembled ship from a visible maintenance bay and records its space pose with the inventory move.</summary>
    public async Task LaunchFromContainerAsync(
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
            var hasMaintenanceBay = _statics.AttributeId("hasShipMaintenanceBay");
            var maintenanceCapacity = _statics.AttributeId("shipMaintenanceBayCapacity");
            var isShipMaintenanceBay = sourceType is not null && _statics.CategoryName(sourceType.CategoryId) == "Ship" &&
                (hasMaintenanceBay is { } hasAttribute && sourceType.Attributes.GetValueOrDefault(hasAttribute) > 0 ||
                    maintenanceCapacity is { } capacityAttribute && sourceType.Attributes.GetValueOrDefault(capacityAttribute) > 0);
            var sourceDeployment = _deployed is null ? null : await _deployed.GetAsync(sourceId, cancellationToken);
            var isPosMaintenanceArray = sourceType is { CategoryId: 23, GroupId: ShipMaintenanceArrayGroup or AssemblyArrayGroup } &&
                sourceDeployment is { Kind: StarbaseModuleKind };
            if (sourceType is null || !isShipMaintenanceBay && !isPosMaintenanceArray)
                throw MaintenanceBayRefuse("That container is not a ship maintenance bay.");

            var insideControlTowerShield = false;
            if (isShipMaintenanceBay && !await MayUseMaintenanceBayAsync(characterId, source, cancellationToken))
                throw MaintenanceBayRefuse("You do not have access to this ship maintenance bay.");
            if (isPosMaintenanceArray)
            {
                if (sourceDeployment is null || _deployed is null || _source is null)
                    throw MaintenanceBayRefuse(PosMaintenanceTowerUnavailable);
                var tower = await _deployed.GetAsync(sourceDeployment.ControlTowerId, cancellationToken);
                if (tower is null)
                    throw MaintenanceBayRefuse(PosMaintenanceTowerUnavailable);
                if (await PosMaintenanceArrayErrorAsync(session, sourceDeployment, tower, null, null, cancellationToken) is { } accessError)
                    throw MaintenanceBayRefuse(accessError);
                if (await _space.PositionOfAsync(session, sourceId, cancellationToken) is not { } sourcePosition ||
                    await _space.PositionOfAsync(session, activeShipId, cancellationToken) is not { } activePosition ||
                    await _space.PositionOfAsync(session, tower.DeployableId, cancellationToken) is not { } towerPosition ||
                    sourcePosition.SolarSystemId != systemId ||
                    activePosition.SolarSystemId != systemId || towerPosition.SolarSystemId != systemId)
                    throw MaintenanceBayRefuse("Unable to launch this ship from the maintenance bay.");
                await _statics.EnsureTypesAsync([tower.TypeId], cancellationToken);
                var shieldRadiusAttribute = _statics.AttributeId("shieldRadius") ?? 680;
                var shieldRadius = _statics.Type(tower.TypeId)?.Attributes.GetValueOrDefault(shieldRadiusAttribute) ?? 0;
                insideControlTowerShield = shieldRadius > 0 && MetersBetween(
                    [activePosition.X, activePosition.Y, activePosition.Z],
                    [towerPosition.X, towerPosition.Y, towerPosition.Z]) < shieldRadius;
            }

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
            var maxArrayDistanceAttribute = _statics.AttributeId("maxConfigureDistance") ?? 715;
            var maximumDistance = isPosMaintenanceArray
                ? Math.Max(0, sourceType.Attributes.GetValueOrDefault(maxArrayDistanceAttribute, 2_500))
                : MaintenanceBayUseRange;
            if (!insideControlTowerShield && distance > maximumDistance)
                throw MaintenanceBayRefuse(isPosMaintenanceArray ? PosMaintenanceArrayTooFar : "You are too far away from the ship maintenance bay.");

            if (await _items.GetAsync(itemId, cancellationToken) is not { } before || before.LocationId != sourceId)
                throw MaintenanceBayRefuse("That ship is not in the ship maintenance bay.");
            await _statics.EnsureTypesAsync([before.TypeId], cancellationToken);
            var type = _statics.Type(before.TypeId)
                ?? throw new InvalidOperationException($"SDE type {before.TypeId} for ship {itemId} is missing.");
            if (_statics.CategoryName(type.CategoryId) != "Ship")
                throw MaintenanceBayRefuse("Unable to launch this ship from the maintenance bay.");
            if (!before.Singleton || before.Quantity != 1)
                throw MaintenanceBayRefuse("Only assembled ships can be launched from a maintenance bay.");
            if (isShipMaintenanceBay && before.Flag != CharacterShips.ShipMaintenanceBayFlag)
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
                if (isPosMaintenanceArray)
                {
                    var lockedModule = await _deployed!.LockAndGetAsync(connection, transaction, sourceId, cancellationToken);
                    var lockedTower = lockedModule is null ? null : await _deployed.LockAndGetAsync(
                        connection, transaction, lockedModule.ControlTowerId, cancellationToken);
                    if (lockedModule is null || lockedTower is null)
                        throw MaintenanceBayRefuse(PosMaintenanceTowerUnavailable);
                    if (await PosMaintenanceArrayErrorAsync(session, lockedModule, lockedTower,
                        connection, transaction, cancellationToken) is { } accessError)
                        throw MaintenanceBayRefuse(accessError);
                }
                await _items.LockContainerAsync(connection, transaction, sourceId, cancellationToken);
                var locked = await _items.LockAndGetAsync(connection, transaction, itemId, cancellationToken);
                if (locked is null || locked.LocationId != sourceId || locked.OwnerId != before.OwnerId ||
                    locked.TypeId != before.TypeId || locked.Quantity != 1 || !locked.Singleton ||
                    isShipMaintenanceBay && locked.Flag != CharacterShips.ShipMaintenanceBayFlag)
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

    /// <summary>
    /// Atomically stores the active hull and takes the selected assembled ship from a maintenance bay into space, then hands the committed swap to the pilot session.
    /// </summary>
    public async Task<PyValue> BoardStoredShipAsync(ClientSession session, long sourceId, long shipId, CancellationToken cancellationToken)
    {
        if (sourceId <= 0 || shipId <= 0 || sourceId == shipId || session.CharacterId is not { } characterId ||
            session.GetInt("shipid") is not { } activeShipId || activeShipId <= 0 || !_space.IsInSpace(session) ||
            _space.SolarSystemOf(session) is not { } systemId || _source is null)
            return PyNone.Instance;
        if (sourceId == activeShipId)
            throw MaintenanceBayRefuse("Unable to board this ship from the maintenance bay.");
        if (_space.IsWarping(session))
            throw MachoErrors.ThrowUserError("ShipInWarp");
        if (_space.IsInAbyssalRun(session))
            throw MaintenanceBayRefuse("You cannot eject, board, launch, or store ships inside Abyssal Deadspace.");
        _space.RequirePublicMoveSettled(session);
        if (_space.LifecycleBlockers is not { } readBlockers)
            return PyNone.Instance;
        var blockers = await readBlockers(ShipLifecycleAction.Board, session, shipId, cancellationToken);
        if (blockers.Count != 0)
            return new PyList([.. blockers.Select(label => new PyStr(label))]);

        var source = await _items.GetAsync(sourceId, cancellationToken);
        if (source is not { LocationId: var sourceLocation, Flag: CharacterShips.InSpaceFlag } || sourceLocation != systemId)
            throw MaintenanceBayRefuse("That container is not a ship maintenance bay.");
        await _statics.EnsureTypesAsync([source.TypeId], cancellationToken);
        var sourceType = _statics.Type(source.TypeId);
        var hasMaintenanceBay = _statics.AttributeId("hasShipMaintenanceBay");
        var maintenanceCapacity = _statics.AttributeId("shipMaintenanceBayCapacity");
        var isShipBay = sourceType is not null && _statics.CategoryName(sourceType.CategoryId) == "Ship" &&
            (hasMaintenanceBay is { } has && sourceType.Attributes.GetValueOrDefault(has) > 0 ||
                maintenanceCapacity is { } capacity && sourceType.Attributes.GetValueOrDefault(capacity) > 0);
        var deployment = _deployed is null ? null : await _deployed.GetAsync(sourceId, cancellationToken);
        var isPosArray = sourceType is { CategoryId: 23, GroupId: ShipMaintenanceArrayGroup or AssemblyArrayGroup } &&
            deployment is { Kind: StarbaseModuleKind };
        if (sourceType is null || !isShipBay && !isPosArray)
            throw MaintenanceBayRefuse("That container is not a ship maintenance bay.");
        if (isShipBay && !await MayUseMaintenanceBayAsync(characterId, source, cancellationToken))
            throw MaintenanceBayRefuse("You do not have access to this ship maintenance bay.");

        DeployedRecord? tower = null;
        var currentShip = await _items.GetAsync(activeShipId, cancellationToken);
        if (currentShip is null)
            throw MaintenanceBayRefuse("Unable to board this ship from the maintenance bay.");
        var currentShipNeedsPut = currentShip.TypeId != CharacterShips.CapsuleTypeId;
        if (isPosArray)
        {
            if (deployment is null || _deployed is null ||
                await _deployed.GetAsync(deployment.ControlTowerId, cancellationToken) is not { } foundTower)
                throw MaintenanceBayRefuse(PosMaintenanceTowerUnavailable);
            tower = foundTower;
            if (await PosMaintenanceArrayErrorAsync(session, deployment, tower, null, null, cancellationToken,
                requireViewPut: currentShipNeedsPut) is { } accessError)
                throw MaintenanceBayRefuse(accessError);
        }

        CommandReply measure;
        CommandReply pose;
        try
        {
            measure = await _sim.CommandAsync((ulong)session.SessionId, SimCommands.Measure, [sourceId], cancellationToken);
            pose = await _sim.CommandAsync((ulong)session.SessionId, SimCommands.PositionOf, [sourceId], cancellationToken);
        }
        catch (SimUnavailableException)
        {
            throw Unavailable();
        }
        if (!measure.Accepted || measure.Distance is not { } distance || !pose.Accepted || pose.Position.Count < 3 || pose.Direction.Count < 3)
            throw MaintenanceBayRefuse("That container is not a ship maintenance bay.");

        var withinTowerShield = false;
        if (isPosArray && tower is not null)
        {
            var activePosition = await _space.PositionOfAsync(session, activeShipId, cancellationToken);
            var towerPosition = await _space.PositionOfAsync(session, tower.DeployableId, cancellationToken);
            if (activePosition is null || towerPosition is null || activePosition.Value.SolarSystemId != systemId || towerPosition.Value.SolarSystemId != systemId)
                throw MaintenanceBayRefuse("Unable to board the ship from this maintenance array.");
            await _statics.EnsureTypesAsync([tower.TypeId], cancellationToken);
            var shieldRadiusAttribute = _statics.AttributeId("shieldRadius") ?? 680;
            var shieldRadius = _statics.Type(tower.TypeId)?.Attributes.GetValueOrDefault(shieldRadiusAttribute) ?? 0;
            withinTowerShield = shieldRadius > 0 && MetersBetween(
                [activePosition.Value.X, activePosition.Value.Y, activePosition.Value.Z],
                [towerPosition.Value.X, towerPosition.Value.Y, towerPosition.Value.Z]) < shieldRadius;
        }
        var maxConfigureDistanceAttribute = _statics.AttributeId("maxConfigureDistance") ?? 715;
        var maximumDistance = isPosArray
            ? Math.Max(0, sourceType.Attributes.GetValueOrDefault(maxConfigureDistanceAttribute, 2_500))
            : MaintenanceBayUseRange;
        if (!withinTowerShield && distance > maximumDistance)
            throw MaintenanceBayRefuse(isPosArray ? PosMaintenanceArrayTooFar : "You are too far away from the ship maintenance bay.");

        var direction = pose.Direction.Take(3).ToArray();
        var length = Math.Sqrt(direction.Sum(value => value * value));
        if (!double.IsFinite(length) || length <= 0)
            direction = [1, 0, 0];
        else
            direction = direction.Select(value => value / length).ToArray();
        var offset = Math.Max(0, sourceType.Attributes.GetValueOrDefault(162, 0)) + MaintenanceBayLaunchOffset;
        var position = new[]
        {
            pose.Position[0] + direction[0] * offset,
            pose.Position[1] + direction[1] * offset,
            pose.Position[2] + direction[2] * offset,
        };

        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task<bool>> authorizeSource = isShipBay
            ? async (connection, transaction, token) =>
            {
                var lockedSource = await _items.LockAndGetAsync(connection, transaction, sourceId, token);
                return lockedSource is not null && lockedSource.TypeId == source.TypeId &&
                    lockedSource.LocationId == systemId && lockedSource.Flag == CharacterShips.InSpaceFlag &&
                    await MayUseMaintenanceBayAsync(characterId, lockedSource, token, connection, transaction);
            }
            : async (connection, transaction, token) =>
            {
                if (_deployed is null || deployment is null)
                    return false;
                var lockedModule = await _deployed.LockAndGetAsync(connection, transaction, sourceId, token);
                if (lockedModule is null || lockedModule.Kind != StarbaseModuleKind ||
                    lockedModule.TypeId != source.TypeId || lockedModule.SolarSystemId != systemId ||
                    lockedModule.ControlTowerId != deployment.ControlTowerId)
                    return false;
                var lockedTower = await _deployed.LockAndGetAsync(connection, transaction, lockedModule.ControlTowerId, token);
                return lockedTower is not null &&
                    await PosMaintenanceArrayErrorAsync(session, lockedModule, lockedTower, connection, transaction, token,
                        requireViewPut: currentShipNeedsPut) is null;
            };
        var storedBoarding = await _ships.BoardStoredShipFromMaintenanceBayAsync(
            characterId, systemId, activeShipId, sourceId, shipId, isShipBay, mayTakeForeignShip: true,
            cancellationToken, authorizeSource);
        if (storedBoarding is null)
            throw MaintenanceBayRefuse("Unable to board this ship from the maintenance bay.");
        return await _space.ApplyStoredShipBoardingAsync(session, storedBoarding, position, direction, cancellationToken);
    }

    private async Task<bool> MayUseMaintenanceBayAsync(
        long characterId, Item source, CancellationToken cancellationToken,
        NpgsqlConnection? connection = null, NpgsqlTransaction? transaction = null)
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
        var configuration = connection is not null && transaction is not null
            ? await _shipConfigurations.GetAsync(connection, transaction, source.Id, cancellationToken)
            : await _shipConfigurations.GetAsync(source.Id, cancellationToken);
        return (sameCorporation && configuration.SmbCorpAccess) ||
               (sameFleet && configuration.SmbFleetAccess);
    }

    private async Task<string?> PosMaintenanceArrayErrorAsync(
        ClientSession session,
        DeployedRecord module,
        DeployedRecord tower,
        NpgsqlConnection? connection,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken,
        bool requireViewPut = false)
    {
        if (_deployed is null || _source is null || module.Kind != StarbaseModuleKind || tower.Kind != ControlTowerKind ||
            module.ControlTowerId != tower.DeployableId || module.SolarSystemId != tower.SolarSystemId ||
            module.OwnerId != tower.OwnerId || module.CorporationId != tower.CorporationId)
            return PosMaintenanceTowerUnavailable;

        var moduleState = connection is null
            ? await _deployed.TowerStateAsync(module.DeployableId, cancellationToken)
            : await _deployed.TowerStateAsync(connection, transaction, module.DeployableId, cancellationToken);
        if (module.ActivateAtMs == NeverAnchored || module.ActivateAtMs > _nowUnixMs() || moduleState != TowerOnline)
            return PosMaintenanceArrayOffline;
        var towerState = connection is null
            ? await _deployed.TowerStateAsync(tower.DeployableId, cancellationToken)
            : await _deployed.TowerStateAsync(connection, transaction, tower.DeployableId, cancellationToken);
        if (tower.ActivateAtMs == NeverAnchored || tower.ActivateAtMs > _nowUnixMs() || towerState != TowerOnline)
            return PosMaintenanceTowerUnavailable;

        NpgsqlConnection? ownedConnection = null;
        try
        {
            var accessConnection = connection ?? (ownedConnection = await _source.OpenConnectionAsync(cancellationToken));
            var raw = await _deployed.TowerConfigurationAsync(accessConnection, transaction, tower.DeployableId, cancellationToken);
            var configuration = Normalize(raw is null ? new PosMgrTowerConfiguration() :
                System.Text.Json.JsonSerializer.Deserialize<PosMgrTowerConfiguration>(raw, PosMgrJson) ?? new PosMgrTowerConfiguration());
            var usage = configuration.UsageFlags.FirstOrDefault(row => row.StructureId == module.DeployableId)
                ?? new TowerUsage(module.DeployableId);
            var corporationId = session.GetInt("corpid") ?? session.GetInt("corporationid") ?? 0;
            var allianceId = session.GetInt("allianceid") ?? session.GetInt("allianceID") ?? 0;
            var roles = session.GetInt("corprole") ?? 0;
            var sameCorporation = corporationId > 0 && tower.OwnerId == corporationId && tower.CorporationId == corporationId;
            var sameAlliance = tower.AllianceId > 0 && allianceId == tower.AllianceId;
            var isCeo = session.Get("isCEO") switch
            {
                PyBool value => value.Value,
                PyInt value => value.Value != 0,
                PyLong value => value.Value != 0,
                _ => false,
            };
            var canConfigure = sameCorporation && (isCeo || (roles & (DirectorRole | StarbaseConfigRole)) != 0);
            return canConfigure ||
                HasPosUsagePermission(usage.Use, sameCorporation, sameAlliance, roles) &&
                HasPosUsagePermission(usage.ViewPutTake, sameCorporation, sameAlliance, roles) &&
                (!requireViewPut || HasPosUsagePermission(usage.ViewPut, sameCorporation, sameAlliance, roles))
                    ? null
                    : PosMaintenanceArrayAccessDenied;
        }
        finally
        {
            if (ownedConnection is not null)
                await ownedConnection.DisposeAsync();
        }
    }

    private static bool HasPosUsagePermission(int permission, bool sameCorporation, bool sameAlliance, long roles) => permission switch
    {
        1 => sameCorporation,
        2 => sameCorporation || sameAlliance,
        3 => sameCorporation && (roles & StarbaseCaretakerRole) != 0,
        _ => false,
    };

    /// <summary>Fleet membership is wired after fleet persistence is composed; a missing reader fails closed for non-owner users.</summary>
    public Func<long, CancellationToken, Task<long?>>? FleetIdForCharacterAsync { get; set; }

    private static MachoWrappedException MaintenanceBayRefuse(string message) =>
        MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr(message)));
}

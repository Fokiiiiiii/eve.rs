using System.Globalization;
using System.Text.Json;
using Eve.Dogma;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Scheduler;
using Eve.Services;
using Eve.Ships;
using Eve.Space;
using Eve.Structures;
using Npgsql;

namespace Eve.Services.Space;

/// <summary>
/// period is one hour: the job pays the bay's fuel blocks of its race for the period, and an empty bay takes it offline until the next period finds fuel.
/// The unanchoring is asked by its corporation while the tower is offline; after the SDE's unanchoring delay the tower leaves space, and its item is not
/// deleted: it is dropped at its place as an unanchor drop (the structure rule, <see cref="PlaceUnanchorDropAsync"/>), where the corporation can scoop it.
/// </summary>
public sealed partial class SpaceContainers
{
    /// <summary>The durable job of a control tower's fuel period (keyed <c>tower:{id}</c>).</summary>
    public const string ControlTowerFuelJob = "space.control_tower.fuel_hour";

    /// <summary>The durable job that completes an explicit POS control-tower online request.</summary>
    public const string ControlTowerOnlineJob = "space.control_tower.online";

    /// <summary>The durable job that completes an associated POS module's onlining transition.</summary>
    public const string StarbaseModuleOnlineJob = "space.starbase_module.online";

    /// <summary>The durable job that completes a control tower's unanchoring (keyed <c>tower:{id}</c>).</summary>
    public const string ControlTowerUnanchorJob = "space.control_tower.unanchor";
    public const string ControlTowerEligibilityChangedTopic = "space.control_tower.eligibility_changed";
    public const string ControlTowerFuelInventoryChangedTopic = "space.control_tower.fuel_inventory_changed";
    public sealed record ControlTowerEligibilityChanged(long CorporationId);

    private sealed record TowerResources(
        int FuelTypeId, long FuelQuantity, int CharterTypeId, long CharterQuantity,
        bool CharterRequired, bool CharterAvailable, long PeriodMs);

    private sealed record MoonAnchorPoint(long MoonId, double X, double Y, double Z, double WarpX, double WarpY, double WarpZ);
    private sealed record NearestMoonPoint(MoonAnchorPoint Moon, double Distance);

    private const int AnchoringSkillTypeId = 11_584;
    private const int MoonAnchorDistanceAttribute = 711;
    private const long StarbaseConfigRole = 9_007_199_254_740_992L;
    private const long DirectorRole = 1;
    private const double MaxControlTowerInteractionDistance = 30_000;
    private const double MinMoonWarpRadius = 90_000;
    private const int StructureUnanchored = 0;
    private const int StructureAnchored = 1;
    private const int StructureOnlining = 2;
    private const int StructureOnline = 4;

    public async Task PublishControlTowerAnchoredAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Eve.Events.EventBus events, long towerId, CancellationToken cancellationToken = default)
    {
        if (_deployed is null || await _deployed.GetAsync(towerId, cancellationToken) is not { Kind: ControlTowerKind } record ||
            record.ActivateAtMs > _nowUnixMs() || record.CorporationId <= 0) return;
        await events.PublishInTransactionAsync(connection, transaction, ControlTowerEligibilityChangedTopic,
            new ControlTowerEligibilityChanged(record.CorporationId), $"tower:{towerId}", cancellationToken);
    }

    /// <summary>The fuel state of a control tower: its period was paid.</summary>
    public const string TowerOnline = "online";

    /// <summary>The explicit online operation is in progress.</summary>
    public const string TowerOnlining = "onlining";

    /// <summary>The fuel state of a control tower whose last period was not paid (or that is not yet fuelled).</summary>
    public const string TowerOffline = "offline";

    /// <summary>A dropped control tower has not received posMgr.AnchorStructure yet.</summary>
    public const string TowerUnanchored = "unanchored";

    /// <summary>posMgr.AnchorStructure started the SDE anchoring transition.</summary>
    public const string TowerAnchoring = "anchoring";

    /// <summary>The fuel state of a control tower whose unanchoring has been asked: it burns no more fuel.</summary>
    public const string TowerUnanchoring = "unanchoring";

    /// <summary>The payload of the fuel job: the tower whose period it pays.</summary>
    public sealed record ControlTowerFuelDue(long DeployableId);

    /// <summary>The tower whose explicit online transition is complete.</summary>
    public sealed record ControlTowerOnlineDue(long DeployableId, long StartedAtMs);

    /// <summary>The module and start time of an associated POS module's onlining transition.</summary>
    public sealed record StarbaseModuleOnlineDue(long DeployableId, long StartedAtMs);

    /// <summary>The payload of the unanchor job: the tower that leaves space.</summary>
    public sealed record ControlTowerUnanchorDue(long DeployableId);

    /// <summary>
    /// Pays one fuel period in the job's transaction. A depleted tower goes offline; it does not automatically come back online when fuel is added.
    /// </summary>
    public async Task<bool> ControlTowerFuelAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long towerId, long periodStartMs, CancellationToken cancellationToken = default)
    {
        if (_deployed is null || _scheduler is null || await _deployed.LockAndGetAsync(connection, transaction, towerId, cancellationToken) is not { Kind: ControlTowerKind } record)
        {
            return false;
        }
        if (await _deployed.TowerStateAsync(connection, transaction, towerId, cancellationToken) != TowerOnline)
        {
            return false;
        }
        if (record.ActivateAtMs == NeverAnchored || record.ActivateAtMs > _nowUnixMs())
        {
            await _deployed.SetTowerStateAsync(connection, transaction, towerId, TowerOffline, cancellationToken);
            await OfflineStarbaseModulesAsync(connection, transaction, towerId, cancellationToken);
            return true;
        }
        await _statics.EnsureTypesAsync([record.TypeId], cancellationToken);
        var type = _statics.Type(record.TypeId)
            ?? throw new InvalidOperationException($"SDE type {record.TypeId} for control tower {towerId} is missing.");
        var resources = await ReadTowerResourcesAsync(connection, transaction, record.SolarSystemId, type, cancellationToken)
            ?? throw new InvalidOperationException($"SDE fuel requirements for control tower {towerId} are missing.");
        var payment = resources.CharterAvailable
            ? await _ships.TakeTowerResourcesWithChangesAsync(connection, transaction, towerId, resources.FuelTypeId, resources.FuelQuantity,
                resources.CharterTypeId, resources.CharterRequired ? resources.CharterQuantity : 0, cancellationToken)
            : new TowerResourcePayment(false, []);
        await _deployed.SetTowerStateAsync(connection, transaction, towerId, payment.Paid ? TowerOnline : TowerOffline, cancellationToken);
        if (!payment.Paid)
            await UpdateProductionScheduleForTowerStateAsync(connection, transaction, record, false, cancellationToken);
        if (!payment.Paid)
            await OfflineStarbaseModulesAsync(connection, transaction, towerId, cancellationToken);
        if (!payment.Paid)
            return true;
        var published = await PublishControlTowerFuelInventoryChangedAsync(connection, transaction, record, periodStartMs, payment.Changes, cancellationToken);
        await _scheduler.ScheduleAsync(connection, transaction, new JobSpec(ControlTowerFuelJob, periodStartMs + resources.PeriodMs)
        {
            Key = $"tower:{towerId}",
            Payload = new ControlTowerFuelDue(towerId),
            OnDuplicate = OnDuplicate.Replace,
        }, cancellationToken);
        return published;
    }

    /// <summary>Completes the explicit online transition, pays its first period and schedules upkeep in one durable-job transaction.</summary>
    public async Task<bool> CompleteControlTowerOnlineAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long towerId, long onlineStartedAtMs,
        CancellationToken cancellationToken = default)
    {
        if (_deployed is null || _scheduler is null ||
            await _deployed.LockAndGetAsync(connection, transaction, towerId, cancellationToken) is not { Kind: ControlTowerKind } record ||
            await _deployed.TowerStateAsync(connection, transaction, towerId, cancellationToken) != TowerOnlining)
            return false;
        if (record.ActivateAtMs == NeverAnchored || record.ActivateAtMs > _nowUnixMs())
        {
            await _deployed.SetTowerStateAsync(connection, transaction, towerId, TowerOffline, cancellationToken);
            await OfflineStarbaseModulesAsync(connection, transaction, towerId, cancellationToken);
            return true;
        }
        await _statics.EnsureTypesAsync([record.TypeId], cancellationToken);
        var type = _statics.Type(record.TypeId)
            ?? throw new InvalidOperationException($"SDE type {record.TypeId} for control tower {towerId} is missing.");
        var resources = await ReadTowerResourcesAsync(connection, transaction, record.SolarSystemId, type, cancellationToken)
            ?? throw new InvalidOperationException($"SDE fuel requirements for control tower {towerId} are missing.");
        var payment = resources.CharterAvailable
            ? await _ships.TakeTowerResourcesWithChangesAsync(connection, transaction, towerId, resources.FuelTypeId, resources.FuelQuantity,
                resources.CharterTypeId, resources.CharterRequired ? resources.CharterQuantity : 0, cancellationToken)
            : new TowerResourcePayment(false, []);
        await _deployed.SetTowerStateAsync(connection, transaction, towerId, payment.Paid ? TowerOnline : TowerOffline, cancellationToken);
        await UpdateProductionScheduleForTowerStateAsync(connection, transaction, record, payment.Paid, cancellationToken);
        if (!payment.Paid)
            await OfflineStarbaseModulesAsync(connection, transaction, towerId, cancellationToken);
        var published = false;
        if (payment.Paid)
        {
            published = await PublishControlTowerFuelInventoryChangedAsync(connection, transaction, record, onlineStartedAtMs, payment.Changes, cancellationToken);
            await _scheduler.ScheduleAsync(connection, transaction, new JobSpec(ControlTowerFuelJob, _nowUnixMs() + resources.PeriodMs)
            {
                Key = $"tower:{towerId}",
                Payload = new ControlTowerFuelDue(towerId),
                OnDuplicate = OnDuplicate.Replace,
            }, cancellationToken);
        }
        return published || !payment.Paid;
    }

    private async Task<bool> PublishControlTowerFuelInventoryChangedAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, DeployedRecord record, long periodKeyMs,
        IReadOnlyList<ItemChanged> changes, CancellationToken cancellationToken)
    {
        if (changes.Count == 0)
            return false;
        await _events.PublishInTransactionAsync(connection, transaction, ControlTowerFuelInventoryChangedTopic,
            new ControlTowerFuelInventoryChanged(record.CorporationId, [.. changes.Select(change => new ControlTowerFuelInventoryChange(
                change.After, change.PreviousLocationId, change.PreviousFlag, change.PreviousQuantity, change.PreviousOwnerId))]),
            $"tower:{record.DeployableId}:fuel:{periodKeyMs}", cancellationToken);
        return true;
    }

    /// <summary>
    /// posMgr.AnchorStructure: a dropped tower remains unanchored until this request. It is snapped to its nearest valid moon placement, then anchored by the
    /// SDE delay. The client sends a zero position for control towers; placement is derived from the static moon data as in EveJS's starbase anchoring policy.
    /// </summary>
    public async Task AnchorControlTowerAsync(ClientSession session, long towerId, IReadOnlyList<double> requestedPosition, CancellationToken cancellationToken = default)
    {
        if (_deployed is not null && await _deployed.GetAsync(towerId, cancellationToken) is { Kind: StarbaseModuleKind })
        {
            await AnchorStarbaseModuleAsync(session, towerId, requestedPosition, cancellationToken);
            return;
        }
        _ = requestedPosition;
        if (_deployed is null || _scheduler is null || _source is null)
            throw Refuse("Control tower anchoring is not available on this server.");
        if (session.CharacterId is not { } characterId || session.GetInt("shipid") is not > 0 || !_space.IsInSpace(session))
            throw Refuse("A control tower can only be anchored by a pilot in space.");

        // the pilot checks and the simulation's position come before any row lock
        if (await _deployed.GetAsync(towerId, cancellationToken) is not { Kind: ControlTowerKind } preview)
            throw Refuse("That control tower is not in space.");
        var unlockedState = await _deployed.TowerStateAsync(towerId, cancellationToken);
        var sight = await PreflightControlTowerAccessAsync(session, preview, requireAnchoringSkill: true, cancellationToken,
            requireInteractionDistance: unlockedState != TowerAnchoring, requireConfigurationRole: false, operation: "anchor");

        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (await _deployed.LockAndGetAsync(connection, transaction, towerId, cancellationToken) is not { Kind: ControlTowerKind } record)
            throw Refuse("That control tower is not in space.");
        var state = await _deployed.TowerStateAsync(connection, transaction, towerId, cancellationToken);
        await RequireTowerAccessUnderLockAsync(session, record, sight, requireConfigurationRole: false, operation: "anchor",
            connection, transaction, cancellationToken);
        if (state == TowerAnchoring)
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }
        if (record.ActivateAtMs != NeverAnchored || state != TowerUnanchored)
            throw Refuse("That control tower is not fully unanchored.");

        await _statics.EnsureTypesAsync([record.TypeId], cancellationToken);
        var type = _statics.Type(record.TypeId)
            ?? throw new InvalidOperationException($"SDE type {record.TypeId} for control tower {towerId} is missing.");
        var resources = await ReadTowerResourcesAsync(connection, transaction, record.SolarSystemId, type, cancellationToken)
            ?? throw Refuse("The control tower requirements are not available from static data.");
        if (!resources.CharterAvailable)
            throw Refuse("This system's sovereign faction does not issue a Starbase Charter, so the control tower cannot be anchored here.");
        if (resources.CharterRequired && !await HasResourceAsync(session.GetInt("shipid")!.Value, resources.CharterTypeId,
            resources.CharterQuantity, [characterId, record.OwnerId], cancellationToken))
            throw Refuse("The control tower requires the system's Starbase Charter in the pilot's cargo hold.");

        var moons = await ReadMoonAnchorPointsAsync(connection, transaction, record.SolarSystemId, cancellationToken);
        var nearest = NearestMoon(moons, record.Position);
        var maximumDistance = type.Attributes.GetValueOrDefault(711);
        if (nearest is null)
            throw Refuse("A control tower must be anchored near a moon.");
        if (maximumDistance <= 0 || nearest.Distance > maximumDistance)
            throw Refuse("The control tower is too far from the nearest moon to anchor.");
        foreach (var other in await _deployed.InSystemAsync(record.SolarSystemId, cancellationToken))
        {
            if (other.DeployableId == towerId || other.Kind != ControlTowerKind || other.ActivateAtMs == NeverAnchored)
                continue;
            var otherState = await _deployed.TowerStateAsync(other.DeployableId, cancellationToken);
            if (otherState is TowerUnanchored or TowerUnanchoring)
                continue;
            if (NearestMoon(moons, other.Position)?.Moon.MoonId == nearest.Moon.MoonId)
                throw Refuse("Another control tower is already anchored or anchoring at that moon.");
        }

        var startedAtMs = _nowUnixMs();
        var anchorDelayMs = (long)Math.Max(0, type.Attributes.GetValueOrDefault(556));
        var completionMs = checked(startedAtMs + anchorDelayMs);
        var anchorPosition = MoonAnchorPosition(nearest.Moon);
        await _deployed.SaveAsync(connection, transaction, record with { Position = anchorPosition, ActivateAtMs = completionMs }, cancellationToken);
        await _deployed.SetTowerStateAsync(connection, transaction, towerId, TowerAnchoring, startedAtMs, cancellationToken);
        await _scheduler.ScheduleAsync(connection, transaction, new JobSpec(AnchorJob, completionMs)
        {
            Key = $"deployable:{towerId}",
            Payload = new DisruptorAnchorDue(towerId),
            OnDuplicate = OnDuplicate.Replace,
        }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        _scheduler.Wake();

        // The legacy server moves the POS ball to the selected moon point as anchoring begins. Removing and placing the same ID preserves the sim's existing scene owner.
        _sim.RemoveDeployed(towerId);
        _sim.PlaceControlTower(new SimPlaceControlTower(record.DeployableId, record.SolarSystemId, record.Name, record.TypeId,
            record.GroupId, anchorPosition, record.Radius, false, record.OwnerId, record.CorporationId, record.AllianceId, record.ShipId));
        SetControlTowerSlimState(towerId, StructureAnchored, startedAtMs);
    }

    private async Task AnchorStarbaseModuleAsync(ClientSession session, long moduleId, IReadOnlyList<double> requestedPosition,
        CancellationToken cancellationToken)
    {
        if (_deployed is null || _scheduler is null || _source is null || requestedPosition.Count != 3 || requestedPosition.Any(value => !double.IsFinite(value)))
            throw Refuse("Starbase structure anchoring is not available on this server.");
        if (session.CharacterId is not { } characterId || session.GetInt("shipid") is not > 0 || !_space.IsInSpace(session))
            throw Refuse("A starbase structure can only be anchored by a pilot in space.");

        var now = _nowUnixMs();
        // the module is read unlocked only to choose its tower; then the tower row is locked before the module row, the order every starbase path takes,
        // and the module's state is checked under its own lock
        if (await _deployed.GetAsync(moduleId, cancellationToken) is not { Kind: StarbaseModuleKind } reference)
            throw Refuse("That starbase structure is not in space.");
        var unlockedState = await _deployed.TowerStateAsync(moduleId, cancellationToken);
        if (unlockedState is TowerAnchoring or TowerOffline or TowerOnline or TowerOnlining)
            return;
        if (unlockedState != TowerUnanchored)
            throw Refuse("That starbase structure is not fully unanchored.");

        await _statics.EnsureTypesAsync([reference.TypeId], cancellationToken);
        var moduleType = _statics.Type(reference.TypeId)
            ?? throw new InvalidOperationException($"SDE type {reference.TypeId} for starbase structure {moduleId} is missing.");
        if (await _ships.MissingSkillAsync(characterId, new Dictionary<int, double> { [182] = AnchoringSkillTypeId, [277] = 1 }, cancellationToken) is not null)
            throw Refuse("Anchoring skill I is required to anchor a starbase structure.");

        var candidates = new List<(DeployedRecord Tower, double Distance)>();
        foreach (var candidate in await _deployed.InSystemAsync(reference.SolarSystemId, cancellationToken))
        {
            if (candidate.Kind != ControlTowerKind || candidate.CorporationId != reference.CorporationId ||
                candidate.OwnerId != reference.OwnerId || candidate.ActivateAtMs == NeverAnchored || candidate.ActivateAtMs > now)
                continue;
            var towerState = await _deployed.TowerStateAsync(candidate.DeployableId, cancellationToken);
            if (towerState is TowerUnanchored or TowerAnchoring or TowerUnanchoring)
                continue;
            var distance = Distance(requestedPosition, candidate.Position[0], candidate.Position[1], candidate.Position[2]);
            candidates.Add((candidate, distance));
        }
        var maximumDistanceAttribute = _statics.AttributeId("maxStructureDistance") ?? 650;
        var inRange = new List<DeployedRecord>();
        foreach (var candidate in candidates.OrderBy(entry => entry.Distance).ThenBy(entry => entry.Tower.DeployableId))
        {
            await _statics.EnsureTypesAsync([candidate.Tower.TypeId], cancellationToken);
            var towerType = _statics.Type(candidate.Tower.TypeId);
            var maximumDistance = towerType?.Attributes.GetValueOrDefault(maximumDistanceAttribute) ?? 300_000;
            if (maximumDistance <= 0 || candidate.Distance > maximumDistance)
                continue;
            inRange.Add(candidate.Tower);
        }

        if (inRange.Count == 0)
            throw Refuse("A starbase structure must be anchored within range of its corporation's anchored control tower.");
        // the nearest tower in range is checked before any row lock is taken: its position comes from the simulation
        var towerSight = await PreflightControlTowerAccessAsync(session, inRange[0], requireAnchoringSkill: false, cancellationToken,
            requireInteractionDistance: false, requireConfigurationRole: false, operation: "anchor");

        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        // the tower row is locked before the module row, the order every starbase path takes
        if (await _deployed.LockAndGetAsync(connection, transaction, inRange[0].DeployableId, cancellationToken) is not { Kind: ControlTowerKind } associatedTower)
            throw Refuse("A starbase structure must be anchored within range of its corporation's anchored control tower.");
        if (await _deployed.LockAndGetAsync(connection, transaction, moduleId, cancellationToken) is not { Kind: StarbaseModuleKind } module ||
            module.SolarSystemId != reference.SolarSystemId || module.CorporationId != reference.CorporationId || module.OwnerId != reference.OwnerId)
            throw Refuse("That starbase structure is not in space.");
        var lockedState = await _deployed.TowerStateAsync(connection, transaction, moduleId, cancellationToken);
        if (lockedState is TowerAnchoring or TowerOffline or TowerOnline or TowerOnlining)
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }
        if (lockedState != TowerUnanchored)
            throw Refuse("That starbase structure is not fully unanchored.");
        await RequireTowerAccessUnderLockAsync(session, associatedTower, towerSight, requireConfigurationRole: false, operation: "anchor",
            connection, transaction, cancellationToken);

        if (await _space.PositionOfAsync(session, moduleId, cancellationToken) is not { } modulePosition ||
            await _space.PositionOfAsync(session, session.GetInt("shipid")!.Value, cancellationToken) is not { } shipPosition ||
            modulePosition.SolarSystemId != module.SolarSystemId || shipPosition.SolarSystemId != module.SolarSystemId ||
            Math.Max(0, Distance([modulePosition.X, modulePosition.Y, modulePosition.Z], shipPosition.X, shipPosition.Y, shipPosition.Z) -
                Math.Max(0, shipPosition.Radius ?? 0) - module.Radius) > 2_500)
            throw Refuse("The starbase structure is too far away to anchor.");

        var startedAtMs = now;
        var delayAttribute = _statics.AttributeId("anchoringDelay") ?? 556;
        var completionMs = checked(startedAtMs + (long)Math.Max(0, moduleType.Attributes.GetValueOrDefault(delayAttribute)));
        var position = requestedPosition.ToArray();
        await _deployed.SaveAsync(connection, transaction, module with
        {
            Position = position,
            ActivateAtMs = completionMs,
            ControlTowerId = associatedTower.DeployableId,
        }, cancellationToken);
        await _deployed.SetTowerStateAsync(connection, transaction, moduleId, TowerAnchoring, startedAtMs, cancellationToken);
        await _scheduler.ScheduleAsync(connection, transaction, new JobSpec(AnchorJob, completionMs)
        {
            Key = $"deployable:{moduleId}",
            Payload = new DisruptorAnchorDue(moduleId),
            OnDuplicate = OnDuplicate.Replace,
        }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        _scheduler.Wake();

        _sim.RemoveDeployed(moduleId);
        _sim.PlaceControlTower(new SimPlaceControlTower(module.DeployableId, module.SolarSystemId, module.Name, module.TypeId,
            module.GroupId, position, module.Radius, false, module.OwnerId, module.CorporationId, module.AllianceId, module.ShipId));
        SetControlTowerSlimState(moduleId, StructureAnchored, startedAtMs);
    }

    /// <summary>Routes dogmaIM's onlineForStructures effect on a control tower to its persisted POS transition.</summary>
    public async Task<ModuleResult?> ControlTowerModuleOperationAsync(ClientSession session, long towerId, bool online, CancellationToken cancellationToken)
    {
        if (_deployed is null || await _deployed.GetAsync(towerId, cancellationToken) is not { } record)
            return null;
        if (record.Kind == ControlTowerKind)
        {
            if (online)
                await OnlineControlTowerAsync(session, towerId, cancellationToken);
            else
                await OfflineControlTowerAsync(session, towerId, cancellationToken);
            return ModuleResult.Done;
        }
        if (record.Kind == StarbaseModuleKind)
        {
            if (online)
                await OnlineStarbaseModuleAsync(session, towerId, cancellationToken);
            else
                await OfflineStarbaseModuleAsync(session, towerId, cancellationToken);
            return ModuleResult.Done;
        }
        return null;
    }

    private async Task OnlineStarbaseModuleAsync(ClientSession session, long moduleId, CancellationToken cancellationToken)
    {
        if (_deployed is null || _scheduler is null || _source is null)
            throw Refuse("Starbase module online operations are not available on this server.");
        if (await _deployed.GetAsync(moduleId, cancellationToken) is not { Kind: StarbaseModuleKind, ControlTowerId: > 0 } reference)
            throw Refuse("That starbase structure is not associated with a control tower.");

        // the tower's pilot checks and the simulation's position come before any row lock
        if (await _deployed.GetAsync(reference.ControlTowerId, cancellationToken) is not { Kind: ControlTowerKind } towerPreview)
            throw Refuse("That starbase structure is not associated with an available control tower.");
        var towerSight = await PreflightControlTowerAccessAsync(session, towerPreview, requireAnchoringSkill: false, cancellationToken,
            requireConfigurationRole: false, operation: "online");
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        // Lock the tower first, matching production-cycle transitions and serializing CPU/power reservations.
        if (await _deployed.LockAndGetAsync(connection, transaction, reference.ControlTowerId, cancellationToken) is not { Kind: ControlTowerKind } tower ||
            await _deployed.LockAndGetAsync(connection, transaction, moduleId, cancellationToken) is not { Kind: StarbaseModuleKind } module ||
            module.ControlTowerId != tower.DeployableId || module.OwnerId != tower.OwnerId || module.CorporationId != tower.CorporationId ||
            module.SolarSystemId != tower.SolarSystemId)
            throw Refuse("That starbase structure is not associated with an available control tower.");

        await RequireTowerAccessUnderLockAsync(session, tower, towerSight, requireConfigurationRole: false, operation: "online",
            connection, transaction, cancellationToken);
        var state = await _deployed.TowerStateAsync(connection, transaction, moduleId, cancellationToken);
        if (state is TowerOnlining or TowerOnline)
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }
        if (state != TowerOffline || module.ActivateAtMs == NeverAnchored || module.ActivateAtMs > _nowUnixMs())
            throw Refuse("The starbase structure must be fully anchored and offline before it can be put online.");
        if (await _deployed.TowerStateAsync(connection, transaction, tower.DeployableId, cancellationToken) != TowerOnline ||
            tower.ActivateAtMs == NeverAnchored || tower.ActivateAtMs > _nowUnixMs())
            throw Refuse("The associated control tower must be anchored and online before a starbase structure can be put online.");

        var resourceError = await StarbaseModuleResourceErrorAsync(connection, transaction, tower, module, cancellationToken)
            .ConfigureAwait(false);
        if (resourceError is not null)
            throw MachoErrors.ThrowUserError(resourceError);

        await _statics.EnsureTypesAsync([module.TypeId], cancellationToken).ConfigureAwait(false);
        var moduleType = _statics.Type(module.TypeId)
            ?? throw new InvalidOperationException($"SDE type {module.TypeId} for starbase structure {moduleId} is missing.");
        var onliningDelay = _statics.AttributeId("onliningDelay")
            ?? throw new InvalidOperationException("The pinned SDE has no onliningDelay dogma attribute.");
        var startedAtMs = _nowUnixMs();
        var completesAtMs = checked(startedAtMs + (long)Math.Max(0, moduleType.Attributes.GetValueOrDefault(onliningDelay)));
        await _deployed.SetTowerStateAsync(connection, transaction, moduleId, TowerOnlining, startedAtMs, cancellationToken);
        await _scheduler.ScheduleAsync(connection, transaction, new JobSpec(StarbaseModuleOnlineJob, completesAtMs)
        {
            Key = $"module:{moduleId}",
            Payload = new StarbaseModuleOnlineDue(moduleId, startedAtMs),
            OnDuplicate = OnDuplicate.Replace,
        }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        SetControlTowerSlimState(moduleId, StructureOnlining, startedAtMs);
        _scheduler.Wake();
    }

    private async Task OfflineStarbaseModuleAsync(ClientSession session, long moduleId, CancellationToken cancellationToken)
    {
        if (_deployed is null || _scheduler is null || _source is null)
            throw Refuse("Starbase module online operations are not available on this server.");
        if (await _deployed.GetAsync(moduleId, cancellationToken) is not { Kind: StarbaseModuleKind, ControlTowerId: > 0 } reference)
            throw Refuse("That starbase structure is not associated with a control tower.");

        // the tower's pilot checks and the simulation's position come before any row lock
        if (await _deployed.GetAsync(reference.ControlTowerId, cancellationToken) is not { Kind: ControlTowerKind } towerPreview)
            throw Refuse("That starbase structure is not associated with an available control tower.");
        var towerSight = await PreflightControlTowerAccessAsync(session, towerPreview, requireAnchoringSkill: false, cancellationToken,
            requireConfigurationRole: false, operation: "offline");
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (await _deployed.LockAndGetAsync(connection, transaction, reference.ControlTowerId, cancellationToken) is not { Kind: ControlTowerKind } tower ||
            await _deployed.LockAndGetAsync(connection, transaction, moduleId, cancellationToken) is not { Kind: StarbaseModuleKind } module ||
            module.ControlTowerId != tower.DeployableId || module.OwnerId != tower.OwnerId || module.CorporationId != tower.CorporationId)
            throw Refuse("That starbase structure is not associated with an available control tower.");

        await RequireTowerAccessUnderLockAsync(session, tower, towerSight, requireConfigurationRole: false, operation: "offline",
            connection, transaction, cancellationToken);
        var state = await _deployed.TowerStateAsync(connection, transaction, moduleId, cancellationToken);
        if (state is not (TowerOnline or TowerOnlining))
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }
        await _deployed.SetTowerStateAsync(connection, transaction, moduleId, TowerOffline, cancellationToken);
        // a battery that goes offline loses its control grant, as the tower's offline does
        await _deployed.ClearBatteryControlAsync(connection, transaction, moduleId, cancellationToken);
        await _scheduler.CancelAsync(connection, transaction, StarbaseModuleOnlineJob, $"module:{moduleId}", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        // the simulation is told after the commit: the battery is offline with no holder
        await OfflineBatteriesAsync(tower.DeployableId, cancellationToken);
        SetControlTowerSlimState(moduleId, StructureAnchored, null);
        _scheduler.Wake();
    }

    /// <summary>Completes a module's onlining transition in the scheduler job transaction; null means this was a stale job.</summary>
    public async Task<bool?> CompleteStarbaseModuleOnlineAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        StarbaseModuleOnlineDue due, CancellationToken cancellationToken = default)
    {
        if (_deployed is null || await _deployed.GetAsync(due.DeployableId, cancellationToken) is not
            { Kind: StarbaseModuleKind, ControlTowerId: > 0 } reference)
            return false;
        if (await _deployed.LockAndGetAsync(connection, transaction, reference.ControlTowerId, cancellationToken) is not
            { Kind: ControlTowerKind } tower ||
            await _deployed.LockAndGetAsync(connection, transaction, due.DeployableId, cancellationToken) is not
            { Kind: StarbaseModuleKind } module || module.ControlTowerId != tower.DeployableId)
            return false;
        if (await _deployed.TowerStateAsync(connection, transaction, module.DeployableId, cancellationToken) != TowerOnlining ||
            await _deployed.TowerStateStartedAtMsAsync(connection, transaction, module.DeployableId, cancellationToken) != due.StartedAtMs)
            return false;

        var towerOnline = await _deployed.TowerStateAsync(connection, transaction, tower.DeployableId, cancellationToken) == TowerOnline &&
            tower.ActivateAtMs != NeverAnchored && tower.ActivateAtMs <= _nowUnixMs();
        var resourceError = towerOnline
            ? await StarbaseModuleResourceErrorAsync(connection, transaction, tower, module, cancellationToken).ConfigureAwait(false)
            : "CONTROL_TOWER_NOT_ONLINE";
        var online = resourceError is null;
        await _deployed.SetTowerStateAsync(connection, transaction, module.DeployableId,
            online ? TowerOnline : TowerOffline, cancellationToken);
        return online;
    }

    /// <summary>Updates the space slim item after the module state transaction commits.</summary>
    public void PublishStarbaseModuleOnlineState(long moduleId, bool online) =>
        SetControlTowerSlimState(moduleId, online ? StructureOnline : StructureAnchored, null);

    /// <summary>The SDE type of a POS control tower's force field (the Force Field, group 411).</summary>
    private const int ForceFieldTypeId = 16_103;

    /// <summary>The simulation's answer to a removal when no field of the tower is in its scene: the field is gone already.</summary>
    private const string ForceFieldNotInSpace = "FORCE_FIELD_NOT_IN_SPACE";

    /// <summary>How many times a removal the simulation refuses is asked again before the reconcile fails, and the wait between the asks.</summary>
    private const int ForceFieldRemovalAttempts = 5;
    private static readonly TimeSpan ForceFieldRemovalRetryDelay = TimeSpan.FromMilliseconds(200);

    /// <summary>The per-tower gates of the force field reconcile: a tower's item commit and the simulation's send to it happen under one gate, so two syncs cannot send in the other's order.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, SemaphoreSlim> _forceFieldGates = new();

    /// <summary>
    /// Makes a control tower's force field agree with its state: an online tower has one field item and one field in its system's scene, and any other state has
    /// neither. The item is written in one transaction and the simulation is told after its commit. A repeat sync keeps the item and sends the same placement,
    /// which the simulation takes as unchanged. A refused placement, or a removal the simulation refuses for another reason, is an error.
    /// </summary>
    private async Task ReconcileForceFieldAsync(long towerId, CancellationToken cancellationToken)
    {
        if (_deployed is null || _source is null)
            return;
        var gate = _forceFieldGates.GetOrAdd(towerId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SimPlaceForceField? placement = null;
            SimRemoveForceField? removal = null;
            await using (var connection = await _source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
            await using (var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
            {
                if (await _deployed.LockAndGetAsync(connection, transaction, towerId, cancellationToken).ConfigureAwait(false) is not { Kind: ControlTowerKind } tower)
                    return;
                var state = await _deployed.TowerStateAsync(connection, transaction, towerId, cancellationToken).ConfigureAwait(false);
                if (state == TowerOnline && tower.ActivateAtMs != NeverAnchored && tower.ActivateAtMs <= _nowUnixMs())
                {
                    placement = await EnsureForceFieldInTransactionAsync(connection, transaction, tower, cancellationToken).ConfigureAwait(false);
                }
                else if (await RemoveForceFieldInTransactionAsync(connection, transaction, towerId, cancellationToken).ConfigureAwait(false) is { } removed)
                {
                    removal = new SimRemoveForceField(towerId, removed.SolarSystemId);
                }
                else if (state == TowerOffline)
                {
                    // an offline tower with no item may still have its field in the simulation (its removal was committed and not answered): the simulation is asked anyway
                    removal = new SimRemoveForceField(towerId, tower.SolarSystemId);
                }
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            if (placement is not null)
            {
                var answer = await _sim.PlaceForceFieldAsync(placement, cancellationToken).ConfigureAwait(false);
                if (answer.Reason.Length > 0)
                {
                    // the simulation holds no field of the tower, so the item the placement wrote is taken back: an item exists only with a field in the scene, and the next sync writes a new one
                    await using (var compensation = await _source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
                    await using (var transaction = await compensation.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
                    {
                        await RemoveForceFieldInTransactionAsync(compensation, transaction, towerId, cancellationToken).ConfigureAwait(false);
                        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    }
                    throw new InvalidOperationException($"The simulation did not place force field {placement.FieldId} of control tower {towerId}: {answer.Reason}");
                }
            }
            if (removal is not null)
            {
                // a refusal, or a link that is down, is asked again until the simulation says the field is gone or not in space
                for (var attempt = 1; ; attempt++)
                {
                    string reason;
                    try
                    {
                        reason = (await _sim.RemoveForceFieldAsync(removal, cancellationToken).ConfigureAwait(false)).Reason;
                    }
                    catch (SimUnavailableException) when (attempt < ForceFieldRemovalAttempts)
                    {
                        reason = "the simulation is unavailable";
                    }
                    if (reason.Length == 0 || reason == ForceFieldNotInSpace)
                        break;
                    if (attempt >= ForceFieldRemovalAttempts)
                        throw new InvalidOperationException($"The simulation did not remove the force field of control tower {towerId}: {reason}");
                    await Task.Delay(ForceFieldRemovalRetryDelay, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Writes a control tower's force field item and its space row (creating the item once), with the SDE's shield radius.</summary>
    private async Task<SimPlaceForceField> EnsureForceFieldInTransactionAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        DeployedRecord tower, CancellationToken cancellationToken)
    {
        await _statics.EnsureTypesAsync([ForceFieldTypeId, tower.TypeId], cancellationToken).ConfigureAwait(false);
        var fieldType = _statics.Type(ForceFieldTypeId)
            ?? throw new InvalidOperationException($"SDE type {ForceFieldTypeId} for a control tower's force field is missing.");
        var towerType = _statics.Type(tower.TypeId)
            ?? throw new InvalidOperationException($"SDE type {tower.TypeId} for control tower {tower.DeployableId} is missing.");
        var shieldRadiusId = _statics.AttributeId("shieldRadius")
            ?? throw new InvalidOperationException("The pinned SDE has no shieldRadius dogma attribute.");
        var radius = towerType.Attributes.GetValueOrDefault(shieldRadiusId);
        if (!double.IsFinite(radius) || radius <= 0)
            throw new InvalidOperationException($"SDE shieldRadius of control tower type {tower.TypeId} is not positive.");

        var existing = await _spaceItems.ForceFieldAsync(connection, transaction, tower.DeployableId, cancellationToken).ConfigureAwait(false);
        long itemId;
        if (existing is null)
        {
            itemId = await _items.CreateAsync(connection, transaction,
                new Eve.Inventory.NewItem(ForceFieldTypeId, tower.OwnerId, tower.SolarSystemId, CharacterShips.InSpaceFlag, 1, true), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            itemId = existing.ItemId;
            if (await _items.LockAndGetAsync(connection, transaction, itemId, cancellationToken).ConfigureAwait(false) is null)
                throw new InvalidDataException($"force field item {itemId} of control tower {tower.DeployableId} has no inventory row");
        }
        var position = tower.Position.ToArray();
        await _spaceItems.SaveAsync(connection, transaction, new SpaceItem(itemId, tower.SolarSystemId, position, [1d, 0d, 0d], radius,
            fieldType.GroupId, tower.DeployableId, 0, tower.CorporationId, tower.AllianceId, 0, long.MaxValue, Kind: SpaceItemKind.ForceField), cancellationToken).ConfigureAwait(false);
        return new SimPlaceForceField(itemId, tower.DeployableId, tower.SolarSystemId, ForceFieldTypeId, fieldType.GroupId, fieldType.Name, position, radius);
    }

    /// <summary>Deletes a control tower's force field item and its space row. Returns the removed row, or null when the tower has no field.</summary>
    private async Task<SpaceItem?> RemoveForceFieldInTransactionAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long towerId,
        CancellationToken cancellationToken)
    {
        var existing = await _spaceItems.ForceFieldAsync(connection, transaction, towerId, cancellationToken).ConfigureAwait(false);
        if (existing is null)
            return null;
        await _items.DeleteAsync(connection, transaction, existing.ItemId, cancellationToken).ConfigureAwait(false);
        await _spaceItems.RemoveAsync(connection, transaction, existing.ItemId, cancellationToken).ConfigureAwait(false);
        return existing;
    }

    /// <summary>Publishes persisted tower and module state after their database transaction commits.</summary>
    public async Task SynchronizeControlTowerSlimStateAsync(long towerId, CancellationToken cancellationToken = default)
    {
        if (_deployed is null || await _deployed.GetAsync(towerId, cancellationToken).ConfigureAwait(false) is not { Kind: ControlTowerKind })
            return;

        var towerState = await _deployed.TowerStateAsync(towerId, cancellationToken).ConfigureAwait(false);
        var towerStartedAt = await _deployed.TowerStateStartedAtMsAsync(towerId, cancellationToken).ConfigureAwait(false);
        SetControlTowerSlimState(towerId, towerState switch
        {
            TowerOnline => StructureOnline,
            TowerOnlining => StructureOnlining,
            TowerUnanchored or TowerUnanchoring => StructureUnanchored,
            _ => StructureAnchored,
        }, towerStartedAt);

        foreach (var module in await _deployed.ByControlTowerAsync(towerId, cancellationToken).ConfigureAwait(false))
        {
            var moduleState = await _deployed.TowerStateAsync(module.DeployableId, cancellationToken).ConfigureAwait(false);
            var startedAt = await _deployed.TowerStateStartedAtMsAsync(module.DeployableId, cancellationToken).ConfigureAwait(false);
            SetControlTowerSlimState(module.DeployableId, moduleState switch
            {
                TowerOnline => StructureOnline,
                TowerOnlining => StructureOnlining,
                TowerUnanchored or TowerUnanchoring => StructureUnanchored,
                _ => StructureAnchored,
            }, startedAt);
        }

        // the sentry pass places every battery of the tower that no pilot holds (online or offline, with no holder): a separate offline sweep placed the same battery twice
        await RefreshForceFieldAccessAsync(towerId, cancellationToken).ConfigureAwait(false);
        await ReconcileForceFieldAsync(towerId, cancellationToken).ConfigureAwait(false);
        await ReconcileSentryAsync(towerId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Turns every associated online or onlining module off in the tower's state transaction.</summary>
    private async Task OfflineStarbaseModulesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        long towerId, CancellationToken cancellationToken)
    {
        if (_deployed is null)
            return;

        foreach (var module in await _deployed.ByControlTowerAsync(connection, transaction, towerId, cancellationToken).ConfigureAwait(false))
        {
            var state = await _deployed.TowerStateAsync(connection, transaction, module.DeployableId, cancellationToken).ConfigureAwait(false);
            if (state is not (TowerOnline or TowerOnlining))
                continue;

            await _deployed.SetTowerStateAsync(connection, transaction, module.DeployableId, TowerOffline, cancellationToken).ConfigureAwait(false);
            await _deployed.ClearBatteryControlAsync(connection, transaction, module.DeployableId, cancellationToken).ConfigureAwait(false);
            if (_scheduler is not null)
                await _scheduler.CancelAsync(connection, transaction, StarbaseModuleOnlineJob, $"module:{module.DeployableId}", cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<string?> StarbaseModuleResourceErrorAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        DeployedRecord tower, DeployedRecord candidate, CancellationToken cancellationToken)
    {
        var modules = await _deployed!.ByControlTowerAsync(connection, transaction, tower.DeployableId, cancellationToken).ConfigureAwait(false);
        var typeIds = modules.Select(module => module.TypeId).Append(candidate.TypeId).Append(tower.TypeId).Distinct().ToArray();
        await _statics.EnsureTypesAsync(typeIds, cancellationToken).ConfigureAwait(false);
        var cpuOutputId = _statics.AttributeId("cpuOutput")
            ?? throw new InvalidOperationException("The pinned SDE has no cpuOutput dogma attribute.");
        var powerOutputId = _statics.AttributeId("powerOutput")
            ?? throw new InvalidOperationException("The pinned SDE has no powerOutput dogma attribute.");
        var cpuUsageId = _statics.AttributeId("cpu")
            ?? throw new InvalidOperationException("The pinned SDE has no cpu dogma attribute.");
        var powerUsageId = _statics.AttributeId("power")
            ?? throw new InvalidOperationException("The pinned SDE has no power dogma attribute.");
        var towerType = _statics.Type(tower.TypeId)
            ?? throw new InvalidOperationException($"SDE type {tower.TypeId} for control tower {tower.DeployableId} is missing.");
        var cpuOutput = Math.Max(0, towerType.Attributes.GetValueOrDefault(cpuOutputId));
        var powerOutput = Math.Max(0, towerType.Attributes.GetValueOrDefault(powerOutputId));
        var cpuUsage = 0d;
        var powerUsage = 0d;
        foreach (var module in modules)
        {
            if (module.DeployableId == candidate.DeployableId)
                continue;
            var state = await _deployed.TowerStateAsync(connection, transaction, module.DeployableId, cancellationToken).ConfigureAwait(false);
            if (module.ActivateAtMs > _nowUnixMs() || state is not (TowerOnlining or TowerOnline))
                continue;
            var type = _statics.Type(module.TypeId)
                ?? throw new InvalidOperationException($"SDE type {module.TypeId} for starbase structure {module.DeployableId} is missing.");
            cpuUsage += Math.Max(0, type.Attributes.GetValueOrDefault(cpuUsageId));
            powerUsage += Math.Max(0, type.Attributes.GetValueOrDefault(powerUsageId));
        }
        var candidateType = _statics.Type(candidate.TypeId)
            ?? throw new InvalidOperationException($"SDE type {candidate.TypeId} for starbase structure {candidate.DeployableId} is missing.");
        cpuUsage += Math.Max(0, candidateType.Attributes.GetValueOrDefault(cpuUsageId));
        powerUsage += Math.Max(0, candidateType.Attributes.GetValueOrDefault(powerUsageId));
        if (cpuUsage > cpuOutput + 0.000001)
            return "STARBASE_CPU_INSUFFICIENT";
        if (powerUsage > powerOutput + 0.000001)
            return "STARBASE_POWERGRID_INSUFFICIENT";
        return null;
    }

    private async Task OnlineControlTowerAsync(ClientSession session, long towerId, CancellationToken cancellationToken)
    {
        if (_deployed is null || _scheduler is null || _source is null)
            throw Refuse("Control tower online operations are not available on this server.");
        // the pilot checks and the simulation's position come before any row lock
        if (await _deployed.GetAsync(towerId, cancellationToken) is not { Kind: ControlTowerKind } preview)
            throw Refuse("That control tower is not in space.");
        var sight = await PreflightControlTowerAccessAsync(session, preview, requireAnchoringSkill: false, cancellationToken,
            requireConfigurationRole: false, operation: "online");
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (await _deployed.LockAndGetAsync(connection, transaction, towerId, cancellationToken) is not { Kind: ControlTowerKind } record)
            throw Refuse("That control tower is not in space.");
        await RequireTowerAccessUnderLockAsync(session, record, sight, requireConfigurationRole: false, operation: "online",
            connection, transaction, cancellationToken);
        if (record.ActivateAtMs == NeverAnchored || record.ActivateAtMs > _nowUnixMs())
            throw Refuse("The control tower must finish anchoring before it can be put online.");
        var state = await _deployed.TowerStateAsync(connection, transaction, towerId, cancellationToken);
        if (state is TowerOnlining or TowerOnline)
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }
        if (state != TowerOffline)
            throw Refuse("The control tower must be offline before it can be put online.");

        await _statics.EnsureTypesAsync([record.TypeId], cancellationToken);
        var type = _statics.Type(record.TypeId)
            ?? throw new InvalidOperationException($"SDE type {record.TypeId} for control tower {towerId} is missing.");
        var resources = await ReadTowerResourcesAsync(connection, transaction, record.SolarSystemId, type, cancellationToken)
            ?? throw Refuse("The control tower fuel requirements are not available from static data.");
        if (!resources.CharterAvailable)
            throw Refuse("This system's sovereign faction does not issue a Starbase Charter, so the control tower cannot be put online.");
        var available = await HasResourceAsync(towerId, resources.FuelTypeId, resources.FuelQuantity, null, cancellationToken)
            && (!resources.CharterRequired || await HasResourceAsync(towerId, resources.CharterTypeId, resources.CharterQuantity, null, cancellationToken));
        if (!available)
            throw Refuse(resources.CharterRequired
                ? "The control tower fuel bay needs one hour of fuel and the system's Starbase Charter before it can be put online."
                : "The control tower fuel bay needs at least one hour of the correct fuel blocks before it can be put online.");

        var startedAtMs = _nowUnixMs();
        var onlineAt = checked(startedAtMs + ControlTowerFuelRules.OnliningDelayMs(type));
        await _deployed.SetTowerStateAsync(connection, transaction, towerId, TowerOnlining, startedAtMs, cancellationToken);
        await _scheduler.ScheduleAsync(connection, transaction, new JobSpec(ControlTowerOnlineJob, onlineAt)
        {
            Key = $"tower:{towerId}",
            Payload = new ControlTowerOnlineDue(towerId, startedAtMs),
            OnDuplicate = OnDuplicate.Replace,
        }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        SetControlTowerSlimState(towerId, StructureOnlining, startedAtMs);
        _scheduler.Wake();
    }

    private async Task OfflineControlTowerAsync(ClientSession session, long towerId, CancellationToken cancellationToken)
    {
        if (_deployed is null || _scheduler is null || _source is null)
            throw Refuse("Control tower online operations are not available on this server.");
        // the pilot checks and the simulation's position come before any row lock
        if (await _deployed.GetAsync(towerId, cancellationToken) is not { Kind: ControlTowerKind } preview)
            throw Refuse("That control tower is not in space.");
        var sight = await PreflightControlTowerAccessAsync(session, preview, requireAnchoringSkill: false, cancellationToken,
            requireConfigurationRole: false, operation: "offline");
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (await _deployed.LockAndGetAsync(connection, transaction, towerId, cancellationToken) is not { Kind: ControlTowerKind } record)
            throw Refuse("That control tower is not in space.");
        await RequireTowerAccessUnderLockAsync(session, record, sight, requireConfigurationRole: false, operation: "offline",
            connection, transaction, cancellationToken);
        var state = await _deployed.TowerStateAsync(connection, transaction, towerId, cancellationToken);
        if (state == TowerOffline)
        {
            await OfflineStarbaseModulesAsync(connection, transaction, towerId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            await SynchronizeControlTowerSlimStateAsync(towerId, cancellationToken);
            return;
        }
        if (state is not (TowerOnline or TowerOnlining))
            throw Refuse("The control tower is not online or onlining.");
        await _deployed.SetTowerStateAsync(connection, transaction, towerId, TowerOffline, cancellationToken);
        await UpdateProductionScheduleForTowerStateAsync(connection, transaction, record, false, cancellationToken);
        await OfflineStarbaseModulesAsync(connection, transaction, towerId, cancellationToken);
        await _scheduler.CancelAsync(connection, transaction, ControlTowerOnlineJob, $"tower:{towerId}", cancellationToken);
        await _scheduler.CancelAsync(connection, transaction, ControlTowerFuelJob, $"tower:{towerId}", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await SynchronizeControlTowerSlimStateAsync(towerId, cancellationToken);
        _scheduler.Wake();
    }

    /// <summary>What the simulation said about a pilot's ship and a control tower, read before any row lock is taken. The tower's system and owners are kept, so
    /// the locked check can confirm that the row it locks is still the tower that was seen.</summary>
    internal readonly record struct ControlTowerSight(int SolarSystemId, long OwnerId, long CorporationId, long AllianceId, double SurfaceDistance);

    /// <summary>The cheap pilot checks: logged in with a corporation, in the tower's system and in space, the corporation the operation needs and the role it
    /// needs. Takes no row lock and makes no simulation call.</summary>
    private (bool SameCorporation, bool HasConfigurationRole) CheckPilotOnTower(ClientSession session, DeployedRecord record, bool requireConfigurationRole,
        string? operation)
    {
        var characterId = session.CharacterId ?? 0;
        var corporationId = session.GetInt("corpid") ?? session.GetInt("corporationid") ?? 0;
        var systemId = (int)(session.GetInt("solarsystemid2") ?? session.GetInt("solarsystemid") ?? 0);
        var shipId = session.GetInt("shipid") ?? 0;
        var isCeo = session.Get("isCEO") switch
        {
            PyBool value => value.Value,
            PyInt value => value.Value != 0,
            PyLong value => value.Value != 0,
            _ => false,
        };
        var roles = session.GetInt("corprole") ?? 0;
        if (characterId <= 0 || corporationId <= 0 || systemId != record.SolarSystemId || shipId <= 0 || !_space.IsInSpace(session))
            throw Refuse("A control tower can only be managed by a pilot in its system and in space.");
        var sameCorporation = record.CorporationId == corporationId && record.OwnerId == corporationId;
        if (!sameCorporation && operation is null)
            throw Refuse("You must belong to the corporation that owns the control tower.");
        var hasConfigurationRole = sameCorporation && (isCeo || (roles & (DirectorRole | StarbaseConfigRole)) != 0);
        if (requireConfigurationRole && !hasConfigurationRole)
            throw MachoErrors.ThrowUserError("CrpAccessDenied", ("reason", Py.Tuple(new PyInt(101), new PyStr("UI/Corporations/AccessRestrictions/InsufficientRoles"))));
        return (sameCorporation, hasConfigurationRole);
    }

    /// <summary>The checks that run before a caller's transaction takes any row lock: the cheap pilot checks, the simulation's position of the tower and the
    /// ship, the interaction range and the anchoring skill. The locked check is <see cref="RequireTowerAccessUnderLockAsync"/>.</summary>
    private async Task<ControlTowerSight> PreflightControlTowerAccessAsync(ClientSession session, DeployedRecord record, bool requireAnchoringSkill,
        CancellationToken cancellationToken, bool requireInteractionDistance = true, bool requireConfigurationRole = true, string? operation = null)
    {
        CheckPilotOnTower(session, record, requireConfigurationRole, operation);
        var characterId = session.CharacterId ?? 0;
        var shipId = session.GetInt("shipid") ?? 0;
        if (await _space.PositionOfAsync(session, record.DeployableId, cancellationToken) is null ||
            await _space.PositionOfAsync(session, shipId, cancellationToken) is not { } shipPosition || shipPosition.SolarSystemId != record.SolarSystemId)
            throw Refuse("The control tower is not visible in the current space scene.");
        var shipRadius = Math.Max(0, shipPosition.Radius ?? 0);
        if (shipRadius <= 0 && await _ships.LoadAsync(characterId, cancellationToken) is { } snapshot && snapshot.Ship.Id == shipId)
            shipRadius = Math.Max(0, snapshot.World.Get(shipId, SimShips.Radius));
        var surfaceDistance = Math.Max(0, Distance(record.Position, shipPosition.X, shipPosition.Y, shipPosition.Z) - shipRadius - Math.Max(0, record.Radius));
        if (requireInteractionDistance && surfaceDistance > MaxControlTowerInteractionDistance)
            throw Refuse("The control tower is too far away to manage.");

        if (requireAnchoringSkill && await _ships.MissingSkillAsync(characterId,
            new Dictionary<int, double> { [182] = AnchoringSkillTypeId, [277] = 1 }, cancellationToken) is not null)
            throw Refuse("Anchoring skill I is required to anchor a control tower.");
        return new ControlTowerSight(record.SolarSystemId, record.OwnerId, record.CorporationId, record.AllianceId, surfaceDistance);
    }

    /// <summary>The check under a tower's row lock. The locked row must still be the tower the preflight saw, the cheap pilot checks must hold, and an operation
    /// the corporation's deploy flags grant passes. No simulation call is made while the lock is held.</summary>
    private async Task RequireTowerAccessUnderLockAsync(ClientSession session, DeployedRecord record, ControlTowerSight sight, bool requireConfigurationRole,
        string? operation, NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        var (sameCorporation, hasConfigurationRole) = CheckPilotOnTower(session, record, requireConfigurationRole, operation);
        if (record.SolarSystemId != sight.SolarSystemId || record.OwnerId != sight.OwnerId || record.CorporationId != sight.CorporationId ||
            record.AllianceId != sight.AllianceId)
            throw Refuse("The control tower changed while it was being checked. Try again.");
        if (!requireConfigurationRole && operation is not null && !hasConfigurationRole)
        {
            if (_deployed is null)
                throw new InvalidOperationException("A transaction is required to check persisted control tower deployment permissions.");
            var rawConfiguration = await _deployed.TowerConfigurationAsync(connection, transaction, record.DeployableId, cancellationToken);
            var configuration = rawConfiguration is null ? new PosMgrTowerConfiguration() :
                JsonSerializer.Deserialize<PosMgrTowerConfiguration>(rawConfiguration, PosMgrJson) ?? new PosMgrTowerConfiguration();
            var permission = Normalize(configuration).DeployFlags.GetValueOrDefault(operation);
            var allianceId = session.GetInt("allianceid") ?? session.GetInt("allianceID") ?? 0;
            var sameAlliance = record.AllianceId > 0 && allianceId == record.AllianceId;
            var permitted = permission == 1 && sameCorporation || permission == 2 && (sameCorporation || sameAlliance);
            if (!permitted)
                throw MachoErrors.ThrowUserError("CrpAccessDenied", ("reason", Py.Tuple(new PyInt(101), new PyStr("UI/Corporations/AccessRestrictions/InsufficientRoles"))));
        }
    }

    /// <summary>The pilot checks for a caller that holds no row lock and needs no deployment permission: the preflight alone.</summary>
    private async Task RequireControlTowerAccessAsync(ClientSession session, DeployedRecord record, bool requireAnchoringSkill, CancellationToken cancellationToken,
        bool requireInteractionDistance = true, bool requireConfigurationRole = true, string? operation = null)
    {
        if (operation is not null)
            throw new InvalidOperationException("A transaction is required to check persisted control tower deployment permissions.");
        await PreflightControlTowerAccessAsync(session, record, requireAnchoringSkill, cancellationToken,
            requireInteractionDistance, requireConfigurationRole, null).ConfigureAwait(false);
    }

    private async Task<TowerResources?> ReadTowerResourcesAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, int systemId, TypeDef type, CancellationToken cancellationToken)
    {
        await using var resourcesCommand = new NpgsqlCommand(
            "SELECT doc::text FROM sde.\"controlTowerResources\" WHERE key = $1", connection, transaction);
        resourcesCommand.Parameters.Add(new NpgsqlParameter { Value = type.TypeId.ToString(CultureInfo.InvariantCulture) });
        var resourcesText = await resourcesCommand.ExecuteScalarAsync(cancellationToken) as string;
        if (resourcesText is null)
            return null;

        await using var systemCommand = new NpgsqlCommand(
            "SELECT (doc->>'securityStatus')::float8, NULLIF(doc->>'factionID', '')::int FROM sde.\"mapSolarSystems\" WHERE key = $1", connection, transaction);
        systemCommand.Parameters.Add(new NpgsqlParameter { Value = systemId.ToString(CultureInfo.InvariantCulture) });
        await using var systemReader = await systemCommand.ExecuteReaderAsync(cancellationToken);
        if (!await systemReader.ReadAsync(cancellationToken) || systemReader.IsDBNull(0))
            return null;
        var security = systemReader.GetDouble(0);
        var factionId = systemReader.IsDBNull(1) ? 0 : systemReader.GetInt32(1);
        await systemReader.DisposeAsync();

        using var document = JsonDocument.Parse(resourcesText);
        if (!document.RootElement.TryGetProperty("resources", out var rows) || rows.ValueKind != JsonValueKind.Array)
            return null;
        var fuelTypeId = 0;
        var fuelQuantity = 0L;
        var charterRows = new List<(int FactionId, int TypeId, long Quantity, double MinimumSecurity)>();
        foreach (var row in rows.EnumerateArray())
        {
            if (!row.TryGetProperty("purpose", out var purpose) || purpose.GetInt32() != 1 ||
                !row.TryGetProperty("resourceTypeID", out var resourceType) || !resourceType.TryGetInt32(out var resourceTypeId) || resourceTypeId <= 0 ||
                !row.TryGetProperty("quantity", out var quantityValue) || !quantityValue.TryGetInt64(out var quantity) || quantity <= 0)
                continue;
            if (!row.TryGetProperty("factionID", out var resourceFaction))
            {
                fuelTypeId = resourceTypeId;
                fuelQuantity = quantity;
                continue;
            }
            var resourceFactionId = resourceFaction.GetInt32();
            var minimumSecurity = row.TryGetProperty("minSecurityLevel", out var minimum) ? minimum.GetDouble() : 0;
            charterRows.Add((resourceFactionId, resourceTypeId, quantity, minimumSecurity));
        }
        if (fuelTypeId <= 0 || fuelQuantity <= 0)
            return null;

        var eligibleCharters = charterRows.Where(row => security >= row.MinimumSecurity).ToArray();
        var charterRequired = eligibleCharters.Length > 0;
        var charter = eligibleCharters.FirstOrDefault(row => row.FactionId == factionId);
        var charterAvailable = !charterRequired || charter.TypeId > 0;
        return new TowerResources(fuelTypeId, fuelQuantity, charterAvailable ? charter.TypeId : 0,
            charterAvailable ? charter.Quantity : 0, charterRequired, charterAvailable,
            ControlTowerFuelRules.PeriodMs(type));
    }

    private async Task<bool> HasResourceAsync(long containerId, int typeId, long quantity, IReadOnlyCollection<long>? allowedOwners, CancellationToken cancellationToken)
    {
        if (quantity <= 0)
            return true;
        if (typeId <= 0)
            return false;
        var available = 0L;
        foreach (var stack in await _items.InContainerAsync(containerId, cancellationToken))
        {
            if (stack.TypeId == typeId && (allowedOwners is null || allowedOwners.Contains(stack.OwnerId)))
                available = checked(available + stack.Quantity);
        }
        return available >= quantity;
    }

    private static double Distance(IReadOnlyList<double> position, double x, double y, double z)
    {
        var dx = position[0] - x;
        var dy = position[1] - y;
        var dz = position[2] - z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private void SetControlTowerSlimState(long towerId, int state, long? startedAtMs)
    {
        _sim.SetSlimField(towerId, "posState", new SimSlimInt(state));
        _sim.SetSlimField(towerId, "posTimestamp", startedAtMs is { } started
            ? new SimSlimInt(Eve.Services.Py.FileTimeFromUnixMs(started))
            : new SimSlimNone());
    }

    internal async Task RestoreControlTowerSlimStateAsync(DeployedRecord record, CancellationToken cancellationToken)
    {
        var state = await _deployed!.TowerStateAsync(record.DeployableId, cancellationToken) ?? TowerOffline;
        var startedAt = await _deployed.TowerStateStartedAtMsAsync(record.DeployableId, cancellationToken);
        switch (state)
        {
            case TowerUnanchored:
                SetControlTowerSlimState(record.DeployableId, StructureUnanchored, null);
                return;
            case TowerAnchoring:
                await _statics.EnsureTypesAsync([record.TypeId], cancellationToken);
                var delayMs = (long)Math.Max(0, _statics.Type(record.TypeId)?.Attributes.GetValueOrDefault(556) ?? 0);
                SetControlTowerSlimState(record.DeployableId, StructureAnchored, startedAt ?? record.ActivateAtMs - delayMs);
                return;
            case TowerOnlining:
                if (startedAt is { } onlineStartedAt)
                    SetControlTowerSlimState(record.DeployableId, StructureOnlining, onlineStartedAt);
                else
                    SetControlTowerSlimState(record.DeployableId, StructureAnchored, null);
                return;
            case TowerOnline:
                SetControlTowerSlimState(record.DeployableId, StructureOnline, null);
                return;
            case TowerUnanchoring:
                SetControlTowerSlimState(record.DeployableId, StructureUnanchored, startedAt);
                return;
            default:
                SetControlTowerSlimState(record.DeployableId, StructureAnchored, null);
                return;
        }
    }

    private static NearestMoonPoint? NearestMoon(IReadOnlyList<MoonAnchorPoint> moons, IReadOnlyList<double> position)
    {
        NearestMoonPoint? nearest = null;
        foreach (var moon in moons)
        {
            var dx = position[0] - moon.WarpX;
            var dy = position[1] - moon.WarpY;
            var dz = position[2] - moon.WarpZ;
            var distance = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (nearest is null || distance < nearest.Distance || distance == nearest.Distance && moon.MoonId < nearest.Moon.MoonId)
                nearest = new NearestMoonPoint(moon, distance);
        }
        return nearest;
    }

    private static double[] MoonAnchorPosition(MoonAnchorPoint moon)
    {
        var dx = moon.X - moon.WarpX;
        var dy = moon.Y - moon.WarpY;
        var dz = moon.Z - moon.WarpZ;
        var length = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        var direction = double.IsFinite(length) && length > 0 ? 100_000 / length : 0;
        return direction > 0
            ? [moon.WarpX + dx * direction, moon.WarpY + dy * direction, moon.WarpZ + dz * direction]
            : [moon.WarpX + 100_000, moon.WarpY, moon.WarpZ];
    }

    private static async Task<IReadOnlyList<MoonAnchorPoint>> ReadMoonAnchorPointsAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, int systemId, CancellationToken cancellationToken)
    {
        var moons = new List<MoonAnchorPoint>();
        await using var command = new NpgsqlCommand(
            "SELECT key, (doc->>'radius')::float8, (doc->'position'->>'x')::float8, (doc->'position'->>'y')::float8, (doc->'position'->>'z')::float8 " +
            "FROM sde.\"mapMoons\" WHERE (doc->>'solarSystemID')::int = $1 ORDER BY key", connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = systemId });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var radius = reader.GetDouble(1);
            if (radius < MinMoonWarpRadius)
                continue;
            var moonId = long.Parse(reader.GetString(0), CultureInfo.InvariantCulture);
            var x = reader.GetDouble(2);
            var y = reader.GetDouble(3);
            var z = reader.GetDouble(4);
            // EveJS's V24 parity point for a moon uses the generic celestial warp-in formula.
            var warpRadius = radius + 5_000_000;
            moons.Add(new MoonAnchorPoint(moonId, x, y, z,
                x + warpRadius * Math.Cos(radius), y + 1.3 * radius - 7_500, z - warpRadius * Math.Sin(radius)));
        }
        return moons;
    }

    /// <summary>
    /// posMgr.UnanchorStructure: the launching corporation asks for a control tower to be unanchored. It must be anchored and offline, in the pilot's system; the
    /// unanchoring runs for the SDE's delay (the unanchor job) and burns no fuel meanwhile. Asking again while it runs is accepted.
    /// </summary>
    public async Task UnanchorControlTowerAsync(ClientSession session, long towerId, CancellationToken cancellationToken = default)
    {
        if (_deployed is null || _scheduler is null || _source is null)
            throw Refuse("Control tower unanchoring is not available on this server.");
        // the pilot checks and the simulation's position come before any row lock
        if (await _deployed.GetAsync(towerId, cancellationToken) is not { Kind: ControlTowerKind } preview)
            throw Refuse("That control tower is not in space.");
        var unlockedState = await _deployed.TowerStateAsync(towerId, cancellationToken);
        var sight = await PreflightControlTowerAccessAsync(session, preview, requireAnchoringSkill: false, cancellationToken,
            requireInteractionDistance: unlockedState != TowerUnanchoring, requireConfigurationRole: false, operation: "unanchor");

        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (await _deployed.LockAndGetAsync(connection, transaction, towerId, cancellationToken) is not { Kind: ControlTowerKind } record)
            throw Refuse("That control tower is not in space.");
        if (record.ActivateAtMs == NeverAnchored || record.ActivateAtMs > _nowUnixMs())
            throw Refuse("The control tower is not anchored yet.");
        var state = await _deployed.TowerStateAsync(connection, transaction, towerId, cancellationToken);
        await RequireTowerAccessUnderLockAsync(session, record, sight, requireConfigurationRole: false, operation: "unanchor",
            connection, transaction, cancellationToken);
        if (state == TowerUnanchoring)
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }
        if (state != TowerOffline)
            throw Refuse("The control tower must be offline before it can be unanchored.");
        await _statics.EnsureTypesAsync([record.TypeId], cancellationToken);
        var type = _statics.Type(record.TypeId)
            ?? throw new InvalidOperationException($"SDE type {record.TypeId} for control tower {towerId} is missing.");
        var startedAtMs = _nowUnixMs();
        await _deployed.SetTowerStateAsync(connection, transaction, towerId, TowerUnanchoring, startedAtMs, cancellationToken);
        await _scheduler.ScheduleAsync(connection, transaction, new JobSpec(ControlTowerUnanchorJob, startedAtMs + ControlTowerFuelRules.UnanchoringDelayMs(type))
        {
            Key = $"tower:{towerId}",
            Payload = new ControlTowerUnanchorDue(towerId),
            OnDuplicate = OnDuplicate.Replace,
        }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        SetControlTowerSlimState(towerId, StructureUnanchored, startedAtMs);
        _scheduler.Wake();
    }

    /// <summary>
    /// The end of a control tower's unanchoring (the unanchor job): it leaves space, its record is forgotten, and its item stays, dropped where the tower was
    /// (the unanchor drop rule: a marked item in space, scoopable by its corporation). A tower that is no longer unanchoring is left alone.
    /// </summary>
    public Task CompleteControlTowerUnanchorAsync(long towerId, CancellationToken cancellationToken = default) =>
        CompleteControlTowerUnanchorAsync(towerId, null, null, null, cancellationToken);

    public async Task CompleteControlTowerUnanchorAsync(long towerId, NpgsqlConnection? connection,
        NpgsqlTransaction? transaction, Eve.Events.EventBus? events, CancellationToken cancellationToken = default,
        Action<Func<Task>>? afterCommit = null)
    {
        if (_deployed is null || await _deployed.GetAsync(towerId, cancellationToken) is not { Kind: ControlTowerKind } record
            || await _deployed.TowerStateAsync(towerId, cancellationToken) != TowerUnanchoring)
        {
            return;
        }
        // a missing item (a manual removal) has nothing to drop; otherwise the item stays where the tower was
        if (await _items.GetAsync(towerId, cancellationToken) is not null)
        {
            await _ships.SetCustomInfoAsync(towerId, StructureUnanchorDrop.HullMarker(towerId), cancellationToken);
            await _spaceItems.SaveAsync(new SpaceItem(
                towerId, record.SolarSystemId, record.Position, [1.0, 0.0, 0.0], record.Radius, record.GroupId, record.ShipId,
                record.OwnerId, record.CorporationId, record.AllianceId, 0, NeverExpires), cancellationToken);
            await PlaceUnanchorDropAsync(towerId, StructureUnanchorDrop.HullMarker(towerId), cancellationToken);
        }
        var (released, removedField) = await RemoveTowerRecordAsync(connection, transaction, events, record, cancellationToken);
        // the simulation hears of the removal only after the commit, so a removal the database refuses leaves the tower in the scene
        Func<Task> tellSimulation = () => TellSimulationOfRemovalAsync(towerId, released, removedField);
        if (afterCommit is null)
            await tellSimulation();
        else
            afterCommit(tellSimulation);
    }

    /// <summary>
    /// Removes an unanchoring tower's record in one transaction with its modules' battery grants: the grants are released, the modules are taken offline and
    /// the tower row is deleted. Without a caller transaction this opens and commits its own.
    /// </summary>
    private async Task<(IReadOnlyList<(long BatteryId, long CharacterId)> Released, SpaceItem? RemovedField)> RemoveTowerRecordAsync(NpgsqlConnection? connection,
        NpgsqlTransaction? transaction, Eve.Events.EventBus? events, DeployedRecord record, CancellationToken cancellationToken)
    {
        if (connection is null || transaction is null)
        {
            await using var ownConnection = await _source!.OpenConnectionAsync(cancellationToken);
            await using var ownTransaction = await ownConnection.BeginTransactionAsync(cancellationToken);
            var ownRemoval = await RemoveTowerRecordAsync(ownConnection, ownTransaction, events, record, cancellationToken);
            await ownTransaction.CommitAsync(cancellationToken);
            return ownRemoval;
        }
        var released = await _deployed!.ReleaseBatteryControlsOfTowerAsync(connection, transaction, record.DeployableId, cancellationToken);
        await OfflineStarbaseModulesAsync(connection, transaction, record.DeployableId, cancellationToken);
        // the tower's force field rows leave in this transaction with the tower row, so no field outlives its tower
        var removedField = await RemoveForceFieldInTransactionAsync(connection, transaction, record.DeployableId, cancellationToken);
        if (await _deployed.RemoveAsync(connection, transaction, record.DeployableId, cancellationToken) && record.CorporationId > 0 && events is not null)
            await events.PublishInTransactionAsync(connection, transaction, ControlTowerEligibilityChangedTopic,
                new ControlTowerEligibilityChanged(record.CorporationId), $"tower:{record.DeployableId}", cancellationToken);
        return (released, removedField);
    }

    /// <summary>The simulation's side of a removed tower: the tower leaves the scene, and its modules, now offline, are told offline with no holder.</summary>
    private async Task TellSimulationOfRemovalAsync(long towerId, IReadOnlyList<(long BatteryId, long CharacterId)> released, SpaceItem? removedField)
    {
        _sim.RemoveDeployed(towerId);
        if (removedField is not null)
        {
            var answer = await _sim.RemoveForceFieldAsync(new SimRemoveForceField(towerId, removedField.SolarSystemId), CancellationToken.None).ConfigureAwait(false);
            if (answer.Reason.Length > 0 && answer.Reason != ForceFieldNotInSpace)
                _log?.Invoke($"control tower {towerId} unanchored: the simulation did not remove its force field: {answer.Reason}");
        }
        await OfflineBatteriesAsync(towerId).ConfigureAwait(false);
        if (released.Count > 0)
            _log?.Invoke($"control tower {towerId} unanchored: released {released.Count} battery grant(s)");
    }
}

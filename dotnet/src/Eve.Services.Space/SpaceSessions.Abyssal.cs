using System.Collections.Concurrent;
using Eve.Characters;
using Eve.Content;
using Eve.Inventory;
using Eve.Dogma;
using Eve.Macho;
using Eve.Marshal;
using Eve.Npc;
using Eve.Ships;
using Eve.Space;

namespace Eve.Services.Space;

/// <summary>A Host transaction result needed to rebind the client to its existing Abyssal ship.</summary>
public sealed record AbyssalReconnectBinding(string ContentId, SimSceneKey Scene);

public sealed record AbyssalParticipantDisconnectedNotice(
    long InstanceId, string ContentId, long CharacterId, long ShipId, int LocationId, int RoomIndex);

public sealed record AbyssalParticipantDestroyedNotice(
    long InstanceId, string ContentId, long CharacterId, long ShipId, int LocationId, int RoomIndex, SimDestroyed Destroyed, ClientSession? Session);

public sealed record AbyssalNpcDestroyedNotice(
    long InstanceId, string ContentId, int LocationId, int RoomIndex, long EntityId, int TypeId, long KillerShipId);

public sealed record AbyssalSimulationParticipantLost(long CharacterId, long ShipId, int LocationId, int RoomIndex, ClientSession? Session);

public sealed record AbyssalSimulationRunLost(long InstanceId, string ContentId, IReadOnlyList<AbyssalSimulationParticipantLost> Participants);

public sealed record SimAbyssalTracePublishReply(
    bool Materialized, bool AlreadyPresent, int RecipientCount, bool ActorNotified, string? Error)
{
    public bool Succeeded => Error is null;
}

public sealed record SimAbyssalTraceRemoveReply(
    bool Removed, bool AlreadyAbsent, int RecipientCount, string? Error)
{
    public bool Succeeded => Error is null;
}

public sealed record AbyssalParticipantSettlementResult(
    bool ResidentRemoved, bool AlreadyAbsent, bool SessionUpdated, string? Error)
{
    public bool Succeeded => Error is null;
}

public sealed record AbyssalParticipantPoseResult(
    long ShipId, SimSceneKey Scene, IReadOnlyList<double> Position, IReadOnlyList<double> Direction, string? Error)
{
    public bool Found => Error is null;
    public SimAbyssalDeploymentSnapshot? Deployment { get; init; }
    public bool Tethered { get; init; }
    public bool SelfDestructPending { get; init; }
    public bool SessionChanging { get; init; }
}

public sealed partial class SpaceSessions
{
    private sealed record AbyssalNpcRuntime(string ContentId, long InstanceId, int RoomIndex);
    private sealed record AbyssalMoveReceipt(
        string ContentId, long CharacterId, long ShipId, SimSceneKey Source, SimSceneKey Destination);
    private sealed record AbyssalOfflineResident(
        ulong SessionId, long CharacterId, long ShipId, string? ContentId, SimSceneKey Scene,
        AbyssalMoveReceipt? LastMove = null);

    private readonly ConcurrentDictionary<long, AbyssalOfflineResident> _abyssalOfflineResidents = new();
    private readonly ConcurrentDictionary<long, SimAbyssalTrace> _abyssalTracesByItem = new();
    private readonly SemaphoreSlim _abyssalTraceSerial = new(1, 1);
    private readonly ConcurrentDictionary<SimSceneKey, IReadOnlyList<EffectDef>> _abyssalEnvironmentByScene = new();

    /// <summary>The Host loads an active participant's run in its own database transaction before space entry.</summary>
    public Func<ClientSession, long, long, CancellationToken, Task<AbyssalReconnectBinding?>>? FindAbyssalReconnectAsync { get; set; }

    /// <summary>The Host persists participant disconnects using its caller-owned transaction.</summary>
    public Func<AbyssalParticipantDisconnectedNotice, CancellationToken, ValueTask>? AbyssalParticipantDisconnectedAsync { get; set; }

    /// <summary>The Host persists participant destruction and coordinates death settlement in its caller-owned transaction.</summary>
    public Func<AbyssalParticipantDestroyedNotice, CancellationToken, ValueTask>? AbyssalParticipantDestroyedAsync { get; set; }

    /// <summary>The Host records an Abyssal NPC death in the domain ledger; the normal bounty, wreck and standing path is bypassed.</summary>
    public Func<AbyssalNpcDestroyedNotice, CancellationToken, ValueTask>? AbyssalNpcDestroyedAsync { get; set; }

    /// <summary>The Host performs committed restart reconciliation and administrative origin returns in its caller-owned transaction.</summary>
    public Func<AbyssalSimulationRunLost, CancellationToken, ValueTask>? AbyssalSimulationLostAsync { get; set; }

    /// <summary>The Host resolves weather and room effects through the pinned SDE and Dogma static data.</summary>
    public Func<string, int, IReadOnlyList<EffectDef>>? AbyssalEnvironmentEffects { get; set; }

    private async Task FinishAbyssalParticipantDeathAsync(Pilot? pilot, AbyssalOfflineResident? offline, SimDestroyed destroyed)
    {
        if (!_dying.TryAdd(destroyed.ShipId, 0)) return;
        var sessionId = destroyed.SessionId;
        try
        {
            if (pilot is not null)
            {
                pilot.End();
                await _charges.CloseAsync((ulong)pilot.Session.SessionId).ConfigureAwait(false);
                await CancelRepairsAsync(pilot, CancellationToken.None).ConfigureAwait(false);
            }

            var scene = pilot is { } activePilot
                ? activePilot.SimulationScene
                : offline?.Scene ?? throw new InvalidOperationException("The destroyed resident has no scene identity.");
            var contentId = pilot?.AbyssalContentId ?? offline?.ContentId;
            var characterId = pilot?.CharacterId ?? offline?.CharacterId ?? 0;
            if (scene.InstanceId <= 0 || contentId is null || characterId <= 0)
                throw new InvalidOperationException("The destroyed resident has no Abyssal participant identity.");
            if (AbyssalParticipantDestroyedAsync is not { } destroyedHandler)
                throw new InvalidOperationException("Abyssal participant death requires the Host domain callback.");

            await destroyedHandler(new AbyssalParticipantDestroyedNotice(
                scene.InstanceId,
                contentId,
                characterId,
                destroyed.ShipId,
                scene.LocationId,
                scene.RoomIndex,
                destroyed,
                pilot?.Session), CancellationToken.None).ConfigureAwait(false);

            if (pilot is not null) _pilots.TryRemove(sessionId, out _);
            _abyssalOfflineResidents.TryRemove(destroyed.ShipId, out _);
            _sim.Leave(sessionId, LeaveWhy.LoggedOff);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _log?.Invoke($"Abyssal ship destruction for {destroyed.ShipId} could not be persisted: {error.Message}");
        }
        finally
        {
            _dying.TryRemove(destroyed.ShipId, out _);
            if (_deaths.TryRemove(destroyed.ShipId, out var waiting)) waiting.TrySetResult(null);
        }
    }

    public Task<string?> EnsureAbyssalSceneAsync(SimAbyssalSceneSpec spec, CancellationToken cancellationToken = default) =>
        EnsureAbyssalSceneAsync(spec, Array.Empty<EffectDef>(), cancellationToken);

    /// <summary>Ensures the private room and installs its exact SDE-resolved Dogma effects for baseline recomputation.</summary>
    public async Task<string?> EnsureAbyssalSceneAsync(
        SimAbyssalSceneSpec spec,
        IReadOnlyList<EffectDef> dogmaEffects,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dogmaEffects);
        if ((spec.Environment.GlobalModifiers.Count > 0 || spec.Environment.Fields.Count > 0) && dogmaEffects.Count == 0)
            throw new InvalidOperationException("Abyssal environment modifiers require their resolved Dogma effects.");
        var error = await _sim.EnsureAbyssalSceneAsync(new SimEnsureAbyssalScene(NextSpawnRequest(), spec), cancellationToken).ConfigureAwait(false);
        if (error is null) _abyssalEnvironmentByScene[spec.Scene] = dogmaEffects.ToArray();
        return error;
    }

    /// <summary>Drops the Host's Dogma view after the sim room has been explicitly cleaned up.</summary>
    /// <summary>Materializes a static Abyssal conduit using its stable item id, SDE type and room-local pose.</summary>
    public async Task<string?> EnsureAbyssalSceneObjectAsync(
        string contentId,
        SimSceneKey scene,
        long itemId,
        int typeId,
        long ownerId,
        IReadOnlyList<double> position,
        IReadOnlyList<double> direction,
        bool isOpen,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(contentId, out _))
            throw new ArgumentException("An Abyssal content id must be a UUID.", nameof(contentId));
        ArgumentNullException.ThrowIfNull(position);
        ArgumentNullException.ThrowIfNull(direction);
        if (!scene.IsValid || scene.InstanceId <= 0 || itemId <= 0 || ownerId <= 0 ||
            typeId is not (47_685 or 47_686) ||
            !IsVector(position) || !IsVector(direction) || direction.All(value => value == 0))
        {
            throw new ArgumentException("An Abyssal scene object needs a stable identity, a transfer/origin conduit type, and a finite room-local pose.");
        }
        if (!_abyssalEnvironmentByScene.ContainsKey(scene) && AbyssalEnvironmentEffects is null)
            throw new InvalidOperationException("The Abyssal room must be ensured before its static scene objects are materialized.");

        await _statics.EnsureTypesAsync([typeId], cancellationToken).ConfigureAwait(false);
        var type = _statics.Type(typeId)
            ?? throw new InvalidDataException($"Abyssal conduit type {typeId} is absent from the pinned SDE.");
        if (type.GroupId != 366 || type.CategoryId != 2 ||
            type.GraphicId != (typeId == 47_685 ? 22_130 : 22_131) ||
            !type.Attributes.TryGetValue(162, out var radius) || radius != 5_000.0 ||
            string.IsNullOrWhiteSpace(type.Name))
        {
            throw new InvalidDataException($"Abyssal conduit type {typeId} does not match the pinned SDE scene-object data.");
        }

        var directionLength = Math.Sqrt(direction.Sum(value => value * value));
        if (!double.IsFinite(directionLength) || directionLength <= 0)
            throw new ArgumentException("An Abyssal scene object direction must have finite nonzero length.", nameof(direction));
        var unitDirection = direction.Select(value => value / directionLength).ToArray();
        var sceneObject = new SimAbyssalSceneObject(
            scene, itemId, type.TypeId, type.GroupId, type.CategoryId, ownerId, radius, type.GraphicId, type.Name,
            position.ToArray(), unitDirection, isOpen);
        return await _sim.EnsureAbyssalSceneObjectAsync(
            new SimEnsureAbyssalSceneObject(NextSpawnRequest(), sceneObject), cancellationToken).ConfigureAwait(false);
    }

    public void ForgetAbyssalEnvironment(SimSceneKey scene) => _abyssalEnvironmentByScene.TryRemove(scene, out _);

    public Task<string?> RemoveAbyssalSceneAsync(SimSceneKey scene, CancellationToken cancellationToken = default) =>
        RemoveAbyssalSceneAsync(scene, [], [], cancellationToken);

    /// <summary>The caller has durably settled every participant and supplies only immutable room-plan/receipt-owned entity IDs.</summary>
    public async Task<string?> RemoveAbyssalSceneAsync(
        SimSceneKey scene, IReadOnlyList<long> settledNpcIds, IReadOnlyList<long> settledLootIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settledNpcIds);
        ArgumentNullException.ThrowIfNull(settledLootIds);
        if (!scene.IsValid || scene.InstanceId <= 0) return "BAD_ABYSSAL_SCENE_KEY";
        static bool Ids(IReadOnlyList<long> ids) => ids.Count <= 4096 && ids.All(id => id > 0) && ids.Distinct().Count() == ids.Count;
        if (!Ids(settledNpcIds) || !Ids(settledLootIds) || settledNpcIds.Intersect(settledLootIds).Any())
            return "BAD_ABYSSAL_SCENE_CLEANUP_IDS";
        if (_pilots.Values.Any(pilot => pilot.SimulationScene == scene) ||
            _abyssalOfflineResidents.Values.Any(resident => resident.Scene == scene))
            return "ABYSSAL_SCENE_NOT_EMPTY";
        var request = new SimRemoveAbyssalScene(NextSpawnRequest(), scene)
        {
            SettledNpcIds = settledNpcIds.ToArray(), SettledLootIds = settledLootIds.ToArray(),
        };
        var error = await _sim.RemoveAbyssalSceneAsync(request, cancellationToken).ConfigureAwait(false);
        if (error is null)
        {
            ForgetAbyssalEnvironment(scene);
            ForgetAbyssalEnvironmentObjects(scene);
            foreach (var cache in _abyssalRewardCaches.Where(pair => pair.Value.Scene == scene))
                _abyssalRewardCaches.TryRemove(cache.Key, out _);
            foreach (var npc in _npcShips.Where(pair => pair.Value.SolarSystemId == scene.LocationId &&
                pair.Value.Abyssal is { } run && run.InstanceId == scene.InstanceId && run.RoomIndex == scene.RoomIndex))
                _npcShips.TryRemove(npc.Key, out _);
        }
        return error;
    }

    /// <summary>Reads both pose vectors from the authoritative resident Pilot in the expected private scene.</summary>
    public async Task<AbyssalParticipantPoseResult> GetAbyssalParticipantPoseAsync(
        string contentId,
        long shipId,
        SimSceneKey expectedScene,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(contentId, out _) || shipId <= 0 || !expectedScene.IsValid || expectedScene.InstanceId <= 0)
            return new(shipId, expectedScene, [], [], "BAD_ABYSSAL_POSE_IDENTITY");
        var pilot = _pilots.Values.FirstOrDefault(candidate => candidate.ShipId == shipId);
        if (pilot is not null &&
            (pilot.SimulationScene != expectedScene ||
             !string.Equals(pilot.AbyssalContentId, contentId, StringComparison.Ordinal)))
        {
            return new(shipId, expectedScene, [], [], "ABYSSAL_HOST_POSE_RESIDENT_MISMATCH");
        }
        if (pilot is null && _abyssalOfflineResidents.TryGetValue(shipId, out var offline) &&
            (offline.Scene != expectedScene || !string.Equals(offline.ContentId, contentId, StringComparison.Ordinal)))
        {
            return new(shipId, expectedScene, [], [], "ABYSSAL_OFFLINE_POSE_RESIDENT_MISMATCH");
        }

        try
        {
            var reply = await _sim.GetAbyssalParticipantPoseAsync(
                new SimGetAbyssalParticipantPose(NextSpawnRequest(), expectedScene, shipId),
                cancellationToken).ConfigureAwait(false);
            if (reply.Error is not null) return new(shipId, expectedScene, [], [], reply.Error);
            if (reply.Scene != expectedScene || reply.ShipId != shipId ||
                !IsVector(reply.Position) || !IsVector(reply.Direction) || reply.Direction.All(value => value == 0))
            {
                return new(shipId, expectedScene, [], [], "ABYSSAL_POSE_REPLY_MISMATCH");
            }
            return new(shipId, expectedScene, reply.Position.ToArray(), reply.Direction.ToArray(), null) { Deployment = reply.Deployment };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            return new(shipId, expectedScene, [], [], error.Message);
        }
    }

    /// <summary>Reads one authoritative pose for the active ship in its public source system, before an Abyssal run exists.</summary>
    public async Task<AbyssalParticipantPoseResult> GetAbyssalDeploymentPoseAsync(
        long shipId,
        SimSceneKey expectedScene,
        CancellationToken cancellationToken = default)
    {
        if (shipId <= 0 || !expectedScene.IsValid || expectedScene.InstanceId != 0)
            return new(shipId, expectedScene, [], [], "BAD_ABYSSAL_DEPLOYMENT_POSE_IDENTITY");

        var matches = _pilots.Values.Where(candidate => candidate.ShipId == shipId).ToArray();
        if (matches.Length == 0)
            return new(shipId, expectedScene, [], [], "ABYSSAL_DEPLOYMENT_PILOT_NOT_ACTIVE");
        if (matches.Length > 1)
            return new(shipId, expectedScene, [], [], "ABYSSAL_DEPLOYMENT_PILOT_AMBIGUOUS");

        var pilot = matches[0];
        if (pilot.SimulationScene != expectedScene || pilot.AbyssalContentId is not null)
            return new(shipId, expectedScene, [], [], "ABYSSAL_DEPLOYMENT_PILOT_SCENE_MISMATCH");

        var result = await QueryAbyssalPoseAsync(shipId, expectedScene, cancellationToken).ConfigureAwait(false);
        if (result.Found && result.Deployment is null)
            return new(shipId, expectedScene, [], [], "ABYSSAL_DEPLOYMENT_SNAPSHOT_MISSING");
        return result with
        {
            Tethered = IsTethered(pilot.Session), SelfDestructPending = _deaths.ContainsKey(shipId),
            SessionChanging = _transitions.SessionChangeActive((ulong)pilot.Session.SessionId),
        };
    }

    private async Task<AbyssalParticipantPoseResult> QueryAbyssalPoseAsync(
        long shipId,
        SimSceneKey expectedScene,
        CancellationToken cancellationToken)
    {
        try
        {
            var requestId = NextSpawnRequest();
            var reply = await _sim.GetAbyssalParticipantPoseAsync(
                new SimGetAbyssalParticipantPose(requestId, expectedScene, shipId),
                cancellationToken).ConfigureAwait(false);
            if (reply.RequestId != requestId)
                return new(shipId, expectedScene, [], [], "ABYSSAL_POSE_REPLY_MISMATCH");
            if (reply.Error is not null)
                return new(shipId, expectedScene, [], [], reply.Error);
            if (reply.Scene != expectedScene || reply.ShipId != shipId ||
                !IsVector(reply.Position) || !IsVector(reply.Direction) || reply.Direction.All(value => value == 0))
            {
                return new(shipId, expectedScene, [], [], "ABYSSAL_POSE_REPLY_MISMATCH");
            }
            return new(shipId, expectedScene, reply.Position.ToArray(), reply.Direction.ToArray(), null) { Deployment = reply.Deployment };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            return new(shipId, expectedScene, [], [], error.Message);
        }
    }

    /// <summary>Performs a gate or origin transfer. The simulator resolves the resident session by ship ID, so disconnected participants can move too.</summary>
    public async Task<string?> MoveAbyssalParticipantAsync(
        string contentId,
        SimMoveAbyssalParticipant move,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(contentId, out _)) throw new ArgumentException("An Abyssal content id must be a UUID.", nameof(contentId));
        var pilot = _pilots.Values.FirstOrDefault(candidate => candidate.ShipId == move.ShipId);
        if (pilot is not null && pilot.SimulationScene != move.Source)
        {
            if (IsExactAbyssalMoveRetry(pilot.LastAbyssalMove, pilot.CharacterId, pilot.ShipId,
                    pilot.SimulationScene, pilot.AbyssalContentId, contentId, move.Source, move.Destination))
                return null;
            return "ABYSSAL_HOST_SCENE_MISMATCH";
        }
        if (pilot?.AbyssalContentId is { } activeContent && !string.Equals(activeContent, contentId, StringComparison.Ordinal))
            return "ABYSSAL_HOST_CONTENT_MISMATCH";
        if (pilot is null && _abyssalOfflineResidents.TryGetValue(move.ShipId, out var offline))
        {
            if (offline.Scene != move.Source)
            {
                if (IsExactAbyssalMoveRetry(offline.LastMove, offline.CharacterId, offline.ShipId,
                        offline.Scene, offline.ContentId, contentId, move.Source, move.Destination))
                    return null;
                return "ABYSSAL_OFFLINE_SCENE_MISMATCH";
            }
            if (offline.ContentId is { } offlineContent && !string.Equals(offlineContent, contentId, StringComparison.Ordinal))
                return "ABYSSAL_OFFLINE_CONTENT_MISMATCH";
        }

        var sessionHandoff = pilot is not null && (move.Source.InstanceId == 0) != (move.Destination.InstanceId == 0);
        var destinationSystem = sessionHandoff ? _systems.Get(move.Destination.LocationId) : null;
        if (sessionHandoff && destinationSystem is null)
            return $"ABYSSAL_SESSION_SYSTEM_NOT_FOUND_{move.Destination.LocationId}";
        var request = move with { SessionHandoff = sessionHandoff };
        string? error;
        if (sessionHandoff)
        {
            error = await _sim.MoveAbyssalParticipantInOrderAsync(request, reply =>
            {
                if (reply.Error is not null || pilot is null || destinationSystem is null) return;
                pilot.SimulationScene = move.Destination;
                pilot.SolarSystemId = move.Destination.LocationId;
                pilot.AbyssalContentId = move.Destination.InstanceId > 0 ? contentId : null;
                pilot.LastAbyssalMove = new AbyssalMoveReceipt(
                    contentId, pilot.CharacterId, pilot.ShipId, move.Source, move.Destination);
                ApplyAbyssalSessionHandoff(pilot.Session, move.Destination, destinationSystem);
            }, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            error = await _sim.MoveAbyssalParticipantAsync(request, cancellationToken).ConfigureAwait(false);
        }
        if (error is not null) return error;
        if (pilot is null)
        {
            if (_abyssalOfflineResidents.TryGetValue(move.ShipId, out var offlineResident))
            {
                _abyssalOfflineResidents[move.ShipId] = offlineResident with
                {
                    Scene = move.Destination,
                    ContentId = move.Destination.InstanceId > 0 ? contentId : null,
                    LastMove = new AbyssalMoveReceipt(
                        contentId, offlineResident.CharacterId, offlineResident.ShipId, move.Source, move.Destination),
                };
            }
            return null;
        }
        pilot.SimulationScene = move.Destination;
        pilot.SolarSystemId = move.Destination.LocationId;
        pilot.AbyssalContentId = move.Destination.InstanceId > 0 ? contentId : null;
        pilot.LastAbyssalMove = new AbyssalMoveReceipt(
            contentId, pilot.CharacterId, pilot.ShipId, move.Source, move.Destination);
        await RefreshEnvironmentAsync(pilot.Session, pilot.CharacterId, pilot.ShipId).ConfigureAwait(false);
        return null;
    }

    private void ApplyAbyssalSessionHandoff(ClientSession session, SimSceneKey destination, Eve.Universe.SolarSystemInfo system)
    {
        ReleaseBoundObjects(session);
        _transitions.StartSessionChange((ulong)session.SessionId);
        session.Change(AbyssalSessionLocationAttributes(destination, system));
    }

    private static (string Name, PyValue Value)[] AbyssalSessionLocationAttributes(
        SimSceneKey destination,
        Eve.Universe.SolarSystemInfo system)
    {
        var systemId = new PyInt(destination.LocationId);
        return
        [
            ("solarsystemid", systemId),
            ("solarsystemid2", systemId),
            ("constellationid", new PyInt(system.ConstellationId)),
            ("regionid", new PyInt(system.RegionId)),
            ("locationid", systemId),
        ];
    }

    /// <summary>Moves a participant while carrying the authoritative pose read by the simulator from its source room.</summary>
    private static bool IsExactAbyssalMoveRetry(
        AbyssalMoveReceipt? receipt,
        long currentCharacterId,
        long currentShipId,
        SimSceneKey currentScene,
        string? currentContentId,
        string contentId,
        SimSceneKey source,
        SimSceneKey destination)
    {
        if (receipt is null || currentCharacterId <= 0 || currentShipId <= 0 || currentScene != destination)
            return false;

        var expectedContentAtDestination = destination.InstanceId > 0 ? contentId : null;
        return receipt.CharacterId == currentCharacterId &&
            receipt.ShipId == currentShipId &&
            receipt.Source == source &&
            receipt.Destination == destination &&
            string.Equals(receipt.ContentId, contentId, StringComparison.Ordinal) &&
            string.Equals(currentContentId, expectedContentAtDestination, StringComparison.Ordinal);
    }

    public Task<string?> MoveAbyssalParticipantAsync(
        string contentId,
        long shipId,
        SimSceneKey source,
        SimSceneKey destination,
        CancellationToken cancellationToken = default)
    {
        if (source.InstanceId <= 0 || destination.InstanceId <= 0)
            return Task.FromResult<string?>("ABYSSAL_SYSTEM_HANDOFF_REQUIRES_EXPLICIT_POSE");
        return MoveAbyssalParticipantAsync(contentId,
            new SimMoveAbyssalParticipant(NextSpawnRequest(), shipId, source, destination, [], [])
            {
                PreserveSourcePose = true
            },
            cancellationToken);
    }

    /// <summary>Transfers a participant with the caller-saved authoritative pose and an internally allocated SimLink request id.</summary>
    public Task<string?> MoveAbyssalParticipantAsync(
        string contentId,
        long shipId,
        SimSceneKey source,
        SimSceneKey destination,
        IReadOnlyList<double> position,
        IReadOnlyList<double> direction,
        CancellationToken cancellationToken = default)
    {
        if (shipId <= 0 || !source.IsValid || !destination.IsValid ||
            !IsVector(position) || !IsVector(direction) || direction.All(value => value == 0))
        {
            return Task.FromResult<string?>("BAD_ABYSSAL_EXPLICIT_POSE_MOVE");
        }

        return MoveAbyssalParticipantAsync(contentId,
            new SimMoveAbyssalParticipant(NextSpawnRequest(), shipId, source, destination, position.ToArray(), direction.ToArray()),
            cancellationToken);
    }

    /// <summary>Spawns a domain-reserved Abyssal NPC without enabling ordinary bounty or wreck handling.</summary>
    /// <summary>Builds a run-owned Abyssal NPC ship from the pinned SDE and NPC weapon rules; position is absolute in the private scene.</summary>
    public async Task<SimSpawnReply> SpawnAbyssalNpcAsync(
        string contentId,
        SimSceneKey scene,
        long shipId,
        int typeId,
        IReadOnlyList<double> position,
        IReadOnlyList<double> direction,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(contentId, out _))
            throw new ArgumentException("An Abyssal content id must be a UUID.", nameof(contentId));
        if (!scene.IsValid || scene.InstanceId <= 0 || shipId <= 0 || typeId <= 0 ||
            !IsVector(position) || !IsVector(direction) || direction.All(value => value == 0))
        {
            throw new ArgumentException("An Abyssal NPC needs a valid private scene, stable ship id, static type, and finite pose.");
        }

        await EnsureNpcTypesAsync([typeId], cancellationToken).ConfigureAwait(false);
        var type = _statics.Type(typeId)
            ?? throw new InvalidDataException($"Abyssal NPC type {typeId} is absent from the pinned SDE.");
        if (!NpcHull.HasHitPoints(type))
            throw new InvalidDataException($"Abyssal NPC type {typeId} has no static hit points and cannot be settled as a kill.");

        var hull = SimShips.OfType(shipId, type);
        var weapon = NpcArmament.HullWeapon(type, shipId, _statics);
        var aggressionRange = NpcArmament.AggressionRange(type, _statics);
        var ship = hull with
        {
            MaxTargetRange = Math.Max(hull.MaxTargetRange, aggressionRange),
            MaxLockedTargets = Math.Max(NpcMinimumLockSlots, hull.MaxLockedTargets),
        };
        var identity = new SimNpcIdentity(
            NpcHostileThreshold,
            NpcFriendlyThreshold,
            aggressionRange,
            weapon is null ? [] : [weapon],
            null,
            HostWreck: false,
            SelfRepairs: NpcArmament.SelfRepairsOf(type, _statics));
        var length = Math.Sqrt(direction.Sum(value => value * value));
        if (!double.IsFinite(length) || length <= 0)
            throw new ArgumentException("An Abyssal NPC direction must have finite nonzero length.", nameof(direction));
        var unitDirection = direction.Select(value => value / length).ToArray();
        var spawn = new SimSpawn(
            NextSpawnRequest(),
            scene,
            ship,
            0,
            position.ToArray(),
            [0.0, 0.0, 0.0],
            unitDirection,
            NpcBehavior,
            identity);
        return await SpawnAbyssalNpcAsync(contentId, spawn, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SimSpawnReply> SpawnAbyssalNpcAsync(
        string contentId,
        SimSpawn spawn,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(contentId, out var parsedContentId)) throw new ArgumentException("An Abyssal content id must be a UUID.", nameof(contentId));
        var normalizedContentId = parsedContentId.ToString("D");
        if (spawn.AbyssalContentId is { } embeddedContentId
            && (!Guid.TryParse(embeddedContentId, out var embeddedGuid) || embeddedGuid != parsedContentId))
            throw new ArgumentException("The spawn payload content id does not match its Abyssal run.", nameof(spawn));
        if (!spawn.Scene.IsValid || spawn.Scene.InstanceId <= 0 || spawn.Ship.ShipId <= 0 || spawn.Ship.TypeId <= 0)
            throw new ArgumentException("The NPC must belong to a valid Abyssal scene and have a static ship type.", nameof(spawn));
        if (!_abyssalEnvironmentByScene.ContainsKey(spawn.Scene) && AbyssalEnvironmentEffects is null)
            throw new InvalidOperationException("The Abyssal room must be ensured with its Dogma environment before NPC spawn.");
        await EnsureNpcTypesAsync([spawn.Ship.TypeId], cancellationToken).ConfigureAwait(false);
        var keyedSpawn = spawn with { AbyssalContentId = normalizedContentId };
        _abyssalNpcBaselines.TryAdd(spawn.Ship.ShipId, keyedSpawn);
        var suppressed = ApplyAbyssalEnvironment(keyedSpawn, normalizedContentId);
        var reply = await _sim.SpawnAsync(suppressed, cancellationToken).ConfigureAwait(false);
        if (reply.Error is null)
        {
            _npcShips[spawn.Ship.ShipId] = new NpcShip(
                null,
                spawn.Ship.TypeId,
                spawn.Scene.LocationId,
                Abyssal: new AbyssalNpcRuntime(normalizedContentId, spawn.Scene.InstanceId, spawn.Scene.RoomIndex));
        }
        return reply;
    }

    /// <summary>
    /// Completes a committed Abyssal participant death. The Host supplies the result of its caller-owned CharacterShips transaction;
    /// this method only removes the resident and updates a live session from the resulting capsule/rookie ship.
    /// </summary>
    public async Task<AbyssalParticipantSettlementResult> CompleteAbyssalParticipantSettlementAsync(
        AbyssalParticipantDeathSettlementRequestedEvent request,
        PodDeath capsuleDeath,
        ShipDeath? shipDeath = null,
        SimDestroyed? destroyed = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(capsuleDeath);
        if (request.InstanceId <= 0 || !Guid.TryParse(request.ContentId, out _) ||
            request.RecipientCharacterId <= 0 || request.ShipId <= 0 ||
            request.SpaceLocationId <= 0 || request.RoomOrdinal is < 1 or > 3 ||
            !Enum.IsDefined(request.Cause) ||
            capsuleDeath.Character.Id != request.RecipientCharacterId ||
            (shipDeath is not null &&
                (shipDeath.Ship.Id != request.ShipId || shipDeath.Character.Id != request.RecipientCharacterId)))
        {
            return new(false, false, false, "BAD_ABYSSAL_PARTICIPANT_SETTLEMENT");
        }

        var scene = new SimSceneKey(request.SpaceLocationId, request.InstanceId, request.RoomOrdinal);
        var pilotMatches = _pilots.Values.Where(candidate => candidate.ShipId == request.ShipId).ToArray();
        if (pilotMatches.Length > 1)
            return new(false, false, false, "ABYSSAL_SETTLEMENT_MULTIPLE_ACTIVE_RESIDENTS");
        var pilot = pilotMatches.SingleOrDefault();
        if (pilot is not null &&
            (pilot.CharacterId != request.RecipientCharacterId ||
             pilot.SimulationScene != scene ||
             !string.Equals(pilot.AbyssalContentId, request.ContentId, StringComparison.Ordinal)))
        {
            return new(false, false, false, "ABYSSAL_SETTLEMENT_ACTIVE_RESIDENT_MISMATCH");
        }

        var hasOffline = _abyssalOfflineResidents.TryGetValue(request.ShipId, out var offline);
        if (hasOffline &&
            (offline!.CharacterId != request.RecipientCharacterId ||
             offline.Scene != scene ||
             !string.Equals(offline.ContentId, request.ContentId, StringComparison.Ordinal)))
        {
            return new(false, false, false, "ABYSSAL_SETTLEMENT_OFFLINE_RESIDENT_MISMATCH");
        }
        if (pilot is not null && hasOffline)
            return new(false, false, false, "ABYSSAL_SETTLEMENT_DUPLICATE_HOST_RESIDENT");

        var station = pilot is null ? null : _stations.Get(capsuleDeath.Ship.LocationId);
        if (pilot is not null && station is null)
            return new(false, false, false, $"ABYSSAL_SETTLEMENT_STATION_NOT_FOUND_{capsuleDeath.Ship.LocationId}");

        SimRemoveAbyssalParticipantReply removed;
        try
        {
            removed = await _sim.RemoveAbyssalParticipantAsync(
                new SimRemoveAbyssalParticipant(NextSpawnRequest(), scene, request.ShipId),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            _log?.Invoke($"Abyssal participant {request.ShipId} could not be detached after settlement: {error.Message}");
            return new(false, false, false, error.Message);
        }
        if (removed.Error is not null)
            return new(false, false, false, removed.Error);

        var sessionUpdated = false;
        if (pilot is not null)
        {
            pilot.End();
            try
            {
                await _charges.CloseAsync((ulong)pilot.Session.SessionId).ConfigureAwait(false);
                await CancelRepairsAsync(pilot, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                _log?.Invoke($"Abyssal participant {request.ShipId} local effects could not be fully closed: {error.Message}");
            }

            _pilots.TryRemove((ulong)pilot.Session.SessionId, out _);
            _abyssalOfflineResidents.TryRemove(request.ShipId, out _);
            var client = pilot.Session;
            ReleaseBoundObjects(client);
            if (shipDeath is not null) NotifyDeathClient(pilot, shipDeath);
            var systemId = new PyInt(station!.SolarSystemId);
            client.Change(
                ("stationid", new PyInt(station.Id)),
                ("stationid2", new PyInt(station.Id)),
                ("solarsystemid", None),
                ("solarsystemid2", systemId),
                ("constellationid", new PyInt(station.ConstellationId)),
                ("regionid", new PyInt(station.RegionId)),
                ("shipid", new PyLong(capsuleDeath.Ship.Id)),
                ("locationid", new PyInt(station.Id)));
            if (shipDeath is not null && destroyed is not null)
                RaiseShipLost(pilot, shipDeath, destroyed);
            NotifyAbyssalCapsuleDeath(client, capsuleDeath);
            sessionUpdated = true;
        }
        else
        {
            _abyssalOfflineResidents.TryRemove(request.ShipId, out _);
        }

        return new(
            !removed.AlreadyAbsent,
            removed.AlreadyAbsent,
            sessionUpdated,
            null);
    }

    /// <summary>
    /// Resumes cleanup after the Host proved, from its persisted item/character rows, that clone settlement already completed.
    /// No synthetic ShipDeath or PodDeath is accepted on this retry path.
    /// </summary>
    public async Task<AbyssalParticipantSettlementResult> ResumeAbyssalParticipantSettlementAsync(
        AbyssalParticipantDeathSettlementRequestedEvent request,
        Character persistedCharacter,
        Item activeShip,
        ShipDeath? shipDeath = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(persistedCharacter);
        ArgumentNullException.ThrowIfNull(activeShip);
        if (request.InstanceId <= 0 || !Guid.TryParse(request.ContentId, out _) ||
            !Enum.IsDefined(request.Cause) ||
            !string.Equals(request.SettlementKey,
                $"abyssal:{request.ContentId}:death:{request.RecipientCharacterId}",
                StringComparison.Ordinal) ||
            request.RecipientCharacterId <= 0 || request.ShipId <= 0 ||
            request.SpaceLocationId <= 0 || request.RoomOrdinal is < 1 or > 3 ||
            persistedCharacter.Id != request.RecipientCharacterId ||
            persistedCharacter.ShipId != activeShip.Id ||
            activeShip.Id <= 0 || activeShip.Id == request.ShipId ||
            activeShip.OwnerId != persistedCharacter.Id ||
            RookieShipOfRace?.Invoke(persistedCharacter.RaceId) is not { } rookieTypeId ||
            activeShip.TypeId != rookieTypeId ||
            activeShip.Flag != ItemFlags.Hangar ||
            !activeShip.Singleton || activeShip.Quantity != 1 ||
            (shipDeath is not null &&
                (shipDeath.Ship.Id != request.ShipId || shipDeath.Character.Id != request.RecipientCharacterId)))
        {
            return new(false, false, false, "BAD_ABYSSAL_ALREADY_SETTLED_SNAPSHOT");
        }

        var station = persistedCharacter.HomeStationId > 0 && _stations.Get(persistedCharacter.HomeStationId) is { } home
            ? home
            : _stations.Get(persistedCharacter.StationId);
        if (station is null || persistedCharacter.StationId != station.Id || activeShip.LocationId != station.Id)
            return new(false, false, false, "ABYSSAL_ALREADY_SETTLED_CLONE_MISMATCH");

        // The caller must invoke this only after its committed clone check also proved the destroyed ship row absent.
        // Validate the persisted Character/item snapshot here; never invent hull or capsule death records for a retry.
        var scene = new SimSceneKey(request.SpaceLocationId, request.InstanceId, request.RoomOrdinal);
        var pilotMatches = _pilots.Values.Where(candidate => candidate.ShipId == request.ShipId).ToArray();
        if (pilotMatches.Length > 1)
            return new(false, false, false, "ABYSSAL_SETTLEMENT_MULTIPLE_ACTIVE_RESIDENTS");
        var pilot = pilotMatches.SingleOrDefault();
        if (pilot is not null &&
            (pilot.CharacterId != request.RecipientCharacterId ||
             pilot.SimulationScene != scene ||
             !string.Equals(pilot.AbyssalContentId, request.ContentId, StringComparison.Ordinal)))
        {
            return new(false, false, false, "ABYSSAL_SETTLEMENT_ACTIVE_RESIDENT_MISMATCH");
        }

        var hasOffline = _abyssalOfflineResidents.TryGetValue(request.ShipId, out var offline);
        if (hasOffline &&
            (offline!.CharacterId != request.RecipientCharacterId ||
             offline.Scene != scene ||
             !string.Equals(offline.ContentId, request.ContentId, StringComparison.Ordinal)))
        {
            return new(false, false, false, "ABYSSAL_SETTLEMENT_OFFLINE_RESIDENT_MISMATCH");
        }
        if (pilot is not null && hasOffline)
            return new(false, false, false, "ABYSSAL_SETTLEMENT_DUPLICATE_HOST_RESIDENT");

        SimRemoveAbyssalParticipantReply removed;
        try
        {
            removed = await _sim.RemoveAbyssalParticipantAsync(
                new SimRemoveAbyssalParticipant(NextSpawnRequest(), scene, request.ShipId),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            _log?.Invoke($"Abyssal already-settled participant {request.ShipId} could not be detached: {error.Message}");
            return new(false, false, false, error.Message);
        }
        if (removed.Error is not null)
            return new(false, false, false, removed.Error);

        var sessionUpdated = false;
        if (pilot is not null)
        {
            pilot.End();
            try
            {
                await _charges.CloseAsync((ulong)pilot.Session.SessionId).ConfigureAwait(false);
                await CancelRepairsAsync(pilot, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                _log?.Invoke($"Abyssal already-settled participant {request.ShipId} local effects could not be fully closed: {error.Message}");
            }

            var client = pilot.Session;
            ReleaseBoundObjects(client);
            if (shipDeath is not null) NotifyDeathClient(pilot, shipDeath);
            var system = new PyInt(station.SolarSystemId);
            client.Change(
                ("stationid", new PyInt(station.Id)),
                ("stationid2", new PyInt(station.Id)),
                ("solarsystemid", None),
                ("solarsystemid2", system),
                ("constellationid", new PyInt(station.ConstellationId)),
                ("regionid", new PyInt(station.RegionId)),
                ("shipid", new PyLong(activeShip.Id)),
                ("locationid", new PyInt(station.Id)));
            _pilots.TryRemove((ulong)pilot.Session.SessionId, out _);
            _abyssalOfflineResidents.TryRemove(request.ShipId, out _);
            sessionUpdated = true;
        }
        else
        {
            _abyssalOfflineResidents.TryRemove(request.ShipId, out _);
        }

        return new(!removed.AlreadyAbsent, removed.AlreadyAbsent, sessionUpdated, null);
    }

    private void NotifyAbyssalCapsuleDeath(ClientSession session, PodDeath death)
    {
        if (death.Corpse is { } corpse)
        {
            session.SendNotification(
                "OnItemChange", "clientID",
                InventoryWire.PackedRow(RowOf(corpse, _statics.Type(corpse.TypeId)), new PyStr("")),
                new PyDict([new(new PyInt(3), new PyInt(0))]),
                None);
        }
        session.SendNotification(
            "OnItemChange", "clientID",
            InventoryWire.PackedRow(
                RowOf(death.Capsule with { LocationId = Junkyard }, _statics.Type(death.Capsule.TypeId)),
                new PyStr("")),
            new PyDict([new(new PyInt(3), InventoryWire.Id(death.Capsule.LocationId))]),
            None);
        session.SendNotification(
            "OnCfgDataChanged", "charid", new PyStr("evelocations"),
            new PyList([
                InventoryWire.Id(death.Capsule.Id),
                new PyStr(_statics.Type(death.Capsule.TypeId)?.Name ?? "Capsule"),
                new PyInt(death.Capsule.LocationId),
                new PyInt(0),
                new PyInt(0),
                new PyInt(0),
                None
            ]));
    }

    /// <summary>Puts a Host-created, domain-owned loot container at its exact room position as a targetable ball.</summary>
    public void PlaceAbyssalLootContainer(SimPlaceWreck container)
    {
        if (!container.Scene.IsValid || container.Scene.InstanceId <= 0)
            throw new ArgumentException("Abyssal loot must target a private room scene.", nameof(container));
        if (!_abyssalEnvironmentByScene.ContainsKey(container.Scene))
            throw new InvalidOperationException("The Abyssal room must be ensured before its loot is placed.");
        _sim.PlaceWreck(container);
    }

    /// <summary>Removes one domain-owned Abyssal loot ball before its private room is removed.</summary>
    public void RemoveAbyssalLootContainer(SimSceneKey scene, long wreckId, int effect = 0)
    {
        if (!scene.IsValid || scene.InstanceId <= 0 || wreckId <= 0 || effect < 0)
            throw new ArgumentException("Abyssal loot removal requires a private room and valid wreck identity.");
        if (!_abyssalEnvironmentByScene.ContainsKey(scene))
            throw new InvalidOperationException("The Abyssal room must be ensured before its loot is removed.");
        _sim.RemoveWreck(wreckId, effect);
    }

    private static bool IsValidAbyssalTrace(SimAbyssalTrace trace) =>
        trace.SolarSystemId > 0 && trace.ItemId > 0 &&
        trace.TypeId == 48_964 && trace.GroupId == 1_991 && trace.CategoryId == 2 &&
        trace.OwnerId > 0 && double.IsFinite(trace.Radius) && trace.Radius > 0 &&
        IsVector(trace.Position) && IsVector(trace.Velocity) &&
        IsVector(trace.Direction) && trace.Direction.Any(value => value != 0) &&
        (trace.TargetPoint is null || IsVector(trace.TargetPoint)) &&
        double.IsFinite(trace.SpeedFraction) && trace.GameModeId is >= 1 and <= 3 &&
        trace.FilamentTypeId > 0 && trace.DifficultyTier is >= 0 and <= 6 && trace.ExpiresAtMs > 0;

    private static bool IsVector(IReadOnlyList<double> vector) =>
        vector.Count == 3 && vector.All(double.IsFinite);

    private static SimAbyssalTrace FreezeAbyssalTrace(SimAbyssalTrace trace) => trace with
    {
        Position = trace.Position.ToArray(),
        Velocity = trace.Velocity.ToArray(),
        Direction = trace.Direction.ToArray(),
        TargetPoint = trace.TargetPoint?.ToArray(),
    };

    private static bool SameAbyssalTrace(SimAbyssalTrace left, SimAbyssalTrace right) =>
        left.SolarSystemId == right.SolarSystemId &&
        left.ItemId == right.ItemId &&
        left.TypeId == right.TypeId &&
        left.GroupId == right.GroupId &&
        left.CategoryId == right.CategoryId &&
        left.OwnerId == right.OwnerId &&
        left.Radius == right.Radius &&
        left.Position.SequenceEqual(right.Position) &&
        left.Velocity.SequenceEqual(right.Velocity) &&
        left.Direction.SequenceEqual(right.Direction) &&
        (left.TargetPoint is null
            ? right.TargetPoint is null
            : right.TargetPoint is not null && left.TargetPoint.SequenceEqual(right.TargetPoint)) &&
        left.SpeedFraction == right.SpeedFraction &&
        left.GameModeId == right.GameModeId &&
        left.FilamentTypeId == right.FilamentTypeId &&
        left.DifficultyTier == right.DifficultyTier &&
        left.ExpiresAtMs == right.ExpiresAtMs;

    /// <summary>
    /// Ensures a Host-persisted trace exists as a real public space entity and publishes its initial AddBalls2.
    /// The item and Domain run are created by the Host in one transaction before this call.
    /// </summary>
    public async Task<SimAbyssalTracePublishReply> EnsureAbyssalTracePublishedAsync(
        SimAbyssalTrace trace,
        ulong? actorSessionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trace);
        if (!IsValidAbyssalTrace(trace) || actorSessionId == 0)
            return new(false, false, 0, false, "BAD_ABYSSAL_TRACE");
        if (trace.ExpiresAtMs <= _nowUnixMs())
            return new(false, false, 0, false, "ABYSSAL_TRACE_EXPIRED");

        await _abyssalTraceSerial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stored = FreezeAbyssalTrace(trace);
            if (_abyssalTracesByItem.TryGetValue(trace.ItemId, out var existing) && !SameAbyssalTrace(existing, stored))
                return new(false, false, 0, false, "ABYSSAL_TRACE_IDENTITY_CONFLICT");
            _abyssalTracesByItem[trace.ItemId] = stored;

            SimEnsureAbyssalTracePublishedReply reply;
            try
            {
                reply = await _sim.EnsureAbyssalTracePublishedAsync(
                    new SimEnsureAbyssalTracePublished(NextSpawnRequest(), actorSessionId, stored),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                _log?.Invoke($"Abyssal trace {trace.ItemId} could not be published: {error.Message}");
                return new(false, false, 0, false, error.Message);
            }

            if (reply.Status == SimAbyssalTraceStatus.Failed)
                return new(false, false, reply.RecipientCount, reply.ActorNotified, reply.Error ?? "ABYSSAL_TRACE_PUBLISH_FAILED");
            return new(
                true,
                reply.Status == SimAbyssalTraceStatus.AlreadyPresent,
                reply.RecipientCount,
                reply.ActorNotified,
                null);
        }
        finally
        {
            _abyssalTraceSerial.Release();
        }
    }

    /// <summary>Removes a public trace after the Host has committed its Domain and item cleanup.</summary>
    public async Task<SimAbyssalTraceRemoveReply> RemoveAbyssalTraceAsync(
        int solarSystemId,
        long traceItemId,
        CancellationToken cancellationToken = default)
    {
        if (solarSystemId <= 0 || traceItemId <= 0)
            return new(false, false, 0, "BAD_ABYSSAL_TRACE_IDENTITY");

        await _abyssalTraceSerial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_abyssalTracesByItem.TryGetValue(traceItemId, out var trace) && trace.SolarSystemId != solarSystemId)
                return new(false, false, 0, "ABYSSAL_TRACE_LOCATION_MISMATCH");

            SimRemoveAbyssalTraceReply reply;
            try
            {
                reply = await _sim.RemoveAbyssalTraceAsync(
                    new SimRemoveAbyssalTrace(NextSpawnRequest(), solarSystemId, traceItemId),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                _log?.Invoke($"Abyssal trace {traceItemId} could not be removed: {error.Message}");
                return new(false, false, 0, error.Message);
            }

            if (reply.Status == SimAbyssalTraceStatus.Failed)
                return new(false, false, reply.RecipientCount, reply.Error ?? "ABYSSAL_TRACE_REMOVE_FAILED");
            _abyssalTracesByItem.TryRemove(traceItemId, out _);
            return new(
                reply.Status == SimAbyssalTraceStatus.Removed,
                reply.Status == SimAbyssalTraceStatus.AlreadyAbsent,
                reply.RecipientCount,
                null);
        }
        finally
        {
            _abyssalTraceSerial.Release();
        }
    }

    /// <summary>Rebuilds trace runtime entities after the SimLink process has restarted; persisted trace rows remain Host-owned.</summary>
    private async Task RestoreAbyssalTracesAsync()
    {
        await _abyssalTraceSerial.WaitAsync().ConfigureAwait(false);
        try
        {
            var now = _nowUnixMs();
            foreach (var trace in _abyssalTracesByItem.Values.OrderBy(value => value.ItemId))
            {
                if (trace.ExpiresAtMs <= now) continue;
                try
                {
                    var reply = await _sim.EnsureAbyssalTracePublishedAsync(
                        new SimEnsureAbyssalTracePublished(NextSpawnRequest(), null, trace),
                        CancellationToken.None).ConfigureAwait(false);
                    if (reply.Status == SimAbyssalTraceStatus.Failed)
                        _log?.Invoke($"Abyssal trace {trace.ItemId} could not be restored: {reply.Error}");
                }
                catch (Exception error)
                {
                    _log?.Invoke($"Abyssal trace {trace.ItemId} could not be restored: {error.Message}");
                }
            }
        }
        finally
        {
            _abyssalTraceSerial.Release();
        }
    }

    private SimSpawn ApplyAbyssalEnvironment(SimSpawn spawn, string contentId)
    {
        var effects = _abyssalEnvironmentByScene.TryGetValue(spawn.Scene, out var installed)
            ? installed
            : AbyssalEnvironmentEffects?.Invoke(contentId, spawn.Scene.LocationId) ?? [];
        effects = [.. effects, .. LocalAbyssalEffects(spawn.Scene, spawn.Ship.ShipId)];
        var identity = spawn.Identity with { Wreck = null, HostWreck = false };
        if (effects.Count == 0) return spawn with { Identity = identity };

        var type = _statics.Type(spawn.Ship.TypeId)
            ?? throw new InvalidDataException($"Abyssal NPC type {spawn.Ship.TypeId} is absent from the pinned SDE.");
        var world = new DogmaWorld(_statics);
        world.Add(new DogmaItem(spawn.Ship.ShipId, type.TypeId, spawn.Ship.OwnerId, spawn.Scene.LocationId));
        world.SetBuffs(spawn.Ship.ShipId, effects);
        var effectiveType = type with { Attributes = world.Attributes(spawn.Ship.ShipId).ToDictionary(pair => pair.Key, pair => pair.Value) };
        var ship = SimShips.OfType(spawn.Ship.ShipId, effectiveType) with
        {
            CharacterId = spawn.Ship.CharacterId,
            Name = spawn.Ship.Name,
            OwnerId = spawn.Ship.OwnerId,
            CorporationId = spawn.Ship.CorporationId,
            AllianceId = spawn.Ship.AllianceId,
            WarFactionId = spawn.Ship.WarFactionId,
            SecurityStatus = spawn.Ship.SecurityStatus,
            Modules = spawn.Ship.Modules,
            Condition = spawn.Ship.Condition,
            Stance = spawn.Ship.Stance,
            DroneDamageMultiplier = spawn.Ship.DroneDamageMultiplier,
        };
        var weapon = NpcArmament.HullWeapon(effectiveType, spawn.Ship.ShipId, _statics);
        var weapons = weapon is null ? identity.Weapons : [weapon];
        var aggressionRange = NpcArmament.AggressionRange(effectiveType, _statics);
        identity = identity with { Weapons = weapons, AggressionRange = Math.Max(identity.AggressionRange, aggressionRange) };
        return spawn with { Ship = ship, Identity = identity };
    }

    private async Task<string?> RebindAbyssalParticipantAsync(
        Pilot pilot,
        string? contentId,
        SimSceneKey scene,
        CancellationToken cancellationToken)
    {
        if (contentId is not null && !Guid.TryParse(contentId, out _)) throw new ArgumentException("An Abyssal content id must be a UUID.", nameof(contentId));
        var request = new SimRebindAbyssalSession(NextSpawnRequest(), (ulong)pilot.Session.SessionId, pilot.ShipId, scene);
        var error = await _sim.RebindAbyssalSessionAsync(request, cancellationToken).ConfigureAwait(false);
        if (error is not null) return error;
        pilot.SimulationScene = scene;
        pilot.SolarSystemId = scene.LocationId;
        pilot.AbyssalContentId = contentId;
        var system = _systems.Get(scene.LocationId)
            ?? throw new InvalidDataException($"Abyssal reconnect system {scene.LocationId} is absent from the location catalog.");
        ApplyAbyssalSessionHandoff(pilot.Session, scene, system);
        return null;
    }

    private async Task<bool> ReconnectAbyssalBeforeNormalEntryAsync(
        ClientSession session,
        long characterId,
        ShipSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var sessionId = (ulong)session.SessionId;
        var binding = FindAbyssalReconnectAsync is { } find
            ? await find(session, characterId, snapshot.Ship.Id, cancellationToken).ConfigureAwait(false)
            : null;
        if (binding is null)
        {
            if (!_abyssalOfflineResidents.TryGetValue(snapshot.Ship.Id, out var returnedResident)) return false;
            if (returnedResident.Scene.InstanceId != 0)
                throw Notify("The stored Abyssal run must be reconciled before this ship can enter space.");
            var returnedPilot = new Pilot(
                session,
                characterId,
                snapshot.Ship.Id,
                snapshot.Ship.TypeId,
                returnedResident.Scene.LocationId,
                FileTimeNow(),
                snapshot.Character.StationId);
            var returnedRefusal = await RebindAbyssalParticipantAsync(returnedPilot, null, returnedResident.Scene, cancellationToken).ConfigureAwait(false);
            if (returnedRefusal is not null) throw Notify($"The returned resident ship could not be rebound: {returnedRefusal}");
            _pilots[sessionId] = returnedPilot;
            _abyssalOfflineResidents.TryRemove(snapshot.Ship.Id, out _);
            await RefreshEnvironmentAsync(session, characterId, snapshot.Ship.Id).ConfigureAwait(false);
            TellEnteredBuffs(sessionId, snapshot.Ship.Id, _nowUnixMs());
            session.Set(LoginInventoryBootstrapPending, new PyInt(1));
            session.Closed += () => OnSessionClosed(session);
            if (_spaceItems is { } returnedItems) await returnedItems.RemoveAsync(snapshot.Ship.Id, cancellationToken).ConfigureAwait(false);
            await NotifyPilotEnteredAsync(session, characterId).ConfigureAwait(false);
            SystemEntered?.Invoke(returnedResident.Scene.LocationId);
            return true;
        }
        if (!binding.Scene.IsValid || binding.Scene.InstanceId <= 0)
            throw new InvalidOperationException("The active Abyssal run returned an invalid scene key.");

        var pilot = new Pilot(
            session,
            characterId,
            snapshot.Ship.Id,
            snapshot.Ship.TypeId,
            binding.Scene.LocationId,
            FileTimeNow(),
            snapshot.Character.StationId);
        var refusal = await RebindAbyssalParticipantAsync(pilot, binding.ContentId, binding.Scene, cancellationToken).ConfigureAwait(false);
        if (refusal is not null)
            throw Notify($"The existing Abyssal ship could not be rebound: {refusal}");

        _pilots[sessionId] = pilot;
        _abyssalOfflineResidents.TryRemove(snapshot.Ship.Id, out _);
        await RefreshEnvironmentAsync(session, characterId, snapshot.Ship.Id).ConfigureAwait(false);
        TellEnteredBuffs(sessionId, snapshot.Ship.Id, _nowUnixMs());
        session.Set(LoginInventoryBootstrapPending, new PyInt(1));
        session.Closed += () => OnSessionClosed(session);
        if (_spaceItems is { } items) await items.RemoveAsync(snapshot.Ship.Id, cancellationToken).ConfigureAwait(false);
        await NotifyPilotEnteredAsync(session, characterId).ConfigureAwait(false);
        if (pilot.SimulationScene.InstanceId == 0)
            SystemEntered?.Invoke(binding.Scene.LocationId);
        return true;
    }

    private async Task NotifyPilotEnteredAsync(ClientSession session, long characterId)
    {
        if (PilotEntered is not { } entered) return;
        try
        {
            await entered(session, characterId).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _log?.Invoke($"Abyssal reconnect for character {characterId}: the squadrons in space could not be taken up: {error.Message}");
        }
    }
}

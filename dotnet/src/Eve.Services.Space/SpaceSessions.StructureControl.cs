using System.Collections.Concurrent;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Ships;
using Eve.Space;

namespace Eve.Services.Space;

public sealed partial class SpaceSessions
{
    // Socket subscribers only; StructurePilotStore owns the exclusive pilot relation.
    private readonly ConcurrentDictionary<ulong, ClientSession> _structureObservers = new();

    public Func<ClientSession, bool, CancellationToken, ValueTask>? StructureControlLeavingAsync { get; set; }

    public bool IsStructureObserver(ClientSession session) =>
        _structureObservers.TryGetValue((ulong)session.SessionId, out var observer) && ReferenceEquals(observer, session);

    public bool StructureControlSessionClosed(ClientSession session) => session.GetInt("structureControlClosed") == 1;

    public void WatchStructureControlSession(ClientSession session)
    {
        if (session.GetInt("structureControlWatched") == 1) return;
        session.Set("structureControlWatched", new PyInt(1));
        session.Closed += () =>
        {
            session.Set("structureControlClosed", new PyInt(1));
            Background(async () =>
            {
                try { await EndStructureControlAsync(session, true, CancellationToken.None); }
                finally { ForgetStructureObserver(session); }
            });
        };
    }

    public async ValueTask SetStructureObserverAsync(ClientSession session, int solarSystemId,
        long structureId, long characterId, bool active, CancellationToken token = default, long controlLeaseGeneration = 0)
    {
        if (session.SessionId <= 0 || solarSystemId <= 0 || active && (structureId <= 0 || characterId <= 0))
            throw new ArgumentOutOfRangeException(nameof(structureId));
        if (controlLeaseGeneration <= 0)
            throw new ArgumentOutOfRangeException(nameof(controlLeaseGeneration));
        var id = (ulong)session.SessionId;
        var wasObserver = IsStructureObserver(session);
        ShipSnapshot? releaseSnapshot = null;
        if (!active && _pilots.TryGetValue(id, out var releasingPilot))
            releaseSnapshot = await ModuleShipAsync(releasingPilot, token);
        long? trackedShipId = null;
        var addedTracking = false;
        if (active && IsInSpace(session) && _pilots.TryGetValue(id, out var shipPilot)
            && shipPilot.TypeId != CharacterShips.CapsuleTypeId)
        {

            var corporationId = session.GetInt("corpid") ?? 0;
            if (shipPilot.CharacterId != session.CharacterId || shipPilot.SolarSystemId != solarSystemId || corporationId <= 0)
                throw Notify("The physical ship could not be verified for structure control.");
            var abandoned = new EjectedShip(
                shipPilot.CharacterId, corporationId, shipPilot.TypeId, solarSystemId, id,
                session.GetInt("allianceid") ?? session.GetInt("allianceID") ?? 0,
                session.GetInt("warfactionid") ?? session.GetInt("warFactionID") ?? 0);
            if (_ejectedShips.TryAdd(shipPilot.ShipId, abandoned))
            {
                trackedShipId = shipPilot.ShipId;
                addedTracking = true;
            }
            else if (_ejectedShips.TryGetValue(shipPilot.ShipId, out var existing) && existing == abandoned)
                trackedShipId = shipPilot.ShipId;
            else
                throw Notify("The physical ship is already tracked as unpiloted.");
        }

        var controllerShipId = _pilots.TryGetValue(id, out var controllerBefore) ? controllerBefore.ShipId : (long?)null;
        if (active) _structureObservers[id] = session;
        try
        {
            if (active && StructureControlLeaseGenerationOf is { } readLease
                && await readLease(session, structureId, token) != controlLeaseGeneration)
                throw Notify("Structure control lease changed before attachment.");
            var refusal = await _sim.ControlStructureAsync(
                new SimControlStructure(id, solarSystemId, active ? structureId : 0, active ? characterId : 0, active, controlLeaseGeneration), token);
            if (refusal is not null) throw Notify($"Structure control was refused: {refusal}");
            session.Set("structureControlLeaseGeneration", active ? new PyLong(controlLeaseGeneration) : None);
            if (active) return;

            try
            {
                await _charges.FlushAsync(id).ConfigureAwait(false);
            }
            catch
            {
                if (!StructureControlSessionClosed(session))
                    await TryRestoreStructureObserverAsync(session, solarSystemId, controlLeaseGeneration);
                throw;
            }
            if (_pilots.TryGetValue(id, out var releasedPilot))
            {
                foreach (var moduleId in releasedPilot.StructureModuleIds.Keys)
                {
                    if (releasedPilot.ActiveModules.TryRemove(moduleId, out _)
                        && releaseSnapshot?.Fitted.Any(item => item.Id == moduleId) == true)
                    {
                        AnnounceModeChange(releasedPilot, releaseSnapshot, moduleId, started: false);
                    }
                    releasedPilot.RunningModules.TryRemove(moduleId, out _);
                    Eve.Services.Space.Effects.ProjectedEffect.MutadaptiveSpool.Reset(moduleId);
                    releasedPilot.ModuleChargeOwners.TryRemove(moduleId, out _);
                    releasedPilot.StructureModuleIds.TryRemove(moduleId, out _);
                }
            }

            try
            {
                if (_pilots.TryGetValue(id, out var currentPilot)
                    && currentPilot.StructureControlDeath is { } pendingDeath)
                {
                    await pendingDeath.ConfigureAwait(false);
                    if (_pilots.TryGetValue(id, out currentPilot) && currentPilot.RetryStructureControlDeath is { } retry)
                        await retry().ConfigureAwait(false);
                    if (!_pilots.TryGetValue(id, out currentPilot)
                        || currentPilot.StructureControlCapsulePosition is not { } deathPosition
                        || currentPilot.StructureControlCapsuleDirection is not { } deathDirection)
                        throw Notify("The destroyed ship was not persisted; structure control remains active.");

                    if (currentPilot.TypeId != CharacterShips.CapsuleTypeId)
                    {
                        var fitting = await _ships.LoadAsync(currentPilot.CharacterId, CancellationToken.None);
                        if (fitting is null
                            || fitting.Ship.TypeId != CharacterShips.CapsuleTypeId
                            || fitting.Character.ShipId != fitting.Ship.Id)
                            throw Notify("The replacement capsule was not persisted.");

                        var capsulePilot = new Pilot(
                            currentPilot.Session, currentPilot.CharacterId, fitting.Ship.Id, fitting.Ship.TypeId,
                            currentPilot.SolarSystemId, 0)
                        {
                            StructureControlDeath = currentPilot.StructureControlDeath,
                            StructureControlCapsulePosition = deathPosition,
                            StructureControlCapsuleDirection = deathDirection
                        };
                        if (!_pilots.TryUpdate(id, capsulePilot, currentPilot))
                            throw Notify("The replacement capsule could not be associated with the controller.");
                        EndTetherPilot(currentPilot);
                        currentPilot.End();
                        currentPilot = capsulePilot;
                    }
                }

                if (!StructureControlSessionClosed(session)
                    && _pilots.TryGetValue(id, out currentPilot)
                    && currentPilot.StructureControlCapsulePosition is { } position)
                {
                    if (currentPilot.StructureControlCapsuleDirection is not { } direction
                        || await _ships.LoadAsync(currentPilot.CharacterId, CancellationToken.None) is not { } capsule
                        || capsule.Ship.Id != currentPilot.ShipId
                        || capsule.Character.ShipId != currentPilot.ShipId
                        || capsule.Ship.TypeId != CharacterShips.CapsuleTypeId)
                        throw Notify("The replacement capsule was not persisted.");

                    var capsuleRefusal = await _sim.RespawnAsync(new SimRespawn(
                        id,
                        SimShips.Of(capsule, _statics, await SecurityStatusAsync(currentPilot.CharacterId, CancellationToken.None)),
                        position,
                        direction), CancellationToken.None);
                    if (capsuleRefusal is not null)
                        throw Notify($"The replacement capsule was refused by the simulation: {capsuleRefusal}");
                    currentPilot.StructureControlDeath = null;
                    currentPilot.StructureControlCapsulePosition = null;
                    currentPilot.StructureControlCapsuleDirection = null;
                }

                if (controllerShipId is { } oldShipId
                    && _ejectedShips.TryGetValue(oldShipId, out var abandonedShip)
                    && abandonedShip.StructureControlSessionId == id)
                    _ejectedShips.TryRemove(oldShipId, out _);
                _structureObservers.TryRemove(id, out _);
            }
            catch
            {
                if (StructureControlSessionClosed(session))
                    _structureObservers.TryRemove(id, out _);
                else
                    await TryRestoreStructureObserverAsync(session, solarSystemId, controlLeaseGeneration);
                throw;
            }
        }
        catch
        {
            if (addedTracking && trackedShipId is { } shipId)
                _ejectedShips.TryRemove(shipId, out _);
            if (active && !wasObserver)
                _structureObservers.TryRemove(id, out _);
            throw;
        }
    }

    private async ValueTask TryRestoreStructureObserverAsync(ClientSession session, int solarSystemId, long controlLeaseGeneration)
    {
        var id = (ulong)session.SessionId;
        if (session.GetInt("structureid") is not { } structureId || session.CharacterId is not { } characterId)
        {
            session.Close();
            _structureObservers.TryRemove(id, out _);
            return;
        }

        if (StructureControlLeaseGenerationOf is { } readLease
            && await readLease(session, structureId, CancellationToken.None) != controlLeaseGeneration)
        {
            _structureObservers.TryRemove(id, out _);
            return;
        }

        _structureObservers[id] = session;
        try
        {
            var refusal = await _sim.ControlStructureAsync(
                new SimControlStructure(id, solarSystemId, structureId, characterId, true, controlLeaseGeneration), CancellationToken.None);
            if (refusal is not null)
                throw Notify($"Structure control was refused while restoring the controller: {refusal}");
            session.Set("structureControlLeaseGeneration", new PyLong(controlLeaseGeneration));
        }
        catch (Exception error)
        {
            _structureObservers.TryRemove(id, out _);
            session.Close();
            _log?.Invoke($"structure control for session {session.SessionId} could not be restored after capsule ejection failed: {error}");
        }
    }

    public void ApplyStructureControlSession(ClientSession session, int system, long structureId,
        long originalShipId, long? originalLocationId, bool fromSpace)
    {
        var previousShipId = session.GetInt("shipid");
        CancelSafeLogoff(session);
        session.Set(LoginInventoryBootstrapPending, None);
        session.Set("deferredDockedShipId", None);
        session.Set("structureControlSystemId", new PyInt(system));
        session.Set("structureControlOriginalShipId", new PyLong(originalShipId));
        session.Set("structureControlOriginalLocationId", originalLocationId is { } location ? new PyLong(location) : None);
        session.Set("structureControlFromSpace", fromSpace ? new PyInt(1) : new PyInt(0));
        _lockedTargets.TryRemove((ulong)session.SessionId, out _);
        if (fromSpace)
        {
            if (_pilots.TryGetValue((ulong)session.SessionId, out var pilot)) EndTetherPilot(pilot);
            session.Change(("structureid", new PyLong(structureId)), ("shipid", new PyLong(structureId)), ("locationid", new PyInt(system)));
        }
        else
            session.Change(("shipid", new PyLong(structureId))); // V24.01 TakeControl preserves every other session attribute.
        if (previousShipId is > 0 && previousShipId != structureId)
            session.SendNotification("OnShipCosmeticsChanged", "charid", new PyLong(previousShipId.Value), new PyDict([]));
    }

    public void RestoreStructureControlSession(ClientSession session, long originalShipId,
        long? originalLocationId, bool fromSpace, int system)
    {
        var previousShipId = session.GetInt("shipid");
        if (fromSpace)
        {
            var physicalShip = ShipIdOf(session) ?? originalShipId;
            session.Change(("structureid", None), ("shipid", new PyLong(physicalShip)),
                ("locationid", new PyLong(originalLocationId ?? system)));
            Background(() => RefreshTetherPolicyAsync(session));
        }
        else session.Change(("shipid", new PyLong(originalShipId)));
        if (session.GetInt("shipid") is { } restoredShipId && restoredShipId > 0 && previousShipId != restoredShipId)
            session.SendNotification("OnShipCosmeticsChanged", "charid", new PyLong(restoredShipId), new PyDict([]));
        session.Set("structureControlSystemId", None);
        session.Set("structureControlOriginalShipId", None);
        session.Set("structureControlOriginalLocationId", None);
        session.Set("structureControlFromSpace", None);
    }

    public void ForgetStructureObserver(ClientSession session) => _structureObservers.TryRemove((ulong)session.SessionId, out _);

    public async ValueTask EndStructureControlAsync(ClientSession session, bool disconnected,
        CancellationToken token = default)
    {
        if (StructureControlLeavingAsync is { } leave)
            await leave(session, disconnected, token);
        else if (IsStructureObserver(session))
            throw Notify("The structure control lifecycle backend is not available.");
    }

    public async Task<bool> CanReturnToStructureShipAsync(ClientSession session, long shipId,
        int system, double x, double y, double z, double structureRadius, double shipRadius, CancellationToken token)
    {
        if (!_pilots.TryGetValue((ulong)session.SessionId, out var pilot) ||
            pilot.CharacterId != session.CharacterId || pilot.ShipId != shipId || pilot.SolarSystemId != system ||
            SolarSystemOf(session) != system ||
            await ScenePositionOfAsync(session, shipId, token) is not { } position || position.SolarSystemId != system)
            return false;
        var dx = position.X - x;
        var dy = position.Y - y;
        var dz = position.Z - z;
        var distance = Math.Sqrt(dx * dx + dy * dy + dz * dz) - structureRadius - shipRadius;
        // V24.01 maxBoardingDistance, also used by BoardAsync; the structure menu measures surface distance.
        return double.IsFinite(distance) && distance <= 6_500d;
    }

    public async ValueTask<bool> EjectStructureControlToCapsuleAsync(ClientSession session, CancellationToken token)
    {
        if (!_pilots.TryGetValue((ulong)session.SessionId, out var pilot)) return false;
        if (_statics.GroupName(_statics.Type(pilot.TypeId)?.GroupId ?? 0) == "Capsule")
        {
            if (pilot.StructureControlCapsulePosition is not null) return false;
            RestoreStructureControlSession(session, pilot.ShipId, pilot.SolarSystemId, true, pilot.SolarSystemId);
            return true;
        }
        var result = await EjectPhysicalShipAsync(session, session.GetInt("shipid") ?? 0, true, token);
        if (result is not PyTuple) return false;
        if (session.GetInt("shipid") is { } capsuleId && capsuleId > 0)
            session.SendNotification("OnShipCosmeticsChanged", "charid", new PyLong(capsuleId), new PyDict([]));
        session.Set("structureControlSystemId", None);
        session.Set("structureControlOriginalShipId", None);
        session.Set("structureControlOriginalLocationId", None);
        session.Set("structureControlFromSpace", None);
        return true;
    }
}

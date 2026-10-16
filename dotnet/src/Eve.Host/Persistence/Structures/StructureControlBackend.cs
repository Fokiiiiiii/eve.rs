using Eve.Dogma;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Space;
using Eve.Ships;
using Eve.Structures;
using Npgsql;

namespace Eve.Host;

/// <summary>A read-only projection of the real structure record and its matching SDE type; never a physical ship or a health store.</summary>
public sealed record StructureControlView(Item Item, TypeDef Type, int SolarSystemId);

/// <summary>ST-5: the persisted owner, observer acknowledgement and client session transition.</summary>
public sealed class StructureControlBackend
{
    // V24.01 structures/settings.py SETTING_DEFENSE_CAN_CONTROL_STRUCTURE.
    public const long DefenseCanControlStructure = 17;
    private readonly NpgsqlDataSource _source;
    private readonly StructureStore _structures;
    private readonly StructurePilotStore _pilots;
    private readonly PgItemStore _items;
    private readonly DogmaStatic _statics;
    private readonly SpaceSessions _space;
    private readonly Func<long, long, long, CancellationToken, ValueTask<bool>> _hasSetting;
    private readonly ConnectedSessions _connected;
    private readonly Action<string>? _log;
    // Control transitions are infrequent; one lane also orders takeover against logout/rights-loss without two-session deadlocks.
    private readonly SemaphoreSlim _gate = new(1, 1);

    public StructureControlBackend(NpgsqlDataSource source, StructureStore structures, StructurePilotStore pilots,
        PgItemStore items, DogmaStatic statics, SpaceSessions space,
        Func<long, long, long, CancellationToken, ValueTask<bool>> characterHasSettingAsync,
        ConnectedSessions connected, Action<string>? log = null)
    {
        _source = source; _structures = structures; _pilots = pilots; _items = items;
        _statics = statics; _space = space; _hasSetting = characterHasSettingAsync; _connected = connected; _log = log;
        _space.StructureControlLeavingAsync = OnSessionLeavingAsync;
    }

    public async ValueTask<long?> GetStructurePilotAsync(ServiceCall call, long structureId)
    {
        await using var connection = await _source.OpenConnectionAsync(call.Cancellation);
        return (await _pilots.GetByStructureAsync(connection, null, structureId, call.Cancellation))?.CharacterId;
    }

    public ValueTask TakeControlAsync(ServiceCall call, long structureId) => ControlAsync(call, structureId, false);
    public ValueTask BoardStructureAsync(ServiceCall call, long structureId) => ControlAsync(call, structureId, true);

    private async ValueTask ControlAsync(ServiceCall call, long structureId, bool fromSpace)
    {
        await _gate.WaitAsync(call.Cancellation);
        try
        {
            var session = call.Session;
            var character = session.CharacterId ?? 0;
            if (character <= 0 || session.SessionId <= 0 || _space.StructureControlSessionClosed(session)) throw Denied("INVALID_SESSION");
            _space.WatchStructureControlSession(session);
            var structure = await TargetAsync(structureId, call.Cancellation);
            var system = structure.SolarSystemId;
            await using var connection = await _source.OpenConnectionAsync(call.Cancellation);
            var owned = await _pilots.GetBySessionAsync(connection, null, session.SessionId, call.Cancellation);
            // the order of the reference (structureControlService.js TakeControl, BoardStructure): docked before the setting, system before the setting
            if (owned is null && !fromSpace && (_space.IsInSpace(session) || session.GetInt("structureid") != structureId)) throw Denied("NOT_DOCKED_IN_STRUCTURE");
            if ((session.GetInt("solarsystemid2") ?? session.GetInt("solarsystemid")) != system) throw Denied("STRUCTURE_NOT_IN_SYSTEM");
            if (!await _hasSetting(character, structureId, DefenseCanControlStructure, call.Cancellation)) throw Denied("STRUCTURE_CONTROL_DENIED");
            if (owned is not null && owned.StructureId != structureId) throw Denied("IDENTITY_IN_USE");
            if (owned is null)
            {
                if (fromSpace)
                {
                    // EVE.js boardStructureControlFromSpace has no size gate (a medium structure has rigSize 2): only the state, the place and the ship are checked
                    if (!_space.IsInSpace(session) || session.GetInt("structureid") is not null
                        || _space.SolarSystemOf(session) != system) throw Denied("NOT_BOARDABLE_FROM_SPACE");
                    if (structure.Snapshot.State is not (StructureState.ShieldVulnerable or StructureState.ArmorReinforce or
                        StructureState.ArmorVulnerable or StructureState.HullReinforce or StructureState.HullVulnerable)) throw Denied("STRUCTURE_NOT_ONLINE");
                }
            }
            var originalShip = owned?.OriginalShipId ?? session.GetInt("shipid") ?? 0;
            if (owned is null && fromSpace) await _space.RecordBoardingPlaceAsync(session, originalShip, call.Cancellation);
            if (owned is null) await PhysicalShipAsync(session, originalShip, fromSpace, system, structureId, call.Cancellation);
            var request = new StructurePilotRequest(structureId, character, session.SessionId, originalShip,
                owned?.OriginalLocationId ?? session.GetInt("locationid"), owned?.FromSpace ?? fromSpace);
            if (_space.StructureControlSessionClosed(session) || session.CharacterId != character) throw Denied("SESSION_ENDED");
            StructurePilotAcquireResult acquired;
            await using (var transaction = await connection.BeginTransactionAsync(call.Cancellation))
            {
                acquired = await _pilots.AcquireAsync(connection, transaction, request, true, call.Cancellation);
                if (acquired.Status is not (StructurePilotAcquireStatus.Acquired or StructurePilotAcquireStatus.AlreadyOwned))
                    throw Denied(acquired.Status.ToString());
                await transaction.CommitAsync(call.Cancellation);
            }
            var current = acquired.Current!;
            var displacedSession = acquired.Displaced is { } displaced
                ? _connected.Snapshot().FirstOrDefault(s => s.SessionId == displaced.SessionId && s.CharacterId == displaced.CharacterId) : null;
            var oldDetached = false;
            var newAttached = false;
            var beforeSession = session.SnapshotAttributes();
            var beforeDisplacedSession = displacedSession?.SnapshotAttributes();
            try
            {
                if (_space.StructureControlSessionClosed(session)) throw Denied("SESSION_ENDED");
                if (displacedSession is not null && acquired.Displaced is { } displacedOwner)
                {
                    oldDetached = true;
                    await _space.SetStructureObserverAsync(displacedSession, system, 0, 0, false, call.Cancellation,
                        controlLeaseGeneration: displacedOwner.Version);
                }
                await _space.SetStructureObserverAsync(session, system, structureId, character, true, call.Cancellation,
                    controlLeaseGeneration: current.Version);
                newAttached = true;
                if (_space.StructureControlSessionClosed(session) || session.CharacterId != character) throw Denied("SESSION_ENDED");
                if (displacedSession is not null && acquired.Displaced is { } previous)
                    RestoreSession(displacedSession, previous, system);
                _space.ApplyStructureControlSession(session, system, structureId, current.OriginalShipId, current.OriginalLocationId, current.FromSpace);
                await _space.RefreshShipAsync(session, call.Cancellation);
            }
            catch (Exception failure)
            {
                if (acquired.Status == StructurePilotAcquireStatus.AlreadyOwned)
                {
                    RestoreControlSnapshot(session, beforeSession);
                    if (failure is Eve.Space.SimUnavailableException) session.Close();
                    throw;
                }

                var compensationFailures = new List<Exception>();
                Eve.Structures.StructurePilotRecord? restoredOwner = null;
                if (newAttached)
                {
                    try
                    {
                        await _space.SetStructureObserverAsync(session, system, 0, 0, false, CancellationToken.None,
                            controlLeaseGeneration: current.Version);
                    }
                    catch (Exception detachFailure)
                    {
                        compensationFailures.Add(detachFailure);
                        _space.ForgetStructureObserver(session);
                        session.Close();
                    }
                }

                try
                {
                    await using var rollback = await connection.BeginTransactionAsync(CancellationToken.None);
                    if (acquired.Displaced is { } previous)
                    {
                        restoredOwner = await _pilots.RestoreAsync(connection, rollback, current, previous, CancellationToken.None);
                        if (restoredOwner is null)
                            throw new InvalidOperationException("Structure control rollback lost its expected owner.");
                    }
                    else if (!await _pilots.ReleaseAsync(connection, rollback, current, CancellationToken.None))
                        throw new InvalidOperationException("Structure control rollback lost its expected owner.");
                    await rollback.CommitAsync(CancellationToken.None);
                    RestoreControlSnapshot(session, beforeSession);
                    if (oldDetached && displacedSession is not null && acquired.Displaced is { } old
                        && restoredOwner is { } restoredLease)
                    {
                        if (beforeDisplacedSession is not null) RestoreControlSnapshot(displacedSession, beforeDisplacedSession);
                        if (!_space.StructureControlSessionClosed(displacedSession))
                        {
                            try
                            {
                                await _space.SetStructureObserverAsync(displacedSession, system, structureId, old.CharacterId, true, CancellationToken.None,
                                    controlLeaseGeneration: restoredLease.Version);
                                await _space.RefreshShipAsync(displacedSession, CancellationToken.None);
                            }
                            catch (Exception restoreFailure)
                            {
                                compensationFailures.Add(restoreFailure);
                                _space.ForgetStructureObserver(displacedSession);
                                displacedSession.Close();
                            }
                        }
                    }
                }
                catch (Exception rollbackFailure)
                {
                    session.Close();
                    displacedSession?.Close();
                    var errors = new List<Exception> { failure, rollbackFailure };
                    errors.AddRange(compensationFailures);
                    throw new AggregateException("Structure control compensation failed.", errors);
                }

                if (failure is Eve.Space.SimUnavailableException) session.Close();
                if (compensationFailures.Count != 0)
                {
                    var errors = new List<Exception> { failure };
                    errors.AddRange(compensationFailures);
                    throw new AggregateException("Structure control compensation failed.", errors);
                }
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    public async ValueTask ReleaseControlAsync(ServiceCall call)
    {
        await _gate.WaitAsync(call.Cancellation);
        try { await EndAsync(call.Session, null, false, call.Cancellation); }
        finally { _gate.Release(); }
    }

    public async ValueTask EjectFromStructureAsync(ServiceCall call, long returnShipId)
    {
        await _gate.WaitAsync(call.Cancellation);
        try { await EndAsync(call.Session, returnShipId, false, call.Cancellation); }
        finally { _gate.Release(); }
    }

    public async ValueTask OnSessionLeavingAsync(ClientSession session, bool disconnected, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try { await EndAsync(session, null, disconnected, token); }
        finally { _gate.Release(); }
    }

    private async Task EndAsync(ClientSession session, long? returnShipId, bool force, CancellationToken token)
    {
        if (session.SessionId <= 0) return;
        await using var connection = await _source.OpenConnectionAsync(token);
        var owner = await _pilots.GetBySessionAsync(connection, null, session.SessionId, token);
        if (owner is null)
        {
            if (_space.IsStructureObserver(session))
            {
                var system = checked((int)(session.GetInt("structureControlSystemId") ?? 0));
                try
                {
                    var generation = session.GetInt("structureControlLeaseGeneration") ?? 0;
                    if (generation > 0)
                        await _space.SetStructureObserverAsync(session, system, 0, 0, false, token,
                            controlLeaseGeneration: generation);
                    else
                    {
                        _space.ForgetStructureObserver(session);
                        session.Close();
                    }
                }
                finally
                {
                    _space.ForgetStructureObserver(session);
                    _space.RestoreStructureControlSession(session, session.GetInt("structureControlOriginalShipId") ?? 0,
                        session.GetInt("structureControlOriginalLocationId"), session.GetInt("structureControlFromSpace") == 1, system);
                }
            }
            return;
        }
        if (!force && session.CharacterId != owner.CharacterId) throw Denied("OWNER_CHANGED");
        var structure = await _structures.GetAsync(owner.StructureId, token);
        var systemId = structure?.SolarSystemId ?? checked((int)(session.GetInt("structureControlSystemId") ?? session.GetInt("solarsystemid2") ?? 0));
        if (returnShipId is { } requested)
        {
            if (!owner.FromSpace || requested < 0) throw Denied("NOT_IN_SPACE_STRUCTURE_CONTROL");
            if (requested > 0)
            {
                if (requested != owner.OriginalShipId || structure is null) throw Denied("INVALID_RETURN_SHIP");
                var ship = await PhysicalShipAsync(session, requested, true, systemId, owner.StructureId, token);
                var shipRadius = _statics.Type(ship.TypeId)!.Attributes.GetValueOrDefault(162);
                if (!await _space.CanReturnToStructureShipAsync(session, requested, systemId, structure.X, structure.Y, structure.Z,
                    _statics.Type(structure.TypeId)?.Attributes.GetValueOrDefault(162) ?? 0, shipRadius, token)) throw Denied("RETURN_SHIP_NOT_BOARDABLE");
            }
        }
        var detached = false;
        try
        {
            await using var transaction = await connection.BeginTransactionAsync(token);
            if (!await _pilots.ReleaseAsync(connection, transaction, owner, token)) throw Denied("OWNER_CHANGED");
            try
            {
                await _space.SetStructureObserverAsync(session, systemId, 0, 0, false, token,
                    controlLeaseGeneration: owner.Version);
                detached = true;
            }
            catch (Exception error) when (force)
            {
                _log?.Invoke($"forced structure observer detach failed: {error.Message}");
                session.Close(); _space.ForgetStructureObserver(session);
            }
            await transaction.CommitAsync(token);
        }
        catch
        {
            if (detached && !force)
                await _space.SetStructureObserverAsync(session, systemId, owner.StructureId, owner.CharacterId, true, CancellationToken.None,
                    controlLeaseGeneration: owner.Version);
            throw;
        }
        if (returnShipId == 0)
        {
            try
            {
                if (!await _space.EjectStructureControlToCapsuleAsync(session, token)) throw Denied("CAPSULE_EJECTION_REFUSED");
            }
            catch
            {
                // A physical transition that closed the session must preserve its real ship/capsule, rather than reacquire a stale controller.
                if (_space.StructureControlSessionClosed(session)) throw;
                // Physical ejection runs only after SQL commit; an ordinary refusal leaves the old physical ship and session intact.
                await using var restore = await connection.BeginTransactionAsync(CancellationToken.None);
                var reacquired = await _pilots.AcquireAsync(connection, restore,
                    new StructurePilotRequest(owner.StructureId, owner.CharacterId, owner.SessionId, owner.OriginalShipId, owner.OriginalLocationId, owner.FromSpace), false);
                if (reacquired.Status is not (StructurePilotAcquireStatus.Acquired or StructurePilotAcquireStatus.AlreadyOwned))
                { session.Close(); throw new InvalidOperationException("The refused capsule ejection could not restore structure control."); }
                await restore.CommitAsync(CancellationToken.None);
                await _space.SetStructureObserverAsync(session, systemId, owner.StructureId, owner.CharacterId, true, CancellationToken.None,
                    controlLeaseGeneration: reacquired.Current!.Version);
                throw;
            }
        }
        else RestoreSession(session, owner, systemId);
    }

    private static void RestoreControlSnapshot(ClientSession session, IReadOnlyDictionary<string, PyValue> before)
    {
        foreach (var name in new[] { "structureControlSystemId", "structureControlOriginalShipId", "structureControlOriginalLocationId",
            "structureControlFromSpace", "loginInventoryBootstrapPending", "deferredDockedShipId" })
            session.Set(name, before.GetValueOrDefault(name) ?? PyNone.Instance);
        // Closed/watch bookkeeping belongs to the socket lifetime and must survive a control rollback.
        session.Change(("shipid", before.GetValueOrDefault("shipid") ?? PyNone.Instance),
            ("structureid", before.GetValueOrDefault("structureid") ?? PyNone.Instance),
            ("locationid", before.GetValueOrDefault("locationid") ?? PyNone.Instance));
    }

    private void RestoreSession(ClientSession session, StructurePilotRecord owner, int system) =>
        _space.RestoreStructureControlSession(session, owner.OriginalShipId, owner.OriginalLocationId, owner.FromSpace, system);

    private async Task<Item> PhysicalShipAsync(ClientSession session, long shipId, bool fromSpace, int system, long structure, CancellationToken token)
    {
        var item = shipId > 0 ? await _items.GetAsync(shipId, token) : null;
        if (item is null || item.OwnerId != session.CharacterId || !item.Singleton || item.Quantity != 1 ||
            item.LocationId != (fromSpace ? system : structure) || item.Flag != (fromSpace ? CharacterShips.InSpaceFlag : ItemFlags.Hangar)) throw Denied("INVALID_PHYSICAL_SHIP");
        await _statics.EnsureTypesAsync([item.TypeId], token);
        if (_statics.Type(item.TypeId)?.CategoryId != _statics.CategoryId("Ship") ||
            fromSpace && (_space.ShipIdOf(session) != shipId || _space.ShipTypeIdOf(session) != item.TypeId)) throw Denied("INVALID_PHYSICAL_SHIP");
        return item;
    }

    private async Task<StructureRecord> TargetAsync(long id, CancellationToken token)
    {
        var structure = id > 0 ? await _structures.GetAsync(id, token) : null;
        if (structure is null || structure.Snapshot.DestroyedMs != 0 || structure.Snapshot.State is StructureState.Unanchored or StructureState.Anchoring)
            throw Denied("STRUCTURE_NOT_FOUND");
        await _statics.EnsureTypesAsync([structure.TypeId], token);
        if (_statics.Type(structure.TypeId) is not { } type || type.CategoryId != _statics.CategoryId("Structure") ||
            !type.Attributes.TryGetValue(162, out var radius) || !double.IsFinite(radius) || radius <= 0) throw Denied("INVALID_STRUCTURE_TYPE");
        return structure;
    }

    public async ValueTask<StructureControlView?> ReadStructureControlViewAsync(ClientSession session, long structureId, CancellationToken token = default)
    {
        if (session.GetInt("shipid") != structureId || session.GetInt("structureid") != structureId || session.SessionId <= 0) return null;
        await using var connection = await _source.OpenConnectionAsync(token);
        var owner = await _pilots.GetBySessionAsync(connection, null, session.SessionId, token);
        if (owner is null || owner.StructureId != structureId || owner.CharacterId != session.CharacterId ||
            !await _hasSetting(owner.CharacterId, structureId, DefenseCanControlStructure, token)) return null;
        var structure = await TargetAsync(structureId, token);
        return new(new Item(structureId, structure.TypeId, structure.OwnerCorporationId, structure.SolarSystemId, 0, 1, true, structure.Name),
            _statics.Type(structure.TypeId)!, structure.SolarSystemId);
    }

    /// <summary>Parent composition subscribes profile/ACL/structure lifecycle changes to this method. No second polling timer.</summary>
    public async Task ReconcileAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            foreach (var session in _connected.Snapshot())
            {
                if (session.SessionId <= 0) continue;
                await using var connection = await _source.OpenConnectionAsync(token);
                var owner = await _pilots.GetBySessionAsync(connection, null, session.SessionId, token);
                if (owner is null)
                {
                    if (_space.IsStructureObserver(session)) await EndAsync(session, null, true, token);
                    continue;
                }
                var structure = await _structures.GetAsync(owner.StructureId, token);
                if (owner.CharacterId != session.CharacterId || structure is null || structure.Snapshot.DestroyedMs != 0 ||
                    !await _hasSetting(owner.CharacterId, owner.StructureId, DefenseCanControlStructure, token))
                    await EndAsync(session, null, true, token);
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>The refusals the reference sends for these reasons (structureControlService.js throwControlDenied); the others keep the reason in the text.</summary>
    private static MachoWrappedException Denied(string reason) => reason switch
    {
        "STRUCTURE_CONTROL_DENIED" => MachoErrors.ThrowUserError("StructureDefenseDenied"),
        "STRUCTURE_NOT_FOUND" => MachoErrors.ThrowUserError("TargetingAttemptCancelled"),
        "NOT_DOCKED_IN_STRUCTURE" => MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("You must be docked in this structure to take control."))),
        "STRUCTURE_NOT_IN_SYSTEM" => MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("That structure is not in your current solar system."))),
        _ => MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr($"Structure control refused: {reason}"))),
    };
}

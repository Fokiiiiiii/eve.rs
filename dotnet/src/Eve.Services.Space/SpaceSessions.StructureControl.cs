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
        long structureId, long characterId, bool active, CancellationToken token = default)
    {
        if (session.SessionId <= 0 || solarSystemId <= 0 || active && (structureId <= 0 || characterId <= 0))
            throw new ArgumentOutOfRangeException(nameof(structureId));
        var id = (ulong)session.SessionId;
        var wasObserver = IsStructureObserver(session);
        if (active) _structureObservers[id] = session; // The bind can send its initial ballpark before its acknowledgement.
        try
        {
            var refusal = await _sim.ControlStructureAsync(
                new SimControlStructure(id, solarSystemId, active ? structureId : 0, active ? characterId : 0, active), token);
            if (refusal is not null) throw Notify($"Structure control was refused: {refusal}");
            if (!active) _structureObservers.TryRemove(id, out _);
        }
        catch
        {
            if (active && !wasObserver) _structureObservers.TryRemove(id, out _);
            throw;
        }
    }

    public void ApplyStructureControlSession(ClientSession session, int system, long structureId,
        long originalShipId, long? originalLocationId, bool fromSpace)
    {
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
    }

    public void RestoreStructureControlSession(ClientSession session, long originalShipId,
        long? originalLocationId, bool fromSpace, int system)
    {
        if (fromSpace)
        {
            var physicalShip = ShipIdOf(session) ?? originalShipId;
            session.Change(("structureid", None), ("shipid", new PyLong(physicalShip)),
                ("locationid", new PyLong(originalLocationId ?? system)));
            Background(() => RefreshTetherPolicyAsync(session));
        }
        else session.Change(("shipid", new PyLong(originalShipId)));
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
        if (ShipIdOf(session) != shipId || SolarSystemOf(session) != system ||
            await PositionOfAsync(session, shipId, token) is not { } position || position.SolarSystemId != system)
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
            RestoreStructureControlSession(session, pilot.ShipId, pilot.SolarSystemId, true, pilot.SolarSystemId);
            return true;
        }
        var result = await EjectPhysicalShipAsync(session, session.GetInt("shipid") ?? 0, true, token);
        if (result is not PyTuple) return false;
        session.Set("structureControlSystemId", None);
        session.Set("structureControlOriginalShipId", None);
        session.Set("structureControlOriginalLocationId", None);
        session.Set("structureControlFromSpace", None);
        return true;
    }
}

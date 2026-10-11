using Eve.Marshal;
using Eve.Space;

namespace Eve.Services.Space;

public sealed record TetherGeometry(double Radius, double Range);

public sealed partial class SpaceSessions
{
    // These intervals bound stale Host authorization; they are not tether gameplay timers.
    public const ulong TetherPolicyLeaseMs = 3_000;
    public const int TetherPolicyRefreshMs = 1_000;
    public const long CynoTetherDelayMs = 30_000;
    private readonly string _tetherTimerKey = $"space.tether:{Guid.NewGuid():N}";
    private int _tetherStopped;
    private int _tetherRefreshArmed;
    private Func<ClientSession, CancellationToken, Task<IReadOnlyList<SimTetherCandidate>>>? _readTetherCandidates;

    public Func<ClientSession, CancellationToken, Task<IReadOnlyList<SimTetherCandidate>>>? ReadTetherCandidatesAsync
    {
        get => _readTetherCandidates;
        set
        {
            _readTetherCandidates = value;
            if (value is not null && Interlocked.CompareExchange(ref _tetherRefreshArmed, 1, 0) == 0)
                ArmTetherRefresh();
        }
    }
    public Func<long, CancellationToken, Task<TetherGeometry?>>? ReadTetherGeometryAsync { get; set; }
    public Func<ClientSession, long, CancellationToken, Task<bool>>? RepairTetheredItemsAsync { get; set; }
    public Func<long, CancellationToken, Task<long>>? ReadTetherBlockUntilAsync { get; set; }
    public Func<long, long, CancellationToken, Task>? WriteTetherBlockUntilAsync { get; set; }

    public bool IsTethered(ClientSession session) =>
        _pilots.TryGetValue((ulong)session.SessionId, out var pilot) && Interlocked.Read(ref pilot.TetherStructureId) > 0;

    public bool CanRepairTetheredShip(ClientSession session, long expectedShipId) =>
        _pilots.TryGetValue((ulong)session.SessionId, out var pilot) && ReferenceEquals(pilot.Session, session)
        && CanRepairTether(pilot, Interlocked.Read(ref pilot.TetherStructureId), expectedShipId);

    private bool TetherPolicyAllows(Pilot pilot, long structureId) =>
        structureId > 0 && pilot.TetherPolicyExpiresMs > _nowUnixMs()
        && pilot.TetherPolicyBlockedUntilMs <= _nowUnixMs()
        && pilot.TetherAllowedCandidates.Any(c => c.StructureId == structureId);

    private bool CanRepairTether(Pilot pilot, long structureId, long expectedShipId) =>
        _pilots.TryGetValue((ulong)pilot.Session.SessionId, out var current) && ReferenceEquals(current, pilot)
        && pilot.ShipId == expectedShipId && pilot.Session.GetInt("shipid") == expectedShipId
        && pilot.Session.CharacterId == pilot.CharacterId
        && Interlocked.Read(ref pilot.TetherStructureId) == structureId
        && _sim.IsConnected && Volatile.Read(ref _tetherStopped) == 0
        && TetherPolicyAllows(pilot, structureId);

    private void ArmTetherRefresh()
    {
        if (Volatile.Read(ref _tetherStopped) != 0) return;
        _timers.Set(_tetherTimerKey, TimeSpan.FromMilliseconds(TetherPolicyRefreshMs), async () =>
        {
            try { await RefreshTetherPoliciesAsync(CancellationToken.None); }
            finally { ArmTetherRefresh(); }
        });
    }

    public Task RefreshTetherPoliciesAsync(CancellationToken token) =>
        Task.WhenAll(_pilots.Values.Select(pilot => RefreshTetherPolicyAsync(pilot.Session, token)));

    public async Task RefreshTetherPolicyAsync(ClientSession session, CancellationToken token = default)
    {
        var id = (ulong)session.SessionId;
        if (!_pilots.TryGetValue(id, out var pilot) || ReadTetherCandidatesAsync is null && pilot.TetherPolicyExpiresMs == 0) return;
        await pilot.TetherPolicyGate.WaitAsync(token);
        try
        {
            IReadOnlyList<SimTetherCandidate> candidates = [];
            ulong blocked = 0;
            try
            {
                if (ReadTetherCandidatesAsync is { } read && CrimewatchStateOf is { } crimewatch)
                {
                    candidates = await read(session, token);
                    if (candidates.Any(c => c.StructureId <= 0 || !double.IsFinite(c.Range) || c.Range <= 0)
                        || candidates.Select(c => c.StructureId).Distinct().Count() != candidates.Count)
                        throw new InvalidOperationException("Invalid tether authorization candidates.");
                    var now = _nowUnixMs();
                    var state = await crimewatch(pilot.CharacterId, token);
                    var until = Math.Max(state.WeaponsUntilMs, pilot.TetherDelayUntilMs);
                    if (ReadTetherBlockUntilAsync is { } persisted)
                        until = Math.Max(until, await persisted(pilot.CharacterId, token));
                    blocked = until > now ? (ulong)(until - now) : 0;
                    var remainingProtectionTicks = pilot.InvulnerableUntil - FileTimeNow();
                    if (!pilot.ProtectionEnded && remainingProtectionTicks > 0)
                        blocked = Math.Max(blocked, (ulong)((remainingProtectionTicks + 9_999) / 10_000));
                    if (await ShipAttributeAsync(pilot, Effects.ModuleAttributes.DisallowTethering, token))
                        blocked = Math.Max(blocked, TetherPolicyLeaseMs);
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                candidates = [];
                blocked = 0;
                _log?.Invoke($"tether policy of character {pilot.CharacterId} was withdrawn: {error.Message}");
            }
            if (_pilots.TryGetValue(id, out var current) && ReferenceEquals(current, pilot) && _sim.IsConnected && Volatile.Read(ref _tetherStopped) == 0)
            {
                pilot.TetherAllowedCandidates = candidates;
                pilot.TetherPolicyExpiresMs = checked(_nowUnixMs() + (long)TetherPolicyLeaseMs);
                pilot.TetherPolicyBlockedUntilMs = blocked > (ulong)(long.MaxValue - _nowUnixMs())
                    ? long.MaxValue : _nowUnixMs() + (long)blocked;
                _sim.SetTetherPolicy(new SimTetherPolicy(id, blocked, TetherPolicyLeaseMs, candidates));

            }
        }
        finally { pilot.TetherPolicyGate.Release(); }
    }

    private void WithdrawTetherPolicy(ulong id)
    {
        if (_sim.IsConnected)
            _sim.SetTetherPolicy(new SimTetherPolicy(id, 0, TetherPolicyLeaseMs, []));
    }

    private void StopTetherRefresh()
    {
        Interlocked.Exchange(ref _tetherStopped, 1);
        _timers.Cancel(_tetherTimerKey);
        foreach (var pilot in _pilots.Values) EndTetherPilot(pilot);
    }

    private void QueueTetherWork(Pilot pilot, Func<Task> action)
    {
        Task work;
        lock (pilot.TetherSync)
        {
            work = pilot.TetherWork.ContinueWith(_ => action(), CancellationToken.None,
                TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
            pilot.TetherWork = work;
        }
        Background(() => work);
    }

    private void BeginCynoTetherProtection(Pilot pilot)
    {
        if (!pilot.CynoArrival) return;
        var remaining = Math.Max(0, (pilot.InvulnerableUntil - FileTimeNow()) / 10_000);
        pilot.TetherDelayUntilMs = checked(_nowUnixMs() + remaining + CynoTetherDelayMs);
        PersistTetherDelay(pilot);
    }

    private void TetherProtectionEnded(Pilot pilot)
    {
        pilot.ProtectionEnded = true;
        if (pilot.CynoArrival)
        {
            pilot.CynoArrival = false;
            pilot.TetherDelayUntilMs = checked(_nowUnixMs() + CynoTetherDelayMs);
            PersistTetherDelay(pilot);
        }
        Background(() => RefreshTetherPolicyAsync(pilot.Session));
    }

    private void PersistTetherDelay(Pilot pilot) => QueueTetherWork(pilot, async () =>
    {
        if (WriteTetherBlockUntilAsync is not { } write)
            throw new InvalidOperationException("The cynosural tether delay has no persistence provider.");
        await write(pilot.CharacterId, pilot.TetherDelayUntilMs, CancellationToken.None);
    });

    private void HandleTetherChanged(SimTetherChanged change)
    {
        if (!_pilots.TryGetValue(change.SessionId, out var pilot)) return;
        var expectedShipId = pilot.ShipId;
        if (change.StructureId > 0 && !TetherPolicyAllows(pilot, change.StructureId))
        {
            WithdrawTetherPolicy(change.SessionId);
            return;
        }
        var previous = Interlocked.Exchange(ref pilot.TetherStructureId, change.StructureId);
        if (previous == change.StructureId) return;
        var previousRepair = pilot.TetherRepairing;
        var previousGeometry = pilot.TetherGeometry;
        pilot.TetherRepairing = change.Repairing;
        QueueTetherWork(pilot, async () =>
        {
            if (previous > 0)
                BroadcastTetherFx(pilot, previous, previousGeometry, false, previousRepair);
            if (change.StructureId == 0) return;
            try
            {
                var geometry = ReadTetherGeometryAsync is { } read
                    ? await read(change.StructureId, CancellationToken.None) : null;
                if (geometry is null || !double.IsFinite(geometry.Radius) || geometry.Radius <= 0
                    || !double.IsFinite(geometry.Range) || geometry.Range <= 0 || !double.IsFinite(geometry.Radius + geometry.Range))
                    throw new InvalidOperationException("The tether structure has no valid SDE geometry.");
                if (!CanRepairTether(pilot, change.StructureId, expectedShipId)) return;
                var repairedItems = RepairTetheredItemsAsync is { } repair
                    ? await repair(pilot.Session, expectedShipId, CancellationToken.None)
                    : throw new InvalidOperationException("The tether repair provider is not installed.");
                if (!CanRepairTether(pilot, change.StructureId, expectedShipId)) return;
                var repairing = change.Repairing || repairedItems;
                pilot.TetherGeometry = geometry;
                pilot.TetherRepairing = repairing;
                if (repairedItems)
                {
                    var snapshot = await FlownShipAsync(pilot, CancellationToken.None);
                    if (!CanRepairTether(pilot, change.StructureId, expectedShipId)) return;
                    if (snapshot is not null && snapshot.Ship.Id == expectedShipId)
                        await _sim.SetHeatProfileAsync(change.SessionId, SimShips.HeatProfileOf(snapshot, _statics), CancellationToken.None);
                }
                if (CanRepairTether(pilot, change.StructureId, expectedShipId))
                    BroadcastTetherFx(pilot, change.StructureId, geometry, true, repairing);
            }
            catch
            {
                WithdrawTetherPolicy(change.SessionId);
                throw;
            }
        });
    }

    private void EndTetherPilot(Pilot pilot)
    {
        var previous = Interlocked.Exchange(ref pilot.TetherStructureId, 0);
        if (pilot.TetherPolicyExpiresMs > 0) WithdrawTetherPolicy((ulong)pilot.Session.SessionId);
        if (previous > 0) BroadcastTetherFx(pilot, previous, pilot.TetherGeometry, false, pilot.TetherRepairing);
        pilot.TetherAllowedCandidates = [];
        pilot.TetherRepairing = false;
    }

    public void CancelCynoTetherArrival(long characterId)
    {
        foreach (var pilot in _pilots.Values.Where(p => p.CharacterId == characterId))
            pilot.CynoJumpPending = false;
    }

    private void BroadcastTetherFx(Pilot pilot, long structureId, TetherGeometry? geometry, bool active, bool repairing)
    {
        foreach (var viewer in _pilots.Values.Where(p => p.SolarSystemId == pilot.SolarSystemId && p.BallparkBound))
            SendTetherFx(viewer.Session, pilot.ShipId, structureId, geometry, active, repairing);
    }

    private static void SendTetherFx(ClientSession session, long shipId, long structureId, TetherGeometry? geometry, bool active, bool repairing)
    {
        var graphic = geometry is null ? new PyDict([]) : new PyDict([
            new(new PyStr("targetRadius"), Py.Number(geometry.Radius)),
            new(new PyStr("linkBarrier"), Py.Number(geometry.Radius + geometry.Range))]);
        if (repairing)
            Send("effects.TetheringRepair", None, active ? 10_000 : -1, active ? new PyInt(10_000) : None, new PyDict([]));
        Send("effects.Tethering", new PyLong(structureId), active ? 86_400_000 : -1, None, graphic);

        void Send(string guid, PyValue target, int duration, PyValue repeat, PyValue info) =>
            session.SendNotification("OnSpecialFX", "shipID", new PyLong(shipId), None, None, target, None,
                new PyStr(guid), new PyInt(0), new PyInt(active ? 1 : 0), new PyInt(active ? 1 : 0),
                new PyInt(duration), repeat, None, new PyInt(0), info);
    }

    private void ReplayTetherFx(ClientSession session)
    {
        if (!_pilots.TryGetValue((ulong)session.SessionId, out var viewer)) return;
        viewer.BallparkBound = true;
        foreach (var pilot in _pilots.Values.Where(p => p.SolarSystemId == viewer.SolarSystemId && Interlocked.Read(ref p.TetherStructureId) > 0))
            if (pilot.TetherGeometry is { } geometry && CanRepairTetheredShip(pilot.Session, pilot.ShipId))
                SendTetherFx(session, pilot.ShipId, Interlocked.Read(ref pilot.TetherStructureId), geometry, true, pilot.TetherRepairing);
    }

    private static bool HasBallparkState(PyValue value) => value switch
    {
        PyStr { Value: "SetState" } => true,
        PyTuple tuple => tuple.Items.Any(HasBallparkState),
        PyList list => list.Items.Any(HasBallparkState),
        _ => false,
    };
}

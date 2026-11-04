using System.Runtime.CompilerServices;
using Eve.Services;

namespace Eve.Services.Station;

public delegate StationGuestLedger.SnapshotSeed? BeginObserverGuestLedgerSeedCallback(
    ClientSession observerSession,
    long locationId);

public sealed class StationGuestLedger
{
    private readonly record struct GuestDelta(long Revision, bool Joined);

    private sealed class Entry(long locationId)
    {
        public long LocationId = locationId;
        public long Generation;
        public long Revision;
        public long NextSeedId;
        public readonly HashSet<long> GuestCharacterIds = [];
        public readonly HashSet<long> PendingSeeds = [];
        public readonly Dictionary<long, GuestDelta> Deltas = [];
    }

    public sealed class SnapshotSeed : IDisposable
    {
        private readonly Action<IReadOnlySet<long>> _complete;
        private readonly Action _cancel;
        private int _finished;

        internal SnapshotSeed(Action<IReadOnlySet<long>> complete, Action cancel)
        {
            _complete = complete;
            _cancel = cancel;
        }

        public void Complete(IReadOnlySet<long> guestCharacterIds)
        {
            if (Interlocked.Exchange(ref _finished, 1) == 0)
                _complete(guestCharacterIds);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _finished, 1) == 0)
                _cancel();
        }
    }

    private readonly ConditionalWeakTable<ClientSession, Entry> _entries = new();

    public SnapshotSeed? BeginObserverGuestLedgerSeed(ClientSession observerSession, long locationId)
    {
        if (locationId <= 0)
            return null;

        var entry = _entries.GetValue(observerSession, _ => new Entry(locationId));
        lock (entry)
        {
            ResetForLocation(entry, locationId);
            var seedId = ++entry.NextSeedId;
            var generation = entry.Generation;
            var revision = entry.Revision;
            entry.PendingSeeds.Add(seedId);
            return new SnapshotSeed(
                guestCharacterIds => CompleteSnapshot(entry, locationId, generation, seedId, revision, guestCharacterIds),
                () => CancelSnapshot(entry, locationId, generation, seedId));
        }
    }

    public void SeedObserverGuestLedger(
        ClientSession observerSession,
        long locationId,
        IReadOnlySet<long> guestCharacterIds)
    {
        using var seed = BeginObserverGuestLedgerSeed(observerSession, locationId);
        seed?.Complete(guestCharacterIds);
    }

    /// <summary>Returns true once per observer/location for a new guest join.</summary>
    public bool TryRecordGuestJoined(ClientSession observerSession, long locationId, long guestCharacterId)
    {
        if (locationId <= 0 || guestCharacterId <= 0)
            return false;

        var entry = _entries.GetValue(observerSession, _ => new Entry(locationId));
        lock (entry)
        {
            ResetForLocation(entry, locationId);
            if (!entry.GuestCharacterIds.Add(guestCharacterId))
                return false;

            RecordDelta(entry, guestCharacterId, joined: true);
            return true;
        }
    }

    /// <summary>Returns true only when the observer was previously told about the guest.</summary>
    public bool TryRecordGuestLeft(ClientSession observerSession, long locationId, long guestCharacterId)
    {
        if (locationId <= 0 || guestCharacterId <= 0)
            return false;

        var entry = _entries.GetValue(observerSession, _ => new Entry(locationId));
        lock (entry)
        {
            ResetForLocation(entry, locationId);
            if (!entry.GuestCharacterIds.Remove(guestCharacterId))
                return false;

            RecordDelta(entry, guestCharacterId, joined: false);
            return true;
        }
    }

    private static void CompleteSnapshot(
        Entry entry,
        long locationId,
        long generation,
        long seedId,
        long revision,
        IReadOnlySet<long> guestCharacterIds)
    {
        var guests = guestCharacterIds.Where(characterId => characterId > 0).ToHashSet();
        lock (entry)
        {
            if (entry.LocationId != locationId ||
                entry.Generation != generation ||
                !entry.PendingSeeds.Remove(seedId))
                return;

            entry.GuestCharacterIds.Clear();
            entry.GuestCharacterIds.UnionWith(guests);
            // Replay transitions observed after this snapshot began over its older result.
            foreach (var (characterId, delta) in entry.Deltas)
            {
                if (delta.Revision <= revision)
                    continue;
                if (delta.Joined)
                    entry.GuestCharacterIds.Add(characterId);
                else
                    entry.GuestCharacterIds.Remove(characterId);
            }

            ClearPendingDeltasWhenIdle(entry);
        }
    }

    private static void CancelSnapshot(Entry entry, long locationId, long generation, long seedId)
    {
        lock (entry)
        {
            if (entry.LocationId == locationId && entry.Generation == generation)
                entry.PendingSeeds.Remove(seedId);
            ClearPendingDeltasWhenIdle(entry);
        }
    }

    private static void RecordDelta(Entry entry, long guestCharacterId, bool joined)
    {
        var revision = ++entry.Revision;
        if (entry.PendingSeeds.Count == 0)
            return;

        entry.Deltas[guestCharacterId] = new GuestDelta(revision, joined);
    }

    private static void ResetForLocation(Entry entry, long locationId)
    {
        if (entry.LocationId == locationId)
            return;

        entry.LocationId = locationId;
        entry.Generation++;
        entry.GuestCharacterIds.Clear();
        entry.PendingSeeds.Clear();
        entry.Deltas.Clear();
    }

    private static void ClearPendingDeltasWhenIdle(Entry entry)
    {
        if (entry.PendingSeeds.Count != 0)
            return;

        entry.Deltas.Clear();
    }
}

public sealed class GuestLoginHook(CharacterSessions sessions, StationGuestLedger ledger) : ICharacterEntryHook
{
    private sealed class CloseRegistration
    {
        public long CharacterId;
        public Action? Handler;
    }

    private readonly ConditionalWeakTable<ClientSession, CloseRegistration> _closedSessions = new();

    public ValueTask OnCharacterLeavingAsync(ClientSession session, long characterId, CancellationToken cancellationToken)
    {
        BroadcastLeft(session, characterId, CurrentStationId(session), CurrentStructureId(session));
        ClearClosedHandler(session, characterId);
        return ValueTask.CompletedTask;
    }

    public ValueTask OnCharacterEnteredAsync(ClientSession session, long characterId, CancellationToken cancellationToken)
    {
        var stationId = CurrentStationId(session);
        var structureId = CurrentStructureId(session);
        if (stationId > 0)
            BroadcastStationJoined(session, characterId, stationId);
        else if (structureId > 0)
            BroadcastStructureJoined(session, characterId, structureId);

        TrackClosedHandler(session, characterId);
        return ValueTask.CompletedTask;
    }

    public void OnLocationChanged(ClientSession session, long previousStationId, long previousStructureId)
    {
        var stationId = CurrentStationId(session);
        var structureId = CurrentStructureId(session);
        var characterId = session.CharacterId ?? 0;
        if (characterId <= 0)
            return;

        if (previousStationId > 0 && previousStationId != stationId)
            BroadcastLeft(session, characterId, stationId: previousStationId);
        if (previousStructureId > 0 && previousStructureId != structureId)
            BroadcastLeft(session, characterId, structureId: previousStructureId);
        if (stationId > 0 && stationId != previousStationId)
            BroadcastStationJoined(session, characterId, stationId);
        if (structureId > 0 && structureId != previousStructureId)
            BroadcastStructureJoined(session, characterId, structureId);
    }

    private void BroadcastStationJoined(ClientSession session, long characterId, long stationId)
    {
        var guest = new Eve.Marshal.PyTuple(GuestIdentity(session, characterId));
        foreach (var observer in sessions.InStation(stationId))
            if (observer.CharacterId != characterId && ledger.TryRecordGuestJoined(observer, stationId, characterId))
                observer.SendNotification("OnCharNowInStation", "stationid", guest);
    }

    private void BroadcastStructureJoined(ClientSession session, long characterId, long structureId)
    {
        var guest = GuestIdentity(session, characterId);
        foreach (var observer in sessions.InStructure(structureId))
            if (observer.CharacterId != characterId && ledger.TryRecordGuestJoined(observer, structureId, characterId))
                observer.SendNotification("OnCharacterEnteredStructure", "clientID", guest);
    }

    private void BroadcastLeft(ClientSession session, long characterId, long stationId = 0, long structureId = 0)
    {
        if (stationId > 0)
        {
            var guest = new Eve.Marshal.PyTuple(GuestIdentity(session, characterId));
            foreach (var observer in sessions.InStation(stationId))
                if (observer.CharacterId != characterId && ledger.TryRecordGuestLeft(observer, stationId, characterId))
                    observer.SendNotification("OnCharNoLongerInStation", "stationid", guest);
        }
        else if (structureId > 0)
        {
            foreach (var observer in sessions.InStructure(structureId))
                if (observer.CharacterId != characterId && ledger.TryRecordGuestLeft(observer, structureId, characterId))
                    observer.SendNotification("OnCharacterLeftStructure", "clientID", new Eve.Marshal.PyInt(characterId));
        }
    }

    private void TrackClosedHandler(ClientSession session, long characterId)
    {
        if (characterId <= 0)
            return;

        var registration = _closedSessions.GetValue(session, _ => new CloseRegistration());
        lock (registration)
        {
            if (registration.CharacterId == characterId && registration.Handler is not null)
                return;
            if (registration.Handler is { } previous)
                session.Closed -= previous;

            registration.CharacterId = characterId;
            Action? handler = null;
            handler = () => OnSessionClosed(session, registration, characterId, handler!);
            registration.Handler = handler;
            session.Closed += handler;
        }
    }

    private void ClearClosedHandler(ClientSession session, long characterId)
    {
        if (!_closedSessions.TryGetValue(session, out var registration))
            return;

        lock (registration)
        {
            if (registration.CharacterId != characterId)
                return;
            if (registration.Handler is { } handler)
                session.Closed -= handler;
            registration.Handler = null;
            registration.CharacterId = 0;
        }
    }

    private void OnSessionClosed(ClientSession session, CloseRegistration registration, long characterId, Action handler)
    {
        long currentCharacterId;
        long stationId;
        long structureId;
        lock (registration)
        {
            if (registration.CharacterId != characterId || !ReferenceEquals(registration.Handler, handler))
                return;

            currentCharacterId = session.CharacterId ?? 0;
            stationId = CurrentStationId(session);
            structureId = CurrentStructureId(session);
            registration.CharacterId = 0;
            registration.Handler = null;
            session.Closed -= handler;
        }

        if (currentCharacterId == characterId)
            BroadcastLeft(session, characterId, stationId, structureId);
    }

    private static long CurrentStationId(ClientSession session) =>
        session.GetInt("stationid") ?? session.GetInt("stationID") ?? 0;

    private static long CurrentStructureId(ClientSession session) =>
        session.GetInt("structureid") ?? session.GetInt("structureID") ?? 0;

    private static Eve.Marshal.PyValue[] GuestIdentity(ClientSession session, long characterId) =>
    [
        new Eve.Marshal.PyInt(characterId),
        new Eve.Marshal.PyInt(Math.Max(0, session.GetInt("corpid") ?? 0)),
        new Eve.Marshal.PyInt(Math.Max(0, session.GetInt("allianceid") ?? 0)),
        new Eve.Marshal.PyInt(Math.Max(0, session.GetInt("warfactionid") ?? 0)),
    ];
}

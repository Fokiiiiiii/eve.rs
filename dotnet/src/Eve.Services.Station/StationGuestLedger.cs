using System.Runtime.CompilerServices;
using Eve.Services;

namespace Eve.Services.Station;

public delegate void SeedObserverGuestLedgerCallback(
    ClientSession observerSession,
    long locationId,
    IReadOnlySet<long> guestCharacterIds);

/// <summary>Per-observer station snapshot used by future guest join/leave broadcasters.</summary>
public sealed class StationGuestLedger
{
    private sealed class Entry(long locationId)
    {
        public long LocationId = locationId;
        public readonly HashSet<long> GuestCharacterIds = [];
    }

    private readonly ConditionalWeakTable<ClientSession, Entry> _entries = new();

    public void SeedObserverGuestLedger(
        ClientSession observerSession,
        long locationId,
        IReadOnlySet<long> guestCharacterIds)
    {
        if (locationId <= 0)
            return;

        var guests = guestCharacterIds.Where(characterId => characterId > 0).ToHashSet();
        var entry = _entries.GetValue(observerSession, _ => new Entry(locationId));
        lock (entry)
        {
            entry.LocationId = locationId;
            entry.GuestCharacterIds.Clear();
            entry.GuestCharacterIds.UnionWith(guests);
        }
    }

    /// <summary>Returns true once per observer/location for a new guest join.</summary>
    public bool TryRecordGuestJoined(ClientSession observerSession, long locationId, long guestCharacterId)
    {
        if (locationId <= 0 || guestCharacterId <= 0)
            return false;

        var entry = Resolve(observerSession, locationId);
        lock (entry)
            return entry.GuestCharacterIds.Add(guestCharacterId);
    }

    /// <summary>Returns true only when the observer was previously told about the guest.</summary>
    public bool TryRecordGuestLeft(ClientSession observerSession, long locationId, long guestCharacterId)
    {
        if (locationId <= 0 || guestCharacterId <= 0)
            return false;

        var entry = Resolve(observerSession, locationId);
        lock (entry)
            return entry.GuestCharacterIds.Remove(guestCharacterId);
    }

    private Entry Resolve(ClientSession observerSession, long locationId)
    {
        var entry = _entries.GetValue(observerSession, _ => new Entry(locationId));
        lock (entry)
        {
            if (entry.LocationId != locationId)
            {
                entry.LocationId = locationId;
                entry.GuestCharacterIds.Clear();
            }
        }
        return entry;
    }
}

public sealed class GuestLoginHook(CharacterSessions sessions, StationGuestLedger ledger) : ICharacterEntryHook
{
    public ValueTask OnCharacterLeavingAsync(ClientSession session, long characterId, CancellationToken cancellationToken)
    {
        var stationId = session.GetInt("stationid") ?? session.GetInt("stationID") ?? 0;
        var structureId = session.GetInt("structureid") ?? session.GetInt("structureID") ?? 0;
        BroadcastLeft(session, characterId, stationId, structureId);
        return ValueTask.CompletedTask;
    }

    public ValueTask OnCharacterEnteredAsync(ClientSession session, long characterId, CancellationToken cancellationToken)
    {
        var stationId = session.GetInt("stationid") ?? session.GetInt("stationID") ?? 0;
        var structureId = session.GetInt("structureid") ?? session.GetInt("structureID") ?? 0;
        if (stationId > 0)
        {
            BroadcastStationJoined(session, characterId, stationId);
            session.Closed += () => BroadcastLeft(session, characterId, stationId, 0);
        }
        else if (structureId > 0)
        {
            BroadcastStructureJoined(session, characterId, structureId);
            session.Closed += () => BroadcastLeft(session, characterId, structureId: structureId);
        }
        return ValueTask.CompletedTask;
    }

    private void BroadcastStationJoined(ClientSession session, long characterId, long stationId)
    {
        var guest = GuestIdentity(session, characterId);
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
            var guest = GuestIdentity(session, characterId);
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

    private static Eve.Marshal.PyTuple GuestIdentity(ClientSession session, long characterId) =>
        new([
            new Eve.Marshal.PyInt(characterId),
            new Eve.Marshal.PyInt(Math.Max(0, session.GetInt("corpid") ?? 0)),
            new Eve.Marshal.PyInt(Math.Max(0, session.GetInt("allianceid") ?? 0)),
            new Eve.Marshal.PyInt(Math.Max(0, session.GetInt("warfactionid") ?? 0)),
        ]);
}

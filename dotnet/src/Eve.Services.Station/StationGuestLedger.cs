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

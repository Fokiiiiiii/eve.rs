using Eve.Services;
using Eve.Services.Station;

namespace Eve.Services.Station.Tests;

public sealed class StationGuestLedgerTests
{
    [Fact]
    public void Snapshot_seed_and_station_changes_suppress_duplicate_join_and_leave_notifications()
    {
        var ledger = new StationGuestLedger();
        var observer = new ClientSession();
        ledger.SeedObserverGuestLedger(observer, 60015249, new HashSet<long> { 1001, 1001, 0, -1 });

        Assert.False(ledger.TryRecordGuestJoined(observer, 60015249, 1001));
        Assert.True(ledger.TryRecordGuestJoined(observer, 60015249, 1002));
        Assert.False(ledger.TryRecordGuestJoined(observer, 60015249, 1002));
        Assert.False(ledger.TryRecordGuestLeft(observer, 60015249, 1999));
        Assert.True(ledger.TryRecordGuestLeft(observer, 60015249, 1002));
        Assert.False(ledger.TryRecordGuestLeft(observer, 60015249, 1002));

        Assert.True(ledger.TryRecordGuestJoined(observer, 60008494, 1001));
        Assert.False(ledger.TryRecordGuestJoined(observer, 60008494, 1001));

        var otherObserver = new ClientSession();
        Assert.True(ledger.TryRecordGuestJoined(otherObserver, 60015249, 1001));
    }


    [Fact]
    public async Task Guest_login_broadcasts_station_and_structure_join_and_leave_once()
    {
        var sessions = new CharacterSessions();
        var ledger = new StationGuestLedger();
        var hook = new GuestLoginHook(sessions, ledger);
        var stationId = 60015249L;
        var structureId = 1_000_000_001L;
        var stationObserverSink = new RecordingSink();
        var stationObserver = new ClientSession(stationObserverSink);
        stationObserver.Set("charid", new Eve.Marshal.PyInt(1001));
        stationObserver.Set("stationid", new Eve.Marshal.PyInt(stationId));
        sessions.Enter(1001, stationObserver);
        ledger.SeedObserverGuestLedger(stationObserver, stationId, new HashSet<long> { 1001 });

        var stationGuestSink = new RecordingSink();
        var stationGuest = new ClientSession(stationGuestSink);
        stationGuest.Set("charid", new Eve.Marshal.PyInt(1002));
        stationGuest.Set("corpid", new Eve.Marshal.PyInt(2001));
        stationGuest.Set("allianceid", new Eve.Marshal.PyInt(3001));
        stationGuest.Set("warfactionid", new Eve.Marshal.PyInt(4001));
        stationGuest.Set("stationid", new Eve.Marshal.PyInt(stationId));
        sessions.Enter(1002, stationGuest);

        await hook.OnCharacterEnteredAsync(stationGuest, 1002, CancellationToken.None);
        await hook.OnCharacterEnteredAsync(stationGuest, 1002, CancellationToken.None);
        Assert.Equal(["OnCharNowInStation"], stationObserverSink.Sent.Select(NotificationName));
        await hook.OnCharacterLeavingAsync(stationGuest, 1002, CancellationToken.None);
        await hook.OnCharacterLeavingAsync(stationGuest, 1002, CancellationToken.None);
        Assert.Equal(
            ["OnCharNowInStation", "OnCharNoLongerInStation"],
            stationObserverSink.Sent.Select(NotificationName));

        var structureObserverSink = new RecordingSink();
        var structureObserver = new ClientSession(structureObserverSink);
        structureObserver.Set("charid", new Eve.Marshal.PyInt(1011));
        structureObserver.Set("structureid", new Eve.Marshal.PyLong(structureId));
        sessions.Enter(1011, structureObserver);
        ledger.SeedObserverGuestLedger(structureObserver, structureId, new HashSet<long> { 1011 });

        var structureGuestSink = new RecordingSink();
        var structureGuest = new ClientSession(structureGuestSink);
        structureGuest.Set("charid", new Eve.Marshal.PyInt(1012));
        structureGuest.Set("corpid", new Eve.Marshal.PyInt(2011));
        structureGuest.Set("allianceid", new Eve.Marshal.PyInt(3011));
        structureGuest.Set("warfactionid", new Eve.Marshal.PyInt(4011));
        structureGuest.Set("structureid", new Eve.Marshal.PyLong(structureId));
        sessions.Enter(1012, structureGuest);

        await hook.OnCharacterEnteredAsync(structureGuest, 1012, CancellationToken.None);
        await hook.OnCharacterEnteredAsync(structureGuest, 1012, CancellationToken.None);
        Assert.Equal(["OnCharacterEnteredStructure"], structureObserverSink.Sent.Select(NotificationName));
        await hook.OnCharacterLeavingAsync(structureGuest, 1012, CancellationToken.None);
        Assert.Equal(
            ["OnCharacterEnteredStructure", "OnCharacterLeftStructure"],
            structureObserverSink.Sent.Select(NotificationName));
    }

    private static string? NotificationName(Eve.Marshal.PyValue packet)
    {
        if (packet is not Eve.Marshal.PyObject { Name: Eve.Marshal.PyStr name, Args: Eve.Marshal.PyTuple args })
            return null;
        return Eve.Macho.MachoAddress.Decode(args.Items[2]).BroadcastId;
    }

    private sealed class RecordingSink : Eve.Services.IPacketSink
    {
        public List<Eve.Marshal.PyValue> Sent { get; } = [];

        public void Send(Eve.Marshal.PyValue packet) => Sent.Add(packet);
    }
}

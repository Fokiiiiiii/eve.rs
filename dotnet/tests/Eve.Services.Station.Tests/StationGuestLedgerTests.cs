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
}

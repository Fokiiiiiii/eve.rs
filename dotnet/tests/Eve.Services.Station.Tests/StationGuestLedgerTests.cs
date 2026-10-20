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
    public async Task Station_to_space_structure_to_space_changes_reach_each_observer_once()
    {
        var sessions = new CharacterSessions();
        var ledger = new StationGuestLedger();
        var hook = new GuestLoginHook(sessions, ledger);
        const long stationId = 60015249;
        const long structureId = 1_000_000_001;
        const long guestId = 2001;
        var stationObserverSinks = new[] { new RecordingSink(), new RecordingSink() };
        var structureObserverSinks = new[] { new RecordingSink(), new RecordingSink() };

        for (var index = 0; index < stationObserverSinks.Length; index++)
        {
            var characterId = 1001 + index;
            var observer = CreateSession(stationObserverSinks[index], characterId, stationId);
            sessions.Enter(characterId, observer);
            ledger.SeedObserverGuestLedger(observer, stationId, new HashSet<long> { characterId });
        }

        for (var index = 0; index < structureObserverSinks.Length; index++)
        {
            var characterId = 1011 + index;
            var observer = CreateSession(structureObserverSinks[index], characterId, structureId: structureId);
            sessions.Enter(characterId, observer);
            ledger.SeedObserverGuestLedger(observer, structureId, new HashSet<long> { characterId });
        }

        var guest = CreateSession(new RecordingSink(), guestId, stationId);
        guest.Set("corpid", new Eve.Marshal.PyInt(2001));
        guest.Set("allianceid", new Eve.Marshal.PyInt(3001));
        guest.Set("warfactionid", new Eve.Marshal.PyInt(4001));
        sessions.Enter(guestId, guest);
        await hook.OnCharacterEnteredAsync(guest, guestId, CancellationToken.None);
        await hook.OnCharacterEnteredAsync(guest, guestId, CancellationToken.None);

        var guestIdentity = new Eve.Marshal.PyTuple([
            new Eve.Marshal.PyInt(guestId),
            new Eve.Marshal.PyInt(2001),
            new Eve.Marshal.PyInt(3001),
            new Eve.Marshal.PyInt(4001),
        ]);
        foreach (var sink in stationObserverSinks)
        {
            Assert.Equal(["OnCharNowInStation"], sink.Sent.Select(NotificationName));
            Assert.Equal(new Eve.Marshal.PyValue[] { guestIdentity }, NotificationArguments(sink.Sent[0]));
        }

        guest.Set("stationid", new Eve.Marshal.PyInt(0));
        hook.OnLocationChanged(guest, stationId, 0);
        hook.OnLocationChanged(guest, stationId, 0);
        foreach (var sink in stationObserverSinks)
        {
            Assert.Equal(
                ["OnCharNowInStation", "OnCharNoLongerInStation"],
                sink.Sent.Select(NotificationName));
            Assert.Equal(new Eve.Marshal.PyValue[] { guestIdentity }, NotificationArguments(sink.Sent[1]));
        }

        guest.Set("structureid", new Eve.Marshal.PyLong(structureId));
        hook.OnLocationChanged(guest, 0, 0);
        hook.OnLocationChanged(guest, 0, 0);
        foreach (var sink in structureObserverSinks)
        {
            Assert.Equal(["OnCharacterEnteredStructure"], sink.Sent.Select(NotificationName));
            Assert.Equal(
                new Eve.Marshal.PyValue[]
                {
                    new Eve.Marshal.PyInt(guestId),
                    new Eve.Marshal.PyInt(2001),
                    new Eve.Marshal.PyInt(3001),
                    new Eve.Marshal.PyInt(4001),
                },
                NotificationArguments(sink.Sent[0]));
        }

        guest.Set("structureid", new Eve.Marshal.PyLong(0));
        hook.OnLocationChanged(guest, 0, structureId);
        hook.OnLocationChanged(guest, 0, structureId);
        foreach (var sink in structureObserverSinks)
        {
            Assert.Equal(
                ["OnCharacterEnteredStructure", "OnCharacterLeftStructure"],
                sink.Sent.Select(NotificationName));
            Assert.Equal(
                new Eve.Marshal.PyValue[] { new Eve.Marshal.PyInt(guestId) },
                NotificationArguments(sink.Sent[1]));
        }
    }

    [Fact]
    public async Task Closed_handlers_follow_undock_redock_and_character_selection_rollback()
    {
        var sessions = new CharacterSessions();
        var ledger = new StationGuestLedger();
        var hook = new GuestLoginHook(sessions, ledger);
        const long stationId = 60015249;
        const long firstCharacterId = 1002;
        const long secondCharacterId = 1003;
        var observerSink = new RecordingSink();
        var observer = CreateSession(observerSink, 1001, stationId);
        sessions.Enter(1001, observer);
        ledger.SeedObserverGuestLedger(observer, stationId, new HashSet<long> { 1001 });

        var undocked = CreateSession(new RecordingSink(), firstCharacterId, stationId);
        undocked.Set("corpid", new Eve.Marshal.PyInt(2001));
        sessions.Enter(firstCharacterId, undocked);
        await hook.OnCharacterEnteredAsync(undocked, firstCharacterId, CancellationToken.None);
        undocked.Set("stationid", new Eve.Marshal.PyInt(0));
        hook.OnLocationChanged(undocked, stationId, 0);
        undocked.Close();

        var redocked = CreateSession(new RecordingSink(), firstCharacterId);
        redocked.Set("corpid", new Eve.Marshal.PyInt(2001));
        sessions.Enter(firstCharacterId, redocked);
        await hook.OnCharacterEnteredAsync(redocked, firstCharacterId, CancellationToken.None);
        redocked.Set("stationid", new Eve.Marshal.PyInt(stationId));
        hook.OnLocationChanged(redocked, 0, 0);

        await hook.OnCharacterLeavingAsync(redocked, firstCharacterId, CancellationToken.None);
        redocked.CharacterId = secondCharacterId;
        redocked.Set("corpid", new Eve.Marshal.PyInt(2002));
        sessions.Enter(secondCharacterId, redocked);
        await hook.OnCharacterEnteredAsync(redocked, secondCharacterId, CancellationToken.None);
        await hook.OnCharacterLeavingAsync(redocked, secondCharacterId, CancellationToken.None);

        redocked.CharacterId = firstCharacterId;
        redocked.Set("corpid", new Eve.Marshal.PyInt(2001));
        sessions.Enter(firstCharacterId, redocked);
        await hook.OnCharacterEnteredAsync(redocked, firstCharacterId, CancellationToken.None);
        redocked.Close();

        Assert.Equal(
            [
                "OnCharNowInStation", "OnCharNoLongerInStation",
                "OnCharNowInStation", "OnCharNoLongerInStation",
                "OnCharNowInStation", "OnCharNoLongerInStation",
                "OnCharNowInStation", "OnCharNoLongerInStation",
            ],
            observerSink.Sent.Select(NotificationName));
        Assert.Equal(
            [firstCharacterId, firstCharacterId, firstCharacterId, firstCharacterId,
                secondCharacterId, secondCharacterId, firstCharacterId, firstCharacterId],
            observerSink.Sent.Select(StationGuestCharacterId));
    }

    private static ClientSession CreateSession(
        RecordingSink sink,
        long characterId,
        long stationId = 0,
        long structureId = 0)
    {
        var session = new ClientSession(sink) { CharacterId = characterId };
        session.Set("stationid", new Eve.Marshal.PyInt(stationId));
        session.Set("structureid", new Eve.Marshal.PyLong(structureId));
        return session;
    }

    private static IReadOnlyList<Eve.Marshal.PyValue> NotificationArguments(Eve.Marshal.PyValue packet)
    {
        var state = Assert.IsType<Eve.Marshal.PyTuple>(Assert.IsType<Eve.Marshal.PyObject>(packet).Args);
        var payload = Assert.IsType<Eve.Marshal.PyTuple>(state.Items[4]);
        var wrappedPayload = Assert.IsType<Eve.Marshal.PyTuple>(payload.Items[0]);
        var bytes = Assert.IsType<Eve.Marshal.PyBuffer>(wrappedPayload.Items[1]).Value;
        var body = Assert.IsType<Eve.Marshal.PyTuple>(Eve.Marshal.MarshalCodec.DecodeExact(bytes));
        var call = Assert.IsType<Eve.Marshal.PyTuple>(body.Items[1]);
        return Assert.IsType<Eve.Marshal.PyTuple>(call.Items[1]).Items;
    }

    private static long StationGuestCharacterId(Eve.Marshal.PyValue packet)
    {
        var guest = Assert.IsType<Eve.Marshal.PyTuple>(Assert.Single(NotificationArguments(packet)));
        return Assert.IsType<Eve.Marshal.PyInt>(guest.Items[0]).Value;
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

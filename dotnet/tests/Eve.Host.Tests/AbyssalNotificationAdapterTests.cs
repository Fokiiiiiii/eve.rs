using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Host;
using Eve.Content;

namespace Eve.Host.Tests;

public sealed class AbyssalNotificationAdapterTests
{
    private const long Character = 41;
    private const long OtherCharacter = 42;

    private sealed class Sink : IPacketSink
    {
        public List<PyValue> Sent { get; } = [];
        public void Send(PyValue packet) => Sent.Add(packet);
    }

    private sealed record Notice(string Name, string IdType, PyTuple Args);

    [Theory]
    [InlineData(1, false)] [InlineData(1, true)]
    [InlineData(2, false)] [InlineData(2, true)]
    [InlineData(3, false)] [InlineData(3, true)]
    public void Filament_consumption_preserves_wire_row_old_values_and_retry_is_not_resent(int consumed, bool exact)
    {
        var sessions = new CharacterSessions();
        var sink = new Sink();
        sessions.Enter(Character, Session(Character, sink));
        var adapter = new AbyssalNotificationAdapter(sessions);
        var before = new Eve.Inventory.Item(123, 56131, Character, 456, 5, exact ? consumed : consumed + 4, false, null);
        var after = exact ? before with { LocationId = 6 } : before with { Quantity = before.Quantity - consumed };
        var change = new AbyssalFilamentConsumedEvent(Guid.NewGuid().ToString("D"), Character, before, after, 1979, 22);
        Assert.True(adapter.SendFilamentConsumed(change));
        Assert.True(adapter.SendFilamentConsumed(change));
        var notice = Assert.Single(Notices(sink));
        Assert.Equal(("OnItemChange", "clientID"), (notice.Name, notice.IdType));
        var row = Assert.IsType<PyPackedRow>(notice.Args.Items[0]);
        Assert.Equal(InventoryWire.Id(after.LocationId), row.Values[3]);
        Assert.Equal(new PyInt(after.Quantity), row.Values[5]);
        Assert.Equal(new PyInt(1979), row.Values[6]);
        Assert.Equal(new PyInt(22), row.Values[7]);
        var old = Assert.IsType<PyDict>(notice.Args.Items[1]);
        Assert.Equal(new PyDict([new(new PyInt(exact ? 3 : 9),
            InventoryWire.Id(exact ? before.LocationId : before.Quantity))]), old);
    }

    [Fact]
    public void Location_entered_sends_the_client_dict_to_its_snapshotted_character()
    {
        var sessions = new CharacterSessions();
        var recipientSink = new Sink();
        var otherSink = new Sink();
        sessions.Enter(Character, Session(Character, recipientSink));
        sessions.Enter(OtherCharacter, Session(OtherCharacter, otherSink));
        var adapter = new AbyssalNotificationAdapter(sessions);

        var sent = adapter.SendLocationEntered(
            new AbyssalLocationEnteredEvent(9001, "1c521683-25ed-4825-96dd-34e7bbfeab77", 4, 30_000_142, Character, 2, 6007, "environment-room-2"),
            difficultyTier: 4);

        Assert.True(sent);
        var notice = Assert.Single(Notices(recipientSink));
        Assert.Equal("OnAbyssalLocationEntered", notice.Name);
        Assert.Equal("charid", notice.IdType);
        var payload = Assert.IsType<PyDict>(Assert.Single(notice.Args.Items));
        var values = payload.Entries.ToDictionary(
            entry => JsSemantics.StringValue(entry.Key) ?? string.Empty,
            entry => entry.Value,
            StringComparer.Ordinal);
        Assert.Equal(new PyInt(4), values["difficulty_tier"]);
        Assert.Equal("1c521683-25ed-4825-96dd-34e7bbfeab77", JsSemantics.StringValue(values["content_id"]));
        Assert.Equal(new PyInt(2), values["location_id"]);
        Assert.Equal(new PyInt(6007), values["nebula_graphic_id"]);
        Assert.Empty(otherSink.Sent);
    }

    [Fact]
    public void Deadline_uses_the_absolute_filetime_and_missing_recipient_is_reported()
    {
        var sessions = new CharacterSessions();
        var sink = new Sink();
        sessions.Enter(Character, Session(Character, sink));
        var adapter = new AbyssalNotificationAdapter(sessions);
        const long expiresAtMs = 1_800_000_123_456;

        Assert.True(adapter.SendDeadlineAdded(
            new AbyssalDeadlineAddedEvent(9002, "b0b4484e-ad88-4930-9e84-548074676ac8", Character, expiresAtMs)));
        var notice = Assert.Single(Notices(sink));
        Assert.Equal("OnAddExpiryDurationForAbyssalContent", notice.Name);
        Assert.Equal("charid", notice.IdType);
        Assert.Equal(new PyLong(Py.FileTimeFromUnixMs(expiresAtMs)), Assert.IsType<PyLong>(Assert.Single(notice.Args.Items)));

        Assert.False(adapter.SendDeadlineAdded(
            new AbyssalDeadlineAddedEvent(9002, "b0b4484e-ad88-4930-9e84-548074676ac8", OtherCharacter, expiresAtMs)));
        Assert.Single(sink.Sent);
    }

    [Fact]
    public void Location_exit_has_no_arguments_and_finished_uses_normal_pve_timer_type()
    {
        var sessions = new CharacterSessions();
        var sink = new Sink();
        sessions.Enter(Character, Session(Character, sink));
        var adapter = new AbyssalNotificationAdapter(sessions);

        Assert.True(adapter.SendLocationExited(
            new AbyssalLocationExitedEvent(9003, "4ad689cc-bbe2-487a-8992-0f7ba7647bb6", Character, 3, "environment-room-3")));
        Assert.True(adapter.SendContentFinished(
            new AbyssalParticipantContentFinishedEvent(9003, "4ad689cc-bbe2-487a-8992-0f7ba7647bb6", Character)));

        var notices = Notices(sink);
        Assert.Equal("OnAbyssalLocationExited", notices[0].Name);
        Assert.Equal("charid", notices[0].IdType);
        Assert.Empty(notices[0].Args.Items);
        Assert.Equal("OnAbyssalContentFinished", notices[1].Name);
        Assert.Equal("charid", notices[1].IdType);
        Assert.Equal("4ad689cc-bbe2-487a-8992-0f7ba7647bb6", JsSemantics.StringValue(notices[1].Args.Items[0]));
        Assert.Equal(new PyInt(11), notices[1].Args.Items[1]);
    }

    [Fact]
    public void Dungeon_room_enter_and_exit_notifications_match_the_ship_channel_wire_shape()
    {
        var sessions = new CharacterSessions();
        var sink = new Sink();
        sessions.Enter(Character, Session(Character, sink));
        var adapter = new AbyssalNotificationAdapter(sessions);

        Assert.True(adapter.SendEnteringDungeonRoom(Character, 9001, 17, [1.25, -2.5, 3]));
        Assert.True(adapter.SendExitingDungeon(Character, 9001));

        var notices = Notices(sink);
        Assert.Collection(notices,
            entered =>
            {
                Assert.Equal("OnEnteringDungeonRoom", entered.Name);
                Assert.Equal("shipid", entered.IdType);
                Assert.Collection(entered.Args.Items,
                    dungeon => Assert.Equal(new PyInt(9001), dungeon),
                    room => Assert.Equal(new PyInt(17), room),
                    position =>
                    {
                        var vector = Assert.IsType<PyTuple>(position);
                        Assert.Equal(new PyReal(1.25), vector.Items[0]);
                        Assert.Equal(new PyReal(-2.5), vector.Items[1]);
                        Assert.Equal(new PyReal(3), vector.Items[2]);
                    },
                    value => Assert.Same(PyNone.Instance, value),
                    value => Assert.Same(PyNone.Instance, value));
            },
            exited =>
            {
                Assert.Equal("OnExitingDungeon", exited.Name);
                Assert.Equal("shipid", exited.IdType);
                Assert.Equal(new PyInt(9001), Assert.Single(exited.Args.Items));
            });
    }


    [Fact]
    public void Room_entry_is_idempotent_per_session_and_client_ready_can_force_resynchronization()
    {
        var sessions = new CharacterSessions();
        var sink = new Sink();
        sessions.Enter(Character, Session(Character, sink));
        var adapter = new AbyssalNotificationAdapter(sessions);
        const string contentId = "a86e4505-e542-4a65-a3f2-f0738cf3bb9e";

        Assert.True(adapter.SendEnteringDungeonRoom(Character, 9001, 2, [1, 2, 3], contentId));
        Assert.True(adapter.SendEnteringDungeonRoom(Character, 9001, 2, [1, 2, 3], contentId));
        Assert.True(adapter.SendEnteringDungeonRoom(Character, 9001, 2, [1, 2, 3], contentId, forceResend: true));

        Assert.Equal(2, Notices(sink).Count(notice => notice.Name == "OnEnteringDungeonRoom"));
    }


    [Fact]
    public void Client_ready_resends_the_same_room_and_absolute_deadline_after_outbox_delivery()
    {
        var sessions = new CharacterSessions();
        var sink = new Sink();
        sessions.Enter(Character, Session(Character, sink));
        var adapter = new AbyssalNotificationAdapter(sessions);
        var entered = new AbyssalLocationEnteredEvent(
            9004, "d7a0e847-28d8-44ec-b34f-9535f2ad59a8", 3, 30_000_142,
            Character, 1, 6123, "environment-room-1");
        var deadline = new AbyssalDeadlineAddedEvent(
            9004, "d7a0e847-28d8-44ec-b34f-9535f2ad59a8", Character, 1_800_000_123_456);

        Assert.True(adapter.SendLocationEntered(entered, entered.DifficultyTier));
        Assert.True(adapter.SendDeadlineAdded(deadline));
        Assert.True(adapter.SendLocationEntered(entered, entered.DifficultyTier));
        Assert.True(adapter.SendDeadlineAdded(deadline));
        Assert.True(adapter.SendLocationEntered(entered, entered.DifficultyTier, forceResend: true));
        Assert.True(adapter.SendDeadlineAdded(deadline, forceResend: true));

        var notices = Notices(sink);
        Assert.Equal(4, notices.Count);
        Assert.Equal(2, notices.Count(notice => notice.Name == "OnAbyssalLocationEntered"));
        Assert.Equal(2, notices.Count(notice => notice.Name == "OnAddExpiryDurationForAbyssalContent"));
        var deadlines = notices.Where(notice => notice.Name == "OnAddExpiryDurationForAbyssalContent").ToArray();
        Assert.All(deadlines, notice =>
            Assert.Equal(
                new PyLong(Py.FileTimeFromUnixMs(deadline.ExpiresAtMs)),
                Assert.IsType<PyLong>(Assert.Single(notice.Args.Items))));
    }

    private static ClientSession Session(long characterId, Sink sink)
    {
        var session = new ClientSession(sink);
        session.Set("charid", new PyLong(characterId));
        session.Set("userid", new PyLong(99));
        return session;
    }

    private static List<Notice> Notices(Sink sink)
    {
        var notices = new List<Notice>();
        foreach (var packet in sink.Sent.OfType<PyObject>())
        {
            var fields = Assert.IsType<PyTuple>(packet.Args).Items;
            if (fields[0] is not PyInt { Value: MachoMessage.Notification })
                continue;
            var destination = MachoAddress.Decode(fields[2]);
            var envelope = Assert.IsType<PyTuple>(Assert.IsType<PyTuple>(fields[4]).Items[0]);
            var call = Assert.IsType<PyTuple>(MarshalCodec.Decode(Assert.IsType<PyBuffer>(envelope.Items[1]).Value));
            var args = Assert.IsType<PyTuple>(Assert.IsType<PyTuple>(call.Items[1]).Items[1]);
            notices.Add(new(destination.BroadcastId ?? "", destination.IdType ?? "", args));
        }
        return notices;
    }
}

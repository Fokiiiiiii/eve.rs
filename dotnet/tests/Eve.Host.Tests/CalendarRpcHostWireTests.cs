using System.Text;
using Eve.Calendar;
using Eve.Host;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [PgFact]
    public async Task Calendar_rpc_methods_are_reachable_over_host_and_share_state_and_notifications()
    {
        var ownerId = Random.Shared.NextInt64(91_000_000, 92_000_000);
        var inviteeId = ownerId + 1;
        var removedInviteeId = ownerId + 2;
        var addedInviteeId = ownerId + 3;
        var corporationId = ownerId + 10;
        var allianceId = ownerId + 11;
        const long chatManagerRole = 36_028_797_018_963_968L;
        await using var dataSource = Npgsql.NpgsqlDataSource.Create(
            Eve.Data.PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        await CalendarStore.MigrateAsync(dataSource);
        var store = new CalendarStore(dataSource);
        var sessions = new CharacterSessions();
        var audience = new WireCalendarAudience(inviteeId, removedInviteeId);
        var opened = 0;
        var allowGroupManager = true;
        Func<long, CancellationToken, ValueTask<(long CorporationId, long AllianceId)>> readGroups =
            (_, _) => ValueTask.FromResult((corporationId, allianceId));

        Start(
            strictRpc: true,
            configure: registry =>
            {
                registry.Register(new CalendarMgrService(
                    store, audience, sessions,
                    (_, _, _, _) => ValueTask.FromResult(allowGroupManager),
                    readGroups));
                registry.Register(new CalendarProxyService(store, readGroups));
            },
            sessionOpened: session =>
            {
                var characterId = Interlocked.Increment(ref opened) switch
                {
                    1 => ownerId,
                    2 => inviteeId,
                    3 => removedInviteeId,
                    _ => addedInviteeId,
                };
                session.Set("charid", new PyLong(characterId));
                session.Set("corpid", new PyLong(corporationId));
                session.Set("allianceid", new PyLong(allianceId));
                session.Set("corprole", new PyLong(chatManagerRole));
                if (characterId != ownerId) sessions.Enter(characterId, session);
            });

        var (ownerFront, ownerOpen) = await OpenCalendarFrontAsync(401);
        var (inviteeFront, inviteeOpen) = await OpenCalendarFrontAsync(402);
        var (removedFront, _) = await OpenCalendarFrontAsync(403);
        var (addedFront, _) = await OpenCalendarFrontAsync(404);

        var originalTime = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc)
            .AddMonths(1).AddDays(2).AddHours(10).ToFileTimeUtc();
        var eventId = WireInteger(await HostRpcAsync(
            ownerFront, ownerOpen, 1, "calendarMgr", "CreatePersonalEvent",
            new PyLong(originalTime), PyNone.Instance, new PyStr("  Wire event  "),
            PyNone.Instance, new PyInt(1),
            new PyTuple([new PyInt(inviteeId), new PyLong(removedInviteeId)])));
        await AssertCalendarNotificationAsync(inviteeFront, "OnNewCalendarEvent");
        await AssertCalendarNotificationAsync(removedFront, "OnNewCalendarEvent");

        var month = DateTime.FromFileTimeUtc(originalTime);
        var list = Assert.IsType<PyTuple>(await HostRpcAsync(
            ownerFront, ownerOpen, 2, "calendarProxy", "GetEventList",
            new PyInt(month.Month), new PyLong(month.Year)));
        Assert.Equal(3, list.Items.Count);
        var personalRows = Assert.IsType<PyList>(list.Items[0]);
        var personalRow = Assert.Single(personalRows.Items);
        Assert.Equal(eventId, WireInteger(CalendarEventRowField(personalRow, "eventID")));
        Assert.Equal("Wire event", WireText(CalendarEventRowField(personalRow, "eventTitle")));
        Assert.Equal(PyNone.Instance, CalendarEventRowField(personalRow, "eventDuration"));
        Assert.Equal(new PyInt(1), CalendarEventRowField(personalRow, "importance"));

        var details = Assert.IsType<PyObject>(await HostRpcAsync(
            ownerFront, ownerOpen, 3, "calendarProxy", "GetEventDetails",
            new PyInt(eventId), new PyLong(ownerId)));
        Assert.Equal("", WireText(WireField(details, "eventText")));
        Assert.Equal(new PyLong(ownerId), WireField(details, "creatorID"));

        var ownerResponses = Assert.IsType<PyList>(await HostRpcAsync(
            ownerFront, ownerOpen, 4, "calendarMgr", "GetResponsesForCharacter"));
        Assert.Single(ownerResponses.Items);
        Assert.Equal(CalendarResponseStates.Accepted,
            WireInteger(WireField(Assert.Single(ownerResponses.Items,
                row => WireInteger(WireField(row, "eventID")) == eventId), "status")));

        var eventResponses = Assert.IsType<PyList>(await HostRpcAsync(
            ownerFront, ownerOpen, 5, "calendarMgr", "GetResponsesToEvent",
            new PyLong(eventId), new PyInt(ownerId)));
        Assert.Equal(3, eventResponses.Items.Count);

        Assert.IsType<PyNone>(await HostRpcAsync(
            inviteeFront, inviteeOpen, 1, "calendarMgr", "SendEventResponse",
            new PyInt(eventId), new PyLong(ownerId), new PyLong(CalendarResponseStates.Maybe)));
        await AssertCalendarNotificationAsync(removedFront, "OnEventResponseByExternal");

        var inviteeResponses = Assert.IsType<PyList>(await HostRpcAsync(
            inviteeFront, inviteeOpen, 2, "calendarMgr", "GetResponsesForCharacter"));
        Assert.Equal(CalendarResponseStates.Maybe,
            WireInteger(WireField(Assert.Single(inviteeResponses.Items,
                row => WireInteger(WireField(row, "eventID")) == eventId), "status")));

        var editedTime = DateTime.FromFileTimeUtc(originalTime).AddDays(1).ToFileTimeUtc();
        Assert.IsType<PyNone>(await HostRpcAsync(
            ownerFront, ownerOpen, 6, "calendarMgr", "EditPersonalEvent",
            new PyLong(eventId), new PyLong(originalTime), new PyLong(editedTime),
            new PyInt(30), new PyStr("Edited event"), new PyStr("updated text"), new PyInt(0)));
        await AssertCalendarNotificationAsync(inviteeFront, "OnEditCalendarEvent");
        await AssertCalendarNotificationAsync(removedFront, "OnEditCalendarEvent");

        eventResponses = Assert.IsType<PyList>(await HostRpcAsync(
            ownerFront, ownerOpen, 7, "calendarMgr", "GetResponsesToEvent",
            new PyInt(eventId), new PyLong(ownerId)));
        Assert.Equal(CalendarResponseStates.Undecided,
            WireInteger(WireField(Assert.Single(eventResponses.Items,
                row => WireInteger(WireField(row, "characterID")) == inviteeId), "status")));

        Assert.IsType<PyNone>(await HostRpcAsync(
            ownerFront, ownerOpen, 8, "calendarMgr", "UpdateEventParticipants",
            new PyLong(eventId), new PyTuple([new PyLong(addedInviteeId)]),
            new PyList([new PyLong(removedInviteeId)])));
        await AssertCalendarNotificationAsync(removedFront, "OnRemoveCalendarEvent");
        await AssertCalendarNotificationAsync(addedFront, "OnNewCalendarEvent");

        eventResponses = Assert.IsType<PyList>(await HostRpcAsync(
            ownerFront, ownerOpen, 9, "calendarMgr", "GetResponsesToEvent",
            new PyLong(eventId), new PyInt(ownerId)));
        Assert.Equal(CalendarResponseStates.Uninvited,
            WireInteger(WireField(Assert.Single(eventResponses.Items,
                row => WireInteger(WireField(row, "characterID")) == removedInviteeId), "status")));
        Assert.Equal(CalendarResponseStates.Undecided,
            WireInteger(WireField(Assert.Single(eventResponses.Items,
                row => WireInteger(WireField(row, "characterID")) == addedInviteeId), "status")));

        var corporationTime = DateTime.FromFileTimeUtc(originalTime).AddHours(1).ToFileTimeUtc();
        var corporationEventId = WireInteger(await HostRpcAsync(
            ownerFront, ownerOpen, 10, "calendarMgr", "CreateCorporationEvent",
            new PyLong(corporationTime), new PyInt(60), new PyStr("Corporation event"),
            new PyStr("corporation text"), new PyInt(0)));
        await AssertCalendarNotificationAsync(inviteeFront, "OnNewCalendarEvent");
        await AssertCalendarNotificationAsync(removedFront, "OnNewCalendarEvent");

        var allianceTime = DateTime.FromFileTimeUtc(originalTime).AddHours(2).ToFileTimeUtc();
        var allianceEventId = WireInteger(await HostRpcAsync(
            ownerFront, ownerOpen, 11, "calendarMgr", "CreateAllianceEvent",
            new PyInt(allianceTime), new PyLong(45), new PyStr("Alliance event"),
            new PyStr("alliance text"), new PyInt(1)));
        await AssertCalendarNotificationAsync(inviteeFront, "OnNewCalendarEvent");
        await AssertCalendarNotificationAsync(removedFront, "OnNewCalendarEvent");

        var groupList = Assert.IsType<PyTuple>(await HostRpcAsync(
            ownerFront, ownerOpen, 12, "calendarProxy", "GetEventList",
            new PyLong(month.Month), new PyInt(month.Year)));
        Assert.Equal(corporationEventId,
            WireInteger(CalendarEventRowField(Assert.Single(Assert.IsType<PyList>(groupList.Items[1]).Items), "eventID")));
        Assert.Equal(allianceEventId,
            WireInteger(CalendarEventRowField(Assert.Single(Assert.IsType<PyList>(groupList.Items[2]).Items), "eventID")));

        var corporationEditedTime = corporationTime + TimeSpan.TicksPerHour;
        Assert.IsType<PyNone>(await HostRpcAsync(
            ownerFront, ownerOpen, 13, "calendarMgr", "EditCorporationEvent",
            new PyLong(corporationEventId), new PyLong(corporationTime), new PyLong(corporationEditedTime),
            new PyInt(60), new PyStr("Edited corporation event"), new PyStr("corp edit"), new PyInt(0)));
        await AssertCalendarNotificationAsync(inviteeFront, "OnEditCalendarEvent");
        await AssertCalendarNotificationAsync(removedFront, "OnEditCalendarEvent");

        var allianceEditedTime = allianceTime + TimeSpan.TicksPerHour;
        Assert.IsType<PyNone>(await HostRpcAsync(
            ownerFront, ownerOpen, 14, "calendarMgr", "EditAllianceEvent",
            new PyLong(allianceEventId), new PyLong(allianceTime), new PyLong(allianceEditedTime),
            new PyInt(45), new PyStr("Edited alliance event"), new PyStr("alliance edit"), new PyInt(1)));
        await AssertCalendarNotificationAsync(inviteeFront, "OnEditCalendarEvent");
        await AssertCalendarNotificationAsync(removedFront, "OnEditCalendarEvent");

        var finalDetails = Assert.IsType<PyObject>(await HostRpcAsync(
            ownerFront, ownerOpen, 15, "calendarProxy", "GetEventDetails",
            new PyLong(corporationEventId), new PyInt(corporationId)));
        Assert.Equal("corp edit", WireText(WireField(finalDetails, "eventText")));

        Assert.IsType<PyNone>(await HostRpcAsync(
            ownerFront, ownerOpen, 16, "calendarMgr", "DeleteEvent",
            new PyInt(eventId), new PyLong(ownerId)));
        await AssertCalendarNotificationAsync(inviteeFront, "OnRemoveCalendarEvent");
        var deletedEvents = Assert.IsType<PyTuple>(await HostRpcAsync(
            ownerFront, ownerOpen, 17, "calendarProxy", "GetEventList",
            new PyLong(month.Month), new PyInt(month.Year)));
        var deletedEvent = Assert.Single(Assert.IsType<PyList>(deletedEvents.Items[0]).Items,
            row => WireInteger(CalendarEventRowField(row, "eventID")) == eventId);
        Assert.Equal(PyBool.True, CalendarEventRowField(deletedEvent, "isDeleted"));
        await AssertCalendarErrorAsync(ownerFront, ownerOpen, 18, "calendarMgr", "EditPersonalEvent",
            "That calendar event has been deleted.", new PyLong(eventId), new PyLong(editedTime),
            new PyLong(editedTime), new PyInt(30), new PyStr("Deleted event"), new PyStr(""), new PyInt(0));
    }

    [Fact]
    public async Task Calendar_rpc_errors_keep_client_specific_user_errors_over_host_wire()
    {
        const long ownerId = 92_001;
        const long inviteeId = 92_002;
        const long outsiderId = 92_003;
        const long corporationId = 99_001;
        const long allianceId = 99_002;
        const long chatManagerRole = 36_028_797_018_963_968L;
        var store = new WireCalendarStore();
        var sessions = new CharacterSessions();
        var audience = new WireCalendarAudience(inviteeId);
        var opened = 0;
        var allowGroupManager = true;
        Func<long, CancellationToken, ValueTask<(long CorporationId, long AllianceId)>> readGroups =
            (_, _) => ValueTask.FromResult((corporationId, allianceId));

        Start(
            strictRpc: true,
            configure: registry =>
            {
                registry.Register(new CalendarMgrService(
                    store, audience, sessions,
                    (_, _, _, _) => ValueTask.FromResult(allowGroupManager),
                    readGroups));
                registry.Register(new CalendarProxyService(store, readGroups));
            },
            sessionOpened: session =>
            {
                var characterId = Interlocked.Increment(ref opened) switch
                {
                    1 => ownerId,
                    2 => inviteeId,
                    _ => outsiderId,
                };
                session.Set("charid", new PyLong(characterId));
                session.Set("corpid", new PyLong(corporationId));
                session.Set("allianceid", new PyLong(allianceId));
                session.Set("corprole", new PyLong(chatManagerRole));
                if (characterId != ownerId) sessions.Enter(characterId, session);
            });

        var (ownerFront, ownerOpen) = await OpenCalendarFrontAsync(411);
        var (inviteeFront, inviteeOpen) = await OpenCalendarFrontAsync(412);
        var (outsiderFront, outsiderOpen) = await OpenCalendarFrontAsync(413);
        var eventTime = DateTime.UtcNow.AddDays(2).ToFileTimeUtc();
        var eventId = WireInteger(await HostRpcAsync(
            ownerFront, ownerOpen, 1, "calendarMgr", "CreatePersonalEvent",
            new PyLong(eventTime), PyNone.Instance, new PyStr("Error event"),
            PyNone.Instance, new PyInt(0), new PyList([new PyLong(inviteeId)])));
        await AssertCalendarNotificationAsync(inviteeFront, "OnNewCalendarEvent");
        var corporationEventId = WireInteger(await HostRpcAsync(
            ownerFront, ownerOpen, 2, "calendarMgr", "CreateCorporationEvent",
            new PyLong(DateTime.UtcNow.AddDays(3).ToFileTimeUtc()), new PyInt(30),
            new PyStr("Permission event"), new PyStr(""), new PyInt(0)));
        await AssertCalendarNotificationAsync(inviteeFront, "OnNewCalendarEvent");
        Assert.Empty(Assert.IsType<PyList>(await HostRpcAsync(
            outsiderFront, outsiderOpen, 1, "calendarMgr", "GetResponsesForCharacter")).Items);

        await AssertCalendarErrorAsync(inviteeFront, inviteeOpen, 1, "calendarMgr", "EditPersonalEvent",
            "You do not have permission to edit that calendar event.",
            new PyLong(eventId), new PyLong(eventTime), new PyLong(eventTime),
            new PyInt(15), new PyStr("Rejected edit"), new PyStr(""), new PyInt(0));

        await AssertCalendarErrorAsync(ownerFront, ownerOpen, 2, "calendarMgr", "EditPersonalEvent",
            "The calendar event changed before it could be edited.",
            new PyLong(eventId), new PyLong(eventTime - 1), new PyLong(eventTime),
            new PyInt(15), new PyStr("Stale edit"), new PyStr(""), new PyInt(0));

        await AssertCalendarErrorAsync(ownerFront, ownerOpen, 4, "calendarMgr", "DeleteEvent",
            "Calendar event owner mismatch.", new PyLong(eventId), new PyLong(ownerId + 1));
        await AssertCalendarErrorAsync(outsiderFront, outsiderOpen, 2, "calendarMgr", "DeleteEvent",
            "You do not have permission to delete that calendar event.",
            new PyLong(eventId), new PyLong(ownerId));
        await AssertCalendarErrorAsync(outsiderFront, outsiderOpen, 3, "calendarMgr", "UpdateEventParticipants",
            "You do not have permission to update event participants.",
            new PyLong(eventId), new PyList([]), new PyList([]));
        await AssertCalendarErrorAsync(outsiderFront, outsiderOpen, 4, "calendarMgr", "GetResponsesToEvent",
            "You do not have access to those calendar responses.",
            new PyLong(eventId), new PyLong(ownerId));
        await AssertCalendarErrorAsync(outsiderFront, outsiderOpen, 5, "calendarProxy", "GetEventDetails",
            "You do not have access to that calendar event.",
            new PyLong(eventId), new PyLong(ownerId));
        await AssertCalendarErrorAsync(outsiderFront, outsiderOpen, 6, "calendarMgr", "SendEventResponse",
            "You do not have access to that calendar event.",
            new PyLong(eventId), new PyLong(ownerId), new PyInt(CalendarResponseStates.Accepted));

        await AssertCalendarErrorAsync(inviteeFront, inviteeOpen, 2, "calendarMgr", "SendEventResponse",
            "Invalid calendar response.", new PyLong(eventId), new PyLong(ownerId), new PyLong(99));

        await AssertCalendarErrorAsync(ownerFront, ownerOpen, 5, "calendarMgr", "UpdateEventParticipants",
            "Only personal events support invitee updates.",
            new PyLong(corporationEventId), new PyList([]), new PyList([]));

        allowGroupManager = false;
        await AssertCalendarErrorAsync(ownerFront, ownerOpen, 6, "calendarMgr", "CreateCorporationEvent",
            "You do not have permission to create corporation calendar events.",
            new PyLong(DateTime.UtcNow.AddDays(3).ToFileTimeUtc()), new PyInt(30),
            new PyStr("Denied corporation event"), new PyStr(""), new PyInt(0));
        await AssertCalendarErrorAsync(ownerFront, ownerOpen, 7, "calendarMgr", "CreateAllianceEvent",
            "You do not have permission to create alliance calendar events.",
            new PyLong(DateTime.UtcNow.AddDays(3).ToFileTimeUtc()), new PyInt(30),
            new PyStr("Denied alliance event"), new PyStr(""), new PyInt(0));
        allowGroupManager = true;

        await AssertCalendarErrorAsync(ownerFront, ownerOpen, 8, "calendarMgr", "CreatePersonalEvent",
            "Calendar event description is too long.",
            new PyLong(DateTime.UtcNow.AddDays(4).ToFileTimeUtc()), PyNone.Instance,
            new PyStr("Long description"), new PyStr(new string('x', 501)), new PyInt(0), new PyList([]));

        await AssertCalendarErrorAsync(ownerFront, ownerOpen, 9, "calendarMgr", "CreatePersonalEvent",
            "Too many invitees selected for that event.",
            new PyLong(DateTime.UtcNow.AddDays(4).ToFileTimeUtc()), PyNone.Instance,
            new PyStr("Many invitees"), new PyStr(""),
            new PyInt(0), new PyList(Enumerable.Range(1, 51).Select(id => new PyLong(100_000 + id)).ToArray()));

        await AssertCalendarUserErrorAsync(ownerFront, ownerOpen, 10, "calendarMgr", "CreatePersonalEvent",
            "CalendarCannotPlanThePast",
            new PyLong(DateTime.UtcNow.AddMinutes(-1).ToFileTimeUtc()), PyNone.Instance,
            new PyStr("Past event"), new PyStr(""), new PyInt(0), new PyList([]));

        await AssertCalendarUserErrorAsync(ownerFront, ownerOpen, 11, "calendarMgr", "CreatePersonalEvent",
            "CalendarEventMustSpecifyTitle",
            new PyLong(DateTime.UtcNow.AddDays(2).ToFileTimeUtc()), PyNone.Instance,
            new PyStr(""), new PyStr(""), new PyInt(0), new PyList([]));
        await AssertCalendarUserErrorAsync(ownerFront, ownerOpen, 12, "calendarMgr", "CreatePersonalEvent",
            "CalendarTitleTooLong",
            new PyLong(DateTime.UtcNow.AddDays(2).ToFileTimeUtc()), PyNone.Instance,
            new PyStr(new string('x', 41)), new PyStr(""), new PyInt(0), new PyList([]));
        await AssertCalendarUserErrorAsync(ownerFront, ownerOpen, 13, "calendarMgr", "CreatePersonalEvent",
            "CalendarInvalidDuration",
            new PyLong(DateTime.UtcNow.AddDays(2).ToFileTimeUtc()), new PyInt(-1),
            new PyStr("Invalid duration"), new PyStr(""), new PyInt(0), new PyList([]));
        await AssertCalendarUserErrorAsync(ownerFront, ownerOpen, 14, "calendarMgr", "CreatePersonalEvent",
            "CalendarTooFarIntoFuture",
            new PyLong(DateTime.UtcNow.AddMonths(13).ToFileTimeUtc()), PyNone.Instance,
            new PyStr("Future event"), new PyStr(""), new PyInt(0), new PyList([]));
        await AssertCalendarErrorAsync(ownerFront, ownerOpen, 15, "calendarProxy", "GetEventDetails",
            "Calendar event not found.", new PyLong(999_999), new PyLong(ownerId));
    }

    private static PyValue CalendarEventRowField(PyValue row, string name) =>
        Assert.IsType<PyDict>(row).Entries.Single(pair => WireText(pair.Key) == name).Value;

    private async Task<(TestFront Front, SessionOpen Open)> OpenCalendarFrontAsync(uint userId)
    {
        var front = await Handshaken(await ConnectAsync());
        var open = Open(1_790_646_377_000UL * 15 + userId, userId);
        await front.SendAsync(open);
        _ = await front.ReceiveAsync();
        return (front, open);
    }

    private async Task AssertCalendarNotificationAsync(TestFront front, string name)
    {
        var send = Assert.IsType<Send>(await front.ReceiveAsync());
        var packet = Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(send.Payload)));
        Assert.Equal(MachoMessage.Notification, packet.Type);
        Assert.Equal(name, packet.Dest.BroadcastId);

        var fields = Assert.IsType<PyTuple>(Assert.IsType<PyObject>(MarshalCodec.Decode(send.Payload)).Args);
        var envelope = Assert.IsType<PyTuple>(Assert.IsType<PyTuple>(fields.Items[4]).Items[0]);
        var notification = Assert.IsType<PyTuple>(MarshalCodec.Decode(
            Assert.IsType<PyBuffer>(envelope.Items[1]).Value));
        var args = Assert.IsType<PyTuple>(Assert.IsType<PyTuple>(notification.Items[1]).Items[1]);
        switch (name)
        {
            case "OnNewCalendarEvent":
                Assert.Equal(8, args.Items.Count);
                Assert.IsType<PyLong>(args.Items[0]);
                Assert.IsType<PyLong>(args.Items[1]);
                Assert.IsType<PyLong>(args.Items[2]);
                Assert.True(args.Items[3] is PyInt or PyNone);
                Assert.True(args.Items[4] is PyStr or PyBytes);
                _ = WireText(args.Items[4]);
                Assert.IsType<PyInt>(args.Items[5]);
                Assert.True(args.Items[6] is PyInt or PyNone);
                Assert.IsType<PyBool>(args.Items[7]);
                break;
            case "OnEditCalendarEvent":
                Assert.Equal(8, args.Items.Count);
                Assert.IsType<PyLong>(args.Items[0]);
                Assert.IsType<PyLong>(args.Items[1]);
                Assert.IsType<PyLong>(args.Items[2]);
                Assert.IsType<PyLong>(args.Items[3]);
                Assert.True(args.Items[4] is PyInt or PyNone);
                Assert.True(args.Items[5] is PyStr or PyBytes);
                _ = WireText(args.Items[5]);
                Assert.IsType<PyInt>(args.Items[6]);
                Assert.True(args.Items[7] is PyInt or PyNone);
                break;
            case "OnRemoveCalendarEvent":
                Assert.Equal(3, args.Items.Count);
                Assert.IsType<PyLong>(args.Items[0]);
                Assert.IsType<PyLong>(args.Items[1]);
                Assert.IsType<PyBool>(args.Items[2]);
                break;
            case "OnEventResponseByExternal":
                Assert.Equal(3, args.Items.Count);
                Assert.IsType<PyLong>(args.Items[0]);
                Assert.IsType<PyObject>(args.Items[1]);
                Assert.IsType<PyLong>(WireField(args.Items[1], "ownerID"));
                Assert.IsType<PyLong>(WireField(args.Items[1], "flag"));
                Assert.IsType<PyLong>(WireField(args.Items[1], "eventDateTime"));
                Assert.IsType<PyInt>(args.Items[2]);
                break;
            default:
                throw new Xunit.Sdk.XunitException($"Unexpected calendar notification {name}");
        }
    }

    private async Task AssertCalendarErrorAsync(
        TestFront front, SessionOpen open, long callId, string service, string method,
        string expectedNotify, params PyValue[] args)
    {
        var send = await SendCalendarCallAsync(front, open, callId, service, method, args);
        var packet = Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(send.Payload)));
        Assert.Equal(MachoMessage.ErrorResponse, packet.Type);
        var error = Encoding.ASCII.GetString(send.Payload.Span);
        Assert.Contains("CustomNotify", error);
        Assert.Contains(expectedNotify, error);
    }

    private async Task AssertCalendarUserErrorAsync(
        TestFront front, SessionOpen open, long callId, string service, string method,
        string expectedError, params PyValue[] args)
    {
        var send = await SendCalendarCallAsync(front, open, callId, service, method, args);
        var packet = Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(send.Payload)));
        Assert.Equal(MachoMessage.ErrorResponse, packet.Type);
        Assert.Contains(expectedError, Encoding.ASCII.GetString(send.Payload.Span));
    }

    private async Task<Send> SendCalendarCallAsync(
        TestFront front, SessionOpen open, long callId, string service, string method, PyValue[] args)
    {
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.CallWithKwargs(open, callId, service, method, new PyTuple(args), TransportKwargs()))));
        return Assert.IsType<Send>(await front.ReceiveAsync());
    }

    private sealed class WireCalendarAudience(params long[] memberIds) : ICalendarAudienceReader
    {
        public Task<IReadOnlyList<long>> GetMemberCharacterIdsAsync(
            short eventTag, long ownerId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<long>>(memberIds);
    }

    private sealed class WireCalendarStore : ICalendarStore
    {
        private readonly Dictionary<long, CalendarEvent> _events = [];
        private readonly Dictionary<(long EventId, long CharacterId), CalendarResponse> _responses = [];
        private long _nextEventId = 50_000;

        public Task<IReadOnlyList<CalendarEvent>> ListMonthAsync(
            long characterId, long corporationId, long allianceId, int month, int year,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CalendarEvent>>(_events.Values
                .Where(value =>
                {
                    var date = DateTime.FromFileTimeUtc(value.EventDateTime);
                    return date.Month == month && date.Year == year;
                })
                .OrderBy(value => value.EventId)
                .ToArray());

        public Task<CalendarEvent?> GetEventAsync(
            long eventId, long ownerId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_events.TryGetValue(eventId, out var value) && value.OwnerId == ownerId ? value : null);

        public Task<CalendarEvent?> GetEventByIdAsync(
            long eventId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_events.GetValueOrDefault(eventId));

        public Task<IReadOnlyList<CalendarResponse>> GetResponsesForCharacterAsync(
            long characterId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CalendarResponse>>(_responses.Values
                .Where(value => value.CharacterId == characterId)
                .OrderBy(value => value.EventId)
                .ToArray());

        public Task<IReadOnlyList<CalendarResponse>> GetResponsesToEventAsync(
            long eventId, long ownerId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CalendarResponse>>(
                _events.TryGetValue(eventId, out var value) && value.OwnerId == ownerId
                    ? _responses.Values.Where(row => row.EventId == eventId).OrderBy(row => row.CharacterId).ToArray()
                    : []);

        public Task<long> CreateEventAsync(
            CalendarEventInput input, IReadOnlyCollection<long> invitees,
            CancellationToken cancellationToken = default)
        {
            var eventId = ++_nextEventId;
            _events[eventId] = new CalendarEvent(
                eventId, input.OwnerId, input.CreatorId, input.Tag, input.EventDateTime,
                input.Duration, input.Title, input.Text, input.Importance,
                DateTime.UtcNow.ToFileTimeUtc(), false, null);
            _responses[(eventId, input.CreatorId)] =
                new CalendarResponse(eventId, input.CreatorId, CalendarResponseStates.Accepted);
            if (input.Tag == CalendarTags.Personal)
                foreach (var invitee in invitees)
                    _responses[(eventId, invitee)] =
                        new CalendarResponse(eventId, invitee, CalendarResponseStates.Undecided);
            return Task.FromResult(eventId);
        }

        public Task<bool> EditEventAsync(
            long eventId, long ownerId, long oldEventDateTime, CalendarEventInput input,
            CancellationToken cancellationToken = default)
        {
            if (!_events.TryGetValue(eventId, out var value) || value.OwnerId != ownerId ||
                value.IsDeleted || value.EventDateTime != oldEventDateTime)
                return Task.FromResult(false);
            _events[eventId] = value with
            {
                EventDateTime = input.EventDateTime,
                Duration = input.Duration,
                Title = input.Title,
                Text = input.Text,
                Importance = input.Importance,
                DateModified = DateTime.UtcNow.ToFileTimeUtc(),
            };
            if (oldEventDateTime != input.EventDateTime)
                foreach (var key in _responses.Keys.Where(key => key.EventId == eventId && key.CharacterId != input.CreatorId).ToArray())
                {
                    var response = _responses[key];
                    if (response.Status is CalendarResponseStates.Accepted or CalendarResponseStates.Maybe or CalendarResponseStates.Undecided)
                        _responses[key] = response with { Status = CalendarResponseStates.Undecided };
                }
            return Task.FromResult(true);
        }

        public Task<bool> DeleteEventAsync(
            long eventId, long ownerId, long dateModified, CancellationToken cancellationToken = default)
        {
            if (!_events.TryGetValue(eventId, out var value) || value.OwnerId != ownerId || value.IsDeleted)
                return Task.FromResult(false);
            _events[eventId] = value with { IsDeleted = true, DateModified = dateModified };
            return Task.FromResult(true);
        }

        public Task<bool> UpdateParticipantsAsync(
            long eventId, long ownerId, IReadOnlyCollection<long> add, IReadOnlyCollection<long> remove,
            CancellationToken cancellationToken = default)
        {
            if (!_events.TryGetValue(eventId, out var value) || value.OwnerId != ownerId)
                return Task.FromResult(false);
            foreach (var characterId in remove)
                _responses[(eventId, characterId)] =
                    new CalendarResponse(eventId, characterId, CalendarResponseStates.Uninvited);
            foreach (var characterId in add.Where(id => !remove.Contains(id)))
                _responses[(eventId, characterId)] =
                    new CalendarResponse(eventId, characterId, CalendarResponseStates.Undecided);
            return Task.FromResult(true);
        }

        public Task<bool> SendResponseAsync(
            long eventId, long ownerId, long characterId, short response,
            CancellationToken cancellationToken = default)
        {
            if (!_events.TryGetValue(eventId, out var value) || value.OwnerId != ownerId ||
                !_responses.TryGetValue((eventId, characterId), out var existing) ||
                existing.Status == CalendarResponseStates.Uninvited)
                return Task.FromResult(false);
            _responses[(eventId, characterId)] = existing with { Status = response };
            return Task.FromResult(true);
        }
    }
}

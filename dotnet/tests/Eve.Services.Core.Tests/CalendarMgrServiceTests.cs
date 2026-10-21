using Eve.Calendar;
using Eve.Marshal;
using Eve.Macho;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Services.Core.Tests;

public class CalendarMgrServiceTests
{
    private const long CharacterId = 90_000_001;
    private const long OtherCharacterId = 90_000_002;
    private const long CorporationId = 98_000_001;
    private const long AllianceId = 99_000_001;
    private const long ChatManagerRole = 36_028_797_018_963_968L;

    [Fact]
    public async Task Client_read_Rpcs_return_stored_events_details_and_responses()
    {
        var eventDate = DateTime.UtcNow.AddDays(2);
        var date = eventDate.ToFileTimeUtc();
        var own = Event(101, CharacterId, CalendarTags.Personal, date, "Personal");
        var invited = Event(102, 90_000_003, CalendarTags.Personal, date, "Invited");
        var corp = Event(103, CorporationId, CalendarTags.Corporation, date, "Corp");
        var alliance = Event(104, AllianceId, CalendarTags.Alliance, date, "Alliance");
        var (services, store) = CalendarServices(own, invited, corp, alliance);
        store.Responses.Add(new(invited.EventId, CharacterId, CalendarResponseStates.Maybe));
        store.Responses.Add(new(own.EventId, CharacterId, CalendarResponseStates.Accepted));

        var proxy = services.Lookup("calendarProxy")!;
        var eventLists = Assert.IsType<PyTuple>(await proxy.CallAsync(Request(
            "GetEventList", Session(), new PyInt(eventDate.Month), new PyInt(eventDate.Year))));
        Assert.Equal(3, eventLists.Items.Count);
        Assert.Equal(2, Assert.IsType<PyList>(eventLists.Items[0]).Items.Count);
        Assert.Single(Assert.IsType<PyList>(eventLists.Items[1]).Items);
        Assert.Single(Assert.IsType<PyList>(eventLists.Items[2]).Items);

        var details = await proxy.CallAsync(Request(
            "GetEventDetails", Session(), new PyLong(invited.EventId), new PyLong(invited.OwnerId)));
        Assert.Equal(MarshalCodec.Encode(Details(invited)), MarshalCodec.Encode(details));

        var manager = services.Lookup("calendarMgr")!;
        var characterResponses = Assert.IsType<PyList>(
            await manager.CallAsync(Request("GetResponsesForCharacter", Session())));
        Assert.Equal(2, characterResponses.Items.Count);
        var eventResponses = Assert.IsType<PyList>(await manager.CallAsync(Request(
            "GetResponsesToEvent", Session(), new PyLong(invited.EventId), new PyLong(invited.OwnerId))));
        Assert.Equal(MarshalCodec.Encode(ResponseRow(CharacterId, CalendarResponseStates.Maybe)),
            MarshalCodec.Encode(Assert.Single(eventResponses.Items)));

        var corporationResponses = Assert.IsType<PyList>(await manager.CallAsync(Request(
            "GetResponsesToEvent", Session(), new PyLong(corp.EventId), new PyLong(CorporationId))));
        Assert.Equal(2, corporationResponses.Items.Count);
        Assert.All(corporationResponses.Items, row =>
            Assert.Equal(CalendarResponseStates.Undecided, ReadStatus(row)));
    }

    [Fact]
    public async Task Personal_event_creation_notifies_online_invitees_after_commit_and_excludes_the_creator_session()
    {
        var creatorSink = new Sink();
        var inviteeSink = new Sink();
        var creator = Session();
        creator.Sink = creatorSink;
        var invitee = Session(OtherCharacterId);
        invitee.Sink = inviteeSink;
        var sessions = new CharacterSessions();
        sessions.Enter(CharacterId, creator);
        sessions.Enter(OtherCharacterId, invitee);
        var (services, store) = CalendarServicesWithSessions(sessions);
        var manager = services.Lookup("calendarMgr")!;
        var start = FutureFileTime();

        var created = Assert.IsType<PyLong>(await manager.CallAsync(Request(
            "CreatePersonalEvent", creator, new PyLong(start), new PyInt(60), new PyStr("Fleet form-up"),
            new PyStr("Rally at the undock"), new PyInt(0), new PyList([new PyInt(OtherCharacterId)]))));

        Assert.Single(store.Events);
        Assert.Empty(creatorSink.Packets);
        var packet = Assert.Single(inviteeSink.Packets);
        Assert.Equal(
            ClientNotification.Build(invitee.UserId ?? 0, invitee.NodeId, "OnNewCalendarEvent", "clientID",
            [new PyTuple([
                new PyLong(created.Value), new PyLong(CharacterId), new PyLong(start), new PyInt(60),
                new PyStr("Fleet form-up"), new PyInt(0), PyNone.Instance, PyBool.True,
            ])], 0),
            packet);
    }

    [Fact]
    public async Task Calendar_mutations_publish_the_reference_notifications_to_the_affected_live_characters()
    {
        var creatorSink = new Sink();
        var inviteeSink = new Sink();
        var addedSink = new Sink();
        var creator = Session(); creator.Sink = creatorSink;
        var invitee = Session(OtherCharacterId); invitee.Sink = inviteeSink;
        var added = Session(90_000_004); added.Sink = addedSink;
        var sessions = new CharacterSessions();
        sessions.Enter(CharacterId, creator);
        sessions.Enter(OtherCharacterId, invitee);
        sessions.Enter(90_000_004, added);
        var (services, _) = CalendarServicesWithSessions(sessions);
        var manager = services.Lookup("calendarMgr")!;
        var start = FutureFileTime();
        var created = Assert.IsType<PyLong>(await manager.CallAsync(Request(
            "CreatePersonalEvent", creator, new PyLong(start), new PyInt(60), new PyStr("Fleet"),
            new PyStr("Meet"), new PyInt(0), new PyList([new PyInt(OtherCharacterId)]))));
        var newStart = start + TimeSpan.FromDays(1).Ticks;

        await manager.CallAsync(Request("EditPersonalEvent", creator, new PyLong(created.Value), new PyLong(start),
            new PyLong(newStart), new PyInt(75), new PyStr("Fleet updated"), new PyStr("Meet elsewhere"), new PyInt(1)));
        await manager.CallAsync(Request("SendEventResponse", invitee, new PyLong(created.Value),
            new PyLong(CharacterId), new PyInt(CalendarResponseStates.Maybe)));
        await manager.CallAsync(Request("UpdateEventParticipants", creator, new PyLong(created.Value),
            new PyList([new PyInt(90_000_004)]), new PyList([new PyInt(OtherCharacterId)])));
        await manager.CallAsync(Request("DeleteEvent", creator, new PyLong(created.Value), new PyLong(CharacterId)));

        Assert.Equal(new[] { "OnEditCalendarEvent", "OnEventResponseByExternal", "OnRemoveCalendarEvent" },
            NotificationNames(creatorSink));
        Assert.Equal(new[] { "OnNewCalendarEvent", "OnEditCalendarEvent", "OnRemoveCalendarEvent" },
            NotificationNames(inviteeSink));
        Assert.Equal(new[] { "OnNewCalendarEvent", "OnRemoveCalendarEvent" }, NotificationNames(addedSink));
    }

    [Fact]
    public async Task Corporation_calendar_creation_notifies_online_members_including_the_creator()
    {
        var creatorSink = new Sink();
        var memberSink = new Sink();
        var creator = Session(); creator.Sink = creatorSink;
        var member = Session(OtherCharacterId); member.Sink = memberSink;
        var sessions = new CharacterSessions();
        sessions.Enter(CharacterId, creator);
        sessions.Enter(OtherCharacterId, member);
        var (services, _) = CalendarServicesWithSessions(sessions);
        var manager = services.Lookup("calendarMgr")!;

        await manager.CallAsync(Request("CreateCorporationEvent", creator, new PyLong(FutureFileTime()), new PyInt(60),
            new PyStr("Corp event"), new PyStr("Details"), new PyInt(0)));

        Assert.Equal(new[] { "OnNewCalendarEvent" }, NotificationNames(creatorSink));
        Assert.Equal(new[] { "OnNewCalendarEvent" }, NotificationNames(memberSink));
    }

    [Fact]
    public async Task Personal_event_create_edit_invite_response_and_delete_persist_their_effects()
    {
        var (services, store) = CalendarServices();
        var manager = services.Lookup("calendarMgr")!;
        var start = FutureFileTime();
        var created = Assert.IsType<PyLong>(await manager.CallAsync(Request(
            "CreatePersonalEvent", Session(),
            new PyLong(start), new PyInt(60), new PyStr("Fleet form-up"), new PyStr("Rally at the undock"),
            new PyInt(0), new PyList([new PyInt(OtherCharacterId)]))));
        var calendarEvent = Assert.Single(store.Events);
        Assert.Equal(created.Value, calendarEvent.EventId);
        Assert.Equal(CharacterId, calendarEvent.OwnerId);
        Assert.Equal(CalendarTags.Personal, calendarEvent.Tag);
        Assert.Equal(CalendarResponseStates.Accepted, ResponseStatus(store, created.Value, CharacterId));
        Assert.Equal(CalendarResponseStates.Undecided, ResponseStatus(store, created.Value, OtherCharacterId));

        var newStart = DateTime.UtcNow.AddDays(3).ToFileTimeUtc();
        Assert.IsType<PyNone>(await manager.CallAsync(Request(
            "EditPersonalEvent", Session(), new PyLong(created.Value), new PyLong(start), new PyLong(newStart),
            new PyInt(90), new PyStr("Updated fleet form-up"), new PyStr("New rally point"), new PyInt(1))));
        Assert.Equal("Updated fleet form-up", store.Events.Single().Title);
        Assert.Equal(CalendarResponseStates.Undecided, ResponseStatus(store, created.Value, OtherCharacterId));

        var overLimit = new PyList(Enumerable.Range(100_100, 50)
            .Select(id => (PyValue)new PyInt(id)).ToArray());
        var inviteLimit = await Assert.ThrowsAsync<MachoWrappedException>(() => manager.CallAsync(Request(
            "UpdateEventParticipants", Session(), new PyLong(created.Value), overLimit, new PyList([]))).AsTask());
        Assert.Equal("CustomInfo", inviteLimit.Error.UserErrorMessage);
        Assert.Equal(CalendarResponseStates.Undecided, ResponseStatus(store, created.Value, OtherCharacterId));

        var nextAttendee = 90_000_004L;
        Assert.IsType<PyNone>(await manager.CallAsync(Request(
            "UpdateEventParticipants", Session(), new PyLong(created.Value),
            new PyList([new PyLong(nextAttendee)]), new PyList([new PyInt(OtherCharacterId)]))));
        Assert.Equal(CalendarResponseStates.Uninvited, ResponseStatus(store, created.Value, OtherCharacterId));
        Assert.Equal(CalendarResponseStates.Undecided, ResponseStatus(store, created.Value, nextAttendee));

        Assert.IsType<PyNone>(await manager.CallAsync(Request(
            "SendEventResponse", Session(nextAttendee), new PyLong(created.Value),
            new PyLong(CharacterId), new PyInt(CalendarResponseStates.Accepted))));
        Assert.Equal(CalendarResponseStates.Accepted, ResponseStatus(store, created.Value, nextAttendee));

        Assert.IsType<PyNone>(await manager.CallAsync(Request(
            "DeleteEvent", Session(), new PyLong(created.Value), new PyLong(CharacterId))));
        Assert.True(store.Events.Single().IsDeleted);
        Assert.All(store.Responses.Where(row => row.EventId == created.Value),
            row => Assert.Equal(CalendarResponseStates.Deleted, row.Status));
    }

    [Fact]
    public async Task Corporation_and_alliance_create_Rpcs_use_the_session_owner_and_manager_role()
    {
        var (services, store) = CalendarServices();
        var manager = services.Lookup("calendarMgr")!;
        var date = FutureFileTime();
        var corporation = Assert.IsType<PyLong>(await manager.CallAsync(Request(
            "CreateCorporationEvent", Session(role: ChatManagerRole), new PyLong(date), new PyInt(30),
            new PyStr("Corp meeting"), new PyStr("Agenda"), new PyInt(0))));
        var alliance = Assert.IsType<PyLong>(await manager.CallAsync(Request(
            "CreateAllianceEvent", Session(role: ChatManagerRole), new PyLong(date), new PyInt(30),
            new PyStr("Alliance meeting"), new PyStr("Agenda"), new PyInt(1))));

        Assert.Equal(CorporationId, store.Events.Single(row => row.EventId == corporation.Value).OwnerId);
        Assert.Equal(AllianceId, store.Events.Single(row => row.EventId == alliance.Value).OwnerId);
        Assert.Equal(CalendarResponseStates.Accepted, ResponseStatus(store, corporation.Value, CharacterId));
        Assert.Equal(CalendarResponseStates.Accepted, ResponseStatus(store, alliance.Value, CharacterId));

        var corpNewDate = DateTime.UtcNow.AddDays(4).ToFileTimeUtc();
        Assert.IsType<PyNone>(await manager.CallAsync(Request(
            "EditCorporationEvent", Session(role: ChatManagerRole), new PyLong(corporation.Value),
            new PyLong(date), new PyLong(corpNewDate), new PyInt(45), new PyStr("Updated corp meeting"),
            new PyStr("Revised"), new PyInt(1))));
        Assert.Equal("Updated corp meeting", store.Events.Single(row => row.EventId == corporation.Value).Title);

        var allianceNewDate = DateTime.UtcNow.AddDays(5).ToFileTimeUtc();
        Assert.IsType<PyNone>(await manager.CallAsync(Request(
            "EditAllianceEvent", Session(role: ChatManagerRole), new PyLong(alliance.Value),
            new PyLong(date), new PyLong(allianceNewDate), new PyInt(45), new PyStr("Updated alliance meeting"),
            new PyStr("Revised"), new PyInt(0))));
        Assert.Equal("Updated alliance meeting", store.Events.Single(row => row.EventId == alliance.Value).Title);

        Assert.IsType<PyNone>(await manager.CallAsync(Request(
            "SendEventResponse", Session(OtherCharacterId), new PyLong(corporation.Value),
            new PyLong(CorporationId), new PyInt(CalendarResponseStates.Accepted))));
        Assert.Equal(CalendarResponseStates.Accepted, ResponseStatus(store, corporation.Value, OtherCharacterId));

        var error = await Assert.ThrowsAsync<MachoWrappedException>(() => manager.CallAsync(Request(
            "CreateCorporationEvent", Session(role: 0), new PyLong(date), new PyInt(30),
            new PyStr("Denied"), new PyStr(""), new PyInt(0))).AsTask());
        Assert.Equal("CustomNotify", error.Error.UserErrorMessage);
    }

    [Fact]
    public async Task Calendar_validates_client_title_limits_and_reports_unknown_events()
    {
        var (services, _) = CalendarServices();
        var manager = services.Lookup("calendarMgr")!;
        var proxy = services.Lookup("calendarProxy")!;
        var error = await Assert.ThrowsAsync<MachoWrappedException>(() => manager.CallAsync(Request(
            "CreatePersonalEvent", Session(), new PyLong(FutureFileTime()), new PyInt(60),
            new PyStr(new string('x', 41)), new PyStr(""), new PyInt(0), new PyList([]))).AsTask());
        Assert.Equal("CalendarTitleTooLong", error.Error.UserErrorMessage);

        var tooManyInvitees = new PyList(Enumerable.Range(100_000, 51)
            .Select(id => (PyValue)new PyInt(id)).ToArray());
        var inviteLimit = await Assert.ThrowsAsync<MachoWrappedException>(() => manager.CallAsync(Request(
            "CreatePersonalEvent", Session(), new PyLong(FutureFileTime()), new PyInt(60),
            new PyStr("Fleet"), new PyStr(""), new PyInt(0), tooManyInvitees)).AsTask());
        Assert.Equal("CustomInfo", inviteLimit.Error.UserErrorMessage);

        var missing = await Assert.ThrowsAsync<MachoWrappedException>(() => proxy.CallAsync(Request(
            "GetEventDetails", Session(), new PyLong(123), new PyLong(456))).AsTask());
        Assert.Equal("CustomNotify", missing.Error.UserErrorMessage);
    }

    [Fact]
    public void All_client_calendar_Rpcs_are_registered_under_their_bound_services()
    {
        var (services, _) = CalendarServices();
        var manager = services.Lookup("calendarMgr")!;
        var proxy = services.Lookup("calendarProxy")!;
        string[] managerMethods =
        [
            "GetResponsesForCharacter", "GetResponsesToEvent",
            "CreatePersonalEvent", "CreateCorporationEvent", "CreateAllianceEvent",
            "EditPersonalEvent", "EditCorporationEvent", "EditAllianceEvent",
            "DeleteEvent", "UpdateEventParticipants", "SendEventResponse",
        ];
        foreach (var method in managerMethods) Assert.True(manager.HasMethod(method), method);
        Assert.True(proxy.HasMethod("GetEventList"));
        Assert.True(proxy.HasMethod("GetEventDetails"));
        Assert.False(manager.HasMethod("GetEventList"));
        Assert.False(proxy.HasMethod("SendEventResponse"));
    }

    [Fact]
    public void Native_calendar_migration_is_embedded_in_the_domain_project()
    {
        var migration = Assert.Single(CalendarStore.MigrationSet());
        Assert.Equal("0001_calendar", migration.Name);
        Assert.Contains("CREATE TABLE events", migration.Sql, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE responses", migration.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Group_calendar_mutations_refuse_a_revoked_persisted_manager_role()
    {
        var date = FutureFileTime();
        var existing = Event(77, CorporationId, CalendarTags.Corporation, date, "Corp meeting");
        var (services, store) = CalendarServicesWithSessions(null, false, existing);
        var manager = services.Lookup("calendarMgr")!;
        var error = await Assert.ThrowsAsync<MachoWrappedException>(() => manager.CallAsync(Request(
            "DeleteEvent", Session(role: ChatManagerRole), new PyLong(existing.EventId),
            new PyLong(existing.OwnerId))).AsTask());

        Assert.Equal("CustomNotify", error.Error.UserErrorMessage);
        Assert.False(store.Events.Single(row => row.EventId == existing.EventId).IsDeleted);
        var detailError = await Assert.ThrowsAsync<MachoWrappedException>(() => services.Lookup("calendarProxy")!
            .CallAsync(Request("GetEventDetails", Session(role: ChatManagerRole), new PyLong(existing.EventId),
                new PyLong(existing.OwnerId))).AsTask());
        Assert.Equal("CustomNotify", detailError.Error.UserErrorMessage);
        var responseError = await Assert.ThrowsAsync<MachoWrappedException>(() => manager.CallAsync(Request(
            "GetResponsesToEvent", Session(role: ChatManagerRole), new PyLong(existing.EventId),
            new PyLong(existing.OwnerId))).AsTask());
        Assert.Equal("CustomNotify", responseError.Error.UserErrorMessage);

        var createError = await Assert.ThrowsAsync<MachoWrappedException>(() => manager.CallAsync(Request(
            "CreateAllianceEvent", Session(role: ChatManagerRole), new PyLong(date), new PyInt(30),
            new PyStr("Denied alliance meeting"), new PyStr(""), new PyInt(0))).AsTask());
        Assert.Equal("CustomNotify", createError.Error.UserErrorMessage);
        Assert.Single(store.Events);
    }

    private static (ServiceRegistry Services, FakeCalendarStore Store) CalendarServices(params CalendarEvent[] events) =>
        CalendarServicesWithSessions(null, events: events);

    private static (ServiceRegistry Services, FakeCalendarStore Store) CalendarServicesWithSessions(
        CharacterSessions? sessions, bool groupAuthority = true, params CalendarEvent[] events)
    {
        var store = new FakeCalendarStore();
        store.Events.AddRange(events);
        var services = new ServiceRegistry();
        services.Register((IService)new CalendarMgrService(store, new FakeCalendarAudienceReader(), sessions,
            (_, _, _, _) => ValueTask.FromResult(groupAuthority),
            (_, _) => ValueTask.FromResult(groupAuthority ? (CorporationId, AllianceId) : (0L, 0L))));
        services.Register((IService)new CalendarProxyService(store,
            (_, _) => ValueTask.FromResult(groupAuthority ? (CorporationId, AllianceId) : (0L, 0L))));
        return (services, store);
    }

    private static ServiceCall Request(string method, ClientSession session, params PyValue[] args) => new()
    {
        Method = method,
        Session = session,
        Args = args,
    };

    private static ClientSession Session(long characterId = CharacterId, long corporationId = CorporationId,
        long allianceId = AllianceId, long role = ChatManagerRole)
    {
        var session = new ClientSession(new Sink()) { CharacterId = characterId };
        session.Set("corpid", new PyInt(corporationId));
        session.Set("allianceid", new PyInt(allianceId));
        session.Set("corprole", new PyLong(role));
        return session;
    }

    private static string[] NotificationNames(Sink sink) => sink.Packets.OfType<PyObject>().Select(packet =>
    {
        var fields = Assert.IsType<PyTuple>(packet.Args).Items;
        Assert.Equal(MachoMessage.Notification, Assert.IsType<PyInt>(fields[0]).Value);
        var address = Assert.IsType<PyTuple>(Assert.IsType<PyObject>(fields[2]).Args).Items;
        return Assert.IsType<PyStr>(address[1]).Value;
    }).ToArray();

    private static long FutureFileTime() => DateTime.UtcNow.AddDays(2).ToFileTimeUtc();

    private static CalendarEvent Event(long id, long owner, short tag, long dateTime, string title) =>
        new(id, owner, owner, tag, dateTime, 60, title, $"{title} details", 0,
            DateTime.UtcNow.ToFileTimeUtc(), false, null);

    private static PyDict Details(CalendarEvent value) => new(
    [
        Pair("eventID", new PyLong(value.EventId)),
        Pair("ownerID", new PyLong(value.OwnerId)),
        Pair("creatorID", new PyLong(value.CreatorId)),
        Pair("eventText", new PyStr(value.Text)),
    ]);

    private static PyDict ResponseRow(long characterId, short status) => new(
    [
        Pair("characterID", new PyLong(characterId)),
        Pair("status", new PyInt(status)),
    ]);

    private static KeyValuePair<PyValue, PyValue> Pair(string key, PyValue value) =>
        new(new PyStr(key), value);

    private static short ResponseStatus(FakeCalendarStore store, long eventId, long characterId) =>
        store.Responses.Single(row => row.EventId == eventId && row.CharacterId == characterId).Status;

    private static short ReadStatus(PyValue value)
    {
        var row = Assert.IsType<PyDict>(value);
        var status = Assert.IsType<PyInt>(row.Entries.Single(pair =>
            Assert.IsType<PyStr>(pair.Key).Value == "status").Value);
        return checked((short)status.Value);
    }

    private sealed class FakeCalendarAudienceReader : ICalendarAudienceReader
    {
        public Task<IReadOnlyList<long>> GetMemberCharacterIdsAsync(
            short eventTag, long ownerId, CancellationToken cancellationToken = default) =>
            eventTag switch
            {
                CalendarTags.Corporation when ownerId == CorporationId =>
                    Task.FromResult<IReadOnlyList<long>>([CharacterId, OtherCharacterId]),
                CalendarTags.Alliance when ownerId == AllianceId =>
                    Task.FromResult<IReadOnlyList<long>>([CharacterId, OtherCharacterId]),
                _ => Task.FromResult<IReadOnlyList<long>>([]),
            };
    }

    private sealed class FakeCalendarStore : ICalendarStore
    {
        private long _nextEventId = 500;
        public List<CalendarEvent> Events { get; } = [];
        public List<CalendarResponse> Responses { get; } = [];

        public Task<IReadOnlyList<CalendarEvent>> ListMonthAsync(
            long characterId, long corporationId, long allianceId, int month, int year, CancellationToken cancellationToken = default)
        {
            var start = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();
            var end = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1).ToFileTimeUtc();
            IReadOnlyList<CalendarEvent> rows = Events.Where(row =>
                    row.EventDateTime >= start && row.EventDateTime < end &&
                    (row.OwnerId == characterId || row.OwnerId == corporationId || row.OwnerId == allianceId ||
                     row.Tag is CalendarTags.Ccp or CalendarTags.Automated ||
                     Responses.Any(response => response.EventId == row.EventId &&
                         response.CharacterId == characterId && response.Status != CalendarResponseStates.Uninvited)))
                .OrderBy(row => row.EventDateTime).ThenBy(row => row.EventId).ToArray();
            return Task.FromResult(rows);
        }

        public Task<CalendarEvent?> GetEventAsync(long eventId, long ownerId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Events.FirstOrDefault(row => row.EventId == eventId && row.OwnerId == ownerId));

        public Task<CalendarEvent?> GetEventByIdAsync(long eventId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Events.FirstOrDefault(row => row.EventId == eventId));

        public Task<IReadOnlyList<CalendarResponse>> GetResponsesForCharacterAsync(long characterId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CalendarResponse>>(Responses.Where(row => row.CharacterId == characterId).ToArray());

        public Task<IReadOnlyList<CalendarResponse>> GetResponsesToEventAsync(
            long eventId, long ownerId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CalendarResponse>>(Events.Any(row => row.EventId == eventId && row.OwnerId == ownerId)
                ? Responses.Where(row => row.EventId == eventId).OrderBy(row => row.CharacterId).ToArray()
                : []);

        public Task<long> CreateEventAsync(
            CalendarEventInput input, IReadOnlyCollection<long> invitees, CancellationToken cancellationToken = default)
        {
            var id = _nextEventId++;
            Events.Add(new(id, input.OwnerId, input.CreatorId, input.Tag, input.EventDateTime, input.Duration,
                input.Title, input.Text, input.Importance, DateTime.UtcNow.ToFileTimeUtc(), false, null));
            Upsert(id, input.CreatorId, CalendarResponseStates.Accepted);
            if (input.Tag == CalendarTags.Personal)
                foreach (var invitee in invitees.Where(id => id > 0 && id != input.CreatorId).Distinct())
                    Upsert(id, invitee, CalendarResponseStates.Undecided);
            return Task.FromResult(id);
        }

        public Task<bool> EditEventAsync(
            long eventId, long ownerId, long oldEventDateTime, CalendarEventInput input, CancellationToken cancellationToken = default)
        {
            var index = Events.FindIndex(row => row.EventId == eventId && row.OwnerId == ownerId &&
                row.EventDateTime == oldEventDateTime && !row.IsDeleted);
            if (index < 0) return Task.FromResult(false);
            var existing = Events[index];
            Events[index] = existing with
            {
                EventDateTime = input.EventDateTime,
                Duration = input.Duration,
                Title = input.Title,
                Text = input.Text,
                Importance = input.Importance,
                DateModified = DateTime.UtcNow.ToFileTimeUtc(),
            };
            if (oldEventDateTime != input.EventDateTime)
                for (var i = 0; i < Responses.Count; i++)
                    if (Responses[i].EventId == eventId && Responses[i].CharacterId != existing.CreatorId &&
                        Responses[i].Status is CalendarResponseStates.Undecided or CalendarResponseStates.Accepted or CalendarResponseStates.Maybe)
                        Responses[i] = Responses[i] with { Status = CalendarResponseStates.Undecided };
            return Task.FromResult(true);
        }

        public Task<bool> DeleteEventAsync(long eventId, long ownerId, long dateModified, CancellationToken cancellationToken = default)
        {
            var index = Events.FindIndex(row => row.EventId == eventId && row.OwnerId == ownerId && !row.IsDeleted);
            if (index < 0) return Task.FromResult(false);
            Events[index] = Events[index] with { IsDeleted = true, DateModified = dateModified };
            for (var i = 0; i < Responses.Count; i++)
                if (Responses[i].EventId == eventId)
                    Responses[i] = Responses[i] with { Status = CalendarResponseStates.Deleted };
            return Task.FromResult(true);
        }

        public Task<bool> UpdateParticipantsAsync(
            long eventId, long ownerId, IReadOnlyCollection<long> add, IReadOnlyCollection<long> remove, CancellationToken cancellationToken = default)
        {
            if (!Events.Any(row => row.EventId == eventId && row.OwnerId == ownerId && !row.IsDeleted))
                return Task.FromResult(false);
            foreach (var id in add.Where(id => id > 0).Distinct()) Upsert(eventId, id, CalendarResponseStates.Undecided);
            foreach (var id in remove.Where(id => id > 0))
                for (var i = 0; i < Responses.Count; i++)
                    if (Responses[i].EventId == eventId && Responses[i].CharacterId == id)
                        Responses[i] = Responses[i] with { Status = CalendarResponseStates.Uninvited };
            return Task.FromResult(true);
        }

        public Task<bool> SendResponseAsync(
            long eventId, long ownerId, long characterId, short response, CancellationToken cancellationToken = default)
        {
            if (!Events.Any(row => row.EventId == eventId && row.OwnerId == ownerId && !row.IsDeleted))
                return Task.FromResult(false);
            Upsert(eventId, characterId, response);
            return Task.FromResult(true);
        }

        private void Upsert(long eventId, long characterId, short status)
        {
            var index = Responses.FindIndex(row => row.EventId == eventId && row.CharacterId == characterId);
            var response = new CalendarResponse(eventId, characterId, status);
            if (index < 0) Responses.Add(response);
            else Responses[index] = response;
        }
    }

    private sealed class Sink : IPacketSink
    {
        public List<PyValue> Packets { get; } = [];
        public void Send(PyValue packet) => Packets.Add(packet);
    }
}

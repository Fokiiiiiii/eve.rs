using Eve.Calendar;
using Eve.Marshal;

namespace Eve.Services.Core;


// server/src/services/calendar/{calendarMgrService,calendarProxyService,calendarPayloads,calendarRuntimeState}.js.
// Client callers: eve-logic, commit 1dbb5711a072a146e1d228365668ac05655eefe1,
// eve/client/script/ui/services/eveCalendarsvc.py: GetEventResponses, GetResponsesToEvent,
// GetEventsByMonthYear and GetEventDetails.
// Personal/corporation/alliance entries and responses:
// https://www.eveonline.com/news/view/introducing-eve-mobile-portal


/// <summary>Persisted calendar RPCs and their post-commit client notifications.</summary>
/// <remarks>Recurring global-event seeding remains a separate provider concern.</remarks>
[Service("calendarMgr")]
public sealed partial class CalendarMgrService
{
    private readonly ICalendarStore? _store;
    private readonly ICalendarAudienceReader? _audience;
    private readonly CharacterSessions? _sessions;
    private readonly Func<long, long, short, CancellationToken, ValueTask<bool>>? _canManageGroupEvent;

    public CalendarMgrService() { }
    public CalendarMgrService(
        ICalendarStore store,
        ICalendarAudienceReader? audience = null,
        CharacterSessions? sessions = null,
        Func<long, long, short, CancellationToken, ValueTask<bool>>? canManageGroupEvent = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _audience = audience;
        _sessions = sessions;
        _canManageGroupEvent = canManageGroupEvent;
    }
    private ICalendarStore Store => _store ?? throw new InvalidOperationException("Host must inject the PostgreSQL calendar store.");

    [Method("GetResponsesForCharacter")]
    public async Task<PyValue> GetResponsesForCharacter(ServiceCall call)
    {
        var characterId = CalendarWire.CharacterId(call);
        var rows = await Store.GetResponsesForCharacterAsync(characterId, call.Cancellation);
        return new PyList(rows.Select(CalendarWire.ResponseForCharacter).ToArray());
    }

    [Method("GetResponsesToEvent")]
    public async Task<PyValue> GetResponsesToEvent(ServiceCall call)
    {
        var eventId = CalendarWire.LongArg(call, 0);
        var ownerId = CalendarWire.LongArg(call, 1);
        var calendarEvent = await Store.GetEventAsync(eventId, ownerId, call.Cancellation);
        if (calendarEvent is null || !await CalendarWire.CanViewAsync(Store, calendarEvent, call)) throw EventNotFound();
        var rows = await Store.GetResponsesToEventAsync(eventId, ownerId, call.Cancellation);
        if (_audience is not null && calendarEvent.Tag is CalendarTags.Corporation or CalendarTags.Alliance)
        {
            var memberIds = await _audience.GetMemberCharacterIdsAsync(
                calendarEvent.Tag, calendarEvent.OwnerId, call.Cancellation);
            rows = CalendarWire.IncludeGroupMembers(eventId, rows, memberIds);
        }
        return new PyList(rows.Select(CalendarWire.ResponseForEvent).ToArray());
    }

    [Method("CreatePersonalEvent")]
    public Task<PyValue> CreatePersonalEvent(ServiceCall call) => CreateEventAsync(call, CalendarTags.Personal, true);

    [Method("CreateCorporationEvent")]
    public Task<PyValue> CreateCorporationEvent(ServiceCall call) => CreateEventAsync(call, CalendarTags.Corporation, false);

    [Method("CreateAllianceEvent")]
    public Task<PyValue> CreateAllianceEvent(ServiceCall call) => CreateEventAsync(call, CalendarTags.Alliance, false);

    private async Task<PyValue> CreateEventAsync(ServiceCall call, short tag, bool personal)
    {
        var characterId = CalendarWire.CharacterId(call);
        var ownerId = CalendarWire.OwnerIdForCreate(call, tag);
        if (!personal && (_canManageGroupEvent is null ||
            !await _canManageGroupEvent(characterId, ownerId, tag, call.Cancellation)))
            throw EventNotFound();
        var dateTimeIndex = 0;
        var durationIndex = 1;
        var titleIndex = 2;
        var textIndex = 3;
        var importanceIndex = 4;
        IReadOnlyList<long> invitees = personal
            ? CalendarWire.CharacterIds(call.Arg(5)).Where(id => id != characterId).ToArray()
            : Array.Empty<long>();
        var input = CalendarWire.ReadInput(call, dateTimeIndex, durationIndex, titleIndex, textIndex, importanceIndex, ownerId, characterId, tag);
        CalendarWire.Validate(input, invitees.Count);
        var eventId = await Store.CreateEventAsync(input, invitees, call.Cancellation);
        var created = await Store.GetEventByIdAsync(eventId, call.Cancellation);
        if (created is not null)
            await NotifyNewAsync(created, call, excludeCallingSession: personal);
        return new PyLong(eventId);
    }

    [Method("EditPersonalEvent")]
    public Task<PyValue> EditPersonalEvent(ServiceCall call) => EditEventAsync(call, CalendarTags.Personal);

    [Method("EditCorporationEvent")]
    public Task<PyValue> EditCorporationEvent(ServiceCall call) => EditEventAsync(call, CalendarTags.Corporation);

    [Method("EditAllianceEvent")]
    public Task<PyValue> EditAllianceEvent(ServiceCall call) => EditEventAsync(call, CalendarTags.Alliance);

    private async Task<PyValue> EditEventAsync(ServiceCall call, short expectedTag)
    {
        var eventId = CalendarWire.LongArg(call, 0);
        var oldEventDateTime = CalendarWire.LongArg(call, 1);
        var calendarEvent = await Store.GetEventByIdAsync(eventId, call.Cancellation);
        if (calendarEvent is null || calendarEvent.Tag != expectedTag || !await CanManageAsync(calendarEvent, call))
            throw EventNotFound();

        var input = CalendarWire.ReadInput(
            call, 2, 3, 4, 5, 6, calendarEvent.OwnerId, calendarEvent.CreatorId, expectedTag);
        CalendarWire.Validate(input, 0);
        if (!await Store.EditEventAsync(eventId, calendarEvent.OwnerId, oldEventDateTime, input, call.Cancellation))
            throw EventNotFound();
        var updated = await Store.GetEventAsync(eventId, calendarEvent.OwnerId, call.Cancellation);
        if (updated is not null)
            await NotifyEditAsync(updated, oldEventDateTime, call);
        return PyNone.Instance;
    }

    [Method("DeleteEvent")]
    public async Task<PyValue> DeleteEvent(ServiceCall call)
    {
        var eventId = CalendarWire.LongArg(call, 0);
        var ownerId = CalendarWire.LongArg(call, 1);
        var calendarEvent = await Store.GetEventAsync(eventId, ownerId, call.Cancellation);
        if (calendarEvent is null || !await CanManageAsync(calendarEvent, call)) throw EventNotFound();
        var recipients = await ListViewerCharacterIdsAsync(calendarEvent, call.Cancellation);
        var modified = CalendarWire.NowFileTime();
        if (!await Store.DeleteEventAsync(eventId, ownerId, modified, call.Cancellation))
            throw EventNotFound();
        await NotifyRemoveAsync(calendarEvent with { IsDeleted = true, DateModified = modified }, true, call, recipients);
        return PyNone.Instance;
    }

    [Method("UpdateEventParticipants")]
    public async Task<PyValue> UpdateEventParticipants(ServiceCall call)
    {
        var eventId = CalendarWire.LongArg(call, 0);
        var add = CalendarWire.CharacterIds(call.Arg(1));
        var remove = CalendarWire.CharacterIds(call.Arg(2));
        var calendarEvent = await Store.GetEventByIdAsync(eventId, call.Cancellation);
        if (calendarEvent is null || !await CanManageAsync(calendarEvent, call)) throw EventNotFound();
        add = add.Where(id => id != calendarEvent.CreatorId).ToArray();
        remove = remove.Where(id => id != calendarEvent.CreatorId).ToArray();
        if (add.Count > 50 || remove.Count > 50) throw CalendarWire.TooManyInvitees();

        if (calendarEvent.Tag == CalendarTags.Personal)
        {
            var current = await Store.GetResponsesToEventAsync(eventId, calendarEvent.OwnerId, call.Cancellation);
            var active = new HashSet<long>(current
                .Where(row => row.Status != CalendarResponseStates.Uninvited && row.CharacterId != calendarEvent.CreatorId)
                .Select(row => row.CharacterId));
            active.ExceptWith(remove);
            active.UnionWith(add.Where(id => !remove.Contains(id)));
            if (active.Count > 50) throw CalendarWire.TooManyInvitees();
        }

        var before = await Store.GetResponsesToEventAsync(eventId, calendarEvent.OwnerId, call.Cancellation);
        if (!await Store.UpdateParticipantsAsync(eventId, calendarEvent.OwnerId, add, remove, call.Cancellation))
            throw EventNotFound();
        var after = await Store.GetResponsesToEventAsync(eventId, calendarEvent.OwnerId, call.Cancellation);
        var beforeVisible = before.Where(row => row.Status != CalendarResponseStates.Uninvited)
            .Select(row => row.CharacterId).ToHashSet();
        var afterVisible = after.Where(row => row.Status != CalendarResponseStates.Uninvited)
            .Select(row => row.CharacterId).ToHashSet();
        var added = afterVisible.Except(beforeVisible).ToArray();
        var removed = beforeVisible.Except(afterVisible).ToArray();
        var updated = await Store.GetEventByIdAsync(eventId, call.Cancellation);
        if (updated is not null)
        {
            await NotifyNewAsync(updated, call, characterIds: added);
            await NotifyRemoveAsync(updated, false, call, removed);
        }
        return PyNone.Instance;
    }

    [Method("SendEventResponse")]
    public async Task<PyValue> SendEventResponse(ServiceCall call)
    {
        var eventId = CalendarWire.LongArg(call, 0);
        var ownerId = CalendarWire.LongArg(call, 1);
        var response = checked((short)CalendarWire.IntArg(call, 2));
        var characterId = CalendarWire.CharacterId(call);
        var calendarEvent = await Store.GetEventAsync(eventId, ownerId, call.Cancellation);
        if (calendarEvent is null || calendarEvent.IsDeleted ||
            response is < CalendarResponseStates.Declined or > CalendarResponseStates.Maybe ||
            !await CalendarWire.CanRespondAsync(Store, calendarEvent, call, characterId))
            throw EventNotFound();
        if (!await Store.SendResponseAsync(eventId, ownerId, characterId, response, call.Cancellation))
            throw EventNotFound();
        await NotifyResponseAsync(calendarEvent, response, call);
        return PyNone.Instance;
    }

    private async Task NotifyNewAsync(
        CalendarEvent calendarEvent, ServiceCall call, bool excludeCallingSession = false, IReadOnlyCollection<long>? characterIds = null)
    {
        var recipients = characterIds ?? await ListViewerCharacterIdsAsync(calendarEvent, call.Cancellation);
        var payload = new PyTuple([
            new PyLong(calendarEvent.EventId), new PyLong(calendarEvent.OwnerId), new PyLong(calendarEvent.EventDateTime),
            calendarEvent.Duration is { } duration ? new PyInt(duration) : PyNone.Instance,
            new PyStr(calendarEvent.Title), new PyInt(calendarEvent.Importance == 0 ? 0 : 1),
            calendarEvent.AutoEventType is { } autoEventType ? new PyInt(autoEventType) : PyNone.Instance,
            PyBool.True,
        ]);
        SendToCharacters(recipients, "OnNewCalendarEvent", payload,
            excludeCallingSession ? call.Session : null);
    }

    private async Task NotifyEditAsync(CalendarEvent calendarEvent, long oldEventDateTime, ServiceCall call)
    {
        var payload = new PyTuple([
            new PyLong(calendarEvent.EventId), new PyLong(calendarEvent.OwnerId), new PyLong(oldEventDateTime),
            new PyLong(calendarEvent.EventDateTime), calendarEvent.Duration is { } duration ? new PyInt(duration) : PyNone.Instance,
            new PyStr(calendarEvent.Title), new PyInt(calendarEvent.Importance == 0 ? 0 : 1),
            calendarEvent.AutoEventType is { } autoEventType ? new PyInt(autoEventType) : PyNone.Instance,
        ]);
        SendToCharacters(await ListViewerCharacterIdsAsync(calendarEvent, call.Cancellation), "OnEditCalendarEvent", payload);
    }

    private async Task NotifyRemoveAsync(
        CalendarEvent calendarEvent, bool isDeleted, ServiceCall call, IReadOnlyCollection<long>? characterIds = null)
    {
        var payload = new PyTuple([
            new PyLong(calendarEvent.EventId), new PyLong(calendarEvent.EventDateTime),
            isDeleted ? PyBool.True : PyBool.False,
        ]);
        SendToCharacters(characterIds ?? await ListViewerCharacterIdsAsync(calendarEvent, call.Cancellation),
            "OnRemoveCalendarEvent", payload);
    }

    private async Task NotifyResponseAsync(CalendarEvent calendarEvent, int response, ServiceCall call)
    {
        var eventRow = Py.KeyVal(
            ("ownerID", new PyLong(calendarEvent.OwnerId)),
            ("flag", new PyLong(calendarEvent.OwnerId)),
            ("eventDateTime", new PyLong(calendarEvent.EventDateTime)));
        var payload = new PyTuple([new PyLong(calendarEvent.EventId), eventRow, new PyInt(response)]);
        SendToCharacters(await ListViewerCharacterIdsAsync(calendarEvent, call.Cancellation),
            "OnEventResponseByExternal", payload, call.Session);
    }

    private async Task<IReadOnlyCollection<long>> ListViewerCharacterIdsAsync(
        CalendarEvent calendarEvent, CancellationToken cancellationToken)
    {
        if (calendarEvent.Tag == CalendarTags.Personal)
        {
            var responses = await Store.GetResponsesToEventAsync(calendarEvent.EventId, calendarEvent.OwnerId, cancellationToken);
            return responses.Where(row => row.Status != CalendarResponseStates.Uninvited)
                .Select(row => row.CharacterId).Distinct().ToArray();
        }
        if (calendarEvent.Tag is CalendarTags.Corporation or CalendarTags.Alliance)
            return _audience is null
                ? Array.Empty<long>()
                : await _audience.GetMemberCharacterIdsAsync(calendarEvent.Tag, calendarEvent.OwnerId, cancellationToken);
        return _sessions?.All().Where(session => session.CharacterId is > 0)
            .Select(session => session.CharacterId!.Value).Distinct().ToArray() ?? Array.Empty<long>();
    }

    private void SendToCharacters(
        IEnumerable<long> characterIds, string notification, PyTuple payload, ClientSession? excludedSession = null)
    {
        if (_sessions is null) return;
        foreach (var characterId in characterIds.Where(id => id > 0).Distinct())
        {
            var session = _sessions.Of(characterId);
            if (session is not null && !ReferenceEquals(session, excludedSession))
                session.SendNotification(notification, "clientID", payload);
        }
    }

    private async Task<bool> CanManageAsync(CalendarEvent calendarEvent, ServiceCall call)
    {
        if (!CalendarWire.CanManage(calendarEvent, call)) return false;
        if (calendarEvent.Tag == CalendarTags.Personal) return true;
        var actorId = call.Session.CharacterId ?? 0;
        return actorId > 0 && _canManageGroupEvent is not null &&
            await _canManageGroupEvent(actorId, calendarEvent.OwnerId, calendarEvent.Tag, call.Cancellation);
    }

    internal static MachoWrappedException EventNotFound() =>
        MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("Calendar event not found.")));
}

/// <summary>Calendar month and event reads, addressed separately by the client's ProxySvc.</summary>
[Service("calendarProxy")]
public sealed partial class CalendarProxyService
{
    private readonly ICalendarStore? _store;

    public CalendarProxyService() { }
    public CalendarProxyService(ICalendarStore store) => _store = store ?? throw new ArgumentNullException(nameof(store));
    private ICalendarStore Store => _store ?? throw new InvalidOperationException("Host must inject the PostgreSQL calendar store.");

    [Method("GetEventList")]
    public async Task<PyValue> GetEventList(ServiceCall call)
    {
        var month = CalendarWire.IntArg(call, 0);
        var year = CalendarWire.IntArg(call, 1);
        var characterId = CalendarWire.CharacterId(call);
        var rows = await Store.ListMonthAsync(
            characterId, CalendarWire.SessionId(call, "corpid", "corporationID"),
            CalendarWire.SessionId(call, "allianceid", "allianceID"), month, year, call.Cancellation);
        var visible = rows.Where(row => CalendarWire.CanViewFromSession(row, call)).ToArray();
        return new PyTuple(
        [
            new PyList(visible.Where(row => row.Tag is CalendarTags.Personal or CalendarTags.Ccp or CalendarTags.Automated)
                .Select(CalendarWire.EventRow).ToArray()),
            new PyList(visible.Where(row => row.Tag == CalendarTags.Corporation).Select(CalendarWire.EventRow).ToArray()),
            new PyList(visible.Where(row => row.Tag == CalendarTags.Alliance).Select(CalendarWire.EventRow).ToArray()),
        ]);
    }

    [Method("GetEventDetails")]
    public async Task<PyValue> GetEventDetails(ServiceCall call)
    {
        var eventId = CalendarWire.LongArg(call, 0);
        var ownerId = CalendarWire.LongArg(call, 1);
        var calendarEvent = await Store.GetEventAsync(eventId, ownerId, call.Cancellation);
        if (calendarEvent is null || !await CalendarWire.CanViewAsync(Store, calendarEvent, call))
            throw CalendarMgrService.EventNotFound();
        return CalendarWire.EventDetails(calendarEvent);
    }
}

/// <summary>Converts the native calendar model to the client's KeyVal and row-list wire shapes.</summary>
internal static class CalendarWire
{
    // eve-logic appConst.py (pinned client revision): corpRoleChatManager.
    private const long ChatManagerRole = 36_028_797_018_963_968L;

    public static long CharacterId(ServiceCall call) =>
        call.Session.CharacterId is long id && id > 0 ? id : throw CalendarMgrService.EventNotFound();

    public static long SessionId(ServiceCall call, string key, string alternate) =>
        call.Session.GetInt(key) ?? call.Session.GetInt(alternate) ?? 0;

    public static long LongArg(ServiceCall call, int index) => LongValue(call.Arg(index));

    public static int IntArg(ServiceCall call, int index) => checked((int)LongArg(call, index));

    private static long LongValue(PyValue value) => value switch
    {
        PyInt number => number.Value,
        PyLong number => number.Value,
        _ => throw new ArgumentException("Calendar RPC expected an integer argument."),
    };

    private static int? NullableInt(PyValue value) => value switch
    {
        PyNone => null,
        PyInt number => checked((int)number.Value),
        PyLong number => checked((int)number.Value),
        _ => throw new ArgumentException("Calendar RPC expected an integer or None."),
    };

    private static string StringValue(PyValue value) => value switch
    {
        var text when Eve.Macho.JsSemantics.StringValue(text) is not null => Eve.Macho.JsSemantics.StringValue(text)!,
        PyNone => string.Empty,
        _ => throw new ArgumentException("Calendar RPC expected text."),
    };

    public static IReadOnlyList<long> CharacterIds(PyValue value)
    {
        if (value is PyNone) return Array.Empty<long>();
        var values = value switch
        {
            PyList list => list.Items,
            PyTuple tuple => tuple.Items,
            _ => throw new ArgumentException("Calendar RPC expected a character ID list."),
        };
        return values.Select(LongValue).Where(id => id > 0).Distinct().ToArray();
    }

    public static CalendarEventInput ReadInput(
        ServiceCall call, int dateIndex, int durationIndex, int titleIndex, int textIndex, int importanceIndex,
        long ownerId, long creatorId, short tag) =>
        new(ownerId, creatorId, tag, LongArg(call, dateIndex),
            NullableInt(call.Arg(durationIndex)),
            StringValue(call.Arg(titleIndex)),
            StringValue(call.Arg(textIndex)),
            IntArg(call, importanceIndex));

    public static void Validate(CalendarEventInput input, int inviteeCount)
    {
        if (string.IsNullOrWhiteSpace(input.Title))
            throw MachoErrors.ThrowUserError("CalendarEventMustSpecifyTitle");
        if (input.Title.Length > 40)
            throw MachoErrors.ThrowUserError("CalendarTitleTooLong");
        if (input.Text.Length > 500)
            throw MachoErrors.ThrowUserError("CalendarDescriptionTooLong");
        if (input.Duration is < 0)
            throw MachoErrors.ThrowUserError("CalendarInvalidDuration");
        if (inviteeCount > 50)
            throw TooManyInvitees();

        DateTime eventTime;
        try
        {
            eventTime = DateTime.FromFileTimeUtc(input.EventDateTime);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw MachoErrors.ThrowUserError("CalendarCannotPlanThePast2");
        }

        var now = DateTime.UtcNow;
        if (eventTime <= now)
            throw MachoErrors.ThrowUserError("CalendarCannotPlanThePast2");
        var monthDistance = (eventTime.Year - now.Year) * 12 + eventTime.Month - now.Month;
        if (monthDistance > 12)
            throw MachoErrors.ThrowUserError("CalendarTooFarIntoFuture", ("numMonths", new PyInt(12)));
    }


    public static MachoWrappedException TooManyInvitees() =>
        MachoErrors.ThrowUserError("CustomInfo", ("info", new PyStr("Too many calendar invitees (maximum 50).")));

    public static long OwnerIdForCreate(ServiceCall call, short tag)
    {
        var characterId = CharacterId(call);
        if (tag == CalendarTags.Personal) return characterId;

        var role = call.Session.GetInt("corprole") ?? 0;
        if ((role & ChatManagerRole) != ChatManagerRole)
            throw CalendarMgrService.EventNotFound();

        var ownerId = tag switch
        {
            CalendarTags.Corporation => SessionId(call, "corpid", "corporationID"),
            CalendarTags.Alliance => SessionId(call, "allianceid", "allianceID"),
            _ => 0,
        };
        return ownerId > 0 ? ownerId : throw CalendarMgrService.EventNotFound();
    }

    public static bool CanManage(CalendarEvent calendarEvent, ServiceCall call)
    {
        var characterId = call.Session.CharacterId ?? 0;
        var role = call.Session.GetInt("corprole") ?? 0;
        return calendarEvent.Tag switch
        {
            CalendarTags.Personal => calendarEvent.OwnerId == characterId && characterId > 0,
            CalendarTags.Corporation => calendarEvent.OwnerId == SessionId(call, "corpid", "corporationID") &&
                calendarEvent.OwnerId > 0 && (role & ChatManagerRole) == ChatManagerRole,
            CalendarTags.Alliance => calendarEvent.OwnerId == SessionId(call, "allianceid", "allianceID") &&
                calendarEvent.OwnerId > 0 && (role & ChatManagerRole) == ChatManagerRole,
            _ => false,
        };
    }

    public static bool CanViewFromSession(CalendarEvent calendarEvent, ServiceCall call) =>
        calendarEvent.Tag switch
        {
            CalendarTags.Personal => true,
            CalendarTags.Corporation => calendarEvent.OwnerId == SessionId(call, "corpid", "corporationID"),
            CalendarTags.Alliance => calendarEvent.OwnerId == SessionId(call, "allianceid", "allianceID"),
            CalendarTags.Ccp or CalendarTags.Automated => true,
            _ => false,
        };

    public static async Task<bool> CanViewAsync(ICalendarStore store, CalendarEvent calendarEvent, ServiceCall call)
    {
        if (calendarEvent.Tag != CalendarTags.Personal)
            return CanViewFromSession(calendarEvent, call);

        var characterId = call.Session.CharacterId ?? 0;
        if (calendarEvent.OwnerId == characterId && characterId > 0) return true;
        var responses = await store.GetResponsesForCharacterAsync(characterId, call.Cancellation);
        return responses.Any(response => response.EventId == calendarEvent.EventId &&
            response.Status != CalendarResponseStates.Uninvited);
    }


    public static IReadOnlyList<CalendarResponse> IncludeGroupMembers(
        long eventId, IReadOnlyList<CalendarResponse> responses, IReadOnlyList<long> memberIds)
    {
        var byCharacter = responses.ToDictionary(row => row.CharacterId);
        foreach (var characterId in memberIds.Where(id => id > 0).Distinct())
            byCharacter.TryAdd(characterId,
                new CalendarResponse(eventId, characterId, CalendarResponseStates.Undecided));
        return byCharacter.Values.OrderBy(row => row.CharacterId).ToArray();
    }

    public static async Task<bool> CanRespondAsync(
        ICalendarStore store, CalendarEvent calendarEvent, ServiceCall call, long characterId)
    {
        return calendarEvent.Tag switch
        {
            CalendarTags.Personal when calendarEvent.OwnerId == characterId => true,
            CalendarTags.Personal => (await store.GetResponsesToEventAsync(
                calendarEvent.EventId, calendarEvent.OwnerId, call.Cancellation))
                .Any(response => response.CharacterId == characterId && response.Status != CalendarResponseStates.Uninvited),
            CalendarTags.Corporation => calendarEvent.OwnerId == SessionId(call, "corpid", "corporationID"),
            CalendarTags.Alliance => calendarEvent.OwnerId == SessionId(call, "allianceid", "allianceID"),
            _ => false,
        };
    }

    public static long NowFileTime() => DateTime.UtcNow.ToFileTimeUtc();

    public static PyDict EventRow(CalendarEvent value) => KeyVal(
        ("eventID", new PyLong(value.EventId)),
        ("ownerID", new PyLong(value.OwnerId)),
        ("creatorID", new PyLong(value.CreatorId)),
        ("eventDateTime", new PyLong(value.EventDateTime)),
        ("eventDuration", value.Duration is { } duration ? new PyInt(duration) : PyNone.Instance),
        ("eventTitle", new PyStr(value.Title)),
        ("importance", new PyInt(value.Importance)),
        ("dateModified", new PyLong(value.DateModified)),
        ("isDeleted", value.IsDeleted ? PyBool.True : PyBool.False),
        ("autoEventType", value.AutoEventType is { } autoEventType ? new PyInt(autoEventType) : PyNone.Instance));

    public static PyDict EventDetails(CalendarEvent value) => KeyVal(
        ("eventID", new PyLong(value.EventId)),
        ("ownerID", new PyLong(value.OwnerId)),
        ("creatorID", new PyLong(value.CreatorId)),
        ("eventText", new PyStr(value.Text)));

    public static PyDict ResponseForCharacter(CalendarResponse value) => KeyVal(
        ("eventID", new PyLong(value.EventId)),
        ("status", new PyInt(value.Status)));

    public static PyDict ResponseForEvent(CalendarResponse value) => KeyVal(
        ("characterID", new PyLong(value.CharacterId)),
        ("status", new PyInt(value.Status)));

    private static PyDict KeyVal(params (string Name, PyValue Value)[] fields) =>
        new(fields.Select(field =>
            new KeyValuePair<PyValue, PyValue>(new PyStr(field.Name), field.Value)).ToArray());
}

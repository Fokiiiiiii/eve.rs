using Eve.Calendar;
using Eve.Marshal;

namespace Eve.Services.Core;

// Wire contracts: EveJS v0.12.9, commit 828823a75f211fdecb945b087a26c225f111491f,
// server/src/services/calendar/{calendarMgrService,calendarProxyService,calendarPayloads,calendarRuntimeState}.js.
// Client callers: eve-logic, commit 1dbb5711a072a146e1d228365668ac05655eefe1,
// eve/client/script/ui/services/eveCalendarsvc.py: GetEventResponses, GetResponsesToEvent,
// GetEventsByMonthYear and GetEventDetails.
// Personal/corporation/alliance entries and responses:
// https://www.eveonline.com/news/view/introducing-eve-mobile-portal
// Calendar visibility and event details: https://wiki.eveuniversity.org/NeoCom#Calendar

/// <summary>Calendar response reads for a native calendar with no stored events or responses.</summary>
/// <remarks>
/// GameStore recognizes calendarEvents.events and calendarResponses.responses, but the native host
/// has no Calendar event/response read model or adapter to those imported roots. The missing writers
/// are CreatePersonalEvent/CreateCorporationEvent/CreateAllianceEvent, their Edit variants,
/// DeleteEvent, UpdateEventParticipants and SendEventResponse. Until that model is connected, these
/// RPCs expose only the empty-state contract; an event-specific read must still report not found.
/// Native notification publication and recurring global-event seeding also require that persistence.
/// </remarks>
[Service("calendarMgr")]
public sealed partial class CalendarMgrService
{
    private readonly ICalendarStore? _store;
    private readonly ICalendarAudienceReader? _audience;

    public CalendarMgrService() { }
    public CalendarMgrService(ICalendarStore store, ICalendarAudienceReader? audience = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _audience = audience;
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
        if (calendarEvent is null || calendarEvent.Tag != expectedTag || !CalendarWire.CanManage(calendarEvent, call))
            throw EventNotFound();

        var input = CalendarWire.ReadInput(
            call, 2, 3, 4, 5, 6, calendarEvent.OwnerId, calendarEvent.CreatorId, expectedTag);
        CalendarWire.Validate(input, 0);
        if (!await Store.EditEventAsync(eventId, calendarEvent.OwnerId, oldEventDateTime, input, call.Cancellation))
            throw EventNotFound();
        return PyNone.Instance;
    }

    [Method("DeleteEvent")]
    public async Task<PyValue> DeleteEvent(ServiceCall call)
    {
        var eventId = CalendarWire.LongArg(call, 0);
        var ownerId = CalendarWire.LongArg(call, 1);
        var calendarEvent = await Store.GetEventAsync(eventId, ownerId, call.Cancellation);
        if (calendarEvent is null || !CalendarWire.CanManage(calendarEvent, call)) throw EventNotFound();
        if (!await Store.DeleteEventAsync(eventId, ownerId, CalendarWire.NowFileTime(), call.Cancellation))
            throw EventNotFound();
        return PyNone.Instance;
    }

    [Method("UpdateEventParticipants")]
    public async Task<PyValue> UpdateEventParticipants(ServiceCall call)
    {
        var eventId = CalendarWire.LongArg(call, 0);
        var add = CalendarWire.CharacterIds(call.Arg(1));
        var remove = CalendarWire.CharacterIds(call.Arg(2));
        var calendarEvent = await Store.GetEventByIdAsync(eventId, call.Cancellation);
        if (calendarEvent is null || !CalendarWire.CanManage(calendarEvent, call)) throw EventNotFound();
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

        if (!await Store.UpdateParticipantsAsync(eventId, calendarEvent.OwnerId, add, remove, call.Cancellation))
            throw EventNotFound();
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
        return PyNone.Instance;
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
            CalendarTags.Personal => true, // ListMonthAsync has already limited these to owners/invitees.
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

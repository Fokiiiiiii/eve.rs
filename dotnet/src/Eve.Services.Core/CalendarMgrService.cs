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
    [Method("GetResponsesForCharacter")]
    public PyValue GetResponsesForCharacter(ServiceCall call) => new PyList([]);

    [Method("GetResponsesToEvent")]
    public PyValue GetResponsesToEvent(ServiceCall call) => throw EventNotFound();

    internal static MachoWrappedException EventNotFound() =>
        MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("Calendar event not found.")));
}

/// <summary>Calendar month and event reads, addressed separately by the client's ProxySvc.</summary>
[Service("calendarProxy")]
public sealed partial class CalendarProxyService
{
    [Method("GetEventList")]
    public PyValue GetEventList(ServiceCall call) =>
        new PyTuple([new PyList([]), PyNone.Instance, PyNone.Instance]);

    [Method("GetEventDetails")]
    public PyValue GetEventDetails(ServiceCall call) => throw CalendarMgrService.EventNotFound();
}

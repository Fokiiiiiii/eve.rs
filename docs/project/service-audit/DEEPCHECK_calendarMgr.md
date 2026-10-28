# Deep check: calendarMgr

## Client callers and request/reply contracts

The full Host and V24 call-flow audit is already recorded in DEEPCHECK_calendar.md. This service-specific file follows its eleven manager methods; the related calendarProxy is a separate service.

| Method | V24 request and consumed reply |
|---|---|
| GetResponsesForCharacter | eve/client/script/ui/services/eveCalendarsvc.py:251-255, no args; rows are keyed by eventID with status. |
| GetResponsesToEvent(eventID, ownerID) | eveCalendarsvc.py:414-419, two positional IDs; rows supply characterID/status, excluding uninvited responses. |
| CreatePersonalEvent(dateTime, duration, title, description, important, invitees) | eveCalendarsvc.py:182,184-187, six positional values; returned eventID updates the local calendar and response caches. |
| CreateCorporationEvent(dateTime, duration, title, description, important) | eveCalendarsvc.py:190-191, five positional values; eventID is consumed by the facade. |
| CreateAllianceEvent(dateTime, duration, title, description, important) | eveCalendarsvc.py:192-193, five positional values; eventID is consumed by the facade. |
| UpdateEventParticipants(eventID, charsToAdd, charsToRemove) | eveCalendarsvc.py:198-201, positional ID plus add/remove sequences; result ignored, response cache invalidated. |
| EditPersonalEvent(eventID, oldDateTime, dateTime, duration, title, description, important) | eveCalendarsvc.py:212-213, seven positional values; result ignored. |
| EditCorporationEvent(...) | eveCalendarsvc.py:214-215, same seven positional values; result ignored. |
| EditAllianceEvent(...) | eveCalendarsvc.py:216-217, same seven positional values; result ignored. |
| DeleteEvent(eventID, ownerID) | eveCalendarsvc.py:318-319, two positional IDs; result ignored and event list refreshes. |
| SendEventResponse(eventID, ownerID, response) | eveCalendarsvc.py:295-309, three positional values; result ignored, response cache invalidated. |

Client requests use positional method values; the transport adds machoVersion separately. V24 supplies None for nullable duration/description and list/tuple invitees in the documented flows. The exact row fields, None handling, named UserErrors/CustomNotify paths, notification argument lists and ordering, authorization checks, and store mutations remain detailed in DEEPCHECK_calendar.md. In particular, mutation replies are None while create replies are integer event IDs; response reads are lists of KeyVal rows.

## Host and EveJS comparison

CalendarMgrService is registered from CoreServices.Create and uses the persistent CalendarStore through MailComposition. Writes commit before observer notifications; observer sends are scheduled after the response. The existing deep check traces calendar audience/role checks and verifies subsequent reads.

EveJS v0.12.9 manager handlers are in server/src/services/calendar/calendarMgrService.js:24-162 and delegate mutations/reads to its calendar runtime state. eve.rs uses CalendarStore transactions and the same client-visible reply and notification shapes.

## Test evidence and limits

CalendarRpcHostWireTests.cs exercises the registered Host listener and covers the create, read, edit, response, delete, authorization and error paths; the prior deep check records 2 focused tests passing and a red full Host shard due to the no-database environment. EVE_RS_COVERAGE.csv still labels these eleven rows IMPLEMENTED-UNTESTED because its generated match list predates the wire test; that status does not describe the current source tree. The deepcheck and test source are the evidence for this group.

No PostgreSQL database or graphical V24 client was used in this pass. No code changed.

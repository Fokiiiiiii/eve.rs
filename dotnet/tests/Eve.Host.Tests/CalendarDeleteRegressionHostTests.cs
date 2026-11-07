using Eve.Calendar;
using Eve.Data;
using Eve.Host;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using Npgsql;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [PgFact]
    public async Task Calendar_host_delete_preserves_declined_invitee_response_after_reconnect()
    {
        var ownerId = Random.Shared.NextInt64(93_000_000, 94_000_000);
        var inviteeId = ownerId + 1;
        await using var dataSource = NpgsqlDataSource.Create(
            PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        await CalendarStore.MigrateAsync(dataSource);
        var store = new CalendarStore(dataSource);
        var sessions = new CharacterSessions();
        var opened = 0;

        Start(
            strictRpc: true,
            configure: registry =>
            {
                registry.Register(new CalendarMgrService(store, sessions: sessions,
                    cspaBilling: FreeCalendarCspaBilling()));
                registry.Register(new CalendarProxyService(store));
            },
            sessionOpened: session =>
            {
                var characterId = Interlocked.Increment(ref opened) == 1 ? ownerId : inviteeId;
                session.Set("charid", new PyLong(characterId));
                if (characterId == inviteeId)
                    sessions.Enter(characterId, session);
            });

        var eventId = 0L;
        try
        {
            var (ownerFront, ownerOpen) = await OpenCalendarFrontAsync(641);
            var (inviteeFront, inviteeOpen) = await OpenCalendarFrontAsync(642);
            var eventTime = DateTime.UtcNow.AddDays(4).ToFileTimeUtc();
            eventId = WireInteger(await HostRpcAsync(
                ownerFront, ownerOpen, 1, "calendarMgr", "CreatePersonalEvent",
                new PyLong(eventTime), new PyInt(30), new PyStr("Declined cancellation"),
                new PyStr(""), new PyInt(0), new PyList([new PyLong(inviteeId)])));
            await AssertCalendarNotificationAsync(inviteeFront, "OnNewCalendarEvent");

            Assert.IsType<PyNone>(await HostRpcAsync(
                inviteeFront, inviteeOpen, 1, "calendarMgr", "SendEventResponse",
                new PyLong(eventId), new PyLong(ownerId), new PyLong(CalendarResponseStates.Declined)));
            Assert.IsType<PyNone>(await HostRpcAsync(
                ownerFront, ownerOpen, 2, "calendarMgr", "DeleteEvent",
                new PyLong(eventId), new PyLong(ownerId)));
            await AssertCalendarNotificationAsync(inviteeFront, "OnRemoveCalendarEvent");

            var (reconnectedFront, reconnectedOpen) = await OpenCalendarFrontAsync(643);
            var responses = Assert.IsType<PyList>(await HostRpcAsync(
                reconnectedFront, reconnectedOpen, 1, "calendarMgr", "GetResponsesForCharacter"));
            Assert.Equal(CalendarResponseStates.Declined,
                WireInteger(WireField(Assert.Single(responses.Items,
                    row => WireInteger(WireField(row, "eventID")) == eventId), "status")));

            var month = DateTime.FromFileTimeUtc(eventTime);
            var eventLists = Assert.IsType<PyTuple>(await HostRpcAsync(
                reconnectedFront, reconnectedOpen, 2, "calendarProxy", "GetEventList",
                new PyInt(month.Month), new PyInt(month.Year)));
            var deletedEvent = Assert.Single(Assert.IsType<PyList>(eventLists.Items[0]).Items,
                row => WireInteger(CalendarEventRowField(row, "eventID")) == eventId);
            Assert.Equal(PyBool.True, CalendarEventRowField(deletedEvent, "isDeleted"));

            var persisted = Assert.Single(await store.GetResponsesForCharacterAsync(inviteeId),
                row => row.EventId == eventId);
            Assert.Equal(CalendarResponseStates.Declined, persisted.Status);
        }
        finally
        {
            if (eventId > 0)
            {
                await using var command = dataSource.CreateCommand(
                    "DELETE FROM calendar.events WHERE event_id = $1");
                command.Parameters.AddWithValue(eventId);
                await command.ExecuteNonQueryAsync();
            }
        }
    }
}

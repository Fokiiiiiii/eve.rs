using Eve.Calendar;
using Eve.Data;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using Npgsql;

namespace Eve.Host.Tests;

public sealed class CalendarParticipantConcurrencyTests
{
    private const long CreatorId = 90_700_001;

    [PgFact]
    public async Task Concurrent_invite_updates_cannot_exceed_the_client_limit()
    {
        var pgUrl = Environment.GetEnvironmentVariable("EVE_PG_URL");
        Assert.False(string.IsNullOrWhiteSpace(pgUrl));
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(pgUrl!));
        await CalendarStore.MigrateAsync(source);
        var store = new CalendarStore(source);
        var invitees = Enumerable.Range(0, 49).Select(index => CreatorId + index + 1L).ToArray();
        var eventId = await store.CreateEventAsync(
            new CalendarEventInput(CreatorId, CreatorId, CalendarTags.Personal,
                DateTime.UtcNow.AddDays(2).ToFileTimeUtc(), 60, "Fleet form-up", "Meet at the undock", 0),
            invitees);
        try
        {
            var synchronizedStore = new FirstConcurrentReadStore(store);
            IService service = new CalendarMgrService(synchronizedStore);
            var session = new ClientSession { CharacterId = CreatorId };
            var calls = new[] { CreatorId + 100, CreatorId + 101 }.Select(characterId =>
                service.CallAsync(Call(session, eventId, characterId)).AsTask()).ToArray();

            var results = await Task.WhenAll(calls.Select(CaptureResult));

            Assert.Single(results, result => result);
            var responses = await store.GetResponsesToEventAsync(eventId, CreatorId);
            Assert.Equal(50, responses.Count(row =>
                row.Status != CalendarResponseStates.Uninvited && row.CharacterId != CreatorId));
        }
        finally
        {
            await using var connection = await source.OpenConnectionAsync();
            await using var command = new NpgsqlCommand(
                "DELETE FROM calendar.responses WHERE event_id = $1",
                connection);
            command.Parameters.AddWithValue(eventId);
            await command.ExecuteNonQueryAsync();
            await using var deleteEvent = new NpgsqlCommand(
                "DELETE FROM calendar.events WHERE event_id = $1", connection);
            deleteEvent.Parameters.AddWithValue(eventId);
            await deleteEvent.ExecuteNonQueryAsync();
        }
    }

    private static ServiceCall Call(ClientSession session, long eventId, long inviteeId) => new()
    {
        Method = "UpdateEventParticipants",
        Args = [new PyLong(eventId), new PyList([new PyLong(inviteeId)]), new PyList([])],
        Session = session,
    };

    private static async Task<bool> CaptureResult(Task<PyValue> call)
    {
        try
        {
            await call;
            return true;
        }
        catch (MachoWrappedException error)
        {
            Assert.Equal("CustomNotify", error.Error.UserErrorMessage);
            return false;
        }
    }

    private sealed class FirstConcurrentReadStore(ICalendarStore inner) : ICalendarStore
    {
        private readonly TaskCompletionSource _firstTwoReads = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _reads;

        public Task<IReadOnlyList<CalendarEvent>> ListMonthAsync(long characterId, long corporationId, long allianceId,
            int month, int year, CancellationToken cancellationToken = default) =>
            inner.ListMonthAsync(characterId, corporationId, allianceId, month, year, cancellationToken);

        public Task<CalendarEvent?> GetEventAsync(long eventId, long ownerId, CancellationToken cancellationToken = default) =>
            inner.GetEventAsync(eventId, ownerId, cancellationToken);

        public Task<CalendarEvent?> GetEventByIdAsync(long eventId, CancellationToken cancellationToken = default) =>
            inner.GetEventByIdAsync(eventId, cancellationToken);

        public Task<IReadOnlyList<CalendarResponse>> GetResponsesForCharacterAsync(long characterId,
            CancellationToken cancellationToken = default) =>
            inner.GetResponsesForCharacterAsync(characterId, cancellationToken);

        public async Task<IReadOnlyList<CalendarResponse>> GetResponsesToEventAsync(long eventId, long ownerId,
            CancellationToken cancellationToken = default)
        {
            var rows = await inner.GetResponsesToEventAsync(eventId, ownerId, cancellationToken);
            if (Interlocked.Increment(ref _reads) <= 2)
            {
                if (Volatile.Read(ref _reads) == 2) _firstTwoReads.TrySetResult();
                await _firstTwoReads.Task.WaitAsync(cancellationToken);
            }
            return rows;
        }

        public Task<long> CreateEventAsync(CalendarEventInput input, IReadOnlyCollection<long> invitees,
            CancellationToken cancellationToken = default) =>
            inner.CreateEventAsync(input, invitees, cancellationToken);

        public Task<bool> EditEventAsync(long eventId, long ownerId, long oldEventDateTime, CalendarEventInput input,
            CancellationToken cancellationToken = default) =>
            inner.EditEventAsync(eventId, ownerId, oldEventDateTime, input, cancellationToken);

        public Task<bool> DeleteEventAsync(long eventId, long ownerId, long dateModified,
            CancellationToken cancellationToken = default) =>
            inner.DeleteEventAsync(eventId, ownerId, dateModified, cancellationToken);

        public Task<CalendarParticipantUpdateResult> UpdateParticipantsAsync(long eventId, long ownerId, IReadOnlyCollection<long> add,
            IReadOnlyCollection<long> remove, CancellationToken cancellationToken = default) =>
            inner.UpdateParticipantsAsync(eventId, ownerId, add, remove, cancellationToken);

        public Task<bool> SendResponseAsync(long eventId, long ownerId, long characterId, short response,
            CancellationToken cancellationToken = default) =>
            inner.SendResponseAsync(eventId, ownerId, characterId, response, cancellationToken);
    }
}

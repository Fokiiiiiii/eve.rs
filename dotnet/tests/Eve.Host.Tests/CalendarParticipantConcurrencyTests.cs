using Eve.Calendar;
using Eve.Data;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using Eve.Wallet;
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
            IService service = new CalendarMgrService(store, cspaBilling: FreeCspaBilling());
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

    [PgFact]
    public async Task Concurrent_approved_additions_charge_a_recipient_once()
    {
        var pgUrl = Environment.GetEnvironmentVariable("EVE_PG_URL");
        Assert.False(string.IsNullOrWhiteSpace(pgUrl));
        var ownerId = Random.Shared.NextInt64(6_000_000_000_000L, 9_000_000_000_000_000L);
        var inviteeId = ownerId + 1;
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(pgUrl!));
        await CalendarStore.MigrateAsync(source);
        await PgWalletStore.MigrateAsync(source);
        var store = new CalendarStore(source);
        var wallets = new WalletTransactions(source);
        await using (var connection = await source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await wallets.EnsureAsync(connection, transaction, ownerId);
            await wallets.AdjustAsync(connection, transaction, ownerId, 1_000, 0, 1,
                "calendar CSPA concurrency test seed", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                $"calendar-cspa-concurrency-seed:{ownerId}");
            await transaction.CommitAsync();
        }

        var eventId = await store.CreateEventAsync(
            new CalendarEventInput(ownerId, ownerId, CalendarTags.Personal,
                DateTime.UtcNow.AddDays(2).ToFileTimeUtc(), 60, "CSPA concurrency", string.Empty, 0), []);
        var billing = new CalendarCspaBilling(
            (_, _) => ValueTask.FromResult<int?>(500),
            (_, _, _) => ValueTask.FromResult(CalendarCspaRecipientRule.Charge),
            wallets);
        IService service = new CalendarMgrService(store, cspaBilling: billing);
        var session = new ClientSession { CharacterId = ownerId };
        var calls = Enumerable.Range(0, 2)
            .Select(_ => service.CallAsync(ApprovedParticipantsCall(session, eventId, inviteeId)).AsTask())
            .ToArray();

        var results = await Task.WhenAll(calls);
        Assert.All(results, result => Assert.IsType<PyNone>(result));
        Assert.Equal(new[] { ownerId, inviteeId }.Order(),
            (await store.GetResponsesToEventAsync(eventId, ownerId)).Select(row => row.CharacterId).Order());
        var journal = (await wallets.JournalForAccountAsync(ownerId, 1000, limit: 1000))
            .Where(entry => entry.RefTypeId == 35)
            .ToArray();
        var charge = Assert.Single(journal);
        Assert.Equal(-500m, charge.Amount);
        Assert.Equal(inviteeId, charge.ReferenceId);
        Assert.Equal(500m, await ReadBalanceAsync(source, ownerId));
    }

    [PgFact]
    public async Task Personal_response_and_invite_removal_serialize_without_reviving_a_revoked_invitee()
    {
        var pgUrl = Environment.GetEnvironmentVariable("EVE_PG_URL");
        Assert.False(string.IsNullOrWhiteSpace(pgUrl));
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(pgUrl!));
        await CalendarStore.MigrateAsync(source);
        var store = new CalendarStore(source);
        var inviteeId = CreatorId + 1;
        var eventId = await store.CreateEventAsync(
            new CalendarEventInput(CreatorId, CreatorId, CalendarTags.Personal,
                DateTime.UtcNow.AddDays(2).ToFileTimeUtc(), 60, "Fleet form-up", "Meet at the undock", 0),
            [inviteeId]);
        var ownerSink = new RecordingSink();
        var ownerSession = new ClientSession(ownerSink) { CharacterId = CreatorId };
        var inviteeSession = new ClientSession { CharacterId = inviteeId };
        var sessions = new CharacterSessions();
        sessions.Enter(CreatorId, ownerSession);
        sessions.Enter(inviteeId, inviteeSession);
        var gatedStore = new FirstConcurrentReadStore(store, synchronizeFirstTwoReads: false);
        IService service = new CalendarMgrService(gatedStore, sessions: sessions, cspaBilling: FreeCspaBilling());
        Task<PyValue>? pendingResponse = null;

        try
        {
            var inFlightResponse = service.CallAsync(ResponseCall(inviteeSession, eventId, CreatorId,
                CalendarResponseStates.Accepted)).AsTask();
            pendingResponse = inFlightResponse;
            await gatedStore.ResponseReached.WaitAsync(TimeSpan.FromSeconds(10));

            await service.CallAsync(ParticipantsCall(ownerSession, eventId, [], [inviteeId]));
            gatedStore.ReleaseResponse();

            var rejected = await Assert.ThrowsAsync<MachoWrappedException>(() => inFlightResponse);
            pendingResponse = null;
            Assert.Equal("CustomNotify", rejected.Error.UserErrorMessage);
            Assert.Equal(CalendarResponseStates.Uninvited,
                Assert.Single(await store.GetResponsesToEventAsync(eventId, CreatorId), row =>
                    row.CharacterId == inviteeId).Status);
            Assert.DoesNotContain("OnEventResponseByExternal", NotificationNames(ownerSink));

            var proxy = new CalendarProxyService(store);
            var detailsError = await Assert.ThrowsAsync<MachoWrappedException>(() => proxy.GetEventDetails(new ServiceCall
            {
                Method = "GetEventDetails",
                Args = [new PyLong(eventId), new PyLong(CreatorId)],
                Session = inviteeSession,
            }));
            Assert.Equal("CustomNotify", detailsError.Error.UserErrorMessage);

            await service.CallAsync(ParticipantsCall(ownerSession, eventId, [inviteeId], []));
            ownerSink.Packets.Clear();
            Assert.IsType<PyNone>(await service.CallAsync(ResponseCall(inviteeSession, eventId, CreatorId,
                CalendarResponseStates.Maybe)));
            Assert.Contains("OnEventResponseByExternal", NotificationNames(ownerSink));

            await service.CallAsync(ParticipantsCall(ownerSession, eventId, [], [inviteeId]));
            Assert.Equal(CalendarResponseStates.Uninvited,
                Assert.Single(await store.GetResponsesToEventAsync(eventId, CreatorId), row =>
                    row.CharacterId == inviteeId).Status);
        }
        finally
        {
            gatedStore.ReleaseResponse();
            if (pendingResponse is not null)
            {
                try { await pendingResponse; }
                catch { }
            }
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

    private static ServiceCall ApprovedParticipantsCall(ClientSession session, long eventId, long inviteeId) => new()
    {
        Method = "UpdateEventParticipants",
        Args = [new PyLong(eventId), new PyList([new PyLong(inviteeId)]), new PyList([])],
        Kwargs = new PyDict([new KeyValuePair<PyValue, PyValue>(new PyStr("approvedCost"), new PyLong(500))]),
        Session = session,
    };

    private static async Task<decimal> ReadBalanceAsync(NpgsqlDataSource source, long ownerId)
    {
        await using var command = source.CreateCommand(
            "SELECT isk FROM wallet.balances WHERE owner_id = $1 AND account_key = 1000");
        command.Parameters.AddWithValue(ownerId);
        return (decimal)(await command.ExecuteScalarAsync())!;
    }

    private static ServiceCall ParticipantsCall(
        ClientSession session, long eventId, long[] add, long[] remove) => new()
    {
        Method = "UpdateEventParticipants",
        Args = [new PyLong(eventId), new PyList(add.Select(id => (PyValue)new PyLong(id)).ToArray()),
            new PyList(remove.Select(id => (PyValue)new PyLong(id)).ToArray())],
        Session = session,
    };

    private static ServiceCall ResponseCall(ClientSession session, long eventId, long ownerId, short response) => new()
    {
        Method = "SendEventResponse",
        Args = [new PyLong(eventId), new PyLong(ownerId), new PyInt(response)],
        Session = session,
    };

    private static string[] NotificationNames(RecordingSink sink) => sink.Packets.OfType<PyObject>().Select(packet =>
    {
        var fields = Assert.IsType<PyTuple>(packet.Args).Items;
        Assert.Equal(MachoMessage.Notification, Assert.IsType<PyInt>(fields[0]).Value);
        var address = Assert.IsType<PyTuple>(Assert.IsType<PyObject>(fields[2]).Args).Items;
        return Assert.IsType<PyStr>(address[1]).Value;
    }).ToArray();

    private static CalendarCspaBilling FreeCspaBilling() => new(
        (_, _) => ValueTask.FromResult<int?>(0),
        (_, _, _) => ValueTask.FromResult(CalendarCspaRecipientRule.Charge),
        null);

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

    private sealed class FirstConcurrentReadStore(ICalendarStore inner, bool synchronizeFirstTwoReads = true) : ICalendarStore
    {
        private readonly TaskCompletionSource _firstTwoReads = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _responseReached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseResponse = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _reads;

        public Task ResponseReached => _responseReached.Task;
        public void ReleaseResponse() => _releaseResponse.TrySetResult();

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
            if (synchronizeFirstTwoReads && Interlocked.Increment(ref _reads) <= 2)
            {
                if (Volatile.Read(ref _reads) == 2) _firstTwoReads.TrySetResult();
                await _firstTwoReads.Task.WaitAsync(cancellationToken);
            }
            return rows;
        }

        public Task<long> CreateEventAsync(CalendarEventInput input, IReadOnlyCollection<long> invitees,
            CancellationToken cancellationToken = default,
            Func<Npgsql.NpgsqlConnection, Npgsql.NpgsqlTransaction, long, IReadOnlyCollection<long>, CancellationToken, Task>? beforeCommitAsync = null) =>
            inner.CreateEventAsync(input, invitees, cancellationToken, beforeCommitAsync);

        public Task<bool> EditEventAsync(long eventId, long ownerId, long oldEventDateTime, CalendarEventInput input,
            CancellationToken cancellationToken = default) =>
            inner.EditEventAsync(eventId, ownerId, oldEventDateTime, input, cancellationToken);

        public Task<bool> DeleteEventAsync(long eventId, long ownerId, long dateModified,
            CancellationToken cancellationToken = default) =>
            inner.DeleteEventAsync(eventId, ownerId, dateModified, cancellationToken);

        public Task<CalendarParticipantUpdate> UpdateParticipantsAsync(long eventId, long ownerId, IReadOnlyCollection<long> add,
            IReadOnlyCollection<long> remove, CancellationToken cancellationToken = default,
            Func<Npgsql.NpgsqlConnection, Npgsql.NpgsqlTransaction, long, IReadOnlyCollection<long>, CancellationToken, Task>? beforeCommitAsync = null) =>
            inner.UpdateParticipantsAsync(eventId, ownerId, add, remove, cancellationToken, beforeCommitAsync);

        public async Task<CalendarResponseUpdate> SendResponseAsync(long eventId, long ownerId, long characterId, short response,
            CancellationToken cancellationToken = default)
        {
            _responseReached.TrySetResult();
            await _releaseResponse.Task.WaitAsync(cancellationToken);
            return await inner.SendResponseAsync(eventId, ownerId, characterId, response, cancellationToken);
        }
    }

    private sealed class RecordingSink : IPacketSink
    {
        public List<PyValue> Packets { get; } = [];
        public void Send(PyValue packet) => Packets.Add(packet);
    }
}

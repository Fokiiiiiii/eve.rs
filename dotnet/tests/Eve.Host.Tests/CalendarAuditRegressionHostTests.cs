using Eve.Calendar;
using Eve.Data;
using Eve.Host;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using Npgsql;
using System.Text;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [PgFact]
    public async Task Calendar_host_serializes_response_and_edit_notifications_in_mutation_order()
    {
        var ownerId = Random.Shared.NextInt64(92_000_000, 93_000_000);
        var inviteeId = ownerId + 1;
        await using var dataSource = NpgsqlDataSource.Create(
            PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        await CalendarStore.MigrateAsync(dataSource);
        var actualStore = new CalendarStore(dataSource);
        var store = new PauseCalendarResponseStore(actualStore);
        var sessions = new CharacterSessions();
        var opened = 0;

        Start(
            strictRpc: true,
            configure: registry =>
            {
                registry.Register(new CalendarMgrService(store, sessions: sessions,
                    cspaBilling: FreeCalendarCspaBilling()));
                registry.Register(new CalendarProxyService(actualStore));
            },
            sessionOpened: session =>
            {
                var characterId = Interlocked.Increment(ref opened) == 1 ? ownerId : inviteeId;
                session.Set("charid", new PyLong(characterId));
                sessions.Enter(characterId, session);
            });

        var eventId = 0L;
        try
        {
            var (ownerFront, ownerOpen) = await OpenCalendarFrontAsync(631);
            var (inviteeFront, inviteeOpen) = await OpenCalendarFrontAsync(632);
            var originalTime = DateTime.UtcNow.AddDays(3).ToFileTimeUtc();
            eventId = WireInteger(await HostRpcAsync(
                ownerFront, ownerOpen, 1, "calendarMgr", "CreatePersonalEvent",
                new PyLong(originalTime), new PyInt(30), new PyStr("Response race"),
                new PyStr(""), new PyInt(0), new PyList([new PyLong(inviteeId)])));
            await AssertCalendarNotificationAsync(inviteeFront, "OnNewCalendarEvent");

            store.PauseNextResponse();
            await inviteeFront.SendAsync(new Packet(inviteeOpen.SessionId, MarshalCodec.Encode(
                Packets.CallWithKwargs(inviteeOpen, 1, "calendarMgr", "SendEventResponse",
                    new PyTuple([new PyLong(eventId), new PyLong(ownerId),
                        new PyLong(CalendarResponseStates.Accepted)]), TransportKwargs()))));
            await store.ResponseReached.WaitAsync(TimeSpan.FromSeconds(10));

            var editedTime = DateTime.FromFileTimeUtc(originalTime).AddDays(1).ToFileTimeUtc();
            await ownerFront.SendAsync(new Packet(ownerOpen.SessionId, MarshalCodec.Encode(
                Packets.CallWithKwargs(ownerOpen, 2, "calendarMgr", "EditPersonalEvent",
                    new PyTuple([new PyLong(eventId), new PyLong(originalTime), new PyLong(editedTime),
                        new PyInt(30), new PyStr("Edited response race"), new PyStr(""), new PyInt(0)]),
                    TransportKwargs()))));
            store.ReleaseResponse();
            Assert.Equal(originalTime, await CalendarResponseNotificationDateAsync(ownerFront));
            Assert.Equal((originalTime, editedTime), await CalendarEditNotificationTimesAsync(ownerFront));
            var editReply = Assert.IsType<Send>(await ownerFront.ReceiveAsync());
            var editReplyPacket = Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(editReply.Payload)));
            Assert.Equal(MachoMessage.CallRsp, editReplyPacket.Type);
            Assert.IsType<PyNone>(Result(MarshalCodec.Decode(editReply.Payload)));

            Assert.Equal(editedTime, (await actualStore.GetEventByIdAsync(eventId))!.EventDateTime);
            var response = Assert.Single(await actualStore.GetResponsesToEventAsync(eventId, ownerId),
                row => row.CharacterId == inviteeId);
            Assert.Equal(CalendarResponseStates.Undecided, response.Status);
        }
        finally
        {
            store.ReleaseResponse();
            if (eventId > 0)
            {
                await using var command = dataSource.CreateCommand(
                    "DELETE FROM calendar.events WHERE event_id = $1");
                command.Parameters.AddWithValue(eventId);
                await command.ExecuteNonQueryAsync();
            }
        }
    }

    [PgFact]
    public async Task Calendar_host_response_notification_uses_the_locked_event_snapshot_after_another_writer_edits()
    {
        var ownerId = Random.Shared.NextInt64(94_000_000, 95_000_000);
        var inviteeId = ownerId + 1;
        await using var dataSource = NpgsqlDataSource.Create(
            PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        await CalendarStore.MigrateAsync(dataSource);
        var actualStore = new CalendarStore(dataSource);
        var store = new PauseCalendarResponseStore(actualStore);
        var sessions = new CharacterSessions();
        var opened = 0;

        Start(
            strictRpc: true,
            configure: registry =>
            {
                registry.Register(new CalendarMgrService(store, sessions: sessions,
                    cspaBilling: FreeCalendarCspaBilling()));
                registry.Register(new CalendarProxyService(actualStore));
            },
            sessionOpened: session =>
            {
                var characterId = Interlocked.Increment(ref opened) == 1 ? ownerId : inviteeId;
                session.Set("charid", new PyLong(characterId));
                sessions.Enter(characterId, session);
            });

        var eventId = 0L;
        try
        {
            var (ownerFront, ownerOpen) = await OpenCalendarFrontAsync(633);
            var (inviteeFront, inviteeOpen) = await OpenCalendarFrontAsync(634);
            var originalTime = DateTime.UtcNow.AddDays(3).ToFileTimeUtc();
            eventId = WireInteger(await HostRpcAsync(
                ownerFront, ownerOpen, 1, "calendarMgr", "CreatePersonalEvent",
                new PyLong(originalTime), new PyInt(30), new PyStr("External writer race"),
                new PyStr(""), new PyInt(0), new PyList([new PyLong(inviteeId)])));
            await AssertCalendarNotificationAsync(inviteeFront, "OnNewCalendarEvent");

            store.PauseNextResponse();
            await inviteeFront.SendAsync(new Packet(inviteeOpen.SessionId, MarshalCodec.Encode(
                Packets.CallWithKwargs(inviteeOpen, 1, "calendarMgr", "SendEventResponse",
                    new PyTuple([new PyLong(eventId), new PyLong(ownerId),
                        new PyLong(CalendarResponseStates.Accepted)]), TransportKwargs()))));
            await store.ResponseReached.WaitAsync(TimeSpan.FromSeconds(10));

            var editedTime = DateTime.FromFileTimeUtc(originalTime).AddDays(1).ToFileTimeUtc();
            Assert.True(await actualStore.EditEventAsync(eventId, ownerId, originalTime,
                new CalendarEventInput(ownerId, ownerId, CalendarTags.Personal, editedTime,
                    30, "External writer edit", string.Empty, 0)));
            store.ReleaseResponse();

            var responseReply = Assert.IsType<Send>(await inviteeFront.ReceiveAsync());
            var responsePacket = Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(responseReply.Payload)));
            Assert.Equal(MachoMessage.CallRsp, responsePacket.Type);
            Assert.IsType<PyNone>(Result(MarshalCodec.Decode(responseReply.Payload)));
            Assert.Equal(editedTime, await CalendarResponseNotificationDateAsync(ownerFront));

            var persistedEvent = await actualStore.GetEventByIdAsync(eventId);
            Assert.Equal(editedTime, persistedEvent?.EventDateTime);
            var response = Assert.Single(await actualStore.GetResponsesToEventAsync(eventId, ownerId),
                row => row.CharacterId == inviteeId);
            Assert.Equal(CalendarResponseStates.Accepted, response.Status);
        }
        finally
        {
            store.ReleaseResponse();
            if (eventId > 0)
            {
                await using var command = dataSource.CreateCommand(
                    "DELETE FROM calendar.events WHERE event_id = $1");
                command.Parameters.AddWithValue(eventId);
                await command.ExecuteNonQueryAsync();
            }
        }
    }

    [PgFact]
    public async Task Calendar_host_reinvite_after_concurrent_removal_notifies_the_committed_transition()
    {
        var ownerId = Random.Shared.NextInt64(91_000_000, 92_000_000);
        var inviteeId = ownerId + 1;
        await using var dataSource = NpgsqlDataSource.Create(
            PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        await CalendarStore.MigrateAsync(dataSource);
        var actualStore = new CalendarStore(dataSource);
        var store = new ParticipantRaceCalendarStore(actualStore, inviteeId);
        var sessions = new CharacterSessions();
        var opened = 0;

        Start(
            strictRpc: true,
            configure: registry =>
            {
                registry.Register(new CalendarMgrService(store, sessions: sessions,
                    cspaBilling: FreeCalendarCspaBilling()));
                registry.Register(new CalendarProxyService(actualStore));
            },
            sessionOpened: session =>
            {
                var characterId = Interlocked.Increment(ref opened) switch
                {
                    1 or 2 => ownerId,
                    _ => inviteeId,
                };
                session.Set("charid", new PyLong(characterId));
                sessions.Enter(characterId, session);
            });

        var eventId = 0L;
        Task<PyValue>? removeCall = null;
        Task<PyValue>? addCall = null;
        try
        {
            var (firstOwnerFront, firstOwnerOpen) = await OpenCalendarFrontAsync(621);
            var (secondOwnerFront, secondOwnerOpen) = await OpenCalendarFrontAsync(622);
            var (inviteeFront, inviteeOpen) = await OpenCalendarFrontAsync(623);
            eventId = WireInteger(await HostRpcAsync(
                firstOwnerFront, firstOwnerOpen, 1, "calendarMgr", "CreatePersonalEvent",
                new PyLong(DateTime.UtcNow.AddDays(2).ToFileTimeUtc()), PyNone.Instance,
                new PyStr("Concurrent invitation"), PyNone.Instance, new PyInt(0),
                new PyList([new PyLong(inviteeId)])));
            await AssertCalendarNotificationAsync(secondOwnerFront, "OnNewCalendarEvent");
            await AssertCalendarNotificationAsync(inviteeFront, "OnNewCalendarEvent");

            removeCall = CalendarAuditHostRpcAsync(firstOwnerFront, firstOwnerOpen, 2, "UpdateEventParticipants",
                new PyLong(eventId), new PyList([]), new PyList([new PyLong(inviteeId)]));
            await store.RemovalCommitted.WaitAsync(TimeSpan.FromSeconds(10));
            addCall = CalendarAuditHostRpcAsync(secondOwnerFront, secondOwnerOpen, 1, "UpdateEventParticipants",
                new PyLong(eventId), new PyList([new PyLong(inviteeId)]), new PyList([]));
            await Task.Delay(TimeSpan.FromMilliseconds(100));
            Assert.False(addCall.IsCompleted);
            var responseDuringRemoval = Assert.Single(await actualStore.GetResponsesToEventAsync(eventId, ownerId),
                row => row.CharacterId == inviteeId);
            Assert.Equal(CalendarResponseStates.Uninvited, responseDuringRemoval.Status);

            store.ReleaseRemoval();
            Assert.IsType<PyNone>(await removeCall.WaitAsync(TimeSpan.FromSeconds(10)));
            await AssertCalendarNotificationAsync(inviteeFront, "OnRemoveCalendarEvent");

            Assert.IsType<PyNone>(await addCall.WaitAsync(TimeSpan.FromSeconds(10)));
            await AssertCalendarNotificationAsync(inviteeFront, "OnNewCalendarEvent")
                .WaitAsync(TimeSpan.FromSeconds(2));

            var response = Assert.Single(await actualStore.GetResponsesToEventAsync(eventId, ownerId),
                row => row.CharacterId == inviteeId);
            Assert.Equal(CalendarResponseStates.Undecided, response.Status);
        }
        finally
        {
            store.ReleaseRemoval();
            if (removeCall is not null)
            {
                try { await removeCall.WaitAsync(TimeSpan.FromSeconds(2)); }
                catch (Exception) { }
            }
            if (addCall is not null)
            {
                try { await addCall.WaitAsync(TimeSpan.FromSeconds(2)); }
                catch (Exception) { }
            }
            if (eventId > 0)
            {
                await using var command = dataSource.CreateCommand(
                    "DELETE FROM calendar.events WHERE event_id = $1");
                command.Parameters.AddWithValue(eventId);
                await command.ExecuteNonQueryAsync();
            }
        }
    }

    private async Task<PyValue> CalendarAuditHostRpcAsync(
        TestFront front, SessionOpen open, long callId, string method, params PyValue[] args)
    {
        await front.SendAsync(new Packet(open.SessionId, MarshalCodec.Encode(
            Packets.CallWithKwargs(open, callId, "calendarMgr", method, new PyTuple(args), TransportKwargs()))));
        var send = Assert.IsType<Send>(await front.ReceiveAsync());
        var decoded = MarshalCodec.Decode(send.Payload);
        var packet = Assert.IsType<MachoPacket>(MachoPacket.Decode(decoded));
        if (packet.Type == MachoMessage.ErrorResponse)
            throw new Xunit.Sdk.XunitException(Encoding.ASCII.GetString(send.Payload.Span));
        Assert.Equal(MachoMessage.CallRsp, packet.Type);
        return Result(decoded)!;
    }

    private async Task<long> CalendarResponseNotificationDateAsync(TestFront front)
    {
        var (name, args) = await CalendarNotificationArgsAsync(front);
        Assert.Equal("OnEventResponseByExternal", name);
        Assert.Equal(3, args.Items.Count);
        Assert.Equal(CalendarResponseStates.Accepted, WireInteger(args.Items[2]));
        return WireInteger(WireField(args.Items[1], "eventDateTime"));
    }

    private async Task<(long OldDateTime, long EventDateTime)> CalendarEditNotificationTimesAsync(TestFront front)
    {
        var (name, args) = await CalendarNotificationArgsAsync(front);
        Assert.Equal("OnEditCalendarEvent", name);
        Assert.Equal(8, args.Items.Count);
        return (WireInteger(args.Items[2]), WireInteger(args.Items[3]));
    }

    private async Task<(string? Name, PyTuple Args)> CalendarNotificationArgsAsync(TestFront front)
    {
        var send = Assert.IsType<Send>(await front.ReceiveAsync());
        var packet = Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(send.Payload)));
        Assert.Equal(MachoMessage.Notification, packet.Type);
        var fields = Assert.IsType<PyTuple>(Assert.IsType<PyObject>(MarshalCodec.Decode(send.Payload)).Args);
        var envelope = Assert.IsType<PyTuple>(Assert.IsType<PyTuple>(fields.Items[4]).Items[0]);
        var notification = Assert.IsType<PyTuple>(MarshalCodec.Decode(
            Assert.IsType<PyBuffer>(envelope.Items[1]).Value));
        var args = Assert.IsType<PyTuple>(Assert.IsType<PyTuple>(notification.Items[1]).Items[1]);
        return (packet.Dest.BroadcastId, args);
    }

    private sealed class ParticipantRaceCalendarStore(ICalendarStore inner, long inviteeId) : ICalendarStore
    {
        private readonly TaskCompletionSource _removalCommitted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseRemoval = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task RemovalCommitted => _removalCommitted.Task;
        public void ReleaseRemoval() => _releaseRemoval.TrySetResult();

        public Task<IReadOnlyList<CalendarEvent>> ListMonthAsync(long characterId, long corporationId, long allianceId,
            int month, int year, CancellationToken cancellationToken = default) =>
            inner.ListMonthAsync(characterId, corporationId, allianceId, month, year, cancellationToken);

        public Task<CalendarEvent?> GetEventAsync(long eventId, long ownerId, CancellationToken cancellationToken = default) =>
            inner.GetEventAsync(eventId, ownerId, cancellationToken);

        public Task<CalendarEvent?> GetEventByIdAsync(long eventId, CancellationToken cancellationToken = default) =>
            inner.GetEventByIdAsync(eventId, cancellationToken);

        public Task<IReadOnlyList<CalendarResponse>> GetResponsesForCharacterAsync(long characterId,
            CancellationToken cancellationToken = default) => inner.GetResponsesForCharacterAsync(characterId, cancellationToken);

        public async Task<IReadOnlyList<CalendarResponse>> GetResponsesToEventAsync(long eventId, long ownerId,
            CancellationToken cancellationToken = default) =>
            await inner.GetResponsesToEventAsync(eventId, ownerId, cancellationToken);

        public Task<long> CreateEventAsync(CalendarEventInput input, IReadOnlyCollection<long> invitees,
            CancellationToken cancellationToken = default,
            Func<NpgsqlConnection, NpgsqlTransaction, long, IReadOnlyCollection<long>, CancellationToken, Task>? beforeCommitAsync = null) =>
            inner.CreateEventAsync(input, invitees, cancellationToken, beforeCommitAsync);

        public Task<bool> EditEventAsync(long eventId, long ownerId, long oldEventDateTime, CalendarEventInput input,
            CancellationToken cancellationToken = default) =>
            inner.EditEventAsync(eventId, ownerId, oldEventDateTime, input, cancellationToken);

        public Task<bool> DeleteEventAsync(long eventId, long ownerId, long dateModified,
            CancellationToken cancellationToken = default) =>
            inner.DeleteEventAsync(eventId, ownerId, dateModified, cancellationToken);

        public async Task<CalendarParticipantUpdate> UpdateParticipantsAsync(long eventId, long ownerId,
            IReadOnlyCollection<long> add, IReadOnlyCollection<long> remove, CancellationToken cancellationToken = default,
            Func<NpgsqlConnection, NpgsqlTransaction, long, IReadOnlyCollection<long>, CancellationToken, Task>? beforeCommitAsync = null)
        {
            var update = await inner.UpdateParticipantsAsync(eventId, ownerId, add, remove, cancellationToken, beforeCommitAsync);
            if (update.RemovedCharacterIds.Contains(inviteeId))
            {
                _removalCommitted.TrySetResult();
                await _releaseRemoval.Task.WaitAsync(cancellationToken);
            }
            return update;
        }

        public Task<CalendarResponseUpdate> SendResponseAsync(long eventId, long ownerId, long characterId, short response,
            CancellationToken cancellationToken = default) =>
            inner.SendResponseAsync(eventId, ownerId, characterId, response, cancellationToken);
    }

    private sealed class PauseCalendarResponseStore(ICalendarStore inner) : ICalendarStore
    {
        private readonly TaskCompletionSource _responseReached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseResponse = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _pauseNextResponse;

        public Task ResponseReached => _responseReached.Task;
        public void PauseNextResponse() => Volatile.Write(ref _pauseNextResponse, 1);
        public void ReleaseResponse() => _releaseResponse.TrySetResult();

        public Task<IReadOnlyList<CalendarEvent>> ListMonthAsync(long characterId, long corporationId, long allianceId,
            int month, int year, CancellationToken cancellationToken = default) =>
            inner.ListMonthAsync(characterId, corporationId, allianceId, month, year, cancellationToken);

        public Task<CalendarEvent?> GetEventAsync(long eventId, long ownerId, CancellationToken cancellationToken = default) =>
            inner.GetEventAsync(eventId, ownerId, cancellationToken);

        public Task<CalendarEvent?> GetEventByIdAsync(long eventId, CancellationToken cancellationToken = default) =>
            inner.GetEventByIdAsync(eventId, cancellationToken);

        public Task<IReadOnlyList<CalendarResponse>> GetResponsesForCharacterAsync(long characterId,
            CancellationToken cancellationToken = default) => inner.GetResponsesForCharacterAsync(characterId, cancellationToken);

        public Task<IReadOnlyList<CalendarResponse>> GetResponsesToEventAsync(long eventId, long ownerId,
            CancellationToken cancellationToken = default) => inner.GetResponsesToEventAsync(eventId, ownerId, cancellationToken);

        public Task<long> CreateEventAsync(CalendarEventInput input, IReadOnlyCollection<long> invitees,
            CancellationToken cancellationToken = default,
            Func<NpgsqlConnection, NpgsqlTransaction, long, IReadOnlyCollection<long>, CancellationToken, Task>? beforeCommitAsync = null) =>
            inner.CreateEventAsync(input, invitees, cancellationToken, beforeCommitAsync);

        public Task<bool> EditEventAsync(long eventId, long ownerId, long oldEventDateTime, CalendarEventInput input,
            CancellationToken cancellationToken = default) =>
            inner.EditEventAsync(eventId, ownerId, oldEventDateTime, input, cancellationToken);

        public Task<bool> DeleteEventAsync(long eventId, long ownerId, long dateModified,
            CancellationToken cancellationToken = default) =>
            inner.DeleteEventAsync(eventId, ownerId, dateModified, cancellationToken);

        public Task<CalendarParticipantUpdate> UpdateParticipantsAsync(long eventId, long ownerId,
            IReadOnlyCollection<long> add, IReadOnlyCollection<long> remove, CancellationToken cancellationToken = default,
            Func<NpgsqlConnection, NpgsqlTransaction, long, IReadOnlyCollection<long>, CancellationToken, Task>? beforeCommitAsync = null) =>
            inner.UpdateParticipantsAsync(eventId, ownerId, add, remove, cancellationToken, beforeCommitAsync);

        public async Task<CalendarResponseUpdate> SendResponseAsync(long eventId, long ownerId, long characterId, short response,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _pauseNextResponse, 0) != 0)
            {
                _responseReached.TrySetResult();
                await _releaseResponse.Task.WaitAsync(cancellationToken);
            }
            return await inner.SendResponseAsync(eventId, ownerId, characterId, response, cancellationToken);
        }
    }
}

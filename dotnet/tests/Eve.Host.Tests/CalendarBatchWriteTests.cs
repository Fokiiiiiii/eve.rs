using Eve.Calendar;
using Eve.Data;
using Npgsql;

namespace Eve.Host.Tests;

public sealed class CalendarBatchWriteTests
{
    private const long CreatorId = 90_800_001;

    [PgFact]
    public async Task Create_and_update_batch_writes_preserve_calendar_response_states()
    {
        var pgUrl = Environment.GetEnvironmentVariable("EVE_PG_URL");
        Assert.False(string.IsNullOrWhiteSpace(pgUrl));
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(pgUrl!));
        await CalendarStore.MigrateAsync(source);
        var store = new CalendarStore(source);
        var eventIds = new List<long>();
        var inviteeId = CreatorId + 1;
        var removedId = CreatorId + 2;
        var addedId = CreatorId + 3;

        try
        {
            var personalEventId = await store.CreateEventAsync(
                new CalendarEventInput(CreatorId, CreatorId, CalendarTags.Personal,
                    DateTime.UtcNow.AddDays(2).ToFileTimeUtc(), 60, "Personal", "Details", 0),
                [CreatorId, inviteeId, inviteeId, removedId, 0, -1]);
            eventIds.Add(personalEventId);

            var initialResponses = await store.GetResponsesToEventAsync(personalEventId, CreatorId);
            Assert.Collection(initialResponses,
                row => Assert.Equal(new CalendarResponse(personalEventId, CreatorId, CalendarResponseStates.Accepted), row),
                row => Assert.Equal(new CalendarResponse(personalEventId, inviteeId, CalendarResponseStates.Undecided), row),
                row => Assert.Equal(new CalendarResponse(personalEventId, removedId, CalendarResponseStates.Undecided), row));

            var corporationId = CreatorId + 100;
            var groupEventId = await store.CreateEventAsync(
                new CalendarEventInput(corporationId, CreatorId, CalendarTags.Corporation,
                    DateTime.UtcNow.AddDays(3).ToFileTimeUtc(), 60, "Group", "Details", 0),
                [inviteeId, addedId]);
            eventIds.Add(groupEventId);

            var groupResponses = await store.GetResponsesToEventAsync(groupEventId, corporationId);
            Assert.Collection(groupResponses,
                row => Assert.Equal(new CalendarResponse(groupEventId, CreatorId, CalendarResponseStates.Accepted), row));

            Assert.True(await store.SendResponseAsync(
                personalEventId, CreatorId, inviteeId, CalendarResponseStates.Accepted));
            var update = await store.UpdateParticipantsAsync(
                personalEventId, CreatorId,
                [inviteeId, addedId, addedId, CreatorId, 0, -1],
                [inviteeId, removedId, removedId, CreatorId, 0, -1]);

            Assert.Equal(CalendarParticipantUpdateResult.Updated, update.Result);
            var updatedResponses = await store.GetResponsesToEventAsync(personalEventId, CreatorId);
            Assert.Collection(updatedResponses,
                row => Assert.Equal(new CalendarResponse(personalEventId, CreatorId, CalendarResponseStates.Accepted), row),
                row => Assert.Equal(new CalendarResponse(personalEventId, inviteeId, CalendarResponseStates.Uninvited), row),
                row => Assert.Equal(new CalendarResponse(personalEventId, removedId, CalendarResponseStates.Uninvited), row),
                row => Assert.Equal(new CalendarResponse(personalEventId, addedId, CalendarResponseStates.Undecided), row));
        }
        finally
        {
            foreach (var eventId in eventIds)
            {
                await using var connection = await source.OpenConnectionAsync();
                await using var command = new NpgsqlCommand(
                    "DELETE FROM calendar.events WHERE event_id = $1", connection);
                command.Parameters.AddWithValue(eventId);
                await command.ExecuteNonQueryAsync();
            }
        }
    }

    [PgFact]
    public async Task Participant_limit_rejection_does_not_persist_batched_additions()
    {
        var pgUrl = Environment.GetEnvironmentVariable("EVE_PG_URL");
        Assert.False(string.IsNullOrWhiteSpace(pgUrl));
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(pgUrl!));
        await CalendarStore.MigrateAsync(source);
        var store = new CalendarStore(source);
        var eventId = await store.CreateEventAsync(
            new CalendarEventInput(CreatorId, CreatorId, CalendarTags.Personal,
                DateTime.UtcNow.AddDays(2).ToFileTimeUtc(), 60, "Full", "Details", 0),
            Enumerable.Range(1, CalendarLimits.MaxInvitees).Select(offset => CreatorId + offset).ToArray());

        try
        {
            var result = await store.UpdateParticipantsAsync(
                eventId, CreatorId, [CreatorId + CalendarLimits.MaxInvitees + 1], []);

            Assert.Equal(CalendarParticipantUpdateResult.TooManyInvitees, result.Result);
            var responses = await store.GetResponsesToEventAsync(eventId, CreatorId);
            Assert.Equal(CalendarLimits.MaxInvitees + 1, responses.Count);
            Assert.DoesNotContain(responses, row => row.CharacterId == CreatorId + CalendarLimits.MaxInvitees + 1);
        }
        finally
        {
            await using var connection = await source.OpenConnectionAsync();
            await using var command = new NpgsqlCommand(
                "DELETE FROM calendar.events WHERE event_id = $1", connection);
            command.Parameters.AddWithValue(eventId);
            await command.ExecuteNonQueryAsync();
        }
    }
}

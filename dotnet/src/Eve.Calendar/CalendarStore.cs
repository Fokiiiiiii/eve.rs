using Eve.Data;
using Npgsql;

namespace Eve.Calendar;

public static class CalendarTags
{
    public const short Personal = 1;
    public const short Corporation = 2;
    public const short Alliance = 4;
    public const short Ccp = 8;
    public const short Automated = 16;
}

public static class CalendarResponseStates
{
    public const short Uninvited = 0;
    public const short Deleted = 1;
    public const short Declined = 2;
    public const short Undecided = 3;
    public const short Accepted = 4;
    public const short Maybe = 5;
}

public static class CalendarLimits
{
    public const int MaxInvitees = 50;
}

public enum CalendarParticipantUpdateResult
{
    Updated,
    EventNotFound,
    TooManyInvitees,
}

public sealed record CalendarEvent(
    long EventId,
    long OwnerId,
    long CreatorId,
    short Tag,
    long EventDateTime,
    int? Duration,
    string Title,
    string Text,
    int Importance,
    long DateModified,
    bool IsDeleted,
    int? AutoEventType);

public sealed record CalendarResponse(long EventId, long CharacterId, short Status);

public sealed record CalendarEventInput(
    long OwnerId,
    long CreatorId,
    short Tag,
    long EventDateTime,
    int? Duration,
    string Title,
    string Text,
    int Importance);

public interface ICalendarStore
{
    Task<IReadOnlyList<CalendarEvent>> ListMonthAsync(
        long characterId, long corporationId, long allianceId, int month, int year, CancellationToken cancellationToken = default);
    Task<CalendarEvent?> GetEventAsync(long eventId, long ownerId, CancellationToken cancellationToken = default);
    Task<CalendarEvent?> GetEventByIdAsync(long eventId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CalendarResponse>> GetResponsesForCharacterAsync(long characterId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CalendarResponse>> GetResponsesToEventAsync(long eventId, long ownerId, CancellationToken cancellationToken = default);
    Task<long> CreateEventAsync(CalendarEventInput input, IReadOnlyCollection<long> invitees, CancellationToken cancellationToken = default,
        Func<NpgsqlConnection, NpgsqlTransaction, long, IReadOnlyCollection<long>, CancellationToken, Task>? beforeCommitAsync = null,
        Func<CalendarEvent, IReadOnlyCollection<long>, CancellationToken, Task>? afterCommitAsync = null);
    Task<bool> EditEventAsync(long eventId, long ownerId, long oldEventDateTime, CalendarEventInput input,
        CancellationToken cancellationToken = default,
        Func<CalendarEvent, IReadOnlyCollection<long>, CancellationToken, Task>? afterCommitAsync = null);
    Task<bool> DeleteEventAsync(long eventId, long ownerId, long dateModified, CancellationToken cancellationToken = default,
        Func<CalendarEvent, IReadOnlyCollection<long>, CancellationToken, Task>? afterCommitAsync = null);
    Task<CalendarParticipantUpdateResult> UpdateParticipantsAsync(long eventId, long ownerId, IReadOnlyCollection<long> add, IReadOnlyCollection<long> remove,
        CancellationToken cancellationToken = default,
        Func<NpgsqlConnection, NpgsqlTransaction, long, IReadOnlyCollection<long>, CancellationToken, Task>? beforeCommitAsync = null,
        Func<CalendarEvent, IReadOnlyCollection<long>, IReadOnlyCollection<long>, CancellationToken, Task>? afterCommitAsync = null);
    Task<bool> SendResponseAsync(long eventId, long ownerId, long characterId, short response, CancellationToken cancellationToken = default,
        Func<CalendarEvent, IReadOnlyCollection<long>, CancellationToken, Task>? afterCommitAsync = null);
}


/// <summary>Supplies live corporation or alliance member IDs for the calendar's participant rowset.</summary>
public interface ICalendarAudienceReader
{
    Task<IReadOnlyList<long>> GetMemberCharacterIdsAsync(
        short eventTag, long ownerId, CancellationToken cancellationToken = default);
}

/// <summary>PostgreSQL calendar domain store. Event timestamps use the client's 100 ns FILETIME value.</summary>
public sealed class CalendarStore : ICalendarStore
{
    public const string DefaultSchema = "calendar";
    private const string EventColumns =
        "event_id, owner_id, creator_id, tag, event_date_time, duration_minutes, title, event_text, importance, date_modified, is_deleted, auto_event_type";
    private readonly NpgsqlDataSource _source;
    private readonly string _schema;

    public CalendarStore(NpgsqlDataSource source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _schema = DefaultSchema;
    }

    public static IReadOnlyList<Migration> MigrationSet() =>
        Migrations.Embedded(typeof(CalendarStore).Assembly, "Migrations");

    public static Task<IReadOnlyList<string>> MigrateAsync(
        NpgsqlDataSource source, CancellationToken cancellationToken = default) =>
        Migrations.ApplyAsync(source, DefaultSchema, MigrationSet(), cancellationToken);

    private string Q(string sql) => Sql.In(_schema, sql);

    public async Task<IReadOnlyList<CalendarEvent>> ListMonthAsync(
        long characterId, long corporationId, long allianceId, int month, int year, CancellationToken cancellationToken = default)
    {
        if (month is < 1 or > 12 || year is < 1601 or > 9998)
            throw new ArgumentOutOfRangeException(nameof(month));

        var start = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();
        var next = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1).ToFileTimeUtc();
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = Command(connection, null,
            Q($"SELECT {EventColumns} FROM {{schema}}.events e WHERE e.event_date_time >= $1 AND e.event_date_time < $2 AND " +
              "(e.owner_id = $3 OR ($4 > 0 AND e.owner_id = $4) OR ($5 > 0 AND e.owner_id = $5) OR " +
              "EXISTS (SELECT 1 FROM {schema}.responses r WHERE r.event_id = e.event_id AND r.character_id = $3 AND r.status <> 0) OR e.tag IN (8,16)) " +
              "ORDER BY e.event_date_time, e.event_id"),
            start, next, characterId, corporationId, allianceId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<CalendarEvent>();
        while (await reader.ReadAsync(cancellationToken)) rows.Add(ReadEvent(reader));
        return rows;
    }

    public async Task<CalendarEvent?> GetEventAsync(long eventId, long ownerId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = Command(connection, null,
            Q($"SELECT {EventColumns} FROM {{schema}}.events WHERE event_id = $1 AND owner_id = $2"),
            eventId, ownerId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadEvent(reader) : null;
    }

    public async Task<CalendarEvent?> GetEventByIdAsync(long eventId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = Command(connection, null,
            Q($"SELECT {EventColumns} FROM {{schema}}.events WHERE event_id = $1"), eventId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadEvent(reader) : null;
    }

    public async Task<IReadOnlyList<CalendarResponse>> GetResponsesForCharacterAsync(long characterId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = Command(connection, null,
            Q("SELECT event_id, character_id, status FROM {schema}.responses WHERE character_id = $1 ORDER BY event_id"),
            characterId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await ReadResponsesAsync(reader, cancellationToken);
    }

    public async Task<IReadOnlyList<CalendarResponse>> GetResponsesToEventAsync(
        long eventId, long ownerId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = Command(connection, null,
            Q("SELECT r.event_id, r.character_id, r.status FROM {schema}.responses r JOIN {schema}.events e USING (event_id) WHERE r.event_id = $1 AND e.owner_id = $2 ORDER BY r.character_id"),
            eventId, ownerId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await ReadResponsesAsync(reader, cancellationToken);
    }

    public async Task<long> CreateEventAsync(
        CalendarEventInput input, IReadOnlyCollection<long> invitees, CancellationToken cancellationToken = default,
        Func<NpgsqlConnection, NpgsqlTransaction, long, IReadOnlyCollection<long>, CancellationToken, Task>? beforeCommitAsync = null,
        Func<CalendarEvent, IReadOnlyCollection<long>, CancellationToken, Task>? afterCommitAsync = null)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        long eventId;
        var now = DateTime.UtcNow.ToFileTimeUtc();
        await using (var command = Command(connection, transaction,
            Q("INSERT INTO {schema}.events (owner_id, creator_id, tag, event_date_time, duration_minutes, title, event_text, importance, date_modified) VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9) RETURNING event_id"),
            input.OwnerId, input.CreatorId, input.Tag, input.EventDateTime, input.Duration, input.Title, input.Text, input.Importance, now))
        {
            eventId = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
        }

        await UpsertResponseAsync(connection, transaction, eventId, input.CreatorId, CalendarResponseStates.Accepted, cancellationToken);
        IReadOnlyCollection<long> actualInvitees = [];
        if (input.Tag == CalendarTags.Personal)
        {
            var inviteeIds = invitees.Where(id => id > 0 && id != input.CreatorId).Distinct().ToArray();
            actualInvitees = inviteeIds;
            await UpsertResponsesAsync(connection, transaction, eventId, inviteeIds,
                CalendarResponseStates.Undecided, cancellationToken);
        }

        if (beforeCommitAsync is not null)
            await beforeCommitAsync(connection, transaction, eventId, actualInvitees, cancellationToken);
        // Keep the event's mutation and its post-commit client notification in one cross-process sequence.
        await using var notificationLock = await AcquireEventNotificationLockAsync(connection, eventId, cancellationToken);
        var createdEvent = await ReadEventAsync(connection, transaction, eventId, cancellationToken);
        var activeParticipantIds = await ReadActiveParticipantIdsAsync(connection, transaction, eventId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        if (createdEvent is not null && afterCommitAsync is not null)
            await afterCommitAsync(createdEvent, activeParticipantIds, cancellationToken);
        return eventId;
    }

    public async Task<bool> EditEventAsync(
        long eventId, long ownerId, long oldEventDateTime, CalendarEventInput input, CancellationToken cancellationToken = default,
        Func<CalendarEvent, IReadOnlyCollection<long>, CancellationToken, Task>? afterCommitAsync = null)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var notificationLock = await AcquireEventNotificationLockAsync(connection, eventId, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = Command(connection, transaction,
            Q("UPDATE {schema}.events SET event_date_time=$1, duration_minutes=$2, title=$3, event_text=$4, importance=$5, date_modified=$6 WHERE event_id=$7 AND owner_id=$8 AND event_date_time=$9 AND is_deleted=false"),
            input.EventDateTime, input.Duration, input.Title, input.Text, input.Importance, DateTime.UtcNow.ToFileTimeUtc(), eventId, ownerId, oldEventDateTime);
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0) return false;

        if (oldEventDateTime != input.EventDateTime)
        {
            await using var reset = Command(connection, transaction,
                Q("UPDATE {schema}.responses SET status=$1 WHERE event_id=$2 AND character_id <> $3 AND status IN ($4,$5,$6)"),
                CalendarResponseStates.Undecided, eventId, input.CreatorId, CalendarResponseStates.Undecided,
                CalendarResponseStates.Accepted, CalendarResponseStates.Maybe);
            await reset.ExecuteNonQueryAsync(cancellationToken);
        }

        var updatedEvent = await ReadEventAsync(connection, transaction, eventId, cancellationToken);
        var activeParticipantIds = await ReadActiveParticipantIdsAsync(connection, transaction, eventId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        if (updatedEvent is not null && afterCommitAsync is not null)
            await afterCommitAsync(updatedEvent, activeParticipantIds, cancellationToken);
        return true;
    }

    public async Task<bool> DeleteEventAsync(
        long eventId, long ownerId, long dateModified, CancellationToken cancellationToken = default,
        Func<CalendarEvent, IReadOnlyCollection<long>, CancellationToken, Task>? afterCommitAsync = null)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var notificationLock = await AcquireEventNotificationLockAsync(connection, eventId, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var existingEvent = await ReadEventAsync(connection, transaction, eventId, cancellationToken, forUpdate: true);
        if (existingEvent is null || existingEvent.OwnerId != ownerId || existingEvent.IsDeleted)
            return false;
        await using var command = Command(connection, transaction,
            Q("UPDATE {schema}.events SET is_deleted=true, date_modified=$1 WHERE event_id=$2 AND owner_id=$3 AND is_deleted=false"),
            dateModified, eventId, ownerId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0) return false;
        var activeParticipantIds = await ReadActiveParticipantIdsAsync(connection, transaction, eventId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        if (afterCommitAsync is not null)
            await afterCommitAsync(existingEvent with { IsDeleted = true, DateModified = dateModified },
                activeParticipantIds, cancellationToken);
        return true;
    }

    public async Task<CalendarParticipantUpdateResult> UpdateParticipantsAsync(
        long eventId, long ownerId, IReadOnlyCollection<long> add, IReadOnlyCollection<long> remove,
        CancellationToken cancellationToken = default,
        Func<NpgsqlConnection, NpgsqlTransaction, long, IReadOnlyCollection<long>, CancellationToken, Task>? beforeCommitAsync = null,
        Func<CalendarEvent, IReadOnlyCollection<long>, IReadOnlyCollection<long>, CancellationToken, Task>? afterCommitAsync = null)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var notificationLock = await AcquireEventNotificationLockAsync(connection, eventId, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var exists = Command(connection, transaction,
            Q($"SELECT {EventColumns} FROM {{schema}}.events WHERE event_id=$1 AND owner_id=$2 AND is_deleted=false FOR UPDATE"),
            eventId, ownerId);
        CalendarEvent calendarEvent;
        await using (var eventReader = await exists.ExecuteReaderAsync(cancellationToken))
        {
            if (!await eventReader.ReadAsync(cancellationToken))
                return CalendarParticipantUpdateResult.EventNotFound;
            calendarEvent = ReadEvent(eventReader);
        }
        var creatorId = calendarEvent.CreatorId;

        var activeInvitees = new HashSet<long>();
        await using (var responses = Command(connection, transaction,
            Q("SELECT character_id, status FROM {schema}.responses WHERE event_id=$1"), eventId))
        await using (var reader = await responses.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var characterId = reader.GetInt64(0);
                if (characterId != creatorId && reader.GetInt16(1) != CalendarResponseStates.Uninvited)
                    activeInvitees.Add(characterId);
            }
        }

        var removed = remove.Where(id => id > 0 && id != creatorId).ToHashSet();
        var removedInvitees = activeInvitees.Where(removed.Contains).Order().ToArray();
        var existingActive = activeInvitees.ToHashSet();
        activeInvitees.ExceptWith(removed);
        var additions = add.Where(id => id > 0 && id != creatorId && !removed.Contains(id)).Distinct().ToArray();
        var newlyAdded = additions.Where(id => !existingActive.Contains(id)).Order().ToArray();
        activeInvitees.UnionWith(additions);
        if (activeInvitees.Count > CalendarLimits.MaxInvitees)
            return CalendarParticipantUpdateResult.TooManyInvitees;

        if (beforeCommitAsync is not null)
            await beforeCommitAsync(connection, transaction, eventId, newlyAdded, cancellationToken);
        await UpsertResponsesAsync(connection, transaction, eventId,
            newlyAdded,
            CalendarResponseStates.Undecided, cancellationToken);
        if (removed.Count > 0)
        {
            await using var command = Command(connection, transaction,
                Q("UPDATE {schema}.responses SET status=$1 WHERE event_id=$2 AND character_id = ANY($3::bigint[])"),
                CalendarResponseStates.Uninvited, eventId, removed.ToArray());
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        if (afterCommitAsync is not null && (newlyAdded.Length > 0 || removedInvitees.Length > 0))
            await afterCommitAsync(calendarEvent, newlyAdded, removedInvitees, cancellationToken);
        return CalendarParticipantUpdateResult.Updated;
    }

    public async Task<bool> SendResponseAsync(
        long eventId, long ownerId, long characterId, short response, CancellationToken cancellationToken = default,
        Func<CalendarEvent, IReadOnlyCollection<long>, CancellationToken, Task>? afterCommitAsync = null)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var notificationLock = await AcquireEventNotificationLockAsync(connection, eventId, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var exists = Command(connection, transaction,
            Q($"SELECT {EventColumns} FROM {{schema}}.events WHERE event_id=$1 AND owner_id=$2 AND is_deleted=false FOR UPDATE"),
            eventId, ownerId);
        CalendarEvent calendarEvent;
        await using (var eventReader = await exists.ExecuteReaderAsync(cancellationToken))
        {
            if (!await eventReader.ReadAsync(cancellationToken)) return false;
            calendarEvent = ReadEvent(eventReader);
        }
        if (calendarEvent.Tag == CalendarTags.Personal &&
            characterId != ownerId)
        {
            await using var invitation = Command(connection, transaction,
                Q("SELECT status FROM {schema}.responses WHERE event_id=$1 AND character_id=$2"),
                eventId, characterId);
            var statusValue = await invitation.ExecuteScalarAsync(cancellationToken);
            if (statusValue is null ||
                Convert.ToInt16(statusValue, System.Globalization.CultureInfo.InvariantCulture) == CalendarResponseStates.Uninvited)
                return false;
        }

        await UpsertResponseAsync(connection, transaction, eventId, characterId, response, cancellationToken);
        var activeParticipantIds = await ReadActiveParticipantIdsAsync(connection, transaction, eventId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        if (afterCommitAsync is not null)
            await afterCommitAsync(calendarEvent, activeParticipantIds, cancellationToken);
        return true;
    }

    private static async Task<EventNotificationLock> AcquireEventNotificationLockAsync(
        NpgsqlConnection connection, long eventId, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, null,
            "SELECT pg_advisory_lock(hashtextextended('eve.calendar.event.' || $1::text, 0))", eventId);
        await command.ExecuteScalarAsync(cancellationToken);
        return new EventNotificationLock(connection, eventId);
    }

    private async Task<CalendarEvent?> ReadEventAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, long eventId, CancellationToken cancellationToken,
        bool forUpdate = false)
    {
        await using var command = Command(connection, transaction,
            Q($"SELECT {EventColumns} FROM {{schema}}.events WHERE event_id=$1{(forUpdate ? " FOR UPDATE" : string.Empty)}"),
            eventId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadEvent(reader) : null;
    }

    private async Task<IReadOnlyCollection<long>> ReadActiveParticipantIdsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long eventId, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction,
            Q("SELECT character_id FROM {schema}.responses WHERE event_id=$1 AND status <> $2 ORDER BY character_id"),
            eventId, CalendarResponseStates.Uninvited);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var characterIds = new List<long>();
        while (await reader.ReadAsync(cancellationToken)) characterIds.Add(reader.GetInt64(0));
        return characterIds;
    }

    private sealed class EventNotificationLock(NpgsqlConnection connection, long eventId) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await using var command = Command(connection, null,
                "SELECT pg_advisory_unlock(hashtextextended('eve.calendar.event.' || $1::text, 0))", eventId);
            await command.ExecuteScalarAsync();
        }
    }

    private async Task UpsertResponseAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long eventId, long characterId, short response, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction,
            Q("INSERT INTO {schema}.responses (event_id, character_id, status) VALUES ($1,$2,$3) ON CONFLICT (event_id, character_id) DO UPDATE SET status=EXCLUDED.status, updated_at=now()"),
            eventId, characterId, response);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task UpsertResponsesAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long eventId, long[] characterIds, short response,
        CancellationToken cancellationToken)
    {
        if (characterIds.Length == 0) return;
        await using var command = Command(connection, transaction,
            Q("INSERT INTO {schema}.responses (event_id, character_id, status) SELECT $1, invitee.character_id, $3 FROM unnest($2::bigint[]) AS invitee(character_id) ON CONFLICT (event_id, character_id) DO UPDATE SET status=EXCLUDED.status, updated_at=now()"),
            eventId, characterIds, response);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<CalendarResponse>> ReadResponsesAsync(
        NpgsqlDataReader reader, CancellationToken cancellationToken)
    {
        var rows = new List<CalendarResponse>();
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(new(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt16(2)));
        return rows;
    }

    private static CalendarEvent ReadEvent(NpgsqlDataReader reader) => new(
        reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt16(3), reader.GetInt64(4),
        reader.IsDBNull(5) ? null : reader.GetInt32(5), reader.GetString(6), reader.GetString(7),
        reader.GetInt32(8), reader.GetInt64(9), reader.GetBoolean(10), reader.IsDBNull(11) ? null : reader.GetInt32(11));

    private static NpgsqlCommand Command(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql, params object?[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var value in parameters)
            command.Parameters.Add(new NpgsqlParameter { Value = value ?? DBNull.Value });
        return command;
    }
}

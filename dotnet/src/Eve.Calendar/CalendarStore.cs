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
    Task<long> CreateEventAsync(CalendarEventInput input, IReadOnlyCollection<long> invitees, CancellationToken cancellationToken = default);
    Task<bool> EditEventAsync(long eventId, long ownerId, long oldEventDateTime, CalendarEventInput input, CancellationToken cancellationToken = default);
    Task<bool> DeleteEventAsync(long eventId, long ownerId, long dateModified, CancellationToken cancellationToken = default);
    Task<bool> UpdateParticipantsAsync(long eventId, long ownerId, IReadOnlyCollection<long> add, IReadOnlyCollection<long> remove, CancellationToken cancellationToken = default);
    Task<bool> SendResponseAsync(long eventId, long ownerId, long characterId, short response, CancellationToken cancellationToken = default);
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
        CalendarEventInput input, IReadOnlyCollection<long> invitees, CancellationToken cancellationToken = default)
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
        if (input.Tag == CalendarTags.Personal)
        {
            foreach (var characterId in invitees.Where(id => id > 0 && id != input.CreatorId).Distinct())
                await UpsertResponseAsync(connection, transaction, eventId, characterId, CalendarResponseStates.Undecided, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return eventId;
    }

    public async Task<bool> EditEventAsync(
        long eventId, long ownerId, long oldEventDateTime, CalendarEventInput input, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
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

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> DeleteEventAsync(
        long eventId, long ownerId, long dateModified, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = Command(connection, transaction,
            Q("UPDATE {schema}.events SET is_deleted=true, date_modified=$1 WHERE event_id=$2 AND owner_id=$3 AND is_deleted=false"),
            dateModified, eventId, ownerId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0) return false;
        await using var responses = Command(connection, transaction,
            Q("UPDATE {schema}.responses SET status=$1 WHERE event_id=$2 AND status <> $1"),
            CalendarResponseStates.Deleted, eventId);
        await responses.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> UpdateParticipantsAsync(
        long eventId, long ownerId, IReadOnlyCollection<long> add, IReadOnlyCollection<long> remove, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var exists = Command(connection, transaction,
            Q("SELECT 1 FROM {schema}.events WHERE event_id=$1 AND owner_id=$2 AND is_deleted=false FOR UPDATE"),
            eventId, ownerId);
        if (await exists.ExecuteScalarAsync(cancellationToken) is null) return false;

        foreach (var characterId in add.Where(id => id > 0).Distinct())
            await UpsertResponseAsync(connection, transaction, eventId, characterId, CalendarResponseStates.Undecided, cancellationToken);
        foreach (var characterId in remove.Where(id => id > 0).Distinct())
        {
            await using var command = Command(connection, transaction,
                Q("UPDATE {schema}.responses SET status=$1 WHERE event_id=$2 AND character_id=$3"),
                CalendarResponseStates.Uninvited, eventId, characterId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> SendResponseAsync(
        long eventId, long ownerId, long characterId, short response, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var exists = Command(connection, transaction,
            Q("SELECT 1 FROM {schema}.events WHERE event_id=$1 AND owner_id=$2 AND is_deleted=false FOR UPDATE"),
            eventId, ownerId);
        if (await exists.ExecuteScalarAsync(cancellationToken) is null) return false;
        await UpsertResponseAsync(connection, transaction, eventId, characterId, response, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private async Task UpsertResponseAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long eventId, long characterId, short response, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction,
            Q("INSERT INTO {schema}.responses (event_id, character_id, status) VALUES ($1,$2,$3) ON CONFLICT (event_id, character_id) DO UPDATE SET status=EXCLUDED.status, updated_at=now()"),
            eventId, characterId, response);
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

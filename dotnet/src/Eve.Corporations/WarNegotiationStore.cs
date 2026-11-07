using Npgsql;

namespace Eve.Corporations;

/// <summary>PostgreSQL persistence for pending and completed war offers.</summary>
public sealed class WarNegotiationStore
{
    private const string Columns = "war_negotiation_id,war_id,war_negotiation_type_id,owner_id1,owner_id2,declared_by_id,against_id,isk_value,owner_id1_account_key,description,negotiation_state,created_by_character_id,created_date_time,time_accepted,time_declined,time_retracted";
    private readonly NpgsqlDataSource _source;
    private readonly string _schema;

    public WarNegotiationStore(NpgsqlDataSource source, string schema = CorporationStore.DefaultSchema)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(schema, "^[a-z_][a-z0-9_]{0,62}$"))
            throw new ArgumentException($"not a usable schema name: {schema}", nameof(schema));
        _source = source;
        _schema = schema;
    }

    public async Task<IReadOnlyList<WarNegotiation>> PendingForOwnerAsync(long ownerId, CancellationToken token = default)
    {
        if (ownerId <= 0) return [];
        await using var command = _source.CreateCommand($"""
            SELECT {Columns} FROM {_schema}.war_negotiations
            WHERE negotiation_state=0 AND (owner_id1=$1 OR owner_id2=$1 OR declared_by_id=$1 OR against_id=$1)
            ORDER BY war_negotiation_id DESC
            """);
        command.Parameters.Add(new NpgsqlParameter { Value = ownerId });
        await using var reader = await command.ExecuteReaderAsync(token);
        var rows = new List<WarNegotiation>();
        while (await reader.ReadAsync(token)) rows.Add(Read(reader));
        return rows;
    }

    public async Task<WarNegotiation?> GetAsync(long id, CancellationToken token = default)
    {
        await using var command = _source.CreateCommand($"SELECT {Columns} FROM {_schema}.war_negotiations WHERE war_negotiation_id=$1");
        command.Parameters.Add(new NpgsqlParameter { Value = id });
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? Read(reader) : null;
    }

    public async Task<WarNegotiation?> GetAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        long id, bool forUpdate, CancellationToken token = default)
    {
        var lockClause = forUpdate ? " FOR UPDATE" : "";
        await using var command = new NpgsqlCommand(
            $"SELECT {Columns} FROM {_schema}.war_negotiations WHERE war_negotiation_id=$1{lockClause}", connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = id });
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? Read(reader) : null;
    }

    public async Task<WarNegotiation> CreateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        long warId, int typeId, long ownerId1, long ownerId2, long declaredById, long againstId,
        decimal iskValue, int ownerId1AccountKey, string description, long createdByCharacterId,
        long createdDateTime, CancellationToken token = default)
    {
        await using var command = new NpgsqlCommand($"""
            INSERT INTO {_schema}.war_negotiations
                (war_id,war_negotiation_type_id,owner_id1,owner_id2,declared_by_id,against_id,isk_value,
                 owner_id1_account_key,description,negotiation_state,created_by_character_id,created_date_time)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,0,$10,$11)
            RETURNING {Columns}
            """, connection, transaction);
        command.Parameters.AddRange(new NpgsqlParameter[]
        {
            new NpgsqlParameter { Value = warId },
            new NpgsqlParameter { Value = typeId },
            new NpgsqlParameter { Value = ownerId1 },
            new NpgsqlParameter { Value = ownerId2 },
            new NpgsqlParameter { Value = declaredById },
            new NpgsqlParameter { Value = againstId },
            new NpgsqlParameter { Value = iskValue },
            new NpgsqlParameter { Value = ownerId1AccountKey },
            new NpgsqlParameter { Value = description },
            new NpgsqlParameter { Value = createdByCharacterId },
            new NpgsqlParameter { Value = createdDateTime },
        });
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) throw new InvalidOperationException("war negotiation was not returned after insert");
        return Read(reader);
    }

    public async Task<WarNegotiation?> TransitionPendingAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        long id, int state, long atFiletime, CancellationToken token = default)
    {
        if (state is < 1 or > 3) throw new ArgumentOutOfRangeException(nameof(state));
        await using var command = new NpgsqlCommand($"""
            UPDATE {_schema}.war_negotiations
            SET negotiation_state=$2,
                time_accepted=CASE WHEN $2=1 THEN $3 ELSE time_accepted END,
                time_declined=CASE WHEN $2=2 THEN $3 ELSE time_declined END,
                time_retracted=CASE WHEN $2=3 THEN $3 ELSE time_retracted END
            WHERE war_negotiation_id=$1 AND negotiation_state=0
            RETURNING {Columns}
            """, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = id });
        command.Parameters.Add(new NpgsqlParameter { Value = state });
        command.Parameters.Add(new NpgsqlParameter { Value = atFiletime });
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? Read(reader) : null;
    }

    public async Task CreatePeaceTreatiesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        long warId, long declaredById, long againstId, int peaceReason, long createdDateTime,
        long expiryDateTime, CancellationToken token = default)
    {
        await using var command = new NpgsqlCommand($"""
            INSERT INTO {_schema}.peace_treaties
                (war_id,owner_id,other_owner_id,peace_reason,created_date_time,expiry_date_time)
            VALUES ($1,$2,$3,$4,$5,$6),($1,$3,$2,$4,$5,$6)
            ON CONFLICT (war_id,owner_id,other_owner_id,peace_reason) DO NOTHING
            """, connection, transaction);
        command.Parameters.AddRange(new NpgsqlParameter[]
        {
            new() { Value = warId },
            new() { Value = declaredById },
            new() { Value = againstId },
            new() { Value = peaceReason },
            new() { Value = createdDateTime },
            new() { Value = expiryDateTime },
        });
        await command.ExecuteNonQueryAsync(token);
    }

    public async Task<IReadOnlyList<PeaceTreatyRecord>> PeaceTreatiesForOwnerAsync(long ownerId,
        long nowFileTime, CancellationToken token = default)
    {
        if (ownerId <= 0) return [];
        await using var command = _source.CreateCommand($"""
            SELECT peace_treaty_id,war_id,owner_id,other_owner_id,peace_reason,created_date_time,expiry_date_time
            FROM {_schema}.peace_treaties
            WHERE expiry_date_time>$1 AND (owner_id=$2 OR other_owner_id=$2)
            ORDER BY peace_treaty_id DESC
            """);
        command.Parameters.Add(new NpgsqlParameter { Value = nowFileTime });
        command.Parameters.Add(new NpgsqlParameter { Value = ownerId });
        await using var reader = await command.ExecuteReaderAsync(token);
        var rows = new List<PeaceTreatyRecord>();
        while (await reader.ReadAsync(token))
            rows.Add(new(reader.GetInt64(0),reader.GetInt64(1),reader.GetInt64(2),reader.GetInt64(3),reader.GetInt32(4),reader.GetInt64(5),reader.GetInt64(6)));
        return rows;
    }

    private static WarNegotiation Read(NpgsqlDataReader reader) => new(
        reader.GetInt64(0), reader.GetInt64(1), reader.GetInt32(2), reader.GetInt64(3), reader.GetInt64(4),
        reader.GetInt64(5), reader.GetInt64(6), reader.GetDecimal(7), reader.GetInt32(8), reader.GetString(9),
        reader.GetInt32(10), reader.GetInt64(11), reader.GetInt64(12),
        reader.IsDBNull(13) ? null : reader.GetInt64(13),
        reader.IsDBNull(14) ? null : reader.GetInt64(14),
        reader.IsDBNull(15) ? null : reader.GetInt64(15));
}

public sealed record PeaceTreatyRecord(long Id, long WarId, long OwnerId, long OtherOwnerId,
    int PeaceReason, long CreatedDateTime, long ExpiryDateTime);

public sealed record WarNegotiation(
    long Id,
    long WarId,
    int TypeId,
    long OwnerId1,
    long OwnerId2,
    long DeclaredById,
    long AgainstId,
    decimal IskValue,
    int OwnerId1AccountKey,
    string Description,
    int State,
    long CreatedByCharacterId,
    long CreatedDateTime,
    long? TimeAccepted,
    long? TimeDeclined,
    long? TimeRetracted);

using System.Text.Json;
using Eve.Data;
using Eve.Wallet;
using Npgsql;
using NpgsqlTypes;

namespace Eve.Npc;

/// <summary>
/// The bounties that are earned and not yet paid, kept in PostgreSQL so that a restart loses none (the reference keeps them in a file and a durable scheduler
/// that it reconciles at start): a kill is added to the killer's bucket, and a bucket that has come due is paid into the character's wallet in the same
/// transaction that deletes it, with a journal entry under a key of its own, so that it is paid exactly once however it is interrupted. A bucket that cannot be
/// paid (no wallet, the balance ceiling) stays and is tried again at the next payout.
/// </summary>
public sealed class PgBountyStore
{
    public const string DefaultSchema = "npc";

    private readonly NpgsqlDataSource _source;
    private readonly WalletTransactions _wallet;
    private readonly string _schema;
    private readonly Action<string>? _log;

    public PgBountyStore(NpgsqlDataSource source, WalletTransactions wallet, string schema = DefaultSchema, Action<string>? log = null)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(schema, "^[a-z_][a-z0-9_]{0,62}$"))
        {
            throw new ArgumentException($"not a usable schema name: {schema}", nameof(schema));
        }
        _source = source;
        _wallet = wallet;
        _schema = schema;
        _log = log;
    }

    public static IReadOnlyList<Migration> MigrationSet() => Migrations.Embedded(typeof(PgBountyStore).Assembly, "Migrations");

    public static Task<IReadOnlyList<string>> MigrateAsync(NpgsqlDataSource source, string schema = DefaultSchema, CancellationToken cancellationToken = default) =>
        Migrations.ApplyAsync(source, schema, MigrationSet(), cancellationToken);

    private NpgsqlCommand Command(string sql, NpgsqlConnection connection, NpgsqlTransaction? transaction, params object[] values)
    {
        var command = new NpgsqlCommand(sql.Replace("{schema}", _schema), connection, transaction);
        foreach (var value in values)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value });
        }
        return command;
    }

    private static BountyBucket ReadBucket(NpgsqlDataReader reader) => new(
        reader.GetString(0), reader.GetInt64(1), reader.GetInt32(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetDecimal(5), reader.GetInt32(6),
        (JsonSerializer.Deserialize<Dictionary<string, int>>(reader.GetString(7)) ?? []).ToDictionary(pair => int.Parse(pair.Key, System.Globalization.CultureInfo.InvariantCulture), pair => pair.Value),
        reader.GetFieldValue<long[]>(8));

    private const string Columns = "key, character_id, solar_system_id, payout_at_ms, payout_time, amount, kills, npc_types::text, source_references";

    /// <summary>
    /// Adds a kill to the killer's bucket (<c>recordNpcBountyKill</c>): the bucket of the solar system that comes due first, a new one a window from now when
    /// there is none. A victim that is in a bucket already is not counted again.
    /// </summary>
    public async Task<BountyAdded> RecordAsync(
        BountyKill kill, long nowMs, CancellationToken cancellationToken = default, Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task>? inTransaction = null)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        // one character's kills are recorded one at a time, so that two of them do not make two buckets of one window
        await using (var gate = Command("SELECT pg_advisory_xact_lock($1)", connection, transaction, kill.CharacterId))
        {
            await gate.ExecuteNonQueryAsync(cancellationToken);
        }
        if (kill.VictimShipId > 0)
        {
            await using var seen = Command("SELECT payout_time FROM {schema}.pending_bounties WHERE source_references @> ARRAY[$1::bigint] LIMIT 1", connection, transaction, kill.VictimShipId);
            if (await seen.ExecuteScalarAsync(cancellationToken) is long payoutTime)
            {
                return new BountyAdded(kill.EnemyTypeId, kill.Amount, payoutTime, kill.IsModified, AlreadyRecorded: true);
            }
        }
        var pending = new List<long>();
        await using (var windows = Command("SELECT payout_at_ms FROM {schema}.pending_bounties WHERE character_id = $1", connection, transaction, kill.CharacterId))
        await using (var reader = await windows.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                pending.Add(reader.GetInt64(0));
            }
        }
        var empty = NpcBounty.Empty(kill.CharacterId, kill.SolarSystemId, NpcBounty.PayoutAt(pending, nowMs));
        BountyBucket? existing = null;
        await using (var find = Command($"SELECT {Columns} FROM {{schema}}.pending_bounties WHERE key = $1 FOR UPDATE", connection, transaction, empty.Key))
        await using (var reader = await find.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                existing = ReadBucket(reader);
            }
        }
        var bucket = NpcBounty.Add(existing ?? empty, kill);
        await using (var save = Command(
            """
            INSERT INTO {schema}.pending_bounties (key, character_id, solar_system_id, payout_at_ms, payout_time, amount, kills, npc_types, source_references)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9)
            ON CONFLICT (key) DO UPDATE SET amount = EXCLUDED.amount, kills = EXCLUDED.kills, npc_types = EXCLUDED.npc_types, source_references = EXCLUDED.source_references
            """,
            connection, transaction, bucket.Key, bucket.CharacterId, bucket.SolarSystemId, bucket.PayoutAtMs, bucket.PayoutTime, bucket.Amount, bucket.Kills,
            JsonSerializer.Serialize(bucket.NpcTypes), bucket.SourceReferences.ToArray()))
        {
            save.Parameters[7].NpgsqlDbType = NpgsqlDbType.Jsonb;
            await save.ExecuteNonQueryAsync(cancellationToken);
        }
        // what belongs with this kill (the ESS share of it) commits or fails with the bucket, never one without the other
        if (inTransaction is not null)
        {
            await inTransaction(connection, transaction, cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return new BountyAdded(kill.EnemyTypeId, kill.Amount, bucket.PayoutTime, kill.IsModified, AlreadyRecorded: false);
    }

    /// <summary>Every bucket that is waiting, soonest first.</summary>
    public async Task<IReadOnlyList<BountyBucket>> PendingAsync(CancellationToken cancellationToken = default) => await ListAsync(null, cancellationToken);

    /// <summary>The buckets whose time has come at <paramref name="nowMs"/>, soonest first.</summary>
    public async Task<IReadOnlyList<BountyBucket>> DueAsync(long nowMs, CancellationToken cancellationToken = default) => await ListAsync(nowMs, cancellationToken);

    private async Task<IReadOnlyList<BountyBucket>> ListAsync(long? dueAtMs, CancellationToken cancellationToken)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = dueAtMs is { } now
            ? Command($"SELECT {Columns} FROM {{schema}}.pending_bounties WHERE payout_at_ms <= $1 ORDER BY payout_at_ms, key", connection, null, now)
            : Command($"SELECT {Columns} FROM {{schema}}.pending_bounties ORDER BY payout_at_ms, key", connection, null);
        var buckets = new List<BountyBucket>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            buckets.Add(ReadBucket(reader));
        }
        return buckets;
    }

    /// <summary>
    /// Pays one bucket into the wallet of its character and deletes it, in one transaction (<c>payoutBucket</c>); null when it is gone or another payout has it.
    /// A bucket that cannot be paid raises what the wallet raised (<see cref="WalletNotFoundException"/>, <see cref="MoneyLimitException"/>) and stays.
    /// </summary>
    public async Task<BountyPaid?> PayAsync(string key, long nowMs, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        BountyBucket? bucket = null;
        await using (var find = Command($"SELECT {Columns} FROM {{schema}}.pending_bounties WHERE key = $1 FOR UPDATE SKIP LOCKED", connection, transaction, key))
        await using (var reader = await find.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                bucket = ReadBucket(reader);
            }
        }
        if (bucket is null)
        {
            return null;
        }
        var balance = await _wallet.AdjustAsync(
            connection, transaction, bucket.CharacterId, bucket.Amount, bucket.CharacterId, NpcBounty.RefType(bucket.Kills), NpcBounty.Description(bucket), nowMs,
            NpcBounty.OperationKey(bucket.Key), cancellationToken);
        await using (var delete = Command("DELETE FROM {schema}.pending_bounties WHERE key = $1", connection, transaction, bucket.Key))
        {
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return new BountyPaid(bucket.CharacterId, bucket.Amount, bucket.Kills, balance, bucket.Key);
    }

    /// <summary>Pays every bucket that is due at <paramref name="nowMs"/>; one that cannot be paid is logged and left for the next time, and does not hold up the others.</summary>
    public async Task<IReadOnlyList<BountyPaid>> PayDueAsync(long nowMs, CancellationToken cancellationToken = default)
    {
        var paid = new List<BountyPaid>();
        foreach (var bucket in await DueAsync(nowMs, cancellationToken))
        {
            try
            {
                if (await PayAsync(bucket.Key, nowMs, cancellationToken) is { } payment)
                {
                    paid.Add(payment);
                }
            }
            catch (Exception error) when (error is WalletNotFoundException or MoneyLimitException or NpgsqlException or WalletOperationConflictException)
            {
                _log?.Invoke($"the bounty {bucket.Key} of {bucket.Amount} could not be paid and stays: {error.Message}");
            }
        }
        return paid;
    }
}

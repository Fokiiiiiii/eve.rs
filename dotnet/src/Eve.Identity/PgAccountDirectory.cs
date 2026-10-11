using System.Reflection;
using System.Text.RegularExpressions;
using Eve.Data;
using Npgsql;

namespace Eve.Identity;

/// <summary>Accounts in PostgreSQL, in the module's own schema (<c>identity</c> unless told otherwise).</summary>
public sealed partial class PgAccountDirectory : IAccountDirectory, IAccountLookup, IAccountSubscriptions
{
    public const string DefaultSchema = "identity";

    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$")]
    private static partial Regex SchemaName();

    private readonly NpgsqlDataSource _source;
    private readonly AccountOptions _options;
    private readonly string _find;
    private readonly string _create;
    private readonly string _touch;
    private readonly string _info;

    public PgAccountDirectory(NpgsqlDataSource source, AccountOptions options, string schema = DefaultSchema)
    {
        if (!SchemaName().IsMatch(schema))
        {
            throw new ArgumentException($"not a usable schema name: {schema}", nameof(schema));
        }
        _source = source;
        _options = options;
        Schema = schema;
        const string Columns = "id, password_hash, role, banned";
        _find = $"SELECT {Columns} FROM {schema}.accounts WHERE user_name = $1";
        _create = $"INSERT INTO {schema}.accounts (user_name, password_hash, role, country_code, subscription_end_at) VALUES ($1, $2, $3, $4, $5) " +
                  $"ON CONFLICT (user_name) DO NOTHING RETURNING {Columns}";
        _info = $"SELECT id, user_name, created_at, subscription_end_at FROM {schema}.accounts WHERE id = $1";
        _touch = $"UPDATE {schema}.accounts SET last_login_at = now() WHERE id = $1";
    }

    public string Schema { get; }

    /// <summary>The migrations of this module.</summary>
    public static IReadOnlyList<Migration> MigrationSet() => Migrations.Embedded(typeof(PgAccountDirectory).Assembly, "Migrations");

    /// <summary>Creates the schema and brings it up to date.</summary>
    public static Task<IReadOnlyList<string>> MigrateAsync(NpgsqlDataSource source, string schema = DefaultSchema, CancellationToken cancellationToken = default) =>
        Migrations.ApplyAsync(source, schema, MigrationSet(), cancellationToken);

    private sealed record Row(int Id, byte[] Hash, long Role, bool Banned);

    private static async Task<Row?> ReadRowAsync(NpgsqlCommand command, CancellationToken ct)
    {
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new Row(reader.GetInt32(0), (byte[])reader[1], reader.GetInt64(2), reader.GetBoolean(3))
            : null;
    }

    public async ValueTask<AccountInfo?> FindAsync(uint accountId, CancellationToken cancellationToken)
    {
        await using var command = _source.CreateCommand(_info);
        command.Parameters.Add(new NpgsqlParameter { Value = checked((int)accountId) });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new AccountInfo((uint)reader.GetInt32(0), reader.GetString(1), reader.GetFieldValue<DateTimeOffset>(2), reader.GetFieldValue<DateTimeOffset>(3))
            : null;
    }

    // The character selection and the Omega readers are synchronous (a session change or a rule check cannot await); one
    // primary-key read is cheap, so they use the blocking ADO.NET calls of the same data source.
    public DateTimeOffset? SubscriptionEndsAt(uint accountId)
    {
        using var command = _source.CreateCommand($"SELECT subscription_end_at FROM {Schema}.accounts WHERE id = $1");
        command.Parameters.Add(new NpgsqlParameter { Value = checked((int)accountId) });
        using var reader = command.ExecuteReader();
        DateTimeOffset? endsAt = reader.Read() ? reader.GetFieldValue<DateTimeOffset>(0) : null;
        return endsAt;
    }

    public void SetSubscriptionEndsAt(uint accountId, DateTimeOffset endsAt)
    {
        using var command = _source.CreateCommand($"UPDATE {Schema}.accounts SET subscription_end_at = $2 WHERE id = $1");
        command.Parameters.Add(new NpgsqlParameter { Value = checked((int)accountId) });
        command.Parameters.Add(new NpgsqlParameter { Value = endsAt });
        command.ExecuteNonQuery();
    }

    /// <summary>The account's stored end time, or null when the account does not exist.</summary>
    public async Task<DateTimeOffset?> SubscriptionEndsAtAsync(uint accountId, CancellationToken cancellationToken)
    {
        await using var command = _source.CreateCommand($"SELECT subscription_end_at FROM {Schema}.accounts WHERE id = $1");
        command.Parameters.Add(new NpgsqlParameter { Value = checked((int)accountId) });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? reader.GetFieldValue<DateTimeOffset>(0) : null;
    }

    /// <summary>
    /// Extends the account's stored end time inside the caller's transaction, so it is written with the receipt of the purchase
    /// (the account row is locked for the grant). Returns the end time now stored.
    /// </summary>
    public async Task<DateTimeOffset> ExtendSubscriptionAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, uint accountId, TimeSpan extension, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var stored = await LockAccountAsync(connection, transaction, accountId, cancellationToken);
        var endsAt = SubscriptionPolicy.Extend(stored, now, extension);
        if (endsAt != stored)
        {
            await using var update = new NpgsqlCommand($"UPDATE {Schema}.accounts SET subscription_end_at = $2 WHERE id = $1", connection, transaction)
            {
                Parameters = { new() { Value = checked((int)accountId) }, new() { Value = endsAt } },
            };
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        return endsAt;
    }

    /// <summary>The account's extra training queues: each slot with its end time (slots whose time has passed are included).</summary>
    public async Task<IReadOnlyDictionary<int, DateTimeOffset>> TrainingSlotsAsync(uint accountId, CancellationToken cancellationToken)
    {
        await using var command = _source.CreateCommand($"SELECT slot, ends_at FROM {Schema}.account_training_slots WHERE account_id = $1");
        command.Parameters.Add(new NpgsqlParameter { Value = checked((int)accountId) });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var slots = new Dictionary<int, DateTimeOffset>();
        while (await reader.ReadAsync(cancellationToken))
        {
            slots[reader.GetInt16(0)] = reader.GetFieldValue<DateTimeOffset>(1);
        }
        return slots;
    }

    /// <summary>How many of the account's extra training queues run at <paramref name="now"/> (synchronous: the training rules read it).</summary>
    public int ActiveTrainingSlotCount(uint accountId, DateTimeOffset now)
    {
        using var command = _source.CreateCommand($"SELECT count(*) FROM {Schema}.account_training_slots WHERE account_id = $1 AND ends_at > $2");
        command.Parameters.Add(new NpgsqlParameter { Value = checked((int)accountId) });
        command.Parameters.Add(new NpgsqlParameter { Value = now });
        return (int)(long)command.ExecuteScalar()!;
    }

    /// <summary>
    /// Adds one unit of extra training time to a slot (the one named, else the planned one: see <see cref="MultipleTrainingRules.Plan"/>)
    /// inside the caller's transaction. Returns the slot and its new end time.
    /// </summary>
    public async Task<(int Slot, DateTimeOffset EndsAt)> ExtendTrainingSlotAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, uint accountId, int? requested, TimeSpan duration, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await LockAccountAsync(connection, transaction, accountId, cancellationToken);
        var stored = new Dictionary<int, DateTimeOffset>();
        await using (var read = new NpgsqlCommand($"SELECT slot, ends_at FROM {Schema}.account_training_slots WHERE account_id = $1", connection, transaction)
        {
            Parameters = { new() { Value = checked((int)accountId) } },
        })
        await using (var reader = await read.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                stored[reader.GetInt16(0)] = reader.GetFieldValue<DateTimeOffset>(1);
            }
        }
        var (slot, endsAt) = MultipleTrainingRules.Plan(stored, requested, now, duration);
        await using var upsert = new NpgsqlCommand(
            $"INSERT INTO {Schema}.account_training_slots (account_id, slot, ends_at) VALUES ($1, $2, $3) " +
            "ON CONFLICT (account_id, slot) DO UPDATE SET ends_at = EXCLUDED.ends_at", connection, transaction)
        {
            Parameters =
            {
                new() { Value = checked((int)accountId) },
                new() { Value = (short)slot },
                new() { Value = endsAt },
            },
        };
        await upsert.ExecuteNonQueryAsync(cancellationToken);
        return (slot, endsAt);
    }

    /// <summary>Locks the account row for the rest of the transaction and returns its stored end time.</summary>
    private async Task<DateTimeOffset> LockAccountAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, uint accountId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT subscription_end_at FROM {Schema}.accounts WHERE id = $1 FOR UPDATE", connection, transaction)
        {
            Parameters = { new() { Value = checked((int)accountId) } },
        };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException($"Account {accountId} does not exist.");
        }
        // a typed read: ExecuteScalar returns timestamptz as DateTime, which is not the DateTimeOffset the rules use
        return reader.GetFieldValue<DateTimeOffset>(0);
    }

    public async ValueTask<LoginResult> LoginAsync(LoginAttempt attempt, CancellationToken cancellationToken)
    {
        if (!AccountNames.IsAcceptable(attempt.UserName))
        {
            return new LoginRefused(LoginRefused.BadCredentials);
        }
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);

        await using var find = new NpgsqlCommand(_find, connection) { Parameters = { new() { Value = attempt.UserName } } };
        var row = await ReadRowAsync(find, cancellationToken);
        if (row is null)
        {
            if (!_options.AutoCreate)
            {
                return new LoginRefused(LoginRefused.BadCredentials);
            }
            await using var create = new NpgsqlCommand(_create, connection)
            {
                Parameters =
                {
                    new() { Value = attempt.UserName },
                    new() { Value = attempt.PasswordHash.ToArray() },
                    new() { Value = unchecked((long)_options.NewAccountRole) },
                    new() { Value = _options.CountryCode },
                    new() { Value = SubscriptionPolicy.NewAccountSubscriptionEnd },
                },
            };
            // Another login may have created the account since the lookup: then the insert returns nothing.
            row = await ReadRowAsync(create, cancellationToken);
            if (row is null)
            {
                await using var again = new NpgsqlCommand(_find, connection) { Parameters = { new() { Value = attempt.UserName } } };
                row = await ReadRowAsync(again, cancellationToken);
            }
            if (row is null)
            {
                return new LoginRefused(LoginRefused.Unavailable);
            }
        }

        var result = InMemoryAccountDirectory.Decide(row.Banned, row.Hash, (uint)row.Id, unchecked((ulong)row.Role), attempt, _options);
        if (result is LoggedIn)
        {
            await using var touch = new NpgsqlCommand(_touch, connection) { Parameters = { new() { Value = row.Id } } };
            await touch.ExecuteNonQueryAsync(cancellationToken);
        }
        return result;
    }
}

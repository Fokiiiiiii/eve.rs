using System.Reflection;
using System.Text.RegularExpressions;
using Eve.Data;
using Npgsql;

namespace Eve.Identity;

/// <summary>Accounts in PostgreSQL, in the module's own schema (<c>identity</c> unless told otherwise).</summary>
public sealed partial class PgAccountDirectory : IAccountDirectory, IAccountLookup
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
        _create = $"INSERT INTO {schema}.accounts (user_name, password_hash, role, country_code) VALUES ($1, $2, $3, $4) " +
                  $"ON CONFLICT (user_name) DO NOTHING RETURNING {Columns}";
        _info = $"SELECT id, user_name, created_at FROM {schema}.accounts WHERE id = $1";
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
            ? new AccountInfo((uint)reader.GetInt32(0), reader.GetString(1), reader.GetFieldValue<DateTimeOffset>(2))
            : null;
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

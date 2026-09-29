using System.Reflection;
using System.Text.RegularExpressions;
using Npgsql;

namespace Eve.Data;

/// <summary>One step of a module's schema: a name that never changes, and the SQL that runs once.</summary>
public sealed record Migration(string Name, string Sql);

/// <summary>
/// Applies a module's migrations to its own PostgreSQL schema. Each module owns one schema and its
/// migrations; the applied names are recorded in <c>&lt;schema&gt;.migrations</c>. A migration runs in a
/// transaction with <c>search_path</c> set to the module's schema, so its SQL uses plain table names. Two
/// processes starting together take turns (an advisory lock), and a migration that was applied is never run
/// again, so a released migration must not be edited: add another.
/// </summary>
public static partial class Migrations
{
    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$")]
    private static partial Regex SchemaName();

    /// <summary>Applies the migrations that are not recorded yet, in order. Returns the names it applied.</summary>
    public static async Task<IReadOnlyList<string>> ApplyAsync(
        NpgsqlDataSource source, string schema, IEnumerable<Migration> migrations, CancellationToken cancellationToken = default)
    {
        if (!SchemaName().IsMatch(schema))
        {
            throw new ArgumentException($"not a usable schema name: {schema}", nameof(schema));
        }
        var ordered = migrations.OrderBy(m => m.Name, StringComparer.Ordinal).ToList();
        var duplicate = ordered.GroupBy(m => m.Name).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException($"migration {duplicate.Key} appears twice", nameof(migrations));
        }

        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await Run(connection, transaction, $"SELECT pg_advisory_xact_lock(hashtext('eve.migrations.{schema}'))", cancellationToken);
        await Run(connection, transaction, $"CREATE SCHEMA IF NOT EXISTS {schema}", cancellationToken);
        await Run(connection, transaction, $"CREATE TABLE IF NOT EXISTS {schema}.migrations (name text PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now())", cancellationToken);

        var applied = new HashSet<string>(StringComparer.Ordinal);
        await using (var read = new NpgsqlCommand($"SELECT name FROM {schema}.migrations", connection, transaction))
        await using (var reader = await read.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                applied.Add(reader.GetString(0));
            }
        }

        var done = new List<string>();
        foreach (var migration in ordered.Where(m => !applied.Contains(m.Name)))
        {
            await Run(connection, transaction, $"SET LOCAL search_path TO {schema}", cancellationToken);
            await Run(connection, transaction, migration.Sql, cancellationToken);
            await using var record = new NpgsqlCommand($"INSERT INTO {schema}.migrations (name) VALUES ($1)", connection, transaction)
            {
                Parameters = { new NpgsqlParameter { Value = migration.Name } },
            };
            await record.ExecuteNonQueryAsync(cancellationToken);
            done.Add(migration.Name);
        }
        await transaction.CommitAsync(cancellationToken);
        return done;
    }

    private static async Task Run(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>The <c>*.sql</c> files embedded in <paramref name="assembly"/> under <paramref name="folder"/>, named by file.</summary>
    public static IReadOnlyList<Migration> Embedded(Assembly assembly, string folder)
    {
        var prefix = folder.Trim('.') + ".";
        var list = new List<Migration>();
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            var name = resource;
            var at = name.IndexOf(prefix, StringComparison.Ordinal);
            if (at < 0 || !name.EndsWith(".sql", StringComparison.Ordinal))
            {
                continue;
            }
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var text = new StreamReader(stream);
            list.Add(new Migration(name[(at + prefix.Length)..^".sql".Length], text.ReadToEnd()));
        }
        return list;
    }
}

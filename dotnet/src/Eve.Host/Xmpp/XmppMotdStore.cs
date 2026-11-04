using System.Text.RegularExpressions;
using Npgsql;

namespace Eve.Host.Xmpp;

/// <summary>Stores corporation and alliance room subjects across Host restarts.</summary>
internal sealed class XmppMotdStore
{
    private readonly NpgsqlDataSource _source;
    private readonly string _schema;

    private XmppMotdStore(NpgsqlDataSource source, string schema, IReadOnlyDictionary<string, string> roomMotds)
    {
        _source = source;
        _schema = schema;
        RoomMotds = roomMotds;
    }

    public IReadOnlyDictionary<string, string> RoomMotds { get; }

    public static async Task<XmppMotdStore> OpenAsync(NpgsqlDataSource source, string schema, CancellationToken cancellationToken = default)
    {
        if (!Regex.IsMatch(schema, "^[a-z_][a-z0-9_]{0,62}$"))
            throw new ArgumentException($"not a usable schema name: {schema}", nameof(schema));
        await using (var createSchema = source.CreateCommand($"CREATE SCHEMA IF NOT EXISTS {schema}"))
            await createSchema.ExecuteNonQueryAsync(cancellationToken);
        await using (var createTable = source.CreateCommand($"CREATE TABLE IF NOT EXISTS {schema}.room_motds (room_name text PRIMARY KEY CHECK (char_length(room_name) BETWEEN 1 AND 96), motd text NOT NULL, updated_ms bigint NOT NULL DEFAULT ((extract(epoch FROM clock_timestamp()) * 1000)::bigint))"))
            await createTable.ExecuteNonQueryAsync(cancellationToken);

        var motds = new Dictionary<string, string>(StringComparer.Ordinal);
        await using (var command = source.CreateCommand($"SELECT room_name,motd FROM {schema}.room_motds"))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                motds.Add(reader.GetString(0), reader.GetString(1));
        }
        return new XmppMotdStore(source, schema, motds);
    }

    public async Task SetAsync(string room, string motd, CancellationToken cancellationToken = default)
    {
        await using var command = _source.CreateCommand($"INSERT INTO {_schema}.room_motds(room_name,motd) VALUES ($1,$2) ON CONFLICT(room_name) DO UPDATE SET motd=EXCLUDED.motd,updated_ms=((extract(epoch FROM clock_timestamp()) * 1000)::bigint)");
        command.Parameters.AddWithValue(room);
        command.Parameters.AddWithValue(motd);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

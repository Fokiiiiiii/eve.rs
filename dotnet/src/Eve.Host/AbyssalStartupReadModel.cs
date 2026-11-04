using Eve.Content;
using Npgsql;

namespace Eve.Host;

/// <summary>Reads active and terminal runs for participant reconciliation and replay-safe scene/item cleanup.</summary>
public sealed class AbyssalStartupReadModel(ContentRuntime content, string schema)
{
    public async Task<IReadOnlyList<ContentInstance>> ReadAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken = default)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(schema, "^[a-z_][a-z0-9_]{0,62}$"))
            throw new ArgumentException("Invalid ContentRuntime schema.", nameof(schema));
        var ids = new List<long>();
        await using (var command = new NpgsqlCommand($"""
            SELECT id FROM {schema}.instances
            WHERE kind = $1 AND state IN ('active', 'completed', 'failed', 'expired', 'abandoned')
            ORDER BY id
            """, connection, transaction))
        {
            command.Parameters.AddWithValue(AbyssalContentRuntime.Kind);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) ids.Add(reader.GetInt64(0));
        }
        var result = new List<ContentInstance>();
        foreach (var id in ids)
            if (await content.GetInTransactionAsync(connection, transaction, id, cancellationToken) is { } instance)
                result.Add(instance);
        return result;
    }
}

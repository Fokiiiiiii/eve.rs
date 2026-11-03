using System.Text.Json.Nodes;
using Eve.Events;
using Eve.GameStore;
using Npgsql;

namespace Eve.Host;

internal static class WarSovereigntyEvents
{
    internal const string Topic = "war.sovereignty_owners_changed";
    internal sealed record OwnersChanged(long[] Corporations, long[] Alliances);

    internal static async Task BeforeApplyAsync(EventBus events, string schema, NpgsqlConnection connection,
        NpgsqlTransaction transaction, PersistenceOperation operation, CancellationToken token)
    {
        if (operation.Table != "sovereignty") return;
        var keys = operation.Upserts.Select(row => row.Key).Concat(operation.Deletes)
            .Where(key => key is "systems" or "skyhooks" ||
                key.StartsWith("systems" + GameStoreTables.RowKeySeparator, StringComparison.Ordinal) ||
                key.StartsWith("skyhooks" + GameStoreTables.RowKeySeparator, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal).ToArray();
        if (keys.Length == 0) return;
        var previous = new List<KeyValuePair<string, JsonNode?>>();
        var next = new List<KeyValuePair<string, JsonNode?>>();
        using var builder = new NpgsqlCommandBuilder();
        await using (var command = new NpgsqlCommand(
            $"SELECT key, doc::text FROM {builder.QuoteIdentifier(schema)}.sovereignty WHERE key = ANY($1)", connection, transaction))
        {
            command.Parameters.AddWithValue(keys);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
                previous.Add(new(reader.GetString(0), JsonNode.Parse(reader.GetString(1))));
        }
        foreach (var row in operation.Upserts.Where(row => keys.Contains(row.Key)))
            next.Add(new(row.Key, JsonNode.Parse(row.Json)));
        var (oldCorporations, oldAlliances) = CharacterServicesComposition.WarSovereigntyOwners(RowCodec.Assemble("sovereignty", previous));
        var (newCorporations, newAlliances) = CharacterServicesComposition.WarSovereigntyOwners(RowCodec.Assemble("sovereignty", next));
        if (oldCorporations.SetEquals(newCorporations) && oldAlliances.SetEquals(newAlliances)) return;
        oldCorporations.UnionWith(newCorporations);
        oldAlliances.UnionWith(newAlliances);
        await events.PublishInTransactionAsync(connection, transaction, Topic,
            new OwnersChanged(oldCorporations.ToArray(), oldAlliances.ToArray()),
            $"sovereignty-operation:{operation.OperationId}", token);
    }
}

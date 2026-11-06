using System.Text.Json.Nodes;
using Eve.Data;
using Npgsql;

namespace Eve.Structures;

public sealed partial class StructureStore
{
    /// <summary>Updates fields kept in the structure's legacy settings document without replacing other owner data.</summary>
    public Task<int> SetDocFieldsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long structureId, JsonObject fields, CancellationToken cancellationToken = default) =>
        Sql.ExecuteAsync(connection, transaction,
            Q("UPDATE {schema}.structures SET doc = doc || $2::jsonb, version = version + 1 WHERE structure_id = $1 AND destroyed_ms = 0"),
            cancellationToken, structureId, fields.ToJsonString());
}

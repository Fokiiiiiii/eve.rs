using System.Text.Json;
using Eve.Data;
using Npgsql;

namespace Eve.Inventory.Mutation;

/// <summary>
/// What a mutated item was made of: the mutaplasmid and the input consumed to make it, the rolled multiplier of each mutated
/// attribute, the source base value it was applied to, and the value it gave. The item is the result of the mutation.
/// </summary>
public sealed record ItemMutationRecord(
    long ItemId,
    int MutatorTypeId,
    long MutatorItemId,
    int SourceTypeId,
    long SourceItemId,
    long CharacterId,
    long CreatedAtMs,
    IReadOnlyDictionary<int, double> Rolls,
    IReadOnlyDictionary<int, double> BaseValues,
    IReadOnlyDictionary<int, double> MutatedValues);

/// <summary>
/// The provenance of mutated items, in <c>inventory.item_mutations</c> (migration <c>0005_item_mutation</c>, applied with the
/// item store's migrations because the table references <c>items</c>). One row per mutated item. A row is written in the caller's
/// transaction, together with the item it describes, and it is read back as stored: it is never recomputed from the static data
/// and never re-rolled.
/// </summary>
public sealed class ItemMutationStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly NpgsqlDataSource _source;
    private readonly string _schema;

    public ItemMutationStore(NpgsqlDataSource source, string schema = PgItemStore.DefaultSchema)
    {
        _source = source;
        _schema = Sql.CheckSchema(schema);
    }

    private string Q(string text) => Sql.In(_schema, text);

    /// <summary>Records the provenance of a mutated item, inside the caller's transaction.</summary>
    public async Task InsertAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, ItemMutationRecord record, CancellationToken cancellationToken = default)
    {
        if (record.ItemId <= 0 || record.CharacterId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(record), "a mutated item and its character are positive ids");
        }
        if (record.Rolls.Count == 0 || record.Rolls.Count != record.BaseValues.Count || record.Rolls.Count != record.MutatedValues.Count)
        {
            throw new ArgumentException("a mutation records one roll, one base value and one value for each mutated attribute", nameof(record));
        }
        await Sql.ExecuteAsync(connection, transaction, Q("""
            INSERT INTO {schema}.item_mutations (item_id, mutator_type_id, mutator_item_id, source_type_id, source_item_id, character_id, created_at_ms,
                                                 rolls, base_values, mutated_values)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8::text::jsonb, $9::text::jsonb, $10::text::jsonb)
            """), cancellationToken,
            record.ItemId, record.MutatorTypeId, record.MutatorItemId, record.SourceTypeId, record.SourceItemId, record.CharacterId, record.CreatedAtMs,
            Serialize(record.Rolls), Serialize(record.BaseValues), Serialize(record.MutatedValues));
    }

    /// <summary>The provenance of an item, or <c>null</c> when the item is not a mutated one.</summary>
    public async Task<ItemMutationRecord?> GetAsync(long itemId, CancellationToken cancellationToken = default)
    {
        await using var command = _source.CreateCommand(Q("""
            SELECT item_id, mutator_type_id, mutator_item_id, source_type_id, source_item_id, character_id, created_at_ms,
                   rolls::text, base_values::text, mutated_values::text
            FROM {schema}.item_mutations WHERE item_id = $1
            """));
        command.Parameters.Add(new NpgsqlParameter { Value = itemId });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }
        return new ItemMutationRecord(
            reader.GetInt64(0), reader.GetInt32(1), reader.GetInt64(2), reader.GetInt32(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6),
            Deserialize(reader.GetString(7)), Deserialize(reader.GetString(8)), Deserialize(reader.GetString(9)));
    }

    private static string Serialize(IReadOnlyDictionary<int, double> values) => JsonSerializer.Serialize(values, Json);

    private static IReadOnlyDictionary<int, double> Deserialize(string json) =>
        JsonSerializer.Deserialize<Dictionary<int, double>>(json, Json)
        ?? throw new InvalidDataException("a stored mutation value is not an object");
}

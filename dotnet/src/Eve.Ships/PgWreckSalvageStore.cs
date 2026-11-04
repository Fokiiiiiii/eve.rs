using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Eve.Ships;

/// <summary>The salvage bundle fixed to a wreck when that wreck is created.</summary>
public sealed class PgWreckSalvageStore
{
    private readonly NpgsqlDataSource _source;
    private readonly string _schema;

    public PgWreckSalvageStore(NpgsqlDataSource source, string schema = PgSpaceItemStore.DefaultSchema)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(schema, "^[a-z_][a-z0-9_]{0,62}$"))
        {
            throw new ArgumentException($"not a usable schema name: {schema}", nameof(schema));
        }
        _source = source;
        _schema = schema;
    }

    public async Task<IReadOnlyList<(int TypeId, long Quantity)>?> GetAsync(long wreckId, CancellationToken cancellationToken = default)
    {
        await using var command = _source.CreateCommand($"SELECT rewards::text FROM {_schema}.wreck_salvage_outcomes WHERE wreck_id = $1");
        command.Parameters.AddWithValue(wreckId);
        var json = (string?)await command.ExecuteScalarAsync(cancellationToken);
        return json is null ? null : Read(json);
    }

    public async Task<IReadOnlyList<(int TypeId, long Quantity)>?> GetAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long wreckId, CancellationToken cancellationToken = default)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT rewards::text FROM {_schema}.wreck_salvage_outcomes WHERE wreck_id = $1 FOR UPDATE",
            connection, transaction);
        command.Parameters.AddWithValue(wreckId);
        var json = (string?)await command.ExecuteScalarAsync(cancellationToken);
        return json is null ? null : Read(json);
    }

    public async Task SaveAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long wreckId,
        IReadOnlyList<(int TypeId, long Quantity)> rewards, CancellationToken cancellationToken = default)
    {
        Validate(rewards);
        var json = JsonSerializer.Serialize(rewards.Select(reward => new Reward(reward.TypeId, reward.Quantity)));
        await using var command = new NpgsqlCommand(
            $"INSERT INTO {_schema}.wreck_salvage_outcomes (wreck_id, rewards) VALUES ($1, $2)",
            connection, transaction);
        command.Parameters.AddWithValue(wreckId);
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = json });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> DeleteAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long wreckId, CancellationToken cancellationToken = default)
    {
        await using var command = new NpgsqlCommand(
            $"DELETE FROM {_schema}.wreck_salvage_outcomes WHERE wreck_id = $1",
            connection, transaction);
        command.Parameters.AddWithValue(wreckId);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    private static IReadOnlyList<(int TypeId, long Quantity)> Read(string json)
    {
        var rewards = JsonSerializer.Deserialize<Reward[]>(json)
            ?? throw new InvalidDataException("A wreck salvage outcome is not a product list.");
        Validate(rewards.Select(reward => (reward.TypeId, reward.Quantity)));
        return rewards.Select(reward => (reward.TypeId, reward.Quantity)).ToArray();
    }

    private static void Validate(IEnumerable<(int TypeId, long Quantity)> rewards)
    {
        if (rewards.Any(reward => reward.TypeId <= 0 || reward.Quantity <= 0))
        {
            throw new InvalidDataException("A wreck salvage outcome contains an invalid product.");
        }
    }

    private sealed record Reward(int TypeId, long Quantity);
}

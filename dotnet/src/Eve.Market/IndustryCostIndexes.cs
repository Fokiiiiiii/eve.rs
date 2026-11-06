using System.Text.Json;
using Npgsql;

namespace Eve.Market;

/// <summary>One system's cost index for one industry activity, as ESI gives it (a fraction: 0.05 is 5 percent).</summary>
public sealed record IndustryCostIndexRow(long SolarSystemId, int ActivityId, double CostIndex);

/// <summary>The ESI activity names of the cost indices, mapped to the industry activity ids the client and the SDE use.</summary>
public static class EsiIndustryActivities
{
    public static int? ActivityId(string name) => name switch
    {
        "manufacturing" => 1,
        "researching_time_efficiency" => 3,
        "researching_material_efficiency" => 4,
        "copying" => 5,
        "reverse_engineering" => 7,
        "invention" => 8,
        "reaction" => 9,
        _ => null,
    };
}

/// <summary>The public ESI system cost indices (<c>GET /industry/systems/</c>, tranquility).</summary>
public sealed class EsiIndustrySystems(HttpClient http, Uri baseUri)
{
    public static readonly Uri DefaultBase = new("https://esi.evetech.net/latest/");

    public async Task<IReadOnlyList<IndustryCostIndexRow>> FetchAsync(CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync(new Uri(baseUri, "industry/systems/?datasource=tranquility"), cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new FormatException("the industry systems are not a list");
        var rows = new List<IndustryCostIndexRow>();
        foreach (var system in document.RootElement.EnumerateArray())
        {
            if (!system.TryGetProperty("solar_system_id", out var systemElement) || !systemElement.TryGetInt64(out var systemId) || systemId <= 0) continue;
            if (!system.TryGetProperty("cost_indices", out var indices) || indices.ValueKind != JsonValueKind.Array) continue;
            foreach (var index in indices.EnumerateArray())
            {
                if (!index.TryGetProperty("activity", out var activity) || activity.ValueKind != JsonValueKind.String) continue;
                if (EsiIndustryActivities.ActivityId(activity.GetString()!) is not { } activityId) continue;
                if (!index.TryGetProperty("cost_index", out var value) || value.ValueKind != JsonValueKind.Number) continue;
                var rate = value.GetDouble();
                if (!double.IsFinite(rate) || rate < 0 || rate > 1) continue;
                rows.Add(new IndustryCostIndexRow(systemId, activityId, rate));
            }
        }
        return rows;
    }
}

/// <summary>The stored system cost indices in PostgreSQL (the market schema), and the time of the last full fetch.</summary>
public sealed class IndustryCostIndexStore(NpgsqlDataSource source, string schema = MarketStore.DefaultSchema)
{
    public async Task<double?> ReadAsync(long solarSystemId, int activityId, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"SELECT cost_index FROM {schema}.industry_cost_indices WHERE solar_system_id = $1 AND activity_id = $2", connection)
        {
            Parameters = { new() { Value = solarSystemId }, new() { Value = activityId } },
        };
        return await command.ExecuteScalarAsync(cancellationToken) is double rate ? rate : null;
    }

    public async Task<IReadOnlyDictionary<long, double>> ReadActivityAsync(int activityId, CancellationToken cancellationToken = default)
    {
        var rates = new Dictionary<long, double>();
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"SELECT solar_system_id, cost_index FROM {schema}.industry_cost_indices WHERE activity_id = $1", connection)
        {
            Parameters = { new() { Value = activityId } },
        };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) rates[reader.GetInt64(0)] = reader.GetDouble(1);
        return rates;
    }

    /// <summary>When the last full fetch was (unix ms), or null when nothing was fetched yet.</summary>
    public async Task<long?> FetchedAtAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"SELECT fetched_ms FROM {schema}.industry_cost_index_state WHERE id = 1", connection);
        return await command.ExecuteScalarAsync(cancellationToken) is long at ? at : null;
    }

    /// <summary>Replaces every stored index with <paramref name="rows"/> in one transaction, and stamps the fetch time.</summary>
    public async Task ReplaceAsync(IReadOnlyList<IndustryCostIndexRow> rows, long fetchedMs, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var clear = new NpgsqlCommand($"DELETE FROM {schema}.industry_cost_indices", connection, transaction))
            await clear.ExecuteNonQueryAsync(cancellationToken);
        if (rows.Count > 0)
        {
            await using var insert = new NpgsqlCommand($"""
                INSERT INTO {schema}.industry_cost_indices(solar_system_id, activity_id, cost_index)
                SELECT * FROM unnest($1::bigint[], $2::integer[], $3::double precision[])
                """, connection, transaction)
            {
                Parameters =
                {
                    new() { Value = rows.Select(row => row.SolarSystemId).ToArray() },
                    new() { Value = rows.Select(row => row.ActivityId).ToArray() },
                    new() { Value = rows.Select(row => row.CostIndex).ToArray() },
                },
            };
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var stamp = new NpgsqlCommand(
            $"INSERT INTO {schema}.industry_cost_index_state(id, fetched_ms) VALUES (1, $1) ON CONFLICT (id) DO UPDATE SET fetched_ms = EXCLUDED.fetched_ms", connection, transaction)
        {
            Parameters = { new() { Value = fetchedMs } },
        })
        {
            await stamp.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }
}

/// <summary>
/// The stored system cost indices, refreshed from ESI at most once a day. With no ESI client (EVE_INDUSTRY_INDEX_SEED=skip)
/// it answers what is stored and never calls out. A failed fetch keeps what is stored, and a system or activity with no
/// stored index is absent from the answer: nothing is invented (the caller applies its configured fallback).
/// </summary>
public sealed class IndustryCostIndexCache(IndustryCostIndexStore store, EsiIndustrySystems? esi, Func<long> nowMs, Action<string>? log = null)
{
    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromDays(1);

    private readonly SemaphoreSlim _refresh = new(1, 1);

    public TimeSpan MaxAge { get; init; } = DefaultMaxAge;

    public async Task<double?> GetAsync(long solarSystemId, int activityId, CancellationToken cancellationToken = default)
    {
        await RefreshIfStaleAsync(cancellationToken);
        return await store.ReadAsync(solarSystemId, activityId, cancellationToken);
    }

    public async Task<IReadOnlyDictionary<long, double>> GetActivityAsync(int activityId, CancellationToken cancellationToken = default)
    {
        await RefreshIfStaleAsync(cancellationToken);
        return await store.ReadActivityAsync(activityId, cancellationToken);
    }

    private async Task RefreshIfStaleAsync(CancellationToken cancellationToken)
    {
        if (esi is null || !await IsStaleAsync(cancellationToken)) return;
        await _refresh.WaitAsync(cancellationToken);
        try
        {
            if (!await IsStaleAsync(cancellationToken)) return;
            try
            {
                var rows = await esi.FetchAsync(cancellationToken);
                await store.ReplaceAsync(rows, nowMs(), cancellationToken);
            }
            catch (Exception exception) when (exception is HttpRequestException or JsonException or FormatException or TaskCanceledException { CancellationToken.IsCancellationRequested: false })
            {
                log?.Invoke($"industry cost indices were not refreshed from ESI: {exception.Message}");
            }
        }
        finally
        {
            _refresh.Release();
        }
    }

    private async Task<bool> IsStaleAsync(CancellationToken cancellationToken) =>
        await store.FetchedAtAsync(cancellationToken) is not { } at || nowMs() - at >= (long)MaxAge.TotalMilliseconds;
}

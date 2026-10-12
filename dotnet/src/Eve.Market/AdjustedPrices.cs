using System.Globalization;
using System.Text.Json;
using Npgsql;

namespace Eve.Market;

/// <summary>One type's prices as ESI gives them. A type with no adjusted price is not in the list.</summary>
public sealed record AdjustedPriceRow(int TypeId, double AdjustedPrice, double? AveragePrice);

/// <summary>The official ESI market prices (<c>GET /markets/prices/</c>, tranquility): the adjusted price CCP uses for taxes and job costs.</summary>
public sealed class EsiAdjustedPrices(HttpClient http, Uri baseUri)
{
    public static readonly Uri DefaultBase = new("https://esi.evetech.net/latest/");

    public async Task<IReadOnlyList<AdjustedPriceRow>> FetchAsync(CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync(new Uri(baseUri, "markets/prices/?datasource=tranquility"), cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new FormatException("the market prices are not a list");
        var rows = new List<AdjustedPriceRow>();
        foreach (var row in document.RootElement.EnumerateArray())
        {
            if (!row.TryGetProperty("type_id", out var typeElement) || !typeElement.TryGetInt32(out var typeId) || typeId <= 0) continue;
            if (!row.TryGetProperty("adjusted_price", out var adjusted) || adjusted.ValueKind != JsonValueKind.Number) continue;
            double? average = row.TryGetProperty("average_price", out var averageElement) && averageElement.ValueKind == JsonValueKind.Number
                ? averageElement.GetDouble() : null;
            rows.Add(new AdjustedPriceRow(typeId, adjusted.GetDouble(), average));
        }
        return rows;
    }
}

/// <summary>The adjusted prices in PostgreSQL (the market schema): one row per type, and the time of the last full fetch.</summary>
public sealed class AdjustedPriceStore(NpgsqlDataSource source, string schema = MarketStore.DefaultSchema)
{
    public async Task<IReadOnlyDictionary<int, double>> ReadAsync(IReadOnlyCollection<int> typeIds, CancellationToken cancellationToken = default)
    {
        var prices = new Dictionary<int, double>();
        if (typeIds.Count == 0) return prices;
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"SELECT type_id, adjusted_price FROM {schema}.adjusted_prices WHERE type_id = ANY($1)", connection)
        {
            Parameters = { new() { Value = typeIds.Distinct().ToArray() } },
        };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) prices[reader.GetInt32(0)] = reader.GetDouble(1);
        return prices;
    }

    /// <summary>When the last full fetch was (unix ms), or null when nothing was fetched yet.</summary>
    public async Task<long?> FetchedAtAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"SELECT fetched_ms FROM {schema}.adjusted_price_state WHERE id = 1", connection);
        return await command.ExecuteScalarAsync(cancellationToken) is long at ? at : null;
    }

    /// <summary>Replaces every stored price with <paramref name="rows"/> in one transaction, and stamps the fetch time.</summary>
    public async Task ReplaceAsync(IReadOnlyList<AdjustedPriceRow> rows, long fetchedMs, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var clear = new NpgsqlCommand($"DELETE FROM {schema}.adjusted_prices", connection, transaction))
            await clear.ExecuteNonQueryAsync(cancellationToken);
        foreach (var row in rows)
        {
            await using var insert = new NpgsqlCommand(
                $"INSERT INTO {schema}.adjusted_prices(type_id, adjusted_price, average_price) VALUES ($1, $2, $3)", connection, transaction)
            {
                Parameters =
                {
                    new() { Value = row.TypeId }, new() { Value = row.AdjustedPrice },
                    new() { Value = (object?)row.AveragePrice ?? DBNull.Value },
                },
            };
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var stamp = new NpgsqlCommand(
            $"INSERT INTO {schema}.adjusted_price_state(id, fetched_ms) VALUES (1, $1) ON CONFLICT (id) DO UPDATE SET fetched_ms = EXCLUDED.fetched_ms", connection, transaction)
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
/// The adjusted prices a caller reads, refreshed from ESI at most once a day. With no ESI client (the offline switch,
/// EVE_ADJUSTED_PRICE_SEED=skip) it answers what is stored and never calls out. A failed fetch keeps what is stored, and a type
/// with no stored price is absent from the answer: nothing is invented.
/// </summary>
public sealed class AdjustedPriceCache(AdjustedPriceStore store, EsiAdjustedPrices? esi, Func<long> nowMs, Action<string>? log = null)
{
    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromDays(1);

    private readonly SemaphoreSlim _refresh = new(1, 1);

    public TimeSpan MaxAge { get; init; } = DefaultMaxAge;

    public async Task<IReadOnlyDictionary<int, double>> GetAsync(IReadOnlyCollection<int> typeIds, CancellationToken cancellationToken = default)
    {
        if (esi is not null && await IsStaleAsync(cancellationToken))
        {
            await _refresh.WaitAsync(cancellationToken);
            try
            {
                if (await IsStaleAsync(cancellationToken))
                {
                    try
                    {
                        var rows = await esi.FetchAsync(cancellationToken);
                        await store.ReplaceAsync(rows, nowMs(), cancellationToken);
                    }
                    catch (Exception exception) when (exception is HttpRequestException or JsonException or FormatException or TaskCanceledException { CancellationToken.IsCancellationRequested: false })
                    {
                        log?.Invoke($"adjusted prices were not refreshed from ESI: {exception.Message}");
                    }
                }
            }
            finally
            {
                _refresh.Release();
            }
        }
        return await store.ReadAsync(typeIds, cancellationToken);
    }

    private async Task<bool> IsStaleAsync(CancellationToken cancellationToken) =>
        await store.FetchedAtAsync(cancellationToken) is not { } at || nowMs() - at >= (long)MaxAge.TotalMilliseconds;
}

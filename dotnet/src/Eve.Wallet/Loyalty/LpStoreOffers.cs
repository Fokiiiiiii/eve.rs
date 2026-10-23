using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using Npgsql;

namespace Eve.Wallet;

/// <summary>An item an offer takes from the buyer, per offer.</summary>
public sealed record LpStoreRequirement(long TypeId, long Quantity);

/// <summary>
/// One offer of a corporation's loyalty store, as ESI returns it. The offer has no standing condition: ESI does not return one, so
/// none is held here (the reference's <c>requiredStandings</c> is absent for these offers).
/// </summary>
public sealed record LpStoreOffer(
    long CorporationId,
    long OfferId,
    long TypeId,
    long Quantity,
    long LpCost,
    long IskCost,
    long AkCost,
    IReadOnlyList<LpStoreRequirement> RequiredItems);

/// <summary>What the store holds for a corporation: its offers, and when they were fetched (null when never).</summary>
public sealed record LpStoreCatalogRead(IReadOnlyList<LpStoreOffer> Offers, DateTimeOffset? FetchedAt);

/// <summary>The offers of loyalty stores in PostgreSQL (migration 007, the wallet schema).</summary>
public sealed class LpStoreOfferStore
{
    private readonly NpgsqlDataSource _source;
    private readonly string _schema;

    public LpStoreOfferStore(NpgsqlDataSource source, string schema = PgWalletStore.DefaultSchema)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(schema, "^[a-z_][a-z0-9_]{0,62}$"))
        {
            throw new ArgumentException($"not a usable schema name: {schema}", nameof(schema));
        }
        _source = source;
        _schema = schema;
    }

    /// <summary>The corporation's offers in offer order, with their required items, and when they were fetched.</summary>
    public async Task<LpStoreCatalogRead> ReadAsync(long corporationId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        var requirements = new Dictionary<long, List<LpStoreRequirement>>();
        await using (var command = new NpgsqlCommand(
            $"SELECT offer_id, type_id, quantity FROM {_schema}.lp_store_offer_requirements WHERE corporation_id = $1 ORDER BY offer_id, type_id",
            connection)
        {
            Parameters = { new() { Value = corporationId } },
        })
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var offerId = reader.GetInt64(0);
                if (!requirements.TryGetValue(offerId, out var list)) requirements[offerId] = list = [];
                list.Add(new LpStoreRequirement(reader.GetInt64(1), reader.GetInt64(2)));
            }
        }

        var offers = new List<LpStoreOffer>();
        await using (var command = new NpgsqlCommand(
            $"SELECT offer_id, type_id, quantity, lp_cost, isk_cost, ak_cost FROM {_schema}.lp_store_offers WHERE corporation_id = $1 ORDER BY offer_id",
            connection)
        {
            Parameters = { new() { Value = corporationId } },
        })
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var offerId = reader.GetInt64(0);
                offers.Add(new LpStoreOffer(corporationId, offerId, reader.GetInt64(1), reader.GetInt64(2),
                    reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5),
                    requirements.TryGetValue(offerId, out var items) ? items : []));
            }
        }

        DateTimeOffset? fetchedAt = null;
        await using (var command = new NpgsqlCommand(
            $"SELECT fetched_at FROM {_schema}.lp_store_refresh WHERE corporation_id = $1", connection)
        {
            Parameters = { new() { Value = corporationId } },
        })
        {
            // timestamptz comes back as a UTC DateTime from a scalar read
            if (await command.ExecuteScalarAsync(cancellationToken) is DateTime at) fetchedAt = new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Utc));
        }
        return new LpStoreCatalogRead(offers, fetchedAt);
    }

    /// <summary>Replaces the corporation's offers with <paramref name="offers"/> and records when they were fetched, in one transaction.</summary>
    public async Task ReplaceAsync(long corporationId, IReadOnlyList<LpStoreOffer> offers, DateTimeOffset fetchedAt, CancellationToken cancellationToken = default)
    {
        if (corporationId <= 0) throw new ArgumentOutOfRangeException(nameof(corporationId), "the corporation id must be positive");
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var clear = new NpgsqlCommand($"DELETE FROM {_schema}.lp_store_offers WHERE corporation_id = $1", connection, transaction)
        {
            Parameters = { new() { Value = corporationId } },
        })
        {
            await clear.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var offer in offers)
        {
            if (offer.CorporationId != corporationId) throw new ArgumentException("an offer belongs to another corporation", nameof(offers));
            await using (var insert = new NpgsqlCommand(
                $"INSERT INTO {_schema}.lp_store_offers (corporation_id, offer_id, type_id, quantity, lp_cost, isk_cost, ak_cost) VALUES ($1, $2, $3, $4, $5, $6, $7)",
                connection, transaction)
            {
                Parameters =
                {
                    new() { Value = corporationId }, new() { Value = offer.OfferId }, new() { Value = offer.TypeId },
                    new() { Value = offer.Quantity }, new() { Value = offer.LpCost }, new() { Value = offer.IskCost }, new() { Value = offer.AkCost },
                },
            })
            {
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
            foreach (var item in offer.RequiredItems)
            {
                await using var require = new NpgsqlCommand(
                    $"INSERT INTO {_schema}.lp_store_offer_requirements (corporation_id, offer_id, type_id, quantity) VALUES ($1, $2, $3, $4)",
                    connection, transaction)
                {
                    Parameters = { new() { Value = corporationId }, new() { Value = offer.OfferId }, new() { Value = item.TypeId }, new() { Value = item.Quantity } },
                };
                await require.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        await using (var refresh = new NpgsqlCommand(
            $"""
            INSERT INTO {_schema}.lp_store_refresh (corporation_id, fetched_at, offer_count) VALUES ($1, $2, $3)
            ON CONFLICT (corporation_id) DO UPDATE SET fetched_at = EXCLUDED.fetched_at, offer_count = EXCLUDED.offer_count
            """,
            connection, transaction)
        {
            Parameters = { new() { Value = corporationId }, new() { Value = fetchedAt }, new() { Value = offers.Count } },
        })
        {
            await refresh.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }
}

/// <summary>
/// The official ESI loyalty store route (<c>GET /loyalty/stores/{corporation_id}/offers</c>, tranquility). It is the only offer
/// </summary>
public sealed class EsiLpStoreOffers(HttpClient http, Uri baseUri)
{
    public static readonly Uri DefaultBase = new("https://esi.evetech.net/latest/");

    public async Task<IReadOnlyList<LpStoreOffer>> FetchAsync(long corporationId, CancellationToken cancellationToken = default)
    {
        if (corporationId <= 0) throw new ArgumentOutOfRangeException(nameof(corporationId), "the corporation id must be positive");
        var uri = new Uri(baseUri, $"loyalty/stores/{corporationId.ToString(CultureInfo.InvariantCulture)}/offers/?datasource=tranquility");
        using var response = await http.GetAsync(uri, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new FormatException("the loyalty store offers are not a list");
        var offers = new List<LpStoreOffer>();
        foreach (var row in document.RootElement.EnumerateArray())
        {
            offers.Add(ParseOffer(corporationId, row));
        }
        return offers;
    }

    private static LpStoreOffer ParseOffer(long corporationId, JsonElement row)
    {
        var requirements = new List<LpStoreRequirement>();
        if (row.TryGetProperty("required_items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                requirements.Add(new LpStoreRequirement(Integer(item, "type_id", 1), Integer(item, "quantity", 1)));
            }
        }
        return new LpStoreOffer(
            corporationId,
            Integer(row, "offer_id", 1),
            Integer(row, "type_id", 1),
            Integer(row, "quantity", 1),
            Integer(row, "lp_cost", 0),
            Integer(row, "isk_cost", 0),
            row.TryGetProperty("ak_cost", out _) ? Integer(row, "ak_cost", 0) : 0,
            requirements);
    }

    private static long Integer(JsonElement element, string name, long minimum)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number) || number < minimum)
        {
            throw new FormatException($"the loyalty store offer has no valid {name}");
        }
        return number;
    }
}

/// <summary>
/// The offers a corporation's store sells, from PostgreSQL, refreshed from ESI at most once a day (<paramref name="maxAge"/>). With no ESI client
/// (the offline switch, EVE_LP_STORE_SEED=skip) it answers what is stored and never calls out. A failed fetch keeps what is stored: the
/// window stays open and the next request tries again.
/// </summary>
public sealed class LpStoreOfferCache(LpStoreOfferStore store, EsiLpStoreOffers? esi, Func<DateTimeOffset> now, Action<string>? log = null)
{
    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromDays(1);

    public TimeSpan MaxAge { get; init; } = DefaultMaxAge;

    public async Task<IReadOnlyList<LpStoreOffer>> GetAsync(long corporationId, CancellationToken cancellationToken = default)
    {
        var stored = await store.ReadAsync(corporationId, cancellationToken);
        var fresh = stored.FetchedAt is { } at && now() - at < MaxAge;
        if (esi is null || fresh) return stored.Offers;
        try
        {
            var fetched = await esi.FetchAsync(corporationId, cancellationToken);
            await store.ReplaceAsync(corporationId, fetched, now(), cancellationToken);
            return fetched;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or FormatException or TaskCanceledException { CancellationToken.IsCancellationRequested: false })
        {
            log?.Invoke($"lp store offers of corporation {corporationId} were not refreshed: {exception.Message}");
            return stored.Offers;
        }
    }
}

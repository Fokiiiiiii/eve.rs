using System.Net;
using System.Text;
using Eve.Data;
using Npgsql;

namespace Eve.Market.Tests;

/// <summary>The ESI adjusted prices: the parse, the daily refresh, the offline switch, a failed fetch that keeps what is stored, and the store.</summary>
public sealed class AdjustedPricesTests
{
    private sealed class FakeEsi(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(answer(request));
        }
    }

    private static HttpClient Client(FakeEsi handler) => new(handler) { BaseAddress = EsiAdjustedPrices.DefaultBase };

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private const string Prices = """
        [
          {"type_id": 34, "adjusted_price": 5.5, "average_price": 5.1},
          {"type_id": 35, "average_price": 9.0},
          {"type_id": 36, "adjusted_price": 12.25}
        ]
        """;

    [Fact]
    public async Task The_parse_reads_each_type_that_has_an_adjusted_price_and_skips_the_rest()
    {
        var handler = new FakeEsi(_ => Json(Prices));
        var rows = await new EsiAdjustedPrices(Client(handler), EsiAdjustedPrices.DefaultBase).FetchAsync();

        Assert.Equal(1, handler.Calls);
        Assert.Equal(new[] { 34, 36 }, rows.Select(r => r.TypeId).ToArray());
        Assert.Equal(5.5, rows[0].AdjustedPrice);
        Assert.Equal(5.1, rows[0].AveragePrice);
        Assert.Null(rows[1].AveragePrice);
    }

    [PgFact]
    public async Task The_cache_fetches_once_a_day_and_answers_from_the_store_in_between()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var schema = "adp_" + Guid.NewGuid().ToString("N")[..10];
        await MarketStore.MigrateAsync(source, schema);
        var store = new AdjustedPriceStore(source, schema);
        var clock = 1_790_000_000_000L;
        var handler = new FakeEsi(_ => Json(Prices));
        var cache = new AdjustedPriceCache(store, new EsiAdjustedPrices(Client(handler), EsiAdjustedPrices.DefaultBase), () => clock);

        var first = await cache.GetAsync([34, 36, 99]);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(new Dictionary<int, double> { [34] = 5.5, [36] = 12.25 }, first);

        clock += 60 * 60 * 1000;
        await cache.GetAsync([34]);
        Assert.Equal(1, handler.Calls);

        clock += 24 * 60 * 60 * 1000;
        await cache.GetAsync([34]);
        Assert.Equal(2, handler.Calls);
    }

    [PgFact]
    public async Task With_the_offline_switch_the_cache_answers_what_is_stored_and_calls_nothing()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var schema = "adp_" + Guid.NewGuid().ToString("N")[..10];
        await MarketStore.MigrateAsync(source, schema);
        var store = new AdjustedPriceStore(source, schema);
        await store.ReplaceAsync([new AdjustedPriceRow(34, 7.0, null)], 1_790_000_000_000L);

        var offline = new AdjustedPriceCache(store, esi: null, () => 1_900_000_000_000L);
        var answer = await offline.GetAsync([34, 36]);

        Assert.Equal(new Dictionary<int, double> { [34] = 7.0 }, answer);
    }

    [PgFact]
    public async Task A_failed_fetch_keeps_what_is_stored_and_invents_nothing()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var schema = "adp_" + Guid.NewGuid().ToString("N")[..10];
        await MarketStore.MigrateAsync(source, schema);
        var store = new AdjustedPriceStore(source, schema);
        await store.ReplaceAsync([new AdjustedPriceRow(34, 7.0, null)], 1_790_000_000_000L);
        var logged = new List<string>();
        var handler = new FakeEsi(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var cache = new AdjustedPriceCache(store, new EsiAdjustedPrices(Client(handler), EsiAdjustedPrices.DefaultBase), () => 1_900_000_000_000L, logged.Add);

        var answer = await cache.GetAsync([34, 36]);

        Assert.Equal(1, handler.Calls);
        Assert.Single(logged);
        Assert.Equal(new Dictionary<int, double> { [34] = 7.0 }, answer);
    }

    [PgFact]
    public async Task A_replace_takes_out_the_types_it_no_longer_has()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var schema = "adp_" + Guid.NewGuid().ToString("N")[..10];
        await MarketStore.MigrateAsync(source, schema);
        var store = new AdjustedPriceStore(source, schema);
        await store.ReplaceAsync([new AdjustedPriceRow(34, 7.0, null), new AdjustedPriceRow(35, 8.0, null)], 1_790_000_000_000L);
        await store.ReplaceAsync([new AdjustedPriceRow(35, 9.0, 8.5)], 1_790_000_100_000L);

        Assert.Equal(new Dictionary<int, double> { [35] = 9.0 }, await store.ReadAsync([34, 35]));
        Assert.Equal(1_790_000_100_000L, await store.FetchedAtAsync());
    }
}

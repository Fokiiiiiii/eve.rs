using System.Net;
using System.Text;
using Eve.Data;
using Npgsql;

namespace Eve.Market.Tests;

/// <summary>The ESI system cost indices: the parse, the daily refresh, the offline switch, a failed fetch that keeps what is stored, and the store.</summary>
public sealed class IndustryCostIndexTests
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

    private static HttpClient Client(FakeEsi handler) => new(handler) { BaseAddress = EsiIndustrySystems.DefaultBase };

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private const string Systems = """
        [
          {"solar_system_id": 30000142, "cost_indices": [
            {"activity": "manufacturing", "cost_index": 0.0321},
            {"activity": "researching_time_efficiency", "cost_index": 0.0110},
            {"activity": "researching_material_efficiency", "cost_index": 0.0120},
            {"activity": "copying", "cost_index": 0.0130},
            {"activity": "invention", "cost_index": 0.0140},
            {"activity": "reaction", "cost_index": 0.0150},
            {"activity": "duplicating", "cost_index": 0.0900},
            {"activity": "manufacturing", "cost_index": 2.5}
          ]},
          {"solar_system_id": 30000144, "cost_indices": [
            {"activity": "manufacturing", "cost_index": 0.0400}
          ]}
        ]
        """;

    [Fact]
    public async Task The_parse_maps_every_activity_it_knows_and_skips_the_rest()
    {
        var handler = new FakeEsi(_ => Json(Systems));
        var rows = await new EsiIndustrySystems(Client(handler), EsiIndustrySystems.DefaultBase).FetchAsync();

        Assert.Equal(1, handler.Calls);
        Assert.Equal(6, rows.Count(row => row.SolarSystemId == 30000142));
        Assert.Contains(rows, row => row.SolarSystemId == 30000142 && row.ActivityId == 1 && row.CostIndex == 0.0321);
        Assert.Contains(rows, row => row.SolarSystemId == 30000142 && row.ActivityId == 3 && row.CostIndex == 0.0110);
        Assert.Contains(rows, row => row.SolarSystemId == 30000142 && row.ActivityId == 4 && row.CostIndex == 0.0120);
        Assert.Contains(rows, row => row.SolarSystemId == 30000142 && row.ActivityId == 5 && row.CostIndex == 0.0130);
        Assert.Contains(rows, row => row.SolarSystemId == 30000142 && row.ActivityId == 8 && row.CostIndex == 0.0140);
        Assert.Contains(rows, row => row.SolarSystemId == 30000142 && row.ActivityId == 9 && row.CostIndex == 0.0150);
        Assert.DoesNotContain(rows, row => row.CostIndex > 1);
    }

    [PgFact]
    public async Task The_cache_fetches_once_a_day_and_answers_from_the_store_in_between()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var schema = "ici_" + Guid.NewGuid().ToString("N")[..10];
        await MarketStore.MigrateAsync(source, schema);
        var store = new IndustryCostIndexStore(source, schema);
        var clock = 1_790_000_000_000L;
        var handler = new FakeEsi(_ => Json(Systems));
        var cache = new IndustryCostIndexCache(store, new EsiIndustrySystems(Client(handler), EsiIndustrySystems.DefaultBase), () => clock);

        Assert.Equal(0.0321, await cache.GetAsync(30000142, 1));
        Assert.Equal(1, handler.Calls);

        clock += 60 * 60 * 1000;
        await cache.GetAsync(30000144, 1);
        Assert.Equal(1, handler.Calls);

        clock += 24 * 60 * 60 * 1000;
        await cache.GetAsync(30000144, 1);
        Assert.Equal(2, handler.Calls);
    }

    [PgFact]
    public async Task With_the_offline_switch_the_cache_answers_what_is_stored_and_calls_nothing()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var schema = "ici_" + Guid.NewGuid().ToString("N")[..10];
        await MarketStore.MigrateAsync(source, schema);
        var store = new IndustryCostIndexStore(source, schema);
        await store.ReplaceAsync([new IndustryCostIndexRow(30000142, 1, 0.05)], 1_790_000_000_000L);

        var offline = new IndustryCostIndexCache(store, esi: null, () => 1_900_000_000_000L);

        Assert.Equal(0.05, await offline.GetAsync(30000142, 1));
        Assert.Null(await offline.GetAsync(30000142, 8));
        Assert.Null(await offline.GetAsync(30000999, 1));
        Assert.Equal(new Dictionary<long, double> { [30000142] = 0.05 }, await offline.GetActivityAsync(1));
    }

    [PgFact]
    public async Task A_failed_fetch_keeps_what_is_stored_and_invents_nothing()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var schema = "ici_" + Guid.NewGuid().ToString("N")[..10];
        await MarketStore.MigrateAsync(source, schema);
        var store = new IndustryCostIndexStore(source, schema);
        await store.ReplaceAsync([new IndustryCostIndexRow(30000142, 1, 0.05)], 1_790_000_000_000L);
        var logged = new List<string>();
        var handler = new FakeEsi(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var cache = new IndustryCostIndexCache(store, new EsiIndustrySystems(Client(handler), EsiIndustrySystems.DefaultBase), () => 1_900_000_000_000L, logged.Add);

        Assert.Equal(0.05, await cache.GetAsync(30000142, 1));
        Assert.Equal(1, handler.Calls);
        Assert.Single(logged);
        Assert.Null(await cache.GetAsync(30000142, 8));
    }

    [PgFact]
    public async Task A_replace_takes_out_the_rows_it_no_longer_has_and_reads_by_activity()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var schema = "ici_" + Guid.NewGuid().ToString("N")[..10];
        await MarketStore.MigrateAsync(source, schema);
        var store = new IndustryCostIndexStore(source, schema);
        await store.ReplaceAsync([new IndustryCostIndexRow(30000142, 1, 0.05), new IndustryCostIndexRow(30000142, 8, 0.02)], 1_790_000_000_000L);
        await store.ReplaceAsync([new IndustryCostIndexRow(30000144, 1, 0.07)], 1_790_000_100_000L);

        Assert.Null(await store.ReadAsync(30000142, 1));
        Assert.Equal(new Dictionary<long, double> { [30000144] = 0.07 }, await store.ReadActivityAsync(1));
        Assert.Equal(1_790_000_100_000L, await store.FetchedAtAsync());
    }
}

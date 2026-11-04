using System.Net;
using Eve.Data;
using Npgsql;

namespace Eve.Wallet.Tests;

/// <summary>Skips a test, loudly, when there is no PostgreSQL to test against (see tools/dev/pg18.sh).</summary>
public sealed class LpStorePgFactAttribute : FactAttribute
{
    public LpStorePgFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("EVE_PG_URL") is null) Skip = "EVE_PG_URL is not set (see tools/dev/pg18.sh)";
    }
}

/// <summary>The loyalty store's offers: the ESI route parsed (a fake handler answers it), the table replaced atomically, and the daily cache policy.</summary>
public class LpStoreOffersTests
{
    private const long Corporation = 1000125;
    private static readonly DateTimeOffset Start = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    private const string Sample = """
    [
      { "offer_id": 20001, "type_id": 1234, "quantity": 1, "lp_cost": 5000, "isk_cost": 100000, "ak_cost": 3,
        "required_items": [ { "type_id": 34, "quantity": 10 }, { "type_id": 35, "quantity": 2 } ] },
      { "offer_id": 20002, "type_id": 5678, "quantity": 5, "lp_cost": 900, "isk_cost": 0,
        "required_items": [] }
    ]
    """;

    private sealed class Handler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    private static EsiLpStoreOffers Esi(Handler handler) => new(new HttpClient(handler), EsiLpStoreOffers.DefaultBase);

    private static LpStoreOffer Offer(long offerId, long typeId) => new(Corporation, offerId, typeId, 1, 500, 0, 0,
        [new LpStoreRequirement(34, 4)]);

    [Fact]
    public async Task The_esi_route_is_parsed_into_offers_with_their_required_items()
    {
        var handler = new Handler(HttpStatusCode.OK, Sample);

        var offers = await Esi(handler).FetchAsync(Corporation);

        Assert.Equal("https://esi.evetech.net/latest/loyalty/stores/1000125/offers/?datasource=tranquility", handler.Requests.Single().ToString());
        Assert.Equal(2, offers.Count);
        Assert.Equal((20001L, 1234L, 1L, 5000L, 100000L, 3L), (offers[0].OfferId, offers[0].TypeId, offers[0].Quantity, offers[0].LpCost, offers[0].IskCost, offers[0].AkCost));
        Assert.Equal(new[] { new LpStoreRequirement(34, 10), new LpStoreRequirement(35, 2) }, offers[0].RequiredItems);
        Assert.Equal((20002L, 5678L, 5L, 900L), (offers[1].OfferId, offers[1].TypeId, offers[1].Quantity, offers[1].LpCost));
        Assert.Empty(offers[1].RequiredItems);
    }

    [Fact]
    public async Task An_offer_without_an_id_or_a_type_is_refused_so_nothing_half_read_is_stored()
    {
        var handler = new Handler(HttpStatusCode.OK, """[ { "type_id": 1234, "quantity": 1, "lp_cost": 5, "isk_cost": 0 } ]""");

        await Assert.ThrowsAsync<FormatException>(() => Esi(handler).FetchAsync(Corporation));
    }

    [LpStorePgFact]
    public async Task Replacing_a_corporations_offers_keeps_only_the_new_ones_and_records_the_fetch_time()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
        var schema = "lps_" + Guid.NewGuid().ToString("N")[..12];
        try
        {
            await PgWalletStore.MigrateAsync(source, schema);
            var store = new LpStoreOfferStore(source, schema);

            await store.ReplaceAsync(Corporation, [Offer(1, 100), Offer(2, 200)], Start);
            await store.ReplaceAsync(Corporation, [Offer(3, 300)], Start.AddHours(2));
            await store.ReplaceAsync(Corporation + 1, [new LpStoreOffer(Corporation + 1, 9, 9, 1, 1, 1, 0, [])], Start);

            var read = await store.ReadAsync(Corporation);
            Assert.Equal([3L], read.Offers.Select(offer => offer.OfferId));
            Assert.Equal(new LpStoreRequirement(34, 4), read.Offers.Single().RequiredItems.Single());
            Assert.Equal(Start.AddHours(2), read.FetchedAt);
            Assert.Equal([9L], (await store.ReadAsync(Corporation + 1)).Offers.Select(offer => offer.OfferId));
            Assert.Null((await store.ReadAsync(Corporation + 2)).FetchedAt);
        }
        finally
        {
            await using var drop = source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
            await drop.ExecuteNonQueryAsync();
        }
    }

    [LpStorePgFact]
    public async Task The_cache_refreshes_once_a_day_and_answers_what_it_holds_when_the_refresh_fails_or_is_switched_off()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
        var schema = "lps_" + Guid.NewGuid().ToString("N")[..12];
        try
        {
            await PgWalletStore.MigrateAsync(source, schema);
            var store = new LpStoreOfferStore(source, schema);
            var now = Start;
            var log = new List<string>();
            var handler = new Handler(HttpStatusCode.OK, Sample);
            var cache = new LpStoreOfferCache(store, Esi(handler), () => now, log.Add);

            var first = await cache.GetAsync(Corporation);
            now = Start.AddHours(23);
            var within = await cache.GetAsync(Corporation);
            Assert.Equal(2, first.Count);
            Assert.Equal(2, within.Count);
            Assert.Single(handler.Requests);

            now = Start.AddDays(1);
            var failing = new LpStoreOfferCache(store, Esi(new Handler(HttpStatusCode.InternalServerError, "")), () => now, log.Add);
            var kept = await failing.GetAsync(Corporation);
            Assert.Equal(2, kept.Count);
            Assert.Single(log);

            var off = new LpStoreOfferCache(store, null, () => now, log.Add);
            Assert.Equal(2, (await off.GetAsync(Corporation)).Count);
            Assert.Empty(await off.GetAsync(Corporation + 7));
        }
        finally
        {
            await using var drop = source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
            await drop.ExecuteNonQueryAsync();
        }
    }
}

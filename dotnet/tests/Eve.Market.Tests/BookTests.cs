using Eve.Data;
using Npgsql;

namespace Eve.Market.Tests;

public class BookTests
{
    [PgFact]
    public async Task Npc_book_configuration_replays_stably_and_invalid_batches_roll_back_completely()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var schema = "market_book_" + Guid.NewGuid().ToString("N");
        await MarketStore.MigrateAsync(source,schema);
        var store = new MarketStore(source,schema);
        var here = new MarketLocation(10,1,100,1000);
        try
        {
            MarketNpcSeed Seed(string key,decimal price,bool bid = false) => new(key,34,bid,price,100,32767,here,0);
            var rows = await store.SeedNpcAsync([Seed("ask-hi",12),Seed("ask-low",10),Seed("bid-low",7,true),Seed("bid-hi",9,true)]);
            var book = await store.BookAsync(1000,34,10000000000000);
            Assert.Equal([10m,12m],book.Asks.Select(o => o.Price));
            Assert.Equal([9m,7m],book.Bids.Select(o => o.Price));
            Assert.Equal(rows.Select(o => o.Id), (await store.SeedNpcAsync([Seed("ask-hi",12),Seed("ask-low",10),Seed("bid-low",7,true),Seed("bid-hi",9,true)])).Select(o => o.Id));
            await using (var sequence=source.CreateCommand($"SELECT last_value FROM {schema}.order_ids"))
                Assert.Equal(4L,(long)(await sequence.ExecuteScalarAsync())!);
            var low = book.Asks.First();
            await using (var consumed = source.CreateCommand($"UPDATE {schema}.orders SET vol_remaining=20 WHERE id=$1"))
            {
                consumed.Parameters.Add(new NpgsqlParameter { Value=low.Id });
                await consumed.ExecuteNonQueryAsync();
            }
            await store.SeedNpcAsync([Seed("ask-low",10)]);
            Assert.Equal(20,(await store.BookAsync(1000,34,0)).Asks.First().VolumeRemaining);
            await Assert.ThrowsAsync<PostgresException>(() => store.SeedNpcAsync([Seed("new-good",3),Seed("bad",0)]));
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.SeedNpcAsync([Seed("ask-low",10) with { TypeId=35 }]));
            Assert.DoesNotContain((await store.BookAsync(1000,34,0)).Asks,o => o.Price == 3);
            var reopened = new MarketStore(source,schema);
            Assert.Equal(20,(await reopened.BookAsync(1000,34,0)).Asks.First().VolumeRemaining);
            Assert.Empty((await store.BookAsync(2000,34,0)).Asks);
            Assert.Equal([low.Id],MarketMatching.Plan(book.Asks,new MarketTopology([]),34,true,here,10,2,-1,0).Select(f => f.Order.Id));
        }
        finally
        {
            await using var drop = source.CreateCommand($"DROP SCHEMA {schema} CASCADE");
            await drop.ExecuteNonQueryAsync();
        }
    }

    [PgFact]
    public async Task Pinned_sde_market_catalog_loads_groups_only()
    {
        await using var r=await TradeRig.Create();
        var catalog=await MarketCatalog.LoadAsync(new Eve.Sde.SdeReader(r.Source));
        Assert.Contains(catalog.Groups,g => g.Id==4 && g.Name.Length>0);
    }


    [Fact]
    public void Tq_order_maps_to_a_persistable_npc_station_seed()
    {
        var station = new Eve.Universe.Station(
            60003760, 1529, "Jita 4-4", 1000125, "Caldari Navy", 500001, "Caldari State", 5, 0,
            30000142, "Jita", 20000020, "Kimotoro", 10000002, "The Forge", 0.9, 0, 4);
        var order = new MarketSeedV4.Order(
            10000002, 123, 34, station.Id, station.SolarSystemId, false, 3.2m, 10, 7, 2, "4",
            DateTimeOffset.UnixEpoch, 30);

        var seed = MarketSeedV4.ToSeed(order, new Eve.Universe.StationCatalog([station]));

        Assert.Equal("tq:10000002:123", seed.Key);
        Assert.Equal(34, seed.TypeId);
        Assert.False(seed.Bid);
        Assert.Equal(3.2m, seed.Price);
        Assert.Equal(10, seed.Volume);
        Assert.Equal(7, seed.VolumeRemaining);
        Assert.Equal(2, seed.MinimumVolume);
        Assert.Equal(MarketRules.RangeEsiFourJumps, seed.Range);
        Assert.Equal(MarketLocation.From(station), seed.Location);
        Assert.Equal(30, seed.Duration);
    }
}


public sealed class MarketSeedV4Tests
{
    private const long RegionId = 10000002;
    private const long StationId = 60003760;
    private static readonly DateTimeOffset LastModified = new(2025,9,30,12,0,0,TimeSpan.Zero);

    [Fact]
    public async Task Captures_full_orders_streams_them_from_the_v4_artifact_and_filters_current_sde_npc_stations()
    {
        using var handler = new FakeHandler((request, _) =>
        {
            Assert.Equal("all",Query(request.RequestUri!,"order_type"));
            Assert.Equal("tranquility",Query(request.RequestUri!,"datasource"));
            Assert.Null(Query(request.RequestUri!,"type_id"));
            Assert.Equal("2025-09-30",request.Headers.GetValues("X-Compatibility-Date").Single());
            var page = Query(request.RequestUri!,"page");
            return Task.FromResult(Response(
                page == "1"
                    ? $"[{Order(20,34,StationId,false,123.456789m)},{Order(19,34,StationId,true,7m)},{Order(21,999,StationId,true,1m)},{Order(22,34,1000000000001,true,2m)}]"
                    : $"[{Order(10,34,60003761,true,45m)}]",
                2,LastModified,DateTimeOffset.UtcNow.AddMinutes(5),$"\"page-{page}\""));
        });
        using var client = new HttpClient(handler);

        var result = await CaptureAndReadAsync(client);

        Assert.Equal(4,result.Metadata.SchemaVersion);
        Assert.Equal("ESI",result.Metadata.Source);
        Assert.Equal("tranquility",result.Metadata.DataSource);
        Assert.Equal(3396210,result.Metadata.SdeBuild);
        Assert.Equal("2025-09-30",result.Metadata.CompatibilityDate);
        Assert.Equal("v4|ESI|tranquility|2025-09-30|sde:3396210",result.Metadata.Fingerprint);
        Assert.Equal(3,result.Metadata.RetainedOrderCount);
        Assert.Equal(TimeSpan.Zero,result.Metadata.CapturedAtUtc.Offset);
        Assert.Equal(2,handler.Requests.Count);
        Assert.Equal([19L,20L,10L],result.Orders.Select(order => order.OrderId));
        var ask = result.Orders[1];
        Assert.Equal(RegionId,ask.RegionId);
        Assert.Equal(StationId,ask.LocationId);
        Assert.Equal(30000142,ask.SystemId);
        Assert.False(ask.IsBuyOrder);
        Assert.Equal(123.456789m,ask.Price);
        Assert.Equal(1000,ask.VolumeTotal);
        Assert.Equal(800,ask.VolumeRemain);
        Assert.Equal(1,ask.MinimumVolume);
        Assert.Equal("4",ask.Range);
        Assert.Equal(new DateTimeOffset(2025,9,30,12,0,0,TimeSpan.Zero),ask.Issued);
        Assert.Equal(90,ask.Duration);
        Assert.Equal($"tq:{RegionId}:20",ask.StableKey);
        Assert.Equal(["\"page-1\"","\"page-2\""],result.Metadata.Regions.Single().Pages.Select(page=>page.ETag));
        Assert.Equal(LastModified,result.Metadata.Regions.Single().LastModified);
    }

    [Fact]
    public async Task Empty_region_produces_an_offline_readable_empty_v4_snapshot()
    {
        using var handler = new FakeHandler((_, _) => Task.FromResult(Response("[]",1,LastModified,DateTimeOffset.UtcNow.AddMinutes(5),"\"empty\"")));
        using var client = new HttpClient(handler);

        var result = await CaptureAndReadAsync(client);

        Assert.Empty(result.Orders);
        Assert.Equal(0,result.Metadata.RetainedOrderCount);
        Assert.Single(handler.Requests);
        Assert.Single(result.Metadata.Regions.Single().Pages);
    }

    [Fact]
    public async Task Restarts_region_when_page_validators_cross_a_cache_refresh()
    {
        var requestNumber = 0;
        using var handler = new FakeHandler((request, _) =>
        {
            var n = requestNumber++;
            var page = Query(request.RequestUri!,"page");
            if (n < 2)
                return Task.FromResult(Response(n == 0 ? $"[{Order(1,34,StationId,true,1m)}]" : "[]",2,
                    n == 0 ? LastModified : LastModified.AddMinutes(1),DateTimeOffset.UtcNow.AddSeconds(6),$"\"old-{page}\""));
            return Task.FromResult(Response(page == "1" ? $"[{Order(2,34,StationId,true,2m)}]" : "[]",2,
                LastModified.AddMinutes(1),DateTimeOffset.UtcNow.AddMinutes(5),$"\"new-{page}\""));
        });
        using var client = new HttpClient(handler);

        var result = await CaptureAndReadAsync(client);

        Assert.Equal(["1","2","1","2"],handler.Requests.Select(uri => Query(new Uri(uri),"page")));
        Assert.Equal(2,result.Orders.Single().OrderId);
        Assert.Equal(LastModified.AddMinutes(1),result.Metadata.Regions.Single().LastModified);
        Assert.Equal(["\"new-1\"","\"new-2\""],result.Metadata.Regions.Single().Pages.Select(page => page.ETag));
    }

    [Fact]
    public async Task Refreshes_page_one_before_scanning_when_its_cache_is_expired()
    {
        var requests = 0;
        using var handler = new FakeHandler((request, _) =>
        {
            var n = requests++;
            var page = Query(request.RequestUri!,"page");
            if (n == 0)
                return Task.FromResult(Response($"[{Order(1,34,StationId,true,1m)}]",2,LastModified,
                    DateTimeOffset.UtcNow.AddSeconds(-1),"\"stale-page-1\""));
            return Task.FromResult(Response(page == "1" ? $"[{Order(2,34,StationId,true,2m)}]" : $"[{Order(3,34,StationId,true,3m)}]",
                2,LastModified.AddMinutes(1),DateTimeOffset.UtcNow.AddMinutes(5),$"\"fresh-{page}\""));
        });
        using var client = new HttpClient(handler);

        var result = await CaptureAndReadAsync(client);

        Assert.Equal(["1","1","2"],handler.Requests.Select(uri => Query(new Uri(uri),"page")));
        Assert.Equal([2L,3L],result.Orders.Select(order => order.OrderId));
        Assert.Equal(["\"fresh-1\"","\"fresh-2\""],
            result.Metadata.Regions.Single().Pages.Select(page => page.ETag));
    }

    [Fact]
    public async Task Restarts_region_when_page_two_cache_is_expired_and_discards_page_one_rows()
    {
        var requests = 0;
        using var handler = new FakeHandler((request, _) =>
        {
            var n = requests++;
            var page = Query(request.RequestUri!,"page");
            var attempt = n / 2;
            if (attempt == 0 && page == "1")
                return Task.FromResult(Response($"[{Order(1,34,StationId,true,1m)}]",2,LastModified,
                    DateTimeOffset.UtcNow.AddMinutes(5),"\"stale-set-page-1\""));
            if (attempt == 0)
                return Task.FromResult(Response($"[{Order(99,34,StationId,true,99m)}]",2,LastModified,
                    DateTimeOffset.UtcNow.AddSeconds(-1),"\"expired-page-2\""));
            return Task.FromResult(Response(page == "1" ? $"[{Order(2,34,StationId,true,2m)}]" : $"[{Order(3,34,StationId,true,3m)}]",
                2,LastModified.AddMinutes(1),DateTimeOffset.UtcNow.AddMinutes(5),$"\"fresh-{page}\""));
        });
        using var client = new HttpClient(handler);

        var result = await CaptureAndReadAsync(client);

        Assert.Equal(["1","2","1","2"],handler.Requests.Select(uri => Query(new Uri(uri),"page")));
        Assert.Equal([2L,3L],result.Orders.Select(order => order.OrderId));
        Assert.Equal(["\"fresh-1\"","\"fresh-2\""],
            result.Metadata.Regions.Single().Pages.Select(page => page.ETag));
    }

    [Fact]
    public async Task Restarts_region_when_x_pages_changes_and_discards_partial_rows()
    {
        var requests = 0;
        using var handler = new FakeHandler((request, _) =>
        {
            var n = requests++;
            var page = Query(request.RequestUri!,"page");
            if (n == 0)
                return Task.FromResult(Response($"[{Order(1,34,StationId,true,1m)}]",2,LastModified,
                    DateTimeOffset.UtcNow.AddSeconds(6),"\"old-1\""));
            if (n == 1)
                return Task.FromResult(Response($"[{Order(2,34,StationId,true,2m)}]",3,LastModified,
                    DateTimeOffset.UtcNow.AddMinutes(5),"\"old-2\""));
            var orderId = page switch { "1" => 3L, "2" => 4L, _ => 5L };
            return Task.FromResult(Response($"[{Order(orderId,34,StationId,true,orderId)}]",3,
                LastModified.AddMinutes(1),DateTimeOffset.UtcNow.AddMinutes(5),$"\"new-{page}\""));
        });
        using var client = new HttpClient(handler);

        var result = await CaptureAndReadAsync(client);

        Assert.Equal(["1","2","1","2","3"],handler.Requests.Select(uri => Query(new Uri(uri),"page")));
        Assert.Equal([3L,4L,5L],result.Orders.Select(order => order.OrderId));
        Assert.Equal(["\"new-1\"","\"new-2\"","\"new-3\""],
            result.Metadata.Regions.Single().Pages.Select(page => page.ETag));
        Assert.Equal(LastModified.AddMinutes(1),result.Metadata.Regions.Single().LastModified);
    }

    [Fact]
    public async Task Retry_limit_reports_last_modified_page_and_discards_partial_region_rows()
    {
        var requests = 0;
        using var handler = new FakeHandler((request, _) =>
        {
            var n = requests++;
            var page = Query(request.RequestUri!,"page");
            var attempt = n / 2;
            if (page == "1")
                return Task.FromResult(Response($"[{Order(100 + attempt,34,StationId,true,1m)}]",2,LastModified,
                    DateTimeOffset.UtcNow.AddSeconds(6),$"\"attempt-{attempt}-1\""));
            return Task.FromResult(Response("[]",2,LastModified.AddMinutes(attempt + 1),
                DateTimeOffset.UtcNow.AddMinutes(5),$"\"attempt-{attempt}-2\""));
        });
        using var client = new HttpClient(handler);
        await using var artifact = new MemoryStream();

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => MarketSeedV4.CaptureAsync(client,artifact,
            [RegionId],new HashSet<long> { 34 },new HashSet<long> { StationId },3396210,DateTimeOffset.UnixEpoch));

        Assert.Contains("Last-Modified",error.Message);
        Assert.Contains($"region {RegionId}",error.Message);
        Assert.Contains("page 2",error.Message);
        Assert.Equal(6,handler.Requests.Count);
        Assert.Equal(["1","2","1","2","1","2"],handler.Requests.Select(uri => Query(new Uri(uri),"page")));
        var incompleteArtifact = System.Text.Encoding.UTF8.GetString(artifact.ToArray());
        Assert.Contains("market-seed-v4-jsonl",incompleteArtifact);
        Assert.DoesNotContain("order_id",incompleteArtifact);
    }

    [Fact]
    public async Task Rejects_malformed_esi_order_records()
    {
        using var handler = new FakeHandler((_, _) => Task.FromResult(Response("[{}]",1,LastModified,DateTimeOffset.UtcNow.AddMinutes(5),"\"bad\"")));
        using var client = new HttpClient(handler);
        await using var artifact = new MemoryStream();

        await Assert.ThrowsAsync<InvalidDataException>(() => MarketSeedV4.CaptureAsync(client,artifact,[RegionId],
            new HashSet<long> { 34 },new HashSet<long> { StationId },3396210,DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public async Task Honors_retry_after_for_429_and_fails_fast_on_the_global_420_error_limit()
    {
        var requests = 0;
        using var limitedHandler = new FakeHandler((_, _) =>
        {
            if (requests++ == 0)
            {
                var limited = new HttpResponseMessage((System.Net.HttpStatusCode)429);
                limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
                return Task.FromResult(limited);
            }
            return Task.FromResult(Response($"[{Order(1,34,StationId,true,1m)}]",1,LastModified,DateTimeOffset.UtcNow.AddMinutes(5),"\"ok\""));
        });
        using var client = new HttpClient(limitedHandler);

        var result = await CaptureAndReadAsync(client);
        Assert.Single(result.Orders);
        Assert.Equal(2,limitedHandler.Requests.Count);

        using var errorHandler = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage((System.Net.HttpStatusCode)420)));
        using var errorClient = new HttpClient(errorHandler);
        await using var artifact = new MemoryStream();
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => MarketSeedV4.CaptureAsync(errorClient,artifact,
            [RegionId],new HashSet<long> { 34 },new HashSet<long> { StationId },3396210,DateTimeOffset.UnixEpoch));
        Assert.Equal((System.Net.HttpStatusCode)420,error.StatusCode);
        Assert.Single(errorHandler.Requests);
    }

    [Fact]
    public async Task Cancellation_interrupts_an_in_flight_esi_request()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new FakeHandler(async (_, token) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan,token);
            return Response("[]",1,LastModified,DateTimeOffset.UtcNow.AddMinutes(5),"\"unused\"");
        });
        using var client = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        await using var artifact = new MemoryStream();

        var capture = MarketSeedV4.CaptureAsync(client,artifact,[RegionId],new HashSet<long> { 34 },
            new HashSet<long> { StationId },3396210,DateTimeOffset.UnixEpoch,cancellation.Token);
        await started.Task;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture);
    }

    private static async Task<(MarketSeedV4.Metadata Metadata,MarketSeedV4.Order[] Orders)> CaptureAndReadAsync(HttpClient client)
    {
        await using var artifact = new MemoryStream();
        var captured = await MarketSeedV4.CaptureAsync(client,artifact,[RegionId],new HashSet<long> { 34 },
            new HashSet<long> { StationId,60003761 },3396210,DateTimeOffset.UnixEpoch);
        Assert.Equal(DateTimeOffset.UnixEpoch,captured.CapturedAtUtc);
        artifact.Position = 0;
        var snapshot = MarketSeedV4.Read(artifact);
        var orders = new List<MarketSeedV4.Order>();
        await foreach (var order in snapshot.Orders) orders.Add(order);
        Assert.Equal(captured.RetainedOrderCount,(await snapshot.Completion).RetainedOrderCount);
        return (captured,orders.ToArray());
    }

    private static string Order(long id,long type,long location,bool buy,decimal price) =>
        $"{{\"order_id\":{id},\"type_id\":{type},\"location_id\":{location},\"system_id\":30000142,\"is_buy_order\":{buy.ToString().ToLowerInvariant()},\"price\":{price.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"volume_total\":1000,\"volume_remain\":800,\"min_volume\":1,\"range\":\"4\",\"issued\":\"2025-09-30T12:00:00Z\",\"duration\":90}}";

    private static HttpResponseMessage Response(string json,int pages,DateTimeOffset modified,DateTimeOffset expires,string etag)
    {
        var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(json,System.Text.Encoding.UTF8,"application/json")
        };
        response.Headers.TryAddWithoutValidation("X-Pages",pages.ToString(System.Globalization.CultureInfo.InvariantCulture));
        response.Headers.TryAddWithoutValidation("ETag",etag);
        response.Content.Headers.LastModified = modified;
        response.Content.Headers.Expires = expires;
        return response;
    }

    private static string? Query(Uri uri,string name)
    {
        var pair = uri.Query.TrimStart('?').Split('&').Select(part => part.Split('=',2))
            .FirstOrDefault(parts => parts.Length == 2 && parts[0] == name);
        return pair is null ? null : Uri.UnescapeDataString(pair[1]);
    }

    private sealed class FakeHandler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> reply) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.ToString());
            return reply(request,cancellationToken);
        }
    }
}

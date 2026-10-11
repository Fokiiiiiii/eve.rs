using System.Net;
using System.Security.Cryptography;

namespace Eve.Market.Tests;

/// <summary>
/// The import path of the host's first start (CorporationComposition.MarketSeed, M9): the ESI capture writes an artifact, the artifact is
/// read back, its orders are streamed into the store as v4 seeds, and the book serves them. The orders of an unknown type and of a station
/// the SDE does not list are not imported.
/// </summary>
public class MarketSeedImportTests
{
    private static readonly DateTimeOffset LastModified = new(2025,9,30,12,0,0,TimeSpan.Zero);

    [PgFact]
    public async Task A_captured_artifact_imports_the_sde_npc_station_orders_and_the_book_serves_them()
    {
        await using var r = await TradeRig.Create();
        var station = r.Station;
        using var handler = new FakeHandler((_, _) => Task.FromResult(Response(
            $"[{Order(1,34,station.Id,false,12.50m)},{Order(2,34,station.Id,true,10.25m)},{Order(3,999,station.Id,true,1m)},{Order(4,34,1000000000001,true,1m)}]",
            1,LastModified,DateTimeOffset.UtcNow.AddMinutes(5),"\"import-1\"")));
        using var client = new HttpClient(handler);
        await using var artifact = new MemoryStream();
        var captured = await MarketSeedV4.CaptureAsync(client,artifact,[station.RegionId],new HashSet<long> { 34 },
            new HashSet<long> { station.Id },3396210,DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_000));
        Assert.Equal(2,captured.RetainedOrderCount);

        var fingerprint = Convert.ToHexString(SHA256.HashData(artifact.ToArray())).ToLowerInvariant();
        artifact.Position = 0;
        var snapshot = MarketSeedV4.Read(artifact);
        var imported = await r.Book.SeedNpcV4Async(MarketSeedV4.ToSeedsAsync(snapshot.Orders,r.Stations),fingerprint,3396210,
            captured.CapturedAtUtc.ToUnixTimeMilliseconds(),() => r.Now);
        Assert.Equal(2L,imported);
        Assert.Equal(captured.RetainedOrderCount,(await snapshot.Completion).RetainedOrderCount);
        Assert.True(await r.Book.HasSeedV4Async(3396210)); // the host's first-start gate: a second start does not capture again

        var book = await r.Book.BookAsync(station.RegionId,34,r.Now);
        Assert.Equal(12.50m,Assert.Single(book.Asks).Price);
        Assert.Equal(10.25m,Assert.Single(book.Bids).Price);
        Assert.Empty((await r.Book.BookAsync(station.RegionId,999,r.Now)).Asks);
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

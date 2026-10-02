// Static market groups; first-run NPC orders are captured from TQ by Market Seed v4.
using System.Text.Json;
using Eve.Sde;
using Eve.Universe;

namespace Eve.Market;

public sealed record MarketGroup(int Id,int? ParentId,string Name,string Description,int? IconId,bool HasTypes);

/// <summary>Static EVE market groups loaded from the current SDE.</summary>
public sealed class MarketCatalog(IReadOnlyList<MarketGroup> groups)
{
    public IReadOnlyList<MarketGroup> Groups { get; } = groups;

    public static async Task<MarketCatalog> LoadAsync(SdeReader sde,CancellationToken token = default)
    {
        var groups=(await sde.ReadAsync("marketGroups",cancellationToken:token)).Select(row =>
        {
            var doc=row.Doc;
            int? OptionalInt(string name) => doc.TryGetProperty(name,out var value) && value.ValueKind==JsonValueKind.Number ? value.GetInt32() : null;
            return new MarketGroup((int)row.Key,OptionalInt("parentGroupID"),SdeReader.English(doc.GetProperty("name")),
                doc.TryGetProperty("description",out var description) ? SdeReader.English(description) : "",OptionalInt("iconID"),
                doc.TryGetProperty("hasTypes",out var hasTypes) && hasTypes.ValueKind==JsonValueKind.True);
        }).ToArray();
        return new MarketCatalog(groups);
    }
}

public static class MarketSeedV4
{
    public const int SchemaVersion = 4;
    public const string Source = "ESI";
    public const string DataSource = "tranquility";
    public const string CompatibilityDate = "2025-09-30";
    private const string Endpoint = "https://esi.evetech.net/latest";
    private const int MaxRegionAttempts = 3;
    private const int MaxRateLimitRetries = 8;
    private static readonly byte[] NewLine = [(byte)'\n'];
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        IgnoreReadOnlyProperties = true
    };

    public sealed record Order(
        long RegionId,long OrderId,long TypeId,long LocationId,long SystemId,bool IsBuyOrder,
        decimal Price,long VolumeTotal,long VolumeRemain,long MinimumVolume,string Range,
        DateTimeOffset Issued,long Duration)
    {
        public string StableKey => $"tq:{RegionId}:{OrderId}";
    }

    public sealed record PageCache(int Page,string? ETag,DateTimeOffset? LastModified,DateTimeOffset Expires);
    public sealed record Region(long RegionId,int PageCount,DateTimeOffset? LastModified,IReadOnlyList<PageCache> Pages);
    public sealed record Metadata(
        int SchemaVersion,string Source,string DataSource,string CompatibilityDate,long SdeBuild,
        DateTimeOffset CapturedAtUtc,long RetainedOrderCount,IReadOnlyList<Region> Regions)
    {
        public string Fingerprint => BuildSeedFingerprint(SdeBuild);
    }


    public static string BuildSeedFingerprint(long sdeBuild)
    {
        if (sdeBuild <= 0) throw new ArgumentOutOfRangeException(nameof(sdeBuild));
        return $"v{SchemaVersion}|{Source}|{DataSource}|{CompatibilityDate}|sde:{sdeBuild.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
    }
    public sealed record Snapshot(IAsyncEnumerable<Order> Orders,Task<Metadata> Completion);

    private sealed record HeaderLine(
        string Record,string Format,int SchemaVersion,string Source,string DataSource,
        string CompatibilityDate,long SdeBuild,DateTimeOffset CapturedAtUtc);
    private sealed record OrderLine(string Record,Order Order);
    private sealed record RegionLine(string Record,Region Region);
    private sealed record CompleteLine(string Record,long RetainedOrderCount,int RegionCount);
    private sealed record EsiOrder(
        [property:System.Text.Json.Serialization.JsonPropertyName("order_id")] long? OrderId,
        [property:System.Text.Json.Serialization.JsonPropertyName("type_id")] long? TypeId,
        [property:System.Text.Json.Serialization.JsonPropertyName("location_id")] long? LocationId,
        [property:System.Text.Json.Serialization.JsonPropertyName("system_id")] long? SystemId,
        [property:System.Text.Json.Serialization.JsonPropertyName("is_buy_order")] bool? IsBuyOrder,
        [property:System.Text.Json.Serialization.JsonPropertyName("price")] decimal? Price,
        [property:System.Text.Json.Serialization.JsonPropertyName("volume_total")] long? VolumeTotal,
        [property:System.Text.Json.Serialization.JsonPropertyName("volume_remain")] long? VolumeRemain,
        [property:System.Text.Json.Serialization.JsonPropertyName("min_volume")] long? MinimumVolume,
        [property:System.Text.Json.Serialization.JsonPropertyName("range")] string? Range,
        [property:System.Text.Json.Serialization.JsonPropertyName("issued")] DateTimeOffset? Issued,
        [property:System.Text.Json.Serialization.JsonPropertyName("duration")] long? Duration);
    private sealed record FetchedPage(int PageCount,PageCache Cache,string Json);

    public static async Task<Metadata> CaptureAsync(
        HttpClient client,Stream destination,IEnumerable<long> regionIds,
        IReadOnlySet<long> validTypeIds,IReadOnlySet<long> npcStationIds,long sdeBuild,
        DateTimeOffset capturedAtUtc,CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(regionIds);
        ArgumentNullException.ThrowIfNull(validTypeIds);
        ArgumentNullException.ThrowIfNull(npcStationIds);
        if (!destination.CanWrite || !destination.CanSeek || destination.Position != 0 || destination.Length != 0)
            throw new ArgumentException("V4 snapshots require an empty, seekable writable stream.",nameof(destination));
        if (sdeBuild <= 0) throw new ArgumentOutOfRangeException(nameof(sdeBuild));

        var regionsToRead = regionIds.Distinct().Order().ToArray();
        if (regionsToRead.Any(id => id <= 0)) throw new ArgumentOutOfRangeException(nameof(regionIds));
        if (validTypeIds.Any(id => id <= 0)) throw new ArgumentOutOfRangeException(nameof(validTypeIds));
        if (npcStationIds.Any(id => id <= 0)) throw new ArgumentOutOfRangeException(nameof(npcStationIds));

        var capturedAt = capturedAtUtc.ToUniversalTime();
        await WriteLineAsync(destination,new HeaderLine("header","market-seed-v4-jsonl",SchemaVersion,Source,
            DataSource,CompatibilityDate,sdeBuild,capturedAt),cancellationToken);

        long retained = 0;
        var regions = new List<Region>(regionsToRead.Length);
        foreach (var regionId in regionsToRead)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var regionStart = destination.Position;
            Region? capturedRegion = null;

            for (var attempt = 1; attempt <= MaxRegionAttempts; attempt++)
            {
                destination.Position = regionStart;
                destination.SetLength(regionStart);
                var pages = new List<PageCache>();
                long regionRows = 0;
                int? pageCount = null;
                DateTimeOffset? pageSetLastModified = null;
                var lastModifiedSet = false;
                var inconsistent = false;
                DateTimeOffset latestExpiry = DateTimeOffset.MinValue;

                for (var page = 1; pageCount is null || page <= pageCount.Value; page++)
                {
                    var fetched = await FetchPageAsync(client,regionId,page,cancellationToken);
                    if (pageCount is null) pageCount = fetched.PageCount;
                    else if (fetched.PageCount != pageCount.Value) inconsistent = true;

                    if (fetched.Cache.Expires <= DateTimeOffset.UtcNow) inconsistent = true;
                    if (fetched.Cache.Expires > latestExpiry) latestExpiry = fetched.Cache.Expires;
                    if (!lastModifiedSet)
                    {
                        pageSetLastModified = fetched.Cache.LastModified;
                        lastModifiedSet = true;
                    }
                    else if (fetched.Cache.LastModified != pageSetLastModified) inconsistent = true;
                    pages.Add(fetched.Cache);

                    var orders = ParseOrders(fetched.Json,regionId,validTypeIds,npcStationIds,page);
                    foreach (var order in orders.OrderBy(order => order.OrderId))
                    {
                        await WriteLineAsync(destination,new OrderLine("order",order),cancellationToken);
                        regionRows++;
                    }
                }

                if (pages.Any(page => page.Expires <= DateTimeOffset.UtcNow)) inconsistent = true;
                if (inconsistent)
                {
                    if (attempt == MaxRegionAttempts)
                        throw new InvalidDataException($"ESI cache validators changed while reading region {regionId}.");
                    var wait = latestExpiry - DateTimeOffset.UtcNow;
                    if (wait > TimeSpan.Zero) await Task.Delay(wait + TimeSpan.FromSeconds(1),cancellationToken);
                    continue;
                }

                capturedRegion = new Region(regionId,pageCount!.Value,pageSetLastModified,pages);
                await WriteLineAsync(destination,new RegionLine("region",capturedRegion),cancellationToken);
                retained = checked(retained + regionRows);
                break;
            }

            if (capturedRegion is null)
                throw new InvalidDataException($"Could not capture a consistent ESI page set for region {regionId}.");
            regions.Add(capturedRegion);
        }

        await WriteLineAsync(destination,new CompleteLine("complete",retained,regions.Count),cancellationToken);
        await destination.FlushAsync(cancellationToken);
        return new Metadata(SchemaVersion,Source,DataSource,CompatibilityDate,sdeBuild,capturedAt,retained,regions);
    }

    public static Snapshot Read(Stream source,CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead) throw new ArgumentException("The snapshot stream must be readable.",nameof(source));
        var completion = new TaskCompletionSource<Metadata>(TaskCreationOptions.RunContinuationsAsynchronously);
        return new Snapshot(ReadOrdersAsync(source,completion,cancellationToken),completion.Task);
    }


    public static MarketNpcSeed ToSeed(Order order,StationCatalog stations)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(stations);
        var station=stations.Get(order.LocationId)
            ?? throw new InvalidDataException($"ESI order {order.StableKey} is not at a catalogued NPC station.");
        var range=order.Range switch
        {
            "station" => MarketRules.RangeStation,
            "solarsystem" => MarketRules.RangeSolarSystem,
            "region" => MarketRules.RangeRegion,
            _ when int.TryParse(order.Range,System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,out var jumps)
                && jumps is 1 or 2 or 3 or 4 or 5 or 10 or 20 or 30 or 40
                => jumps==4 ? MarketRules.RangeEsiFourJumps : jumps,
            _ => throw new InvalidDataException($"Unknown ESI market range '{order.Range}'."),
        };
        return new MarketNpcSeed(order.StableKey,checked((int)order.TypeId),order.IsBuyOrder,order.Price,
            order.VolumeTotal,range,MarketLocation.From(station),order.Issued.ToUnixTimeMilliseconds(),
            checked((int)order.Duration),order.MinimumVolume,order.VolumeRemain);
    }


    public static async IAsyncEnumerable<MarketNpcSeed> ToSeedsAsync(
        IAsyncEnumerable<Order> orders,StationCatalog stations,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var order in orders.WithCancellation(cancellationToken).ConfigureAwait(false))
            yield return ToSeed(order,stations);
    }

    private static async IAsyncEnumerable<Order> ReadOrdersAsync(
        Stream source,TaskCompletionSource<Metadata> completion,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var finished = false;
        try
        {
            using var reader = new StreamReader(source,System.Text.Encoding.UTF8,false,65536,true);
            var firstLine = await reader.ReadLineAsync(cancellationToken);
            var header = firstLine is null ? null : JsonSerializer.Deserialize<HeaderLine>(firstLine,JsonOptions);
            if (header is null || header.Record != "header" || header.Format != "market-seed-v4-jsonl" ||
                header.SchemaVersion != SchemaVersion || header.Source != Source || header.DataSource != DataSource ||
                header.CompatibilityDate != CompatibilityDate || header.SdeBuild <= 0 || header.CapturedAtUtc.Offset != TimeSpan.Zero)
                throw new InvalidDataException("Invalid or unsupported Market Seed v4 header.");

            long rowCount = 0;
            var regions = new List<Region>();
            long lastRegionId = 0;
            string? line;
            while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
            {
                using var json = JsonDocument.Parse(line);
                var record = json.RootElement.GetProperty("record").GetString();
                if (record == "order")
                {
                    var orderLine = JsonSerializer.Deserialize<OrderLine>(line,JsonOptions)
                        ?? throw new InvalidDataException("Invalid Market Seed v4 order line.");
                    ValidateSnapshotOrder(orderLine.Order);
                    rowCount++;
                    yield return orderLine.Order;
                }
                else if (record == "region")
                {
                    var regionLine = JsonSerializer.Deserialize<RegionLine>(line,JsonOptions)
                        ?? throw new InvalidDataException("Invalid Market Seed v4 region line.");
                    if (regionLine.Region.RegionId <= lastRegionId) throw new InvalidDataException("V4 regions are not in deterministic order.");
                    lastRegionId = regionLine.Region.RegionId;
                    regions.Add(regionLine.Region);
                }
                else if (record == "complete")
                {
                    var complete = JsonSerializer.Deserialize<CompleteLine>(line,JsonOptions)
                        ?? throw new InvalidDataException("Invalid Market Seed v4 completion line.");
                    if (complete.RetainedOrderCount != rowCount || complete.RegionCount != regions.Count ||
                        await reader.ReadLineAsync(cancellationToken) is not null)
                        throw new InvalidDataException("Market Seed v4 completion metadata does not match the streamed rows.");
                    completion.TrySetResult(new Metadata(header.SchemaVersion,header.Source,header.DataSource,
                        header.CompatibilityDate,header.SdeBuild,header.CapturedAtUtc,rowCount,regions));
                    finished = true;
                    yield break;
                }
                else
                {
                    throw new InvalidDataException($"Unknown Market Seed v4 record kind '{record}'.");
                }
            }

            throw new InvalidDataException("Market Seed v4 artifact is incomplete.");
        }
        finally
        {
            if (!finished) completion.TrySetCanceled(cancellationToken);
        }
    }

    private static async Task<FetchedPage> FetchPageAsync(HttpClient client,long regionId,int page,CancellationToken cancellationToken)
    {
        for (var retry = 0; ; retry++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"{Endpoint}/markets/{regionId}/orders/?datasource=tranquility&order_type=all&page={page}");
            request.Headers.TryAddWithoutValidation("X-Compatibility-Date",CompatibilityDate);
            request.Headers.UserAgent.ParseAdd("eve.rs-market-seed-v4/4 (+https://github.com/Fokiiiiiii/eve.rs)");
            using var response = await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,cancellationToken);

            if ((int)response.StatusCode == 429)
            {
                if (retry >= MaxRateLimitRetries) throw new HttpRequestException("ESI market order rate limit retry limit exceeded.",null,response.StatusCode);
                var retryAfter = response.Headers.RetryAfter;
                var delay = retryAfter?.Delta ?? (retryAfter?.Date is { } retryAt ? retryAt - DateTimeOffset.UtcNow : null);
                if (delay is null) throw new HttpRequestException("ESI returned 429 without Retry-After.",null,response.StatusCode);
                await Task.Delay(delay.Value > TimeSpan.Zero ? delay.Value : TimeSpan.Zero,cancellationToken);
                continue;
            }

            if ((int)response.StatusCode == 420)
            {
                var reset = GetHeader(response,"X-ESI-Error-Limit-Reset") ?? "unknown";
                throw new HttpRequestException($"ESI global error limit reached; reset in {reset} seconds.",null,response.StatusCode);
            }
            if (!response.IsSuccessStatusCode) response.EnsureSuccessStatusCode();

            var pageHeader = GetHeader(response,"X-Pages");
            if (!int.TryParse(pageHeader,System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,out var pageCount) || pageCount < 1)
                throw new InvalidDataException("ESI market order response has an invalid X-Pages header.");
            var expires = response.Content.Headers.Expires
                ?? throw new InvalidDataException("ESI market order response omitted Expires.");
            var lastModified = response.Content.Headers.LastModified;
            var cache = new PageCache(page,response.Headers.ETag?.ToString() ?? GetHeader(response,"ETag"),
                lastModified?.ToUniversalTime(),expires.ToUniversalTime());
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            return new FetchedPage(pageCount,cache,content);
        }
    }

    private static List<Order> ParseOrders(string json,long regionId,IReadOnlySet<long> validTypeIds,
        IReadOnlySet<long> npcStationIds,int page)
    {
        EsiOrder[]? rows;
        try { rows = JsonSerializer.Deserialize<EsiOrder[]>(json,JsonOptions); }
        catch (JsonException error) { throw new InvalidDataException($"Malformed ESI orders for region {regionId}, page {page}.",error); }
        if (rows is null) throw new InvalidDataException($"ESI returned a non-array order response for region {regionId}, page {page}.");

        var retained = new List<Order>(rows.Length);
        for (var index = 0; index < rows.Length; index++)
        {
            var row = rows[index] ?? throw new InvalidDataException($"Null ESI order at region {regionId}, page {page}, row {index}.");
            var orderId = Need(row.OrderId,"order_id",regionId,page,index);
            var typeId = Need(row.TypeId,"type_id",regionId,page,index);
            var locationId = Need(row.LocationId,"location_id",regionId,page,index);
            var systemId = Need(row.SystemId,"system_id",regionId,page,index);
            var buy = Need(row.IsBuyOrder,"is_buy_order",regionId,page,index);
            var price = Need(row.Price,"price",regionId,page,index);
            var total = Need(row.VolumeTotal,"volume_total",regionId,page,index);
            var remain = Need(row.VolumeRemain,"volume_remain",regionId,page,index);
            var minimum = Need(row.MinimumVolume,"min_volume",regionId,page,index);
            var range = row.Range;
            var issued = Need(row.Issued,"issued",regionId,page,index).ToUniversalTime();
            var duration = Need(row.Duration,"duration",regionId,page,index);
            if (orderId <= 0 || typeId <= 0 || locationId <= 0 || systemId <= 0 || price <= 0 ||
                total <= 0 || remain < 0 || remain > total || minimum <= 0 || duration <= 0 || string.IsNullOrWhiteSpace(range))
                throw new InvalidDataException($"Invalid ESI order values at region {regionId}, page {page}, row {index}.");

            // SDE membership alone defines a known type; station membership excludes every player structure.
            if (!validTypeIds.Contains(typeId) || !npcStationIds.Contains(locationId)) continue;
            retained.Add(new Order(regionId,orderId,typeId,locationId,systemId,buy,price,total,remain,minimum,range,issued,duration));
        }

        return retained;
    }

    private static T Need<T>(T? value,string field,long region,int page,int row) where T : struct =>
        value ?? throw new InvalidDataException($"Missing ESI {field} at region {region}, page {page}, row {row}.");

    private static void ValidateSnapshotOrder(Order order)
    {
        if (order.RegionId <= 0 || order.OrderId <= 0 || order.TypeId <= 0 || order.LocationId <= 0 ||
            order.SystemId <= 0 || order.Price <= 0 || order.VolumeTotal <= 0 || order.VolumeRemain < 0 ||
            order.VolumeRemain > order.VolumeTotal || order.MinimumVolume <= 0 || order.Duration <= 0 ||
            string.IsNullOrWhiteSpace(order.Range) || order.Issued.Offset != TimeSpan.Zero)
            throw new InvalidDataException("Invalid order in Market Seed v4 artifact.");
    }

    private static string? GetHeader(HttpResponseMessage response,string name)
    {
        if (response.Headers.TryGetValues(name,out var values)) return values.FirstOrDefault();
        if (response.Content.Headers.TryGetValues(name,out values)) return values.FirstOrDefault();
        return null;
    }

    private static async Task WriteLineAsync<T>(Stream destination,T value,CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value,JsonOptions);
        await destination.WriteAsync(bytes,cancellationToken);
        await destination.WriteAsync(NewLine,cancellationToken);
    }
}

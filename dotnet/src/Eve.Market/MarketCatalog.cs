// Static market groups and the offline v1 price fallback from EVE.js marketState.js; v3 cost/overlay parity remains open.
using System.Text.Json;
using Eve.Sde;
using Eve.Universe;

namespace Eve.Market;

public sealed record MarketGroup(int Id,int? ParentId,string Name,string Description,int? IconId,bool HasTypes);

/// <summary>Published SDE types and groups available without an EVE.js daemon or Node process.</summary>
public sealed class MarketCatalog(IReadOnlyList<MarketGroup> groups,IReadOnlyList<MarketNpcSeed> initialOrders)
{
    public IReadOnlyList<MarketGroup> Groups { get; } = groups;
    public IReadOnlyList<MarketNpcSeed> InitialOrders { get; } = initialOrders;

    public static async Task<MarketCatalog> LoadAsync(SdeReader sde,StationCatalog stations,long issuedMs,CancellationToken token = default)
    {
        const long jitaStationId = 60003760;
        var station = stations.Get(jitaStationId) ?? throw new InvalidOperationException("The Jita 4-4 seed station is absent from the pinned SDE.");
        var groups = (await sde.ReadAsync("marketGroups",cancellationToken:token)).Select(row =>
        {
            var doc=row.Doc;
            int? OptionalInt(string name) => doc.TryGetProperty(name,out var value) && value.ValueKind==JsonValueKind.Number ? value.GetInt32() : null;
            return new MarketGroup((int)row.Key,OptionalInt("parentGroupID"),SdeReader.English(doc.GetProperty("name")),
                doc.TryGetProperty("description",out var description) ? SdeReader.English(description) : "",OptionalInt("iconID"),
                doc.TryGetProperty("hasTypes",out var hasTypes) && hasTypes.ValueKind==JsonValueKind.True);
        }).ToArray();
        var location=MarketLocation.From(station);
        var orders=new List<MarketNpcSeed>();
        foreach (var row in await sde.ReadAsync("types",cancellationToken:token))
        {
            var doc=row.Doc;
            if (!doc.TryGetProperty("published",out var published) || published.ValueKind!=JsonValueKind.True ||
                !doc.TryGetProperty("marketGroupID",out var marketGroup) || marketGroup.ValueKind!=JsonValueKind.Number ||
                !doc.TryGetProperty("basePrice",out var basePrice) || basePrice.ValueKind!=JsonValueKind.Number) continue;
            var price=basePrice.GetDouble();
            if (!double.IsFinite(price) || price<=0 || row.Key is <=0 or >int.MaxValue) continue;
            var ask=MarketRules.RoundIsk(price*1.12);
            var bid=MarketRules.RoundIsk(price*0.92);
            if (bid<=0 || ask>MarketRules.MaximumOrderPrice) continue;
            orders.Add(new($"jita:{row.Key}:ask",(int)row.Key,false,(decimal)ask,1000,MarketRules.RangeRegion,location,issuedMs));
            orders.Add(new($"jita:{row.Key}:bid",(int)row.Key,true,(decimal)bid,1000,MarketRules.RangeRegion,location,issuedMs));
        }
        return new MarketCatalog(groups,orders);
    }
}

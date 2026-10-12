using Eve.Trade;
using Npgsql;
using static Eve.Host.HostConfiguration;

namespace Eve.Host;

internal sealed partial class CharacterServicesComposition
{
    private TradeAuthority tradeAuthority = default!;
    private Eve.Market.AdjustedPriceCache adjustedPrices = default!;
    private static readonly System.Net.Http.HttpClient adjustedPriceHttp = new();

    /// <summary>
    /// The ESI adjusted prices (tax and job cost input), in the market schema, refreshed at most once a day. EVE_ADJUSTED_PRICE_SEED=skip
    /// answers what is stored and calls nothing (CI, development); a type with no stored price has none, and none is invented.
    /// </summary>
    private void AdjustedPriceCacheSetup()
    {
        var esi = Env("EVE_ADJUSTED_PRICE_SEED", "auto") is "skip" or "off"
            ? null
            : new Eve.Market.EsiAdjustedPrices(adjustedPriceHttp, Eve.Market.EsiAdjustedPrices.DefaultBase);
        adjustedPrices = new Eve.Market.AdjustedPriceCache(new Eve.Market.AdjustedPriceStore(source, marketSchema), esi, () => now(), msg => Console.Error.WriteLine("eve-host: " + msg));
    }

    /// <summary>
    /// The direct-trade store (schema <c>trade</c>). A trade left open by a stopped host is ended first: its items go back to where they were,
    /// because no pilot is connected to continue it.
    /// </summary>
    private async Task TradeAuthorityStore()
    {
        var schema = Env("EVE_TRADE_SCHEMA", TradeAuthority.DefaultSchema);
        await TradeAuthority.MigrateAsync(source, schema);
        tradeAuthority = new TradeAuthority(source, items, economyWallets, wallets, now, schema);
        var ended = await tradeAuthority.RecoverOpenAsync();
        if (ended.Count > 0)
            Console.Error.WriteLine($"eve-host: {ended.Count} direct trade(s) left open by a stopped host were ended and their items returned");
    }
}

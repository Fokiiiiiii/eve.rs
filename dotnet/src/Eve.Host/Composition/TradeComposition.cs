using Eve.Trade;
using Eve.Corporations;
using Eve.Inventory;
using Eve.Services.Inventory;
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
        tradeAuthority = new TradeAuthority(source, items, economyWallets, wallets, now, schema, itemTypes,
            CanTradeCorporationOfficeItemAsync);
        var ended = await tradeAuthority.RecoverOpenAsync();
        if (ended.Count > 0)
            Console.Error.WriteLine($"eve-host: {ended.Count} direct trade(s) left open by a stopped host were ended and their items returned");
    }

    private async Task<bool> CanTradeCorporationOfficeItemAsync(long characterId, long corporationId, long stationId,
        int divisionFlag, long itemId, CancellationToken cancellationToken)
    {
        if (divisionFlag is < ItemFlags.CorpSAG1 or > ItemFlags.CorpSAG7
            || await corporations.GetAsync(corporationId, cancellationToken).ConfigureAwait(false) is not { IsNpc: false, Deleted: false } corporation
            || await corporations.MemberAsync(characterId, cancellationToken).ConfigureAwait(false) is not { } member
            || member.CorporationId != corporation.Id)
            return false;

        var office = (await corporationOfficeStore.ForCorporationAsync(corporationId, cancellationToken).ConfigureAwait(false))
            .SingleOrDefault(row => row.StationId == stationId);
        if (office is null || office.ExpiresAtMs <= now()
            || await items.GetAsync(office.OfficeFolderId, cancellationToken).ConfigureAwait(false) is not
                { TypeId: CorporationOfficeStore.OfficeFolderTypeId, OwnerId: var folderOwner, LocationId: var folderLocation, Flag: ItemFlags.Hangar }
            || folderOwner != corporationId || folderLocation != stationId)
            return false;

        var canTake = await HostInventoryServiceFactory.HangarAccessOf(corporations)(characterId, corporationId,
            stationId, divisionFlag, CorporationHangarOperation.Take, cancellationToken).ConfigureAwait(false);
        if (!canTake)
            return false;

        return !(await corporations.LockedItemsAsync(corporationId, stationId, cancellationToken).ConfigureAwait(false))
            .Any(locked => locked.ItemId == itemId);
    }
}

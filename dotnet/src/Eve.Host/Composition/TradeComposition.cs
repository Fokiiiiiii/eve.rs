using Eve.Trade;
using Npgsql;
using static Eve.Host.HostConfiguration;

namespace Eve.Host;

internal sealed partial class CharacterServicesComposition
{
    private TradeAuthority tradeAuthority = default!;

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

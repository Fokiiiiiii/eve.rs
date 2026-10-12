using Eve.Data;
using Eve.Inventory;
using Eve.Wallet;
using Npgsql;

namespace Eve.Trade.Tests;

public sealed class PgFactAttribute : FactAttribute
{
    public PgFactAttribute() { if (Environment.GetEnvironmentVariable("EVE_PG_URL") is null) Skip = "EVE_PG_URL is not set"; }
}

/// <summary>Stores in scratch schemas (dropped with the database at the end of the run): items, wallets and the trade, as one host.</summary>
internal sealed class TradeRig : IAsyncDisposable
{
    public const int Station = 60000001;
    public const int Hangar = ItemFlags.Hangar;
    public const int Cargo = ItemFlags.Cargo;
    public const int Tritanium = 34;

    private long _now = 1_790_000_000_000;

    private TradeRig(NpgsqlDataSource source, string suffix)
    {
        Source = source;
        Items = new PgItemStore(source, "ti_" + suffix);
        Wallets = new PgWalletStore(source, "tw_" + suffix);
        Transactions = new WalletTransactions(source, "tw_" + suffix);
        Trades = new TradeAuthority(source, Items, Transactions, Wallets, () => _now, "tt_" + suffix);
    }

    public NpgsqlDataSource Source { get; }
    public PgItemStore Items { get; }
    public PgWalletStore Wallets { get; }
    public WalletTransactions Transactions { get; }
    public TradeAuthority Trades { get; private set; }

    public static async Task<TradeRig> CreateAsync()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
        var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(url));
        var suffix = Guid.NewGuid().ToString("N")[..10];
        await PgItemStore.MigrateAsync(source, "ti_" + suffix);
        await PgWalletStore.MigrateAsync(source, "tw_" + suffix);
        await TradeAuthority.MigrateAsync(source, "tt_" + suffix);
        return new TradeRig(source, suffix);
    }

    public long Tick() => _now += 1000;

    public async Task OpenWalletAsync(long owner, decimal isk)
    {
        await using var connection = await Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await Wallets.OpenAsync(connection, transaction, owner, isk, 0);
        await transaction.CommitAsync();
    }

    public async Task<decimal> IskAsync(long owner) => (await Wallets.GetAsync(owner))!.Isk;

    public async Task<long> CreateItemAsync(long owner, long location, int flag, long quantity = 1, bool singleton = false, int typeId = Tritanium)
    {
        await using var connection = await Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var id = await Items.CreateAsync(connection, transaction, new NewItem(typeId, owner, location, flag, quantity, singleton));
        await transaction.CommitAsync();
        return id;
    }

    public async ValueTask DisposeAsync() => await Source.DisposeAsync();
}

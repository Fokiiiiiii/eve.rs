using Eve.Data;
using Npgsql;

namespace Eve.Wallet.Tests;

/// <summary>Skips a test, loudly, when there is no PostgreSQL to test against (see tools/dev/pg18.sh).</summary>
public sealed class PgFactAttribute : FactAttribute
{
    public PgFactAttribute()
    {
        if (Rig.Url is null)
        {
            Skip = "EVE_PG_URL is not set (see tools/dev/pg18.sh)";
        }
    }
}

internal sealed class Rig : IAsyncDisposable
{
    public static readonly string? Url = Environment.GetEnvironmentVariable("EVE_PG_URL");

    private readonly string _schema = $"wl_{Guid.NewGuid():N}"[..20];

    private Rig(NpgsqlDataSource source)
    {
        Source = source;
        Store = new PgWalletStore(source, _schema);
    }

    public NpgsqlDataSource Source { get; }

    public PgWalletStore Store { get; }

    public static async Task<Rig> CreateAsync()
    {
        var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(Url!));
        var rig = new Rig(source);
        await PgWalletStore.MigrateAsync(source, rig._schema);
        return rig;
    }

    public async Task OpenAsync(long owner, decimal isk, long plex, bool commit = true)
    {
        await using var connection = await Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await Store.OpenAsync(connection, transaction, owner, isk, plex);
        if (commit)
        {
            await transaction.CommitAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await using var command = Source.CreateCommand($"DROP SCHEMA IF EXISTS {_schema} CASCADE");
        await command.ExecuteNonQueryAsync();
        await Source.DisposeAsync();
    }
}

public class WalletTests
{
    [PgFact]
    public async Task A_wallet_holds_isk_to_the_cent_and_whole_plex()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.OpenAsync(140_000_001, 100_000m, 2_222);
        await rig.OpenAsync(140_000_002, 12.34m, 0);
        Assert.Equal(new Balance(140_000_001, 100_000m, 2_222), await rig.Store.GetAsync(140_000_001));
        Assert.Equal(12.34m, (await rig.Store.GetAsync(140_000_002))!.Isk);
        Assert.Null(await rig.Store.GetAsync(140_000_003));
    }

    [PgFact]
    public async Task Several_owners_are_read_at_once_and_the_ones_without_a_wallet_are_left_out()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.OpenAsync(1, 10m, 1);
        await rig.OpenAsync(2, 20m, 2);
        var balances = await rig.Store.ForOwnersAsync([1, 2, 3]);
        Assert.Equal([1L, 2L], balances.Keys.Order());
        Assert.Equal(20m, balances[2].Isk);
        Assert.Empty(await rig.Store.ForOwnersAsync([]));
    }

    [PgFact]
    public async Task An_owner_has_one_wallet_and_plex_cannot_be_negative()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.OpenAsync(1, 10m, 1);
        await Assert.ThrowsAsync<PostgresException>(() => rig.OpenAsync(1, 5m, 0));
        await Assert.ThrowsAsync<PostgresException>(() => rig.OpenAsync(2, 5m, -1));
        Assert.Equal(10m, (await rig.Store.GetAsync(1))!.Isk);
    }

    [PgFact]
    public async Task A_wallet_opened_in_a_transaction_that_is_rolled_back_does_not_exist()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.OpenAsync(1, 10m, 1, commit: false);
        Assert.Null(await rig.Store.GetAsync(1));
    }
}

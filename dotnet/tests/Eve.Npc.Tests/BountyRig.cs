using Eve.Data;
using Eve.Npc;
using Eve.Wallet;
using Npgsql;

namespace Eve.Npc.Tests;

/// <summary>Skips a test, loudly, when there is no PostgreSQL to test against (see tools/dev/pg18.sh).</summary>
public sealed class PgFactAttribute : FactAttribute
{
    public PgFactAttribute()
    {
        if (BountyRig.Url is null)
        {
            Skip = "EVE_PG_URL is not set (see tools/dev/pg18.sh)";
        }
    }
}

/// <summary>A wallet and a bounty ledger in schemas of their own, dropped when the test is over.</summary>
internal sealed class BountyRig : IAsyncDisposable
{
    public static readonly string? Url = Environment.GetEnvironmentVariable("EVE_PG_URL");

    private readonly string _walletSchema = $"bw_{Guid.NewGuid():N}"[..20];
    private readonly string _npcSchema = $"bn_{Guid.NewGuid():N}"[..20];

    public List<string> Log { get; } = [];

    private BountyRig(NpgsqlDataSource source)
    {
        Source = source;
        Wallet = new PgWalletStore(source, _walletSchema);
        Transactions = new WalletTransactions(source, _walletSchema);
        Store = new PgBountyStore(source, Transactions, _npcSchema, Log.Add);
    }

    public NpgsqlDataSource Source { get; }

    public string NpcSchema => _npcSchema;

    public PgWalletStore Wallet { get; }

    public WalletTransactions Transactions { get; }

    public PgBountyStore Store { get; }

    public static async Task<BountyRig> CreateAsync()
    {
        // shortcut: a test rig keeps at most 4 connections, since the server allows 100 for all lanes and test classes run in parallel;
        // the 24-way concurrent kill test queues on the pool instead of opening 24 connections at once
        var source = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(PgConnection.ToConnectionString(Url!)) { MaxPoolSize = 4 }.ConnectionString);
        var rig = new BountyRig(source);
        await PgWalletStore.MigrateAsync(source, rig._walletSchema);
        await PgBountyStore.MigrateAsync(source, rig._npcSchema);
        return rig;
    }

    public async Task OpenWalletAsync(long owner, decimal isk)
    {
        await using var connection = await Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await Wallet.OpenAsync(connection, transaction, owner, isk, 0);
        await transaction.CommitAsync();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var schema in new[] { _walletSchema, _npcSchema })
        {
            await using var command = Source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
            await command.ExecuteNonQueryAsync();
        }
        await Source.DisposeAsync();
    }
}

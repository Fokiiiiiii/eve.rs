using Eve.Data;
using Npgsql;

namespace Eve.Wallet.Tests;

/// <summary>A wallet schema of its own for the loyalty point tables (migration 006 of the wallet module).</summary>
internal sealed class LoyaltyRig : IAsyncDisposable
{
    private readonly string _schema;

    private LoyaltyRig(NpgsqlDataSource source, string schema)
    {
        Source = source;
        _schema = schema;
    }

    public NpgsqlDataSource Source { get; }

    public string Schema => _schema;

    public LoyaltyPointStore Store() => new(Source, _schema);

    public static async Task<LoyaltyRig> CreateAsync()
    {
        var schema = $"lp_{Guid.NewGuid():N}"[..20];
        var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(Rig.Url!));
        await PgWalletStore.MigrateAsync(source, schema);
        return new LoyaltyRig(source, schema);
    }

    public async ValueTask DisposeAsync()
    {
        await using var command = Source.CreateCommand($"DROP SCHEMA IF EXISTS {_schema} CASCADE");
        await command.ExecuteNonQueryAsync();
        await Source.DisposeAsync();
    }
}

/// <summary>Loyalty point balances: real rows, one journal row per applied change, and no double application.</summary>
public class LoyaltyPointStoreTests
{
    private static readonly LoyaltyHolder Pilot = new(LoyaltyHolderKind.Character, 140000001);
    private const long IssuerA = 1000125;
    private const long IssuerB = 1000127;

    [PgFact]
    public async Task A_credit_creates_the_balance_and_reads_back_in_issuer_order()
    {
        await using var rig = await LoyaltyRig.CreateAsync();
        var store = rig.Store();

        var first = await store.ApplyAndCommitAsync(Pilot, IssuerB, 300, "mission", "m-1");
        var second = await store.ApplyAndCommitAsync(Pilot, IssuerA, 120, "mission", "m-2");

        Assert.Equal(new LoyaltyPointChange(Pilot, IssuerB, 0, 300, "mission"), first);
        Assert.Equal(new LoyaltyPointChange(Pilot, IssuerA, 0, 120, "mission"), second);
        var balances = await store.ReadAsync(Pilot);
        Assert.Equal([new LoyaltyPointBalance(IssuerA, 120), new LoyaltyPointBalance(IssuerB, 300)], balances);
    }

    [PgFact]
    public async Task A_debit_lowers_the_balance_and_a_debit_below_zero_is_refused_without_a_change()
    {
        await using var rig = await LoyaltyRig.CreateAsync();
        var store = rig.Store();
        await store.ApplyAndCommitAsync(Pilot, IssuerA, 50, "mission", "credit-1");

        var debit = await store.ApplyAndCommitAsync(Pilot, IssuerA, -20, "purchase", "debit-1");
        Assert.Equal(new LoyaltyPointChange(Pilot, IssuerA, 50, 30, "purchase"), debit);

        await Assert.ThrowsAsync<InsufficientLoyaltyPointsException>(() =>
            store.ApplyAndCommitAsync(Pilot, IssuerA, -31, "purchase", "debit-2"));
        Assert.Equal([new LoyaltyPointBalance(IssuerA, 30)], await store.ReadAsync(Pilot));
    }

    [PgFact]
    public async Task A_reference_applies_once_per_holder_and_issuer()
    {
        await using var rig = await LoyaltyRig.CreateAsync();
        var store = rig.Store();
        var announced = new List<LoyaltyPointChange>();
        store.BalanceCommitted += announced.Add;

        var applied = await store.ApplyAndCommitAsync(Pilot, IssuerA, 75, "mission", "agent-award-7");
        var replay = await store.ApplyAndCommitAsync(Pilot, IssuerA, 75, "mission", "agent-award-7");
        var otherIssuer = await store.ApplyAndCommitAsync(Pilot, IssuerB, 75, "mission", "agent-award-7");

        Assert.NotNull(applied);
        Assert.Null(replay);
        Assert.NotNull(otherIssuer);
        Assert.Equal([new LoyaltyPointBalance(IssuerA, 75), new LoyaltyPointBalance(IssuerB, 75)], await store.ReadAsync(Pilot));
        Assert.Equal([applied, otherIssuer], announced);
    }

    [PgFact]
    public async Task A_rolled_back_change_leaves_no_balance_and_announces_nothing()
    {
        await using var rig = await LoyaltyRig.CreateAsync();
        var store = rig.Store();
        var announced = 0;
        store.BalanceCommitted += _ => announced++;

        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await store.ApplyAsync(connection, transaction, Pilot, IssuerA, 500, "mission", "rolled-back");
            await transaction.RollbackAsync();
        }

        Assert.Empty(await store.ReadAsync(Pilot));
        Assert.Equal(0, announced);
    }

    [PgFact]
    public async Task Corporation_balances_are_separate_from_the_character_and_survive_a_restart()
    {
        await using var rig = await LoyaltyRig.CreateAsync();
        var corporation = new LoyaltyHolder(LoyaltyHolderKind.Corporation, 98000001);
        await rig.Store().ApplyAndCommitAsync(corporation, IssuerA, 999, "donation", "corp-1");
        await rig.Store().ApplyAndCommitAsync(Pilot, IssuerA, 1, "mission", "char-1");

        // A new store on a new data source reads what the first one committed.
        await using var restarted = NpgsqlDataSource.Create(PgConnection.ToConnectionString(Rig.Url!));
        var reopened = new LoyaltyPointStore(restarted, rig.Schema);
        Assert.Equal([new LoyaltyPointBalance(IssuerA, 999)], await reopened.ReadAsync(corporation));
        Assert.Equal([new LoyaltyPointBalance(IssuerA, 1)], await reopened.ReadAsync(Pilot));
    }

    [PgFact]
    public async Task Concurrent_credits_to_one_balance_all_count()
    {
        await using var rig = await LoyaltyRig.CreateAsync();
        var store = rig.Store();

        await Task.WhenAll(Enumerable.Range(0, 20).Select(i =>
            store.ApplyAndCommitAsync(Pilot, IssuerA, 5, "mission", $"parallel-{i}")));

        Assert.Equal([new LoyaltyPointBalance(IssuerA, 100)], await store.ReadAsync(Pilot));
    }
}

using Eve.Data;
using Eve.Marshal;
using Eve.Npc;
using Eve.Services;
using Eve.Wallet;
using Npgsql;

namespace Eve.Services.Space.Tests;

/// <summary>An ESS store, a wallet and a bounty ledger in schemas of their own, dropped when the test is over.</summary>
internal sealed class EssRig : IAsyncDisposable
{
    public static readonly string? Url = Environment.GetEnvironmentVariable("EVE_PG_URL");

    private readonly string _walletSchema = $"ew_{Guid.NewGuid():N}"[..20];
    private readonly string _npcSchema = $"en_{Guid.NewGuid():N}"[..20];
    private readonly string _essSchema = $"es_{Guid.NewGuid():N}"[..20];

    /// <summary>What the link geometry answers (a test sets it to refuse or allow a link).</summary>
    public bool LinkAllowed { get; set; } = true;

    public List<string> Log { get; } = [];

    private EssRig(NpgsqlDataSource source)
    {
        Source = source;
        Wallets = new PgWalletStore(source, _walletSchema);
        Transactions = new WalletTransactions(source, _walletSchema);
        Bounties = new PgBountyStore(source, Transactions, _npcSchema, Log.Add);
        Ess = Store();
    }

    public NpgsqlDataSource Source { get; }

    public string EssSchema => _essSchema;

    public PgWalletStore Wallets { get; }

    public WalletTransactions Transactions { get; }

    public PgBountyStore Bounties { get; }

    public PgEssStore Ess { get; }

    /// <summary>A store of its own with another reserve payout (the default one pays nothing).</summary>
    public PgEssStore Store(double reserveMaxPayout = EssRules.ReserveBankMaxPayout) => new(
        Source, Transactions, _essSchema, Log.Add,
        linkGeometry: (_, _, _, _) => ValueTask.FromResult(LinkAllowed),
        reserveMaxPayout: reserveMaxPayout);

    public static async Task<EssRig> CreateAsync()
    {
        var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(Url!));
        var rig = new EssRig(source);
        await PgWalletStore.MigrateAsync(source, rig._walletSchema);
        await PgBountyStore.MigrateAsync(source, rig._npcSchema);
        await PgEssStore.MigrateAsync(source, rig._essSchema);
        return rig;
    }

    public async Task OpenWalletAsync(long owner, decimal isk)
    {
        await using var connection = await Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await Wallets.OpenAsync(connection, transaction, owner, isk, 0);
        await transaction.CommitAsync();
    }

    public async Task<decimal> BalanceAsync(long owner) =>
        await Transactions.AccountBalanceAsync(new WalletAccount(owner)) ?? throw new InvalidOperationException($"no wallet for {owner}");

    /// <summary>Sets the reserve balance directly: no rule puts money in a reserve yet (its share of a bounty is not in the sources).</summary>
    public async Task FundReserveAsync(int solarSystemId, decimal isk)
    {
        await using var command = Source.CreateCommand($"UPDATE {_essSchema}.systems SET reserve_value = $2 WHERE solar_system_id = $1");
        command.Parameters.AddWithValue(solarSystemId);
        command.Parameters.AddWithValue(isk);
        await command.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var schema in new[] { _walletSchema, _npcSchema, _essSchema })
        {
            await using var command = Source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
            await command.ExecuteNonQueryAsync();
        }
        await Source.DisposeAsync();
    }
}

public sealed class PgEssStoreTests
{
    private const int Jita = 30000142;
    private const int Elsewhere = 30000144;
    private const long Owner = 98_000_001;
    private const long Thief = 140_000_001;
    private const long Other = 140_000_002;
    private const long EssId = 1_030_000_000_001;
    private const long Now = 1_700_000_000_000;

    private static EssMgrOperationRequest Request(EssMgrOperation operation, int system, long character, long keyTypeId = 0) =>
        new(operation, system, character, keyTypeId, new ServiceCall { Method = operation.ToString(), Args = [], Session = new ClientSession(null) });

    [PgFact]
    public async Task A_bounty_in_an_ess_system_pays_its_main_bank_share_and_the_rest_to_the_pilot_in_one_transaction()
    {
        await using var rig = await EssRig.CreateAsync();
        await rig.Ess.RegisterAsync(Jita, EssId, Owner, Now);
        var (pilot, mainBank) = EssRules.SplitBounty(1001m, essOnline: true);

        var added = await rig.Bounties.RecordAsync(
            new BountyKill(Thief, Jita, 23245, 4_100_000_000_000_001, pilot), Now,
            inTransaction: (connection, transaction, token) => rig.Ess.CreditMainBankAsync(connection, transaction, Jita, mainBank, token));

        Assert.False(added.AlreadyRecorded);
        Assert.Equal(600.60m, Assert.Single(await rig.Bounties.PendingAsync()).Amount);
        Assert.Equal(400.40m, (await rig.Ess.ReadAsync(Jita))!.MainValue);
    }

    [PgFact]
    public async Task A_bounty_whose_bank_cannot_be_credited_is_not_recorded_for_the_pilot_either()
    {
        await using var rig = await EssRig.CreateAsync();
        var (pilot, mainBank) = EssRules.SplitBounty(1001m, essOnline: true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Bounties.RecordAsync(
            new BountyKill(Thief, Elsewhere, 23245, 4_100_000_000_000_002, pilot), Now,
            inTransaction: (connection, transaction, token) => rig.Ess.CreditMainBankAsync(connection, transaction, Elsewhere, mainBank, token)));

        Assert.Empty(await rig.Bounties.PendingAsync());
    }

    [PgFact]
    public async Task A_link_needs_the_geometry_and_only_one_pilot_holds_the_main_bank()
    {
        await using var rig = await EssRig.CreateAsync();
        await rig.Ess.RegisterAsync(Jita, EssId, Owner, Now);

        rig.LinkAllowed = false;
        var refused = await rig.Ess.ExecuteAsync(Request(EssMgrOperation.AttemptLinkToMainBank, Jita, Thief), Now);
        Assert.False(refused.Succeeded);
        Assert.Null((await rig.Ess.ReadAsync(Jita))!.MainLink);

        rig.LinkAllowed = true;
        var linked = await rig.Ess.ExecuteAsync(Request(EssMgrOperation.AttemptLinkToMainBank, Jita, Thief), Now);
        Assert.True(linked.Succeeded);
        Assert.Equal(Thief, linked.State!.MainBankLink!.CharacterId);
        Assert.Equal(NpcBounty.Filetime(Now + EssRules.LinkDurationMs), linked.State.MainBankLink.CompletesAtFileTime);

        var occupied = await rig.Ess.ExecuteAsync(Request(EssMgrOperation.AttemptLinkToMainBank, Jita, Other), Now + 1);
        Assert.False(occupied.Succeeded);
    }

    [PgFact]
    public async Task Only_the_thief_who_holds_the_main_bank_link_can_let_it_go()
    {
        await using var rig = await EssRig.CreateAsync();
        await rig.Ess.RegisterAsync(Jita, EssId, Owner, Now);
        await rig.Ess.ExecuteAsync(Request(EssMgrOperation.AttemptLinkToMainBank, Jita, Thief), Now);

        var notYours = await rig.Ess.ExecuteAsync(Request(EssMgrOperation.RequestMainBankUnlink, Jita, Other), Now);
        Assert.False(notYours.Succeeded);
        Assert.NotNull((await rig.Ess.ReadAsync(Jita))!.MainLink);

        var released = await rig.Ess.ExecuteAsync(Request(EssMgrOperation.RequestMainBankUnlink, Jita, Thief), Now);
        Assert.True(released.Succeeded);
        Assert.Equal(Thief, released.RemovedMainBankLink!.CharacterId);
        Assert.Null((await rig.Ess.ReadAsync(Jita))!.MainLink);
    }

    [PgFact]
    public async Task A_reserve_bank_opens_with_a_key_for_its_pulses_and_links_and_unlinks_by_character()
    {
        await using var rig = await EssRig.CreateAsync();
        await rig.Ess.RegisterAsync(Jita, EssId, Owner, Now);

        var notAKey = await rig.Ess.ExecuteAsync(Request(EssMgrOperation.RequestUnlockReserveBank, Jita, Thief, keyTypeId: 1), Now);
        Assert.False(notAKey.Succeeded);
        var opened = await rig.Ess.ExecuteAsync(Request(EssMgrOperation.RequestUnlockReserveBank, Jita, Thief, keyTypeId: 57714), Now);
        Assert.True(opened.Succeeded);
        Assert.Equal((15, 15), (opened.State!.ReserveBankPulsesTotal, opened.State.ReserveBankPulsesRemaining));
        Assert.Equal(NpcBounty.Filetime(Now), opened.State.ReserveBankLastPulseInitiatedFileTime);
        var alreadyOpen = await rig.Ess.ExecuteAsync(Request(EssMgrOperation.RequestUnlockReserveBank, Jita, Thief, keyTypeId: 57715), Now);
        Assert.False(alreadyOpen.Succeeded);

        var linked = await rig.Ess.ExecuteAsync(Request(EssMgrOperation.AttemptLinkToReserveBank, Jita, Thief), Now);
        Assert.Contains(Thief, linked.State!.ReserveLinkedCharacterIds!);
        var notLinked = await rig.Ess.ExecuteAsync(Request(EssMgrOperation.RequestReserveBankUnlink, Jita, Other), Now);
        Assert.False(notLinked.Succeeded);
        var unlinked = await rig.Ess.ExecuteAsync(Request(EssMgrOperation.RequestReserveBankUnlink, Jita, Thief), Now);
        Assert.True(unlinked.Succeeded);
        Assert.DoesNotContain(Thief, unlinked.State!.ReserveLinkedCharacterIds!);
    }

    [PgFact]
    public async Task A_thief_who_stays_until_the_link_completes_takes_four_fifths_of_the_bank()
    {
        await using var rig = await EssRig.CreateAsync();
        await rig.Ess.RegisterAsync(Jita, EssId, Owner, Now);
        await rig.OpenWalletAsync(Thief, 0);
        await rig.Ess.CreditAsync(Jita, 400.40m);
        await rig.Ess.ExecuteAsync(Request(EssMgrOperation.AttemptLinkToMainBank, Jita, Thief), Now);

        var events = await rig.Ess.TickAsync(Now + EssRules.LinkDurationMs - 1, (_, _) => true);
        Assert.Empty(events);
        events = await rig.Ess.TickAsync(Now + EssRules.LinkDurationMs, (_, _) => true);

        var theft = Assert.Single(events);
        Assert.Equal((EssEventKind.Theft, Thief, 320.32m), (theft.Kind, theft.CharacterId, theft.Amount));
        Assert.Equal(320.32m, await rig.BalanceAsync(Thief));
        var state = (await rig.Ess.ReadAsync(Jita))!;
        Assert.Equal((0m, (EssLinkRecord?)null), (state.MainValue, state.MainLink));
        Assert.Equal(Thief, Assert.Single(state.MainThefts).CharacterId);
    }

    [PgFact]
    public async Task A_link_whose_thief_has_left_the_system_ends_and_the_bank_keeps_its_money()
    {
        await using var rig = await EssRig.CreateAsync();
        await rig.Ess.RegisterAsync(Jita, EssId, Owner, Now);
        await rig.OpenWalletAsync(Thief, 0);
        await rig.Ess.CreditAsync(Jita, 400.40m);
        await rig.Ess.ExecuteAsync(Request(EssMgrOperation.AttemptLinkToMainBank, Jita, Thief), Now);

        var events = await rig.Ess.TickAsync(Now + EssRules.LinkDurationMs, (_, _) => false);

        var ended = Assert.Single(events);
        Assert.Equal((EssEventKind.LinkEnded, Thief, EssRules.UnlinkedProximity), (ended.Kind, ended.CharacterId, ended.Reason));
        var state = (await rig.Ess.ReadAsync(Jita))!;
        Assert.Equal((400.40m, (EssLinkRecord?)null), (state.MainValue, state.MainLink));
        Assert.Equal(0m, await rig.BalanceAsync(Thief));
    }

    [PgFact]
    public async Task The_owner_is_paid_the_main_bank_on_the_clock_and_its_next_payout_moves_on()
    {
        await using var rig = await EssRig.CreateAsync();
        await rig.Ess.RegisterAsync(Jita, EssId, Owner, Now);
        await rig.OpenWalletAsync(Owner, 0);
        await rig.Ess.CreditAsync(Jita, 400.40m);

        Assert.Empty(await rig.Ess.TickAsync(Now + EssRules.AutoPayIntervalMs - 1, (_, _) => true));
        var events = await rig.Ess.TickAsync(Now + EssRules.AutoPayIntervalMs, (_, _) => true);

        var paid = Assert.Single(events);
        Assert.Equal((EssEventKind.Changed, Owner, 400.40m), (paid.Kind, paid.CharacterId, paid.Amount));
        Assert.Equal(400.40m, await rig.BalanceAsync(Owner));
        var state = (await rig.Ess.ReadAsync(Jita))!;
        Assert.Equal((0m, Now + 2 * EssRules.AutoPayIntervalMs), (state.MainValue, state.NextPayoutMs));
    }

    [PgFact]
    public async Task An_owner_who_cannot_take_the_payout_keeps_the_money_and_is_paid_when_it_can()
    {
        await using var rig = await EssRig.CreateAsync();
        await rig.Ess.RegisterAsync(Jita, EssId, Owner, Now);
        await rig.Ess.CreditAsync(Jita, 400.40m);

        var events = await rig.Ess.TickAsync(Now + EssRules.AutoPayIntervalMs, (_, _) => true);

        Assert.Empty(events);
        Assert.Equal(400.40m, (await rig.Ess.ReadAsync(Jita))!.MainValue);
        Assert.Contains(rig.Log, line => line.Contains("could not be worked and is retried", StringComparison.Ordinal));
        await rig.OpenWalletAsync(Owner, 0);
        await rig.Ess.TickAsync(Now + EssRules.AutoPayIntervalMs, (_, _) => true);
        Assert.Equal(400.40m, await rig.BalanceAsync(Owner));
    }

    [PgFact]
    public async Task Each_reserve_pulse_that_is_due_pays_the_linked_characters_their_share_and_runs_the_bank_down()
    {
        await using var rig = await EssRig.CreateAsync();
        var store = rig.Store(reserveMaxPayout: 100);
        await store.RegisterAsync(Jita, EssId, null, Now);
        await rig.FundReserveAsync(Jita, 1000m);
        await rig.OpenWalletAsync(Thief, 0);
        await rig.OpenWalletAsync(Other, 0);
        await store.ExecuteAsync(Request(EssMgrOperation.RequestUnlockReserveBank, Jita, Thief, keyTypeId: 57714), Now);
        await store.ExecuteAsync(Request(EssMgrOperation.AttemptLinkToReserveBank, Jita, Thief), Now);
        await store.ExecuteAsync(Request(EssMgrOperation.AttemptLinkToReserveBank, Jita, Other), Now);

        await store.TickAsync(Now + EssRules.PulseIntervalMs, (_, _) => true);

        // pulse 1 of 15 pays 100 * (1 / 10)^2 = 1.00, shared by the two linked characters
        Assert.Equal(0.50m, await rig.BalanceAsync(Thief));
        Assert.Equal(0.50m, await rig.BalanceAsync(Other));
        var state = (await store.ReadAsync(Jita))!;
        Assert.Equal((999m, 14), (state.ReserveValue, state.PulsesRemaining));
        Assert.Equal(2, state.ReserveThefts.Count);
    }

}

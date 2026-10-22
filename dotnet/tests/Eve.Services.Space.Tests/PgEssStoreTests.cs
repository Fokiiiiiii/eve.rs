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

    /// <summary>What the simulation says of the pilot in the system (null: not in it, or it may not link); a test sets it.</summary>
    public EssHolder? Holder { get; set; } = new(true, true, 0, 500);

    /// <summary>Whether the character holds a key (the key is taken when a bank is opened).</summary>
    public bool KeyHeld { get; set; } = true;

    /// <summary>The quadrant of the systems (null: in none).</summary>
    public string? SystemQuadrant { get; set; } = "NW";

    /// <summary>What the key removal does inside the bank's transaction (a test writes a marker there, or fails after it).</summary>
    public Func<NpgsqlConnection, NpgsqlTransaction, Task>? KeyEffect { get; set; }

    public const long LinkedCorporation = 900_000_001;

    /// <summary>The alliance that holds the sovereignty of the systems (null: none does).</summary>
    public long? SovereigntyOwner { get; set; }

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

    /// <summary>A store of its own with another reserve payout (the default one is the reference's).</summary>
    public PgEssStore Store(double reserveMaxPayout = EssRules.ReserveBankMaxPayout) => new(
        Source, Transactions, _essSchema, Log.Add,
        holderOf: (_, _, _, _) => ValueTask.FromResult(Holder),
        consumeKey: async (connection, transaction, _, _, _, _) =>
        {
            if (!KeyHeld)
            {
                return false;
            }
            if (KeyEffect is { } effect)
            {
                await effect(connection, transaction);
            }
            return true;
        },
        quadrantOf: _ => SystemQuadrant,
        sovereigntyOwnerOf: (_, _) => ValueTask.FromResult(SovereigntyOwner),
        reserveMaxPayout: reserveMaxPayout,
        corporationOf: (_, _) => ValueTask.FromResult<long?>(LinkedCorporation));

    public static async Task<EssRig> CreateAsync()
    {
        var source = NpgsqlDataSource.Create(PgConnection.ForTest(Url!));
        var rig = new EssRig(source);
        await PgWalletStore.MigrateAsync(source, rig._walletSchema);
        await PgBountyStore.MigrateAsync(source, rig._npcSchema);
        await PgEssStore.MigrateAsync(source, rig._essSchema);
        await using (var create = source.CreateCommand($"CREATE TABLE {rig._essSchema}.key_marks (id bigserial PRIMARY KEY)"))
        {
            await create.ExecuteNonQueryAsync();
        }
        return rig;
    }

    /// <summary>The markers a key removal left in the bank's transaction (committed ones only, from another connection).</summary>
    public async Task<long> MarkerCountAsync()
    {
        await using var command = Source.CreateCommand($"SELECT count(*) FROM {_essSchema}.key_marks");
        return (long)(await command.ExecuteScalarAsync())!;
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

    /// <summary>Sets the reserve balance directly: a bounty's share is the only money that enters a reserve, and a test needs a funded one.</summary>
    public async Task OpenCorporationAsync(long corporationId)
    {
        await using var connection = await Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await Transactions.OpenCorporationDivisionsAsync(connection, transaction, corporationId);
        await transaction.CommitAsync();
    }

    public async Task<decimal> CorporationBalanceAsync(long corporationId) =>
        await Transactions.AccountBalanceAsync(new WalletAccount(corporationId, 1001))
        ?? throw new InvalidOperationException($"no corporation wallet for {corporationId}");

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

    private static EssMgrOperationRequest Request(EssMgrOperation operation, int system, long character, long keyTypeId = 0)
    {
        var session = new ClientSession(null);
        session.Set("shipid", new PyLong(98_000_012));
        return new EssMgrOperationRequest(operation, system, character, keyTypeId,
            new ServiceCall { Method = operation.ToString(), Args = [], Session = session });
    }

    [PgFact]
    public async Task A_bounty_in_an_ess_system_pays_its_shares_into_the_banks_and_the_rest_to_the_pilot_in_one_transaction()
    {
        await using var rig = await EssRig.CreateAsync();
        await rig.Ess.RegisterAsync(Jita, EssId, Owner, Now);
        var (pilot, mainBank, reserveBank) = EssRules.SplitBounty(1001m, essOnline: true);

        var added = await rig.Bounties.RecordAsync(
            new BountyKill(Thief, Jita, 23245, 4_100_000_000_000_001, pilot), Now,
            inTransaction: (connection, transaction, token) => rig.Ess.CreditBountyShareAsync(connection, transaction, Jita, mainBank, reserveBank, token));

        Assert.False(added.AlreadyRecorded);
        Assert.Equal(550.55m, Assert.Single(await rig.Bounties.PendingAsync()).Amount);
        var state = (await rig.Ess.ReadAsync(Jita))!;
        Assert.Equal((400.40m, 50.05m), (state.MainValue, state.ReserveValue));
    }

    [PgFact]
    public async Task A_bounty_whose_bank_cannot_be_credited_is_not_recorded_for_the_pilot_either()
    {
        await using var rig = await EssRig.CreateAsync();
        var (pilot, mainBank, reserveBank) = EssRules.SplitBounty(1001m, essOnline: true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Bounties.RecordAsync(
            new BountyKill(Thief, Elsewhere, 23245, 4_100_000_000_000_002, pilot), Now,
            inTransaction: (connection, transaction, token) => rig.Ess.CreditBountyShareAsync(connection, transaction, Elsewhere, mainBank, reserveBank, token)));

        Assert.Empty(await rig.Bounties.PendingAsync());
    }

    [PgFact]
    public async Task A_link_needs_a_linkable_ship_in_range_and_only_one_pilot_holds_the_main_bank()
    {
        await using var rig = await EssRig.CreateAsync();
        await rig.Ess.RegisterAsync(Jita, EssId, Owner, Now);

        rig.Holder = null;
        var refused = await rig.Ess.ExecuteAsync(Request(EssMgrOperation.AttemptLinkToMainBank, Jita, Thief), Now);
        Assert.False(refused.Succeeded);
        Assert.Null((await rig.Ess.ReadAsync(Jita))!.MainLink);

        rig.Holder = new EssHolder(true, LinkableShip: false, 0, 500);
        Assert.False((await rig.Ess.ExecuteAsync(Request(EssMgrOperation.AttemptLinkToMainBank, Jita, Thief), Now)).Succeeded);
        rig.Holder = new EssHolder(true, true, EssRules.MaxLinkDistanceMeters + 1, 500);
        Assert.False((await rig.Ess.ExecuteAsync(Request(EssMgrOperation.AttemptLinkToMainBank, Jita, Thief), Now)).Succeeded);

        rig.Holder = new EssHolder(true, true, 4_000, 500);
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
    public async Task A_reserve_bank_opens_with_a_key_of_its_quadrant_that_the_pilot_holds_and_links_and_unlinks_by_character()
    {
        await using var rig = await EssRig.CreateAsync();
        await rig.Ess.RegisterAsync(Jita, EssId, Owner, Now);

        var notAKey = await rig.Ess.ExecuteAsync(Request(EssMgrOperation.RequestUnlockReserveBank, Jita, Thief, keyTypeId: 1), Now);
        Assert.False(notAKey.Succeeded);
        rig.SystemQuadrant = "SE";
        var otherQuadrant = await rig.Ess.ExecuteAsync(Request(EssMgrOperation.RequestUnlockReserveBank, Jita, Thief, keyTypeId: 57714), Now);
        Assert.False(otherQuadrant.Succeeded);
        rig.SystemQuadrant = "NW";
        rig.KeyHeld = false;
        var noKey = await rig.Ess.ExecuteAsync(Request(EssMgrOperation.RequestUnlockReserveBank, Jita, Thief, keyTypeId: 57714), Now);
        Assert.False(noKey.Succeeded);
        rig.KeyHeld = true;

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
    public async Task The_key_is_taken_in_the_same_transaction_that_opens_the_bank()
    {
        await using var rig = await EssRig.CreateAsync();
        await rig.Ess.RegisterAsync(Jita, EssId, Owner, Now);
        rig.KeyEffect = (connection, transaction) => InsertMarkerAsync(connection, transaction, rig.EssSchema);

        var opened = await rig.Ess.ExecuteAsync(Request(EssMgrOperation.RequestUnlockReserveBank, Jita, Thief, keyTypeId: 57714), Now);

        Assert.True(opened.Succeeded);
        Assert.Equal((15, 1L), (opened.State!.ReserveBankPulsesRemaining, await rig.MarkerCountAsync()));
    }

    [PgFact]
    public async Task A_failure_after_the_key_is_taken_takes_the_key_back_and_the_bank_stays_shut()
    {
        await using var rig = await EssRig.CreateAsync();
        await rig.Ess.RegisterAsync(Jita, EssId, Owner, Now);
        rig.KeyEffect = async (connection, transaction) =>
        {
            await InsertMarkerAsync(connection, transaction, rig.EssSchema);
            throw new InvalidOperationException("the bank could not be opened");
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            rig.Ess.ExecuteAsync(Request(EssMgrOperation.RequestUnlockReserveBank, Jita, Thief, keyTypeId: 57714), Now));

        Assert.Equal((0L, 0), (await rig.MarkerCountAsync(), (await rig.Ess.ReadAsync(Jita))!.PulsesRemaining));
    }

    private static async Task InsertMarkerAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string schema)
    {
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = $"INSERT INTO {schema}.key_marks DEFAULT VALUES";
        await insert.ExecuteNonQueryAsync();
    }

    [PgFact]
    public async Task A_thief_who_stays_until_the_link_completes_takes_four_fifths_of_the_bank_and_the_rest_is_destroyed()
    {
        await using var rig = await EssRig.CreateAsync();
        await rig.Ess.RegisterAsync(Jita, EssId, Owner, Now);
        await rig.OpenWalletAsync(Thief, 0);
        await rig.Ess.CreditAsync(Jita, 400.40m, 0m);
        await rig.Ess.ExecuteAsync(Request(EssMgrOperation.AttemptLinkToMainBank, Jita, Thief), Now);

        Assert.Empty(await rig.Ess.TickAsync(Now + EssRules.LinkDurationMs - 1));
        var events = await rig.Ess.TickAsync(Now + EssRules.LinkDurationMs);

        var theft = Assert.Single(events);
        Assert.Equal((EssEventKind.Theft, Thief, 320.32m), (theft.Kind, theft.CharacterId, theft.Amount));
        Assert.Equal(320.32m, await rig.BalanceAsync(Thief));
        var state = (await rig.Ess.ReadAsync(Jita))!;
        Assert.Equal((0m, (EssLinkRecord?)null), (state.MainValue, state.MainLink));
        Assert.Equal(Thief, Assert.Single(state.MainThefts).CharacterId);
    }

    [PgFact]
    public async Task A_link_breaks_when_the_thief_leaves_the_system_changes_ship_or_goes_out_of_range()
    {
        await using var rig = await EssRig.CreateAsync();
        await rig.Ess.RegisterAsync(Jita, EssId, Owner, Now);
        await rig.OpenWalletAsync(Thief, 0);
        await rig.Ess.CreditAsync(Jita, 400.40m, 0m);

        await rig.Ess.ExecuteAsync(Request(EssMgrOperation.AttemptLinkToMainBank, Jita, Thief), Now);
        rig.Holder = null;
        var left = Assert.Single(await rig.Ess.TickAsync(Now + 1));
        Assert.Equal((EssEventKind.LinkEnded, EssRules.UnlinkedProximity), (left.Kind, left.Reason));

        rig.Holder = new EssHolder(true, true, 0, 500);
        await rig.Ess.ExecuteAsync(Request(EssMgrOperation.AttemptLinkToMainBank, Jita, Thief), Now + 2);
        rig.Holder = new EssHolder(true, true, 0, 999);
        var changed = Assert.Single(await rig.Ess.TickAsync(Now + 3));
        Assert.Equal((EssEventKind.LinkEnded, EssRules.UnlinkedShipChange), (changed.Kind, changed.Reason));

        rig.Holder = new EssHolder(true, true, 0, 500);
        await rig.Ess.ExecuteAsync(Request(EssMgrOperation.AttemptLinkToMainBank, Jita, Thief), Now + 4);
        rig.Holder = new EssHolder(true, true, EssRules.MaxLinkDistanceMeters + 1, 500);
        var far = Assert.Single(await rig.Ess.TickAsync(Now + 5));
        Assert.Equal((EssEventKind.LinkEnded, EssRules.UnlinkedProximity), (far.Kind, far.Reason));

        Assert.Equal((400.40m, 0m), ((await rig.Ess.ReadAsync(Jita))!.MainValue, await rig.BalanceAsync(Thief)));
    }

    [PgFact]
    public async Task The_owner_is_the_sovereignty_holder_when_there_is_one_and_is_paid_the_main_bank_on_the_clock()
    {
        const long Sovereign = 555_000_001;
        await using var rig = await EssRig.CreateAsync();
        await rig.Ess.RegisterAsync(Jita, EssId, Owner, Now);
        await rig.OpenWalletAsync(Owner, 0);
        await rig.OpenWalletAsync(Sovereign, 0);
        await rig.Ess.CreditAsync(Jita, 400.40m, 0m);
        rig.SovereigntyOwner = Sovereign;

        Assert.Empty(await rig.Ess.TickAsync(Now + EssRules.AutoPayIntervalMs - 1));
        var paid = Assert.Single(await rig.Ess.TickAsync(Now + EssRules.AutoPayIntervalMs));

        Assert.Equal((EssEventKind.Changed, Sovereign, 400.40m), (paid.Kind, paid.CharacterId, paid.Amount));
        Assert.Equal(400.40m, await rig.BalanceAsync(Sovereign));
        Assert.Equal(0m, await rig.BalanceAsync(Owner));
        var state = (await rig.Ess.ReadAsync(Jita))!;
        Assert.Equal((0m, Now + 2 * EssRules.AutoPayIntervalMs), (state.MainValue, state.NextPayoutMs));
    }

    [PgFact]
    public async Task An_owner_who_cannot_take_the_payout_keeps_the_money_and_is_paid_when_it_can()
    {
        await using var rig = await EssRig.CreateAsync();
        await rig.Ess.RegisterAsync(Jita, EssId, Owner, Now);
        await rig.Ess.CreditAsync(Jita, 400.40m, 0m);

        var events = await rig.Ess.TickAsync(Now + EssRules.AutoPayIntervalMs);

        Assert.Empty(events);
        Assert.Equal(400.40m, (await rig.Ess.ReadAsync(Jita))!.MainValue);
        Assert.Contains(rig.Log, line => line.Contains("could not be worked and is retried", StringComparison.Ordinal));
        await rig.OpenWalletAsync(Owner, 0);
        await rig.Ess.TickAsync(Now + EssRules.AutoPayIntervalMs);
        Assert.Equal(400.40m, await rig.BalanceAsync(Owner));
    }

    [PgFact]
public async Task Each_reserve_pulse_credits_the_linked_pilots_corporation_and_expires_after_its_key_duration()
{
    await using var rig = await EssRig.CreateAsync();
    var store = rig.Store(reserveMaxPayout: 100);
    await store.RegisterAsync(Jita, EssId, null, Now);
    await rig.FundReserveAsync(Jita, 1000m);
    await rig.OpenCorporationAsync(EssRig.LinkedCorporation);
    await store.ExecuteAsync(Request(EssMgrOperation.RequestUnlockReserveBank, Jita, Thief, keyTypeId: 57714), Now);
    await store.ExecuteAsync(Request(EssMgrOperation.AttemptLinkToReserveBank, Jita, Thief), Now);
    await store.ExecuteAsync(Request(EssMgrOperation.AttemptLinkToReserveBank, Jita, Other), Now);

    await store.TickAsync(Now + EssRules.PulseIntervalMs);

    // Pulse 1 of 15 pays 1.00 ISK; two linked pilots in one corporation share it into that corporation's first wallet division.
    Assert.Equal(1.00m, await rig.CorporationBalanceAsync(EssRig.LinkedCorporation));
    var firstJournal = await rig.Transactions.JournalForAccountAsync(EssRig.LinkedCorporation, 1001);
    var firstEntry = Assert.Single(firstJournal);
    Assert.Equal(1.00m, firstEntry.Amount);
    Assert.Equal("ESS reserve bank payout", firstEntry.Reason);
    var afterFirstPulse = (await store.ReadAsync(Jita))!;
    Assert.Equal((999m, 14), (afterFirstPulse.ReserveValue, afterFirstPulse.PulsesRemaining));
    Assert.Equal(2, afterFirstPulse.ReserveThefts.Count);

    await store.TickAsync(Now + EssRules.PulseIntervalMs);
    Assert.Equal(1.00m, await rig.CorporationBalanceAsync(EssRig.LinkedCorporation));
    Assert.Single(await rig.Transactions.JournalForAccountAsync(EssRig.LinkedCorporation, 1001));

    var expectedPayout = Enumerable.Range(1, 15)
        .Sum(pulse =>
        {
            var pulsePayout = EssRules.Money((decimal)EssRules.ReservePulsePayout(pulse, 15, 100));
            var pilotShare = EssRules.Money(pulsePayout / 2);
            return pilotShare * 2;
        });
    await store.TickAsync(Now + 15 * EssRules.PulseIntervalMs);

    var expired = (await store.ReadAsync(Jita))!;
    Assert.Equal(0, expired.PulsesRemaining);
    Assert.Null(expired.NextPulseMs);
    Assert.Empty(expired.ReserveLinked);
    Assert.Equal(1000m - expectedPayout, expired.ReserveValue);
    Assert.Equal(expectedPayout, await rig.CorporationBalanceAsync(EssRig.LinkedCorporation));
    Assert.Equal(15, (await rig.Transactions.JournalForAccountAsync(EssRig.LinkedCorporation, 1001)).Count);

    var refused = await store.ExecuteAsync(Request(EssMgrOperation.AttemptLinkToReserveBank, Jita, Thief), Now + 15 * EssRules.PulseIntervalMs);
    Assert.False(refused.Succeeded);
}
}

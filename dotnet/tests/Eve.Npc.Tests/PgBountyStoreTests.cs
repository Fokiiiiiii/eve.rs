using Eve.Npc;
using Eve.Wallet;

namespace Eve.Npc.Tests;

public sealed class PgBountyStoreTests
{
    private const long Character = 140_000_001;
    private const int Jita = 30000142;
    private const long Now = 1_700_000_000_000;
    private const long Window = NpcBounty.PayoutIntervalMs;
    private const long Corporation = 98_000_001;

    private static BountyKill Kill(long ship, decimal amount = 15_000m, int system = Jita, long character = Character, int type = 23245) =>
        new(character, system, type, 4_100_000_000_000_000 + ship, amount);

    [PgFact]
    public async Task The_first_kill_starts_a_window_and_a_later_one_joins_it()
    {
        await using var rig = await BountyRig.CreateAsync();

        var first = await rig.Store.RecordAsync(Kill(1), Now);
        var second = await rig.Store.RecordAsync(Kill(2, 5_000m, type: 17), Now + 60_000);

        Assert.Equal(new BountyAdded(23245, 15_000m, NpcBounty.Filetime(Now + Window), false, false), first);
        Assert.Equal(first.PayoutTime, second.PayoutTime);
        // what the client is told is what this kill added, not what the bucket holds
        Assert.Equal(5_000m, second.Amount);
        var bucket = Assert.Single(await rig.Store.PendingAsync());
        Assert.Equal((Character, Jita, Now + Window, 20_000m, 2), (bucket.CharacterId, bucket.SolarSystemId, bucket.PayoutAtMs, bucket.Amount, bucket.Kills));
        Assert.Equal(new Dictionary<int, int> { [23245] = 1, [17] = 1 }, bucket.NpcTypes);
        Assert.Equal([4_100_000_000_000_001, 4_100_000_000_000_002], bucket.SourceReferences);
    }

    [PgFact]
    public async Task A_kill_in_another_system_is_a_bucket_of_its_own_in_the_same_window_and_another_character_has_a_window_of_its_own()
    {
        await using var rig = await BountyRig.CreateAsync();

        await rig.Store.RecordAsync(Kill(1), Now);
        var elsewhere = await rig.Store.RecordAsync(Kill(2, system: 30000144), Now + 1_000);
        var other = await rig.Store.RecordAsync(Kill(3, character: 140_000_002), Now + 2_000);

        Assert.Equal(NpcBounty.Filetime(Now + Window), elsewhere.PayoutTime);
        Assert.Equal(NpcBounty.Filetime(Now + 2_000 + Window), other.PayoutTime);
        Assert.Equal(3, (await rig.Store.PendingAsync()).Count);
    }

    [PgFact]
    public async Task A_victim_that_is_in_a_bucket_is_not_paid_twice()
    {
        await using var rig = await BountyRig.CreateAsync();

        var first = await rig.Store.RecordAsync(Kill(1), Now);
        var again = await rig.Store.RecordAsync(Kill(1), Now + 5_000);

        Assert.False(first.AlreadyRecorded);
        Assert.True(again.AlreadyRecorded);
        Assert.Equal(first.PayoutTime, again.PayoutTime);
        var bucket = Assert.Single(await rig.Store.PendingAsync());
        Assert.Equal((15_000m, 1), (bucket.Amount, bucket.Kills));
    }

    [PgFact]
    public async Task A_bucket_is_paid_when_it_is_due_into_the_wallet_with_a_journal_entry_and_is_gone()
    {
        await using var rig = await BountyRig.CreateAsync();
        await rig.OpenWalletAsync(Character, 1_000m);
        await rig.Store.RecordAsync(Kill(1), Now);

        Assert.Empty(await rig.Store.PayDueAsync(Now + Window - 1));
        Assert.Equal(1_000m, (await rig.Wallet.GetAsync(Character))!.Isk);
        var paid = Assert.Single(await rig.Store.PayDueAsync(Now + Window));

        Assert.Equal((Character, 15_000m, 1, 16_000m), (paid.CharacterId, paid.Amount, paid.Kills, paid.Balance));
        Assert.Equal(16_000m, (await rig.Wallet.GetAsync(Character))!.Isk);
        Assert.Empty(await rig.Store.PendingAsync());
        var entry = Assert.Single(await rig.Transactions.JournalAsync(Character));
        Assert.Equal((15_000m, 16_000m, NpcBounty.PrizeRefType, Now + Window), (entry.Amount, entry.Balance, entry.RefTypeId, entry.TimeMs));
        Assert.Equal(Character, entry.CounterpartyId);
        Assert.Equal("NBL:\n  23245: 1\nsolarSystemID: 30000142\nkills: 1", entry.Reason);
        Assert.Empty(await rig.Store.PayDueAsync(Now + 2 * Window));
    }

    [PgFact]
    public async Task Several_kills_are_paid_in_one_credit_under_the_entry_type_of_bounty_prizes()
    {
        await using var rig = await BountyRig.CreateAsync();
        await rig.OpenWalletAsync(Character, 0m);
        for (var ship = 1; ship <= 3; ship++)
        {
            await rig.Store.RecordAsync(Kill(ship, 1_000m), Now + ship);
        }

        var paid = Assert.Single(await rig.Store.PayDueAsync(Now + 10 * Window));

        Assert.Equal((3_000m, 3), (paid.Amount, paid.Kills));
        var entry = Assert.Single(await rig.Transactions.JournalAsync(Character));
        Assert.Equal(NpcBounty.PrizesRefType, entry.RefTypeId);
        Assert.Equal("NBL:\n  23245: 3\nsolarSystemID: 30000142\nkills: 3", entry.Reason);
    }

    [Fact]
    public void The_corporation_tax_is_none_below_the_minimum_and_otherwise_the_rate_to_the_cent()
    {
        Assert.Equal(0m, NpcBounty.CorporationTaxOf(99_999.99m, 0.1m));
        Assert.Equal(10_000m, NpcBounty.CorporationTaxOf(100_000m, 0.1m));
        Assert.Equal(7_500.08m, NpcBounty.CorporationTaxOf(100_001.05m, 0.075m));
        Assert.Equal(0m, NpcBounty.CorporationTaxOf(500_000m, 0m));
    }

    [PgFact]
    public async Task A_bounty_pays_the_corporation_its_tax_in_the_same_payout_and_the_member_the_net()
    {
        await using var rig = await BountyRig.CreateAsync((_, _) => Task.FromResult<BountyTaxTerms?>(new BountyTaxTerms(Corporation, false, 0.1m)));
        await rig.OpenWalletAsync(Character, 1_000m);
        await rig.OpenWalletAsync(Corporation, 0m);
        await rig.Store.RecordAsync(Kill(1, 60_000m), Now);
        await rig.Store.RecordAsync(Kill(2, 60_000m), Now + 1);

        var paid = Assert.Single(await rig.Store.PayDueAsync(Now + Window));

        Assert.Equal((120_000m, 2, 109_000m), (paid.Amount, paid.Kills, paid.Balance));
        Assert.Equal(12_000m, (await rig.Wallet.GetAsync(Corporation))!.Isk);
        var member = Assert.Single(await rig.Transactions.JournalAsync(Character));
        Assert.Equal((108_000m, NpcBounty.PrizesRefType), (member.Amount, member.RefTypeId));
        Assert.Equal("NBL:\n  23245: 2\nsolarSystemID: 30000142\nkills: 2\n92: [98000001, 12000]", member.Reason);
        var corporate = Assert.Single(await rig.Transactions.JournalAsync(Corporation));
        Assert.Equal((12_000m, NpcBounty.CorporationTaxRefType, Character, "Corporation tax on pirate bounties"),
            (corporate.Amount, corporate.RefTypeId, corporate.CounterpartyId, corporate.Reason));
        Assert.Empty(await rig.Store.PendingAsync());
    }

    [PgFact]
    public async Task An_npc_corporation_takes_its_tax_from_the_member_and_is_not_credited()
    {
        await using var rig = await BountyRig.CreateAsync((_, _) => Task.FromResult<BountyTaxTerms?>(new BountyTaxTerms(Corporation, true, 0.1m)));
        await rig.OpenWalletAsync(Character, 0m);
        await rig.Store.RecordAsync(Kill(1, 120_000m), Now);

        var paid = Assert.Single(await rig.Store.PayDueAsync(Now + Window));

        Assert.Equal(108_000m, paid.Balance);
        Assert.Empty(await rig.Transactions.JournalAsync(Corporation));
    }

    [PgFact]
    public async Task A_bucket_whose_character_has_no_wallet_stays_and_is_paid_once_there_is_one_and_the_rest_are_not_held_up()
    {
        await using var rig = await BountyRig.CreateAsync();
        await rig.OpenWalletAsync(140_000_002, 0m);
        await rig.Store.RecordAsync(Kill(1), Now);
        await rig.Store.RecordAsync(Kill(2, character: 140_000_002), Now);

        var paid = await rig.Store.PayDueAsync(Now + Window);

        Assert.Equal(140_000_002, Assert.Single(paid).CharacterId);
        Assert.Equal(Character, Assert.Single(await rig.Store.PendingAsync()).CharacterId);
        Assert.Single(rig.Log);
        Assert.Empty(await rig.Transactions.JournalAsync(Character));

        await rig.OpenWalletAsync(Character, 5m);
        Assert.Equal(15_005m, Assert.Single(await rig.Store.PayDueAsync(Now + Window + 1)).Balance);
        Assert.Empty(await rig.Store.PendingAsync());
    }

    [PgFact]
    public async Task A_bounty_that_would_pass_the_balance_ceiling_is_not_paid_and_waits()
    {
        await using var rig = await BountyRig.CreateAsync();
        await rig.OpenWalletAsync(Character, WalletLimits.MaximumIsk - 10_000m);
        await rig.Store.RecordAsync(Kill(1), Now);

        Assert.Empty(await rig.Store.PayDueAsync(Now + Window));

        Assert.Equal(WalletLimits.MaximumIsk - 10_000m, (await rig.Wallet.GetAsync(Character))!.Isk);
        Assert.Empty(await rig.Transactions.JournalAsync(Character));
        Assert.Single(await rig.Store.PendingAsync());
        Assert.Contains("ceiling", rig.Log.Single());
    }

    [PgFact]
    public async Task Two_payers_of_one_bucket_credit_it_once()
    {
        await using var rig = await BountyRig.CreateAsync();
        await rig.OpenWalletAsync(Character, 0m);
        await rig.Store.RecordAsync(Kill(1), Now);
        var key = Assert.Single(await rig.Store.PendingAsync()).Key;

        var payments = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => rig.Store.PayAsync(key, Now + Window)));

        Assert.Equal(1, payments.Count(payment => payment is not null));
        Assert.Equal(15_000m, (await rig.Wallet.GetAsync(Character))!.Isk);
        Assert.Single(await rig.Transactions.JournalAsync(Character));
    }

    [PgFact]
    public async Task Kills_recorded_at_the_same_time_make_one_bucket_with_all_of_them()
    {
        await using var rig = await BountyRig.CreateAsync();

        await Task.WhenAll(Enumerable.Range(1, 24).Select(ship => rig.Store.RecordAsync(Kill(ship, 100m), Now)));

        var bucket = Assert.Single(await rig.Store.PendingAsync());
        Assert.Equal((2_400m, 24), (bucket.Amount, bucket.Kills));
        Assert.Equal(24, bucket.SourceReferences.Distinct().Count());
    }

    [PgFact]
    public async Task A_character_whose_kills_are_being_recorded_has_its_next_kill_wait_for_them()
    {
        await using var rig = await BountyRig.CreateAsync();
        // another transaction holds the character's lock, as a kill that is being recorded does
        await using var holder = await rig.Source.OpenConnectionAsync();
        await using var transaction = await holder.BeginTransactionAsync();
        await using (var lockIt = new Npgsql.NpgsqlCommand("SELECT pg_advisory_xact_lock($1)", holder, transaction) { Parameters = { new() { Value = Character } } })
        {
            await lockIt.ExecuteNonQueryAsync();
        }

        var recording = rig.Store.RecordAsync(Kill(1), Now);
        var waited = await Task.WhenAny(recording, Task.Delay(500));
        Assert.NotSame(recording, waited);
        // other characters are not held up
        await rig.Store.RecordAsync(Kill(2, character: 140_000_002), Now);

        await transaction.CommitAsync();
        await recording.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, (await rig.Store.PendingAsync()).Count);
    }

    [PgFact]
    public async Task What_is_waiting_is_in_the_database_so_another_store_finds_it_as_after_a_restart()
    {
        await using var rig = await BountyRig.CreateAsync();
        await rig.OpenWalletAsync(Character, 0m);
        await rig.Store.RecordAsync(Kill(1), Now);

        var restarted = new PgBountyStore(rig.Source, rig.Transactions, rig.NpcSchema);

        Assert.Equal(15_000m, Assert.Single(await restarted.PendingAsync()).Amount);
        Assert.Equal(15_000m, Assert.Single(await restarted.PayDueAsync(Now + Window)).Balance);
        Assert.Empty(await rig.Store.PendingAsync());
    }

    [PgFact]
    public async Task A_kill_after_a_payout_starts_a_new_window()
    {
        await using var rig = await BountyRig.CreateAsync();
        await rig.OpenWalletAsync(Character, 0m);
        await rig.Store.RecordAsync(Kill(1), Now);
        await rig.Store.PayDueAsync(Now + Window);

        var next = await rig.Store.RecordAsync(Kill(2), Now + Window + 1);

        Assert.Equal(NpcBounty.Filetime(Now + Window + 1 + Window), next.PayoutTime);
    }
}

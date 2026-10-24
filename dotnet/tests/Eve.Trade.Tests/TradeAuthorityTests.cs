using Eve.Inventory;
using Eve.Trade;
using Eve.Wallet;

namespace Eve.Trade.Tests;

public sealed class TradeAuthorityTests
{
    private const long Alice = 9101;
    private const long Bob = 9102;

    private static async Task<TradeRig> RigWithTwoPilotsAsync(decimal aliceIsk = 1000m, decimal bobIsk = 1000m)
    {
        var rig = await TradeRig.CreateAsync();
        await rig.OpenWalletAsync(Alice, aliceIsk);
        await rig.OpenWalletAsync(Bob, bobIsk);
        return rig;
    }

    [PgFact]
    public async Task Initiate_opens_one_trade_per_pilot_and_refuses_a_second()
    {
        await using var rig = await RigWithTwoPilotsAsync();
        var view = await rig.Trades.InitiateAsync(Alice, Bob, TradeRig.Station, 30000142);

        Assert.True(view.IsOpen);
        Assert.Equal(new long[] { Alice, Bob }, new[] { view.InitiatorId, view.TargetId });
        Assert.Equal(TradeRig.Station, view.LocationId);
        Assert.Empty(view.Items);
        Assert.Equal(view.TradeId, (await rig.Trades.OpenViewForAsync(Bob))!.TradeId);

        await rig.OpenWalletAsync(9103, 0m);
        var refused = await Assert.ThrowsAsync<TradeRefusedException>(() => rig.Trades.InitiateAsync(9103, Alice, TradeRig.Station, 30000142));
        Assert.Equal("One of those pilots is already in another direct trade.", refused.Message);
    }

    [PgFact]
    public async Task Stage_moves_a_whole_item_into_the_container_and_abort_returns_it_to_its_origin()
    {
        await using var rig = await RigWithTwoPilotsAsync();
        var view = await rig.Trades.InitiateAsync(Alice, Bob, TradeRig.Station, 30000142);
        var item = await rig.CreateItemAsync(Alice, TradeRig.Station, TradeRig.Hangar, quantity: 40);

        var change = await rig.Trades.StageAsync(view.TradeId, Alice, item, 0);

        Assert.Equal(view.TradeId, (await rig.Items.GetAsync(item))!.LocationId);
        Assert.Single(change.View.Items);
        Assert.Equal(item, change.Moves.Single().After.Id);
        Assert.Equal(TradeRig.Station, change.Moves.Single().PreviousLocationId);

        var aborted = await rig.Trades.AbortAsync(view.TradeId);
        Assert.NotNull(aborted);
        Assert.Equal("aborted", aborted!.View.Status);
        var back = (await rig.Items.GetAsync(item))!;
        Assert.Equal((TradeRig.Station, TradeRig.Hangar, Alice), (back.LocationId, back.Flag, back.OwnerId));
        Assert.Null(await rig.Trades.OpenViewForAsync(Alice));
    }

    [PgFact]
    public async Task Stage_splits_a_partial_stack_and_abort_merges_it_back_into_the_source()
    {
        await using var rig = await RigWithTwoPilotsAsync();
        var view = await rig.Trades.InitiateAsync(Alice, Bob, TradeRig.Station, 30000142);
        var source = await rig.CreateItemAsync(Alice, TradeRig.Station, TradeRig.Hangar, quantity: 10);

        var change = await rig.Trades.StageAsync(view.TradeId, Alice, source, 3);
        var staged = change.View.Items.Single();
        Assert.NotEqual(source, staged.Id);
        Assert.Equal(3, staged.Quantity);
        Assert.Equal(7, (await rig.Items.GetAsync(source))!.Quantity);

        await rig.Trades.AbortAsync(view.TradeId);
        var restored = (await rig.Items.GetAsync(source))!;
        Assert.Equal((10, TradeRig.Station, TradeRig.Hangar), (restored.Quantity, restored.LocationId, restored.Flag));
        Assert.Null(await rig.Items.GetAsync(staged.Id));
    }

    [PgFact]
    public async Task Stage_refuses_an_item_outside_the_hangar_or_not_owned_by_the_offering_pilot()
    {
        await using var rig = await RigWithTwoPilotsAsync();
        var view = await rig.Trades.InitiateAsync(Alice, Bob, TradeRig.Station, 30000142);
        var cargo = await rig.CreateItemAsync(Alice, TradeRig.Station, TradeRig.Cargo);
        var bobs = await rig.CreateItemAsync(Bob, TradeRig.Station, TradeRig.Hangar);

        var notHangar = await Assert.ThrowsAsync<TradeRefusedException>(() => rig.Trades.StageAsync(view.TradeId, Alice, cargo, 0));
        var notOwned = await Assert.ThrowsAsync<TradeRefusedException>(() => rig.Trades.StageAsync(view.TradeId, Alice, bobs, 0));

        Assert.Equal("Only items in your hangar here can be offered.", notHangar.Message);
        Assert.Equal("Only items in your hangar here can be offered.", notOwned.Message);
        Assert.Empty((await rig.Trades.ViewAsync(view.TradeId))!.Items);
    }

    [PgFact]
    public async Task Money_cannot_exceed_the_wallet_and_any_change_clears_both_acceptances()
    {
        await using var rig = await RigWithTwoPilotsAsync(aliceIsk: 500m);
        var view = await rig.Trades.InitiateAsync(Alice, Bob, TradeRig.Station, 30000142);

        var over = await Assert.ThrowsAsync<TradeRefusedException>(() => rig.Trades.SetMoneyAsync(view.TradeId, Alice, 501m));
        Assert.Equal("You do not have enough ISK to offer that amount.", over.Message);

        await rig.Trades.SetMoneyAsync(view.TradeId, Alice, 200m);
        var accepted = await rig.Trades.OfferAsync(view.TradeId, Alice, [200m, 0m], new HashSet<long>());
        Assert.Equal(TradeOfferOutcome.Pending, accepted.Outcome);
        Assert.True(accepted.Change.View.InitiatorAccepted);

        var changed = await rig.Trades.SetMoneyAsync(view.TradeId, Alice, 300m);
        Assert.False(changed.View.InitiatorAccepted);
        Assert.Equal(300m, changed.View.InitiatorIsk);
    }

    [PgFact]
    public async Task Completion_moves_items_and_isk_in_one_transaction_when_both_accept_the_same_manifest()
    {
        await using var rig = await RigWithTwoPilotsAsync(aliceIsk: 1000m, bobIsk: 200m);
        var view = await rig.Trades.InitiateAsync(Alice, Bob, TradeRig.Station, 30000142);
        var aliceItem = await rig.CreateItemAsync(Alice, TradeRig.Station, TradeRig.Hangar, quantity: 5);
        var bobItem = await rig.CreateItemAsync(Bob, TradeRig.Station, TradeRig.Hangar, quantity: 2);
        await rig.Trades.StageAsync(view.TradeId, Alice, aliceItem, 0);
        await rig.Trades.StageAsync(view.TradeId, Bob, bobItem, 0);
        await rig.Trades.SetMoneyAsync(view.TradeId, Alice, 300m);
        await rig.Trades.SetMoneyAsync(view.TradeId, Bob, 50m);
        var manifest = (await rig.Trades.ViewAsync(view.TradeId))!;
        var items = manifest.Items.Select(i => i.Id).ToHashSet();

        var first = await rig.Trades.OfferAsync(view.TradeId, Alice, [300m, 50m], items);
        Assert.Equal(TradeOfferOutcome.Pending, first.Outcome);
        Assert.Equal(1000m, await rig.IskAsync(Alice));

        var done = await rig.Trades.OfferAsync(view.TradeId, Bob, [300m, 50m], items);
        Assert.Equal(TradeOfferOutcome.Completed, done.Outcome);
        Assert.Equal("completed", done.Change.View.Status);
        Assert.Equal(2, done.Change.Moves.Count);

        Assert.Equal(Bob, (await rig.Items.GetAsync(aliceItem))!.OwnerId);
        Assert.Equal(Alice, (await rig.Items.GetAsync(bobItem))!.OwnerId);
        Assert.Equal(TradeRig.Station, (await rig.Items.GetAsync(aliceItem))!.LocationId);
        Assert.Equal(750m, await rig.IskAsync(Alice));
        Assert.Equal(450m, await rig.IskAsync(Bob));  // 200 + 300 received - 50 paid
        Assert.Null(await rig.Trades.OpenViewForAsync(Alice));
        Assert.NotNull(await rig.Trades.InitiateAsync(Alice, Bob, TradeRig.Station, 30000142));
    }

    [PgFact]
    public async Task A_manifest_that_is_not_the_trade_clears_the_acceptances_and_moves_nothing()
    {
        await using var rig = await RigWithTwoPilotsAsync();
        var view = await rig.Trades.InitiateAsync(Alice, Bob, TradeRig.Station, 30000142);
        var item = await rig.CreateItemAsync(Alice, TradeRig.Station, TradeRig.Hangar);
        await rig.Trades.StageAsync(view.TradeId, Alice, item, 0);
        var items = (await rig.Trades.ViewAsync(view.TradeId))!.Items.Select(i => i.Id).ToHashSet();

        await rig.Trades.OfferAsync(view.TradeId, Alice, [0m, 0m], items);
        var mismatch = await rig.Trades.OfferAsync(view.TradeId, Bob, [0m, 0m], new HashSet<long> { 1 });

        Assert.Equal(TradeOfferOutcome.Mismatch, mismatch.Outcome);
        Assert.False(mismatch.Change.View.InitiatorAccepted);
        Assert.False(mismatch.Change.View.TargetAccepted);
        Assert.Equal(item, (await rig.Items.GetAsync(item))!.Id);
        Assert.Equal(view.TradeId, (await rig.Items.GetAsync(item))!.LocationId);
        Assert.True((await rig.Trades.ViewAsync(view.TradeId))!.IsOpen);
    }

    [PgFact]
    public async Task A_pilot_who_spent_the_money_refuses_the_completion_and_the_trade_stays_open()
    {
        await using var rig = await RigWithTwoPilotsAsync(aliceIsk: 400m);
        var view = await rig.Trades.InitiateAsync(Alice, Bob, TradeRig.Station, 30000142);
        var item = await rig.CreateItemAsync(Bob, TradeRig.Station, TradeRig.Hangar);
        await rig.Trades.StageAsync(view.TradeId, Bob, item, 0);
        await rig.Trades.SetMoneyAsync(view.TradeId, Alice, 400m);
        var items = (await rig.Trades.ViewAsync(view.TradeId))!.Items.Select(i => i.Id).ToHashSet();
        await rig.Trades.OfferAsync(view.TradeId, Alice, [400m, 0m], items);

        // Alice spends the money elsewhere after offering it
        await rig.OpenWalletAsync(9199, 0m);
        await rig.Transactions.TransferAndCommitAsync(new WalletAccount(Alice, 1000), new WalletAccount(9199, 1000), 400m, 1, "spent", rig.Tick(), "spend:1", "test");

        var refused = await rig.Trades.OfferAsync(view.TradeId, Bob, [400m, 0m], items);
        Assert.Equal(TradeOfferOutcome.Refused, refused.Outcome);
        Assert.Equal("One pilot no longer has enough ISK to complete the trade.", refused.Reason);
        Assert.True(refused.Change.View.IsOpen);
        Assert.Equal(Bob, (await rig.Items.GetAsync(item))!.OwnerId);
        Assert.Equal(view.TradeId, (await rig.Items.GetAsync(item))!.LocationId);
        Assert.Equal(1000m, await rig.IskAsync(Bob));  // nothing moved
    }

    [PgFact]
    public async Task Recovery_after_a_restart_ends_open_trades_and_returns_their_items()
    {
        await using var rig = await RigWithTwoPilotsAsync();
        var view = await rig.Trades.InitiateAsync(Alice, Bob, TradeRig.Station, 30000142);
        var item = await rig.CreateItemAsync(Alice, TradeRig.Station, TradeRig.Hangar, quantity: 9);
        await rig.Trades.StageAsync(view.TradeId, Alice, item, 4);

        // a restarted host has no memory of the trade: the store alone decides what is ended
        var recovered = await rig.Trades.RecoverOpenAsync();

        Assert.Single(recovered);
        Assert.Equal("aborted", (await rig.Trades.ViewAsync(view.TradeId))!.Status);
        Assert.Equal((9, TradeRig.Station, TradeRig.Hangar), ((await rig.Items.GetAsync(item))!.Quantity, (await rig.Items.GetAsync(item))!.LocationId, (await rig.Items.GetAsync(item))!.Flag));
        Assert.Null(await rig.Trades.OpenViewForAsync(Bob));
    }
    [PgFact]
    public async Task MultiAdd_validates_the_whole_batch_before_moving_any_item()
    {
        await using var rig = await RigWithTwoPilotsAsync();
        var view = await rig.Trades.InitiateAsync(Alice, Bob, TradeRig.Station, 30000142);
        var valid = await rig.CreateItemAsync(Alice, TradeRig.Station, TradeRig.Hangar);
        var foreign = await rig.CreateItemAsync(Bob, TradeRig.Station, TradeRig.Hangar);

        var refused = await Assert.ThrowsAsync<TradeRefusedException>(() =>
            rig.Trades.StageManyAsync(view.TradeId, Alice, [valid, foreign], TradeRig.Station, 0));

        Assert.Equal("That item is not available to trade.", refused.Message);
        Assert.Equal(TradeRig.Station, (await rig.Items.GetAsync(valid))!.LocationId);
        Assert.Equal(Bob, (await rig.Items.GetAsync(foreign))!.OwnerId);
        Assert.Empty((await rig.Trades.ViewAsync(view.TradeId))!.Items);
    }

    [PgFact]
    public async Task An_accessible_nested_source_can_be_staged_and_returned_to_its_container()
    {
        await using var rig = await RigWithTwoPilotsAsync();
        var view = await rig.Trades.InitiateAsync(Alice, Bob, TradeRig.Station, 30000142);
        var container = await rig.CreateItemAsync(Alice, TradeRig.Station, TradeRig.Hangar, singleton: true, typeId: 3293);
        var child = await rig.CreateItemAsync(Alice, container, TradeRig.Hangar);

        var staged = await rig.Trades.StageAsync(view.TradeId, Alice, child, 0, container, 0);
        Assert.Equal(view.TradeId, (await rig.Items.GetAsync(child))!.LocationId);
        Assert.Equal(Alice, (await rig.Items.GetAsync(container))!.OwnerId);

        await rig.Trades.AbortAsync(view.TradeId);

        Assert.Equal((Alice, container), ((await rig.Items.GetAsync(child))!.OwnerId, (await rig.Items.GetAsync(child))!.LocationId));
        Assert.Contains(staged.Moves, move => move.After.Id == child);
    }

    [PgFact]
    public async Task Concurrent_acceptance_completes_once_and_moves_the_item_once()
    {
        await using var rig = await RigWithTwoPilotsAsync();
        var view = await rig.Trades.InitiateAsync(Alice, Bob, TradeRig.Station, 30000142);
        var item = await rig.CreateItemAsync(Alice, TradeRig.Station, TradeRig.Hangar);
        await rig.Trades.StageAsync(view.TradeId, Alice, item, 0, TradeRig.Station, 0);
        var manifest = (await rig.Trades.ViewAsync(view.TradeId))!.Items.Select(staged => staged.Id).ToHashSet();

        var results = await Task.WhenAll(
            rig.Trades.OfferAsync(view.TradeId, Alice, [0m, 0m], manifest),
            rig.Trades.OfferAsync(view.TradeId, Bob, [0m, 0m], manifest));

        Assert.Single(results, result => result.Outcome == TradeOfferOutcome.Completed);
        Assert.Single(results, result => result.Outcome == TradeOfferOutcome.Pending);
        Assert.Equal(Bob, (await rig.Items.GetAsync(item))!.OwnerId);
        Assert.Equal("completed", (await rig.Trades.ViewAsync(view.TradeId))!.Status);
    }

}

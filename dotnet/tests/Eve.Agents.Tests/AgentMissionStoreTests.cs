namespace Eve.Agents.Tests;

/// <summary>The mission state machine on PostgreSQL: each transition applies once, a refusal changes nothing, and the journal and the commit notices follow the committed changes.</summary>
public class AgentMissionStoreTests
{
    private const long Character = 90000001;
    private const long Agent = 3008416;
    private static readonly AgentMissionContent Courier = new(MissionId: 5001, NameId: 9100, Title: "", MissionTypeLabel: "courier", Important: false);
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    [PgFact]
    public async Task An_offer_is_held_with_its_expiry_and_one_journal_row_and_one_notice()
    {
        await using var rig = await Rig.Create();
        var now = Rig.Start;

        var change = await rig.Store.OfferAsync(Character, Agent, Courier, now, "offer:1");

        Assert.NotNull(change);
        Assert.Equal(AgentMissionState.Allocated, change.From);
        Assert.Equal(AgentMissionState.Offered, change.To);
        Assert.Equal("offered", change.Action);
        var held = await rig.Store.ReadAsync(Character, Agent);
        Assert.NotNull(held);
        Assert.Equal(AgentMissionState.Offered, held.State);
        Assert.Equal(Courier, held.Content);
        Assert.Equal(now + AgentMissionStore.OfferLifetime, held.ExpiresAt);
        Assert.Equal(["offered:0>1"], await rig.JournalAsync(Character, Agent));
        Assert.Equal(change, Assert.Single(rig.Changes));
    }

    [PgFact]
    public async Task Accepting_an_offer_moves_it_to_accepted_and_a_replayed_accept_changes_nothing()
    {
        await using var rig = await Rig.Create();
        await rig.Store.OfferAsync(Character, Agent, Courier, Rig.Start, "offer:1");

        var first = await rig.Store.AcceptAsync(Character, Agent, Rig.Start + Hour, "accept:1");
        var replay = await rig.Store.AcceptAsync(Character, Agent, Rig.Start + Hour, "accept:1");

        Assert.NotNull(first);
        Assert.Null(replay);
        Assert.Equal(AgentMissionState.Accepted, (await rig.Store.ReadAsync(Character, Agent))!.State);
        Assert.Equal(["offered:0>1", "accepted:1>2"], await rig.JournalAsync(Character, Agent));
        Assert.Equal(2, rig.Changes.Count);
    }

    [PgFact]
    public async Task The_completed_missions_of_a_character_come_from_the_journal_with_their_time()
    {
        await using var rig = await Rig.Create();
        await rig.Store.OfferAsync(Character, Agent, Courier, Rig.Start, "offer:1");
        await rig.Store.AcceptAsync(Character, Agent, Rig.Start + Hour, "accept:1");
        await rig.Store.CompleteAsync(Character, Agent, Rig.Start + Hour * 2, "complete:1");

        var completed = await rig.Store.ReadCompletedAsync(Character);
        Assert.Equal((Courier.MissionId, Rig.Start + Hour * 2), Assert.Single(completed));
        Assert.Empty(await rig.Store.ReadCompletedAsync(Character + 1));
    }

    [PgFact]
    public async Task A_refused_transition_names_its_reason_and_writes_nothing()
    {
        await using var rig = await Rig.Create();

        var none = await Assert.ThrowsAsync<AgentMissionRefusedException>(() =>
            rig.Store.AcceptAsync(Character, Agent, Rig.Start, "accept:none"));
        Assert.Equal("not_offered", none.Reason);
        Assert.Empty(await rig.JournalAsync(Character, Agent));
        Assert.Null(await rig.Store.ReadAsync(Character, Agent));

        await rig.Store.OfferAsync(Character, Agent, Courier, Rig.Start, "offer:1");
        var second = await Assert.ThrowsAsync<AgentMissionRefusedException>(() =>
            rig.Store.OfferAsync(Character, Agent, Courier, Rig.Start, "offer:2"));
        Assert.Equal("already_offered", second.Reason);
        Assert.Equal(["offered:0>1"], await rig.JournalAsync(Character, Agent));
        Assert.Single(rig.Changes);
    }

    [PgFact]
    public async Task A_declined_offer_is_not_offered_again_for_four_hours()
    {
        await using var rig = await Rig.Create();
        await rig.Store.OfferAsync(Character, Agent, Courier, Rig.Start, "offer:1");
        await rig.Store.DeclineAsync(Character, Agent, Rig.Start, "decline:1");

        var early = await Assert.ThrowsAsync<AgentMissionRefusedException>(() =>
            rig.Store.OfferAsync(Character, Agent, Courier, Rig.Start + TimeSpan.FromHours(3), "offer:2"));
        Assert.Equal("replay_wait", early.Reason);

        var later = await rig.Store.OfferAsync(Character, Agent, Courier, Rig.Start + TimeSpan.FromHours(4), "offer:3");
        Assert.NotNull(later);
        Assert.Equal(["offered:0>1", "offer_declined:1>5", "offered:5>1"], await rig.JournalAsync(Character, Agent));
    }

    [PgFact]
    public async Task An_expired_offer_is_refused_on_accept_and_removed_once_by_the_sweep()
    {
        await using var rig = await Rig.Create();
        await rig.Store.OfferAsync(Character, Agent, Courier, Rig.Start, "offer:1");
        var late = Rig.Start + AgentMissionStore.OfferLifetime;

        var refused = await Assert.ThrowsAsync<AgentMissionRefusedException>(() =>
            rig.Store.AcceptAsync(Character, Agent, late, "accept:late"));
        Assert.Equal("offer_expired", refused.Reason);

        var swept = await rig.Store.ExpireOffersAsync(late);
        var again = await rig.Store.ExpireOffersAsync(late);

        Assert.Equal("offer_expired", Assert.Single(swept).Action);
        Assert.Empty(again);
        Assert.Equal(AgentMissionState.Allocated, (await rig.Store.ReadAsync(Character, Agent))!.State);
        Assert.Equal(["offered:0>1", "offer_expired:1>0"], await rig.JournalAsync(Character, Agent));
    }

    [PgFact]
    public async Task An_accepted_mission_can_be_quit_or_completed_but_not_both()
    {
        await using var rig = await Rig.Create();
        await rig.Store.OfferAsync(Character, Agent, Courier, Rig.Start, "offer:1");
        await rig.Store.AcceptAsync(Character, Agent, Rig.Start, "accept:1");
        await rig.Store.CompleteAsync(Character, Agent, Rig.Start + Hour, "complete:1");

        var quit = await Assert.ThrowsAsync<AgentMissionRefusedException>(() =>
            rig.Store.QuitAsync(Character, Agent, Rig.Start + Hour, "quit:1"));
        Assert.Equal("not_accepted", quit.Reason);
        var done = await rig.Store.ReadAsync(Character, Agent);
        Assert.Equal(AgentMissionState.Completed, done!.State);
        Assert.Equal(Rig.Start + Hour, done.CompletedAt);
        Assert.Equal(["offered:0>1", "accepted:1>2", "completed:2>4"], await rig.JournalAsync(Character, Agent));
    }

    [PgFact]
    public async Task The_held_missions_survive_a_restart_of_the_store()
    {
        await using var rig = await Rig.Create();
        await rig.Store.OfferAsync(Character, Agent, Courier, Rig.Start, "offer:1");
        await rig.Store.AcceptAsync(Character, Agent, Rig.Start, "accept:1");
        rig.Changes.Clear();

        var restarted = rig.Restart();
        var rows = await restarted.ReadAsync(Character);
        var replay = await restarted.AcceptAsync(Character, Agent, Rig.Start, "accept:1");

        Assert.Equal(AgentMissionState.Accepted, Assert.Single(rows).State);
        Assert.Null(replay);
        Assert.Empty(rig.Changes);
    }

    [PgFact]
    public async Task A_quit_mission_leaves_the_store_with_its_time_and_the_journal_keeps_the_path()
    {
        await using var rig = await Rig.Create();
        await rig.Store.OfferAsync(Character, Agent, Courier, Rig.Start, "offer:1");
        await rig.Store.AcceptAsync(Character, Agent, Rig.Start, "accept:1");
        await rig.Store.QuitAsync(Character, Agent, Rig.Start + Hour, "quit:1");

        var quit = await rig.Store.ReadAsync(Character, Agent);
        Assert.Equal(AgentMissionState.Quit, quit!.State);
        Assert.Equal(Rig.Start + Hour, quit.QuitAt);
        Assert.Equal(["offered:0>1", "accepted:1>2", "quit:2>6"], await rig.JournalAsync(Character, Agent));
    }
}

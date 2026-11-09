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
        Assert.Equal(now + TimeSpan.FromDays(7), held.ExpiresAt);
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
    public async Task A_failed_mission_comes_back_from_the_journal_with_its_time()
    {
        await using var rig = await Rig.Create();
        await rig.Store.OfferAsync(Character, Agent, Courier, Rig.Start, "offer:f");
        await rig.Store.AcceptAsync(Character, Agent, Rig.Start + Hour, "accept:f");
        await rig.Store.FailAsync(Character, Agent, Rig.Start + Hour * 2, "fail:f");

        Assert.Equal((Courier.MissionId, Rig.Start + Hour * 2), Assert.Single(await rig.Store.ReadFailedAsync(Character)));
        Assert.Empty(await rig.Store.ReadCompletedAsync(Character));
    }

    [PgFact]
    public async Task A_completion_rolled_back_with_its_standing_write_leaves_the_mission_accepted_and_announces_nothing()
    {
        await using var rig = await Rig.Create();
        await rig.Store.OfferAsync(Character, Agent, Courier, Rig.Start, "offer:r");
        await rig.Store.AcceptAsync(Character, Agent, Rig.Start + Hour, "accept:r");

        // the completion and the standing write share one transaction: when the standing write fails, the transaction is rolled back
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            Assert.NotNull(await rig.Store.CompleteInAsync(connection, transaction, Character, Agent, Rig.Start + Hour * 2, "complete:r", CancellationToken.None));
            await transaction.RollbackAsync();
        }

        Assert.Equal(AgentMissionState.Accepted, (await rig.Store.ReadAsync(Character, Agent))!.State);
        Assert.Equal(["offered:0>1", "accepted:1>2"], await rig.JournalAsync(Character, Agent));
        Assert.Equal(2, rig.Changes.Count);
        Assert.Empty(await rig.Store.ReadCompletedAsync(Character));
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
    public async Task A_declined_offer_can_be_reissued_and_keeps_its_four_hour_deadline()
    {
        await using var rig = await Rig.Create();
        await rig.Store.OfferAsync(Character, Agent, Courier, Rig.Start, "offer:1");
        await rig.Store.DeclineAsync(Character, Agent, Rig.Start, "decline:1");

        var reoffered = await rig.Store.OfferAsync(Character, Agent, Courier, Rig.Start + TimeSpan.FromHours(3), "offer:2");
        Assert.NotNull(reoffered);
        Assert.Equal(AgentMissionState.Offered, (await rig.Store.ReadAsync(Character, Agent))!.State);
        Assert.Equal(Rig.Start + AgentMissionStore.DeclineReplayDelay,
            (await rig.Store.ReadAsync(Character, Agent))!.DeclineUntil);
        Assert.Equal(["offered:0>1", "declined:1>5", "offered:5>1"], await rig.JournalAsync(Character, Agent));
    }

    [PgFact]
    public async Task A_decline_retry_with_the_same_reference_is_idempotent()
    {
        await using var rig = await Rig.Create();
        await rig.Store.OfferAsync(Character, Agent, Courier, Rig.Start, "offer:decline-retry");

        var first = await rig.Store.DeclineAsync(Character, Agent, Rig.Start, "decline:retry");
        var retry = await rig.Store.DeclineAsync(Character, Agent, Rig.Start, "decline:retry");

        Assert.Equal("declined", first!.Action);
        Assert.Null(retry);
        Assert.Equal(AgentMissionState.Declined, (await rig.Store.ReadAsync(Character, Agent))!.State);
        Assert.Equal(Rig.Start + AgentMissionStore.DeclineReplayDelay,
            (await rig.Store.ReadAsync(Character, Agent))!.DeclineUntil);
        Assert.Equal(["offered:0>1", "declined:1>5"], await rig.JournalAsync(Character, Agent));
    }

    [PgFact]
    public async Task Concurrent_decline_retries_write_one_transition_and_one_deadline()
    {
        await using var rig = await Rig.Create();
        await rig.Store.OfferAsync(Character, Agent, Courier, Rig.Start, "offer:decline-concurrent");

        var first = rig.Store.DeclineAsync(Character, Agent, Rig.Start, "decline:concurrent");
        var second = rig.Store.DeclineAsync(Character, Agent, Rig.Start, "decline:concurrent");
        var changes = await Task.WhenAll(first, second);

        Assert.Single(changes, change => change is not null);
        Assert.Equal(AgentMissionState.Declined, (await rig.Store.ReadAsync(Character, Agent))!.State);
        Assert.Equal(Rig.Start + AgentMissionStore.DeclineReplayDelay,
            (await rig.Store.ReadAsync(Character, Agent))!.DeclineUntil);
        Assert.Equal(["offered:0>1", "declined:1>5"], await rig.JournalAsync(Character, Agent));
        Assert.Equal(2, rig.Changes.Count);
    }

    [PgFact]
    public async Task A_decline_deadline_survives_a_store_restart_and_can_be_used_by_the_next_offer()
    {
        await using var rig = await Rig.Create();
        await rig.Store.OfferAsync(Character, Agent, Courier, Rig.Start, "offer:decline-restart");
        await rig.Store.DeclineAsync(Character, Agent, Rig.Start, "decline:restart");

        var restarted = rig.Restart();
        var persisted = await restarted.ReadAsync(Character, Agent);
        var reoffered = await restarted.OfferAsync(Character, Agent, Courier,
            Rig.Start + TimeSpan.FromHours(2), "offer:after-restart");

        Assert.Equal(Rig.Start + AgentMissionStore.DeclineReplayDelay, persisted!.DeclineUntil);
        Assert.NotNull(reoffered);
        Assert.Equal(persisted.DeclineUntil, (await restarted.ReadAsync(Character, Agent))!.DeclineUntil);
    }

    [PgFact]
    public async Task An_offer_expires_after_seven_days_and_the_late_accept_is_refused()
    {
        await using var rig = await Rig.Create();
        await rig.Store.OfferAsync(Character, Agent, Courier, Rig.Start, "offer:1");
        var late = Rig.Start + TimeSpan.FromDays(30);

        var expired = Assert.Single(await rig.Store.ExpireDueAsync(late));
        Assert.Equal((AgentMissionState.Offered, AgentMissionState.Allocated, "offer_expired"), (expired.From, expired.To, expired.Action));
        var refused = await Assert.ThrowsAsync<AgentMissionRefusedException>(() =>
            rig.Store.AcceptAsync(Character, Agent, late, "accept:late"));
        Assert.Equal("not_offered", refused.Reason);
        Assert.Equal(AgentMissionState.Allocated, (await rig.Store.ReadAsync(Character, Agent))!.State);
        Assert.Equal(["offered:0>1", "offer_expired:1>0"], await rig.JournalAsync(Character, Agent));
    }

    [PgFact]
    public async Task Accepting_an_offer_restarts_its_authored_mission_deadline()
    {
        await using var rig = await Rig.Create();
        var authored = Courier with { ExpirationMinutes = 30 };
        await rig.Store.OfferAsync(Character, Agent, authored, Rig.Start, "offer:authored");
        Assert.Equal(Rig.Start + TimeSpan.FromMinutes(30), (await rig.Store.ReadAsync(Character, Agent))!.ExpiresAt);

        var acceptedAt = Rig.Start + TimeSpan.FromMinutes(5);
        await rig.Store.AcceptAsync(Character, Agent, acceptedAt, "accept:authored");

        var accepted = await rig.Store.ReadAsync(Character, Agent);
        Assert.Equal(AgentMissionState.Accepted, accepted!.State);
        Assert.Equal(acceptedAt + TimeSpan.FromMinutes(30), accepted.ExpiresAt);
        Assert.Equal(30, accepted.Content.ExpirationMinutes);
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

    [PgFact]
    public async Task A_reset_returns_an_accepted_mission_to_none_and_the_agent_offers_again()
    {
        await using var rig = await Rig.Create();
        await rig.Store.OfferAsync(Character, Agent, Courier, Rig.Start, "offer:1");
        await rig.Store.AcceptAsync(Character, Agent, Rig.Start + Hour, "accept:1");

        var change = await rig.Store.ResetAsync(Character, Agent, Rig.Start + Hour * 2, "reset:1");

        Assert.NotNull(change);
        Assert.Equal((AgentMissionState.Accepted, AgentMissionState.Allocated, "reset"), (change.From, change.To, change.Action));
        var held = await rig.Store.ReadAsync(Character, Agent);
        Assert.True(held is null || held.State == AgentMissionState.Allocated);
        Assert.Equal(["offered:0>1", "accepted:1>2", "reset:2>0"], await rig.JournalAsync(Character, Agent));
        Assert.Equal(change, rig.Changes[^1]);
        Assert.NotNull(await rig.Store.OfferAsync(Character, Agent, Courier, Rig.Start + Hour * 2, "offer:2"));
    }

    [PgFact]
    public async Task A_reset_clears_a_decline_timer_so_the_agent_offers_at_once()
    {
        await using var rig = await Rig.Create();
        await rig.Store.OfferAsync(Character, Agent, Courier, Rig.Start, "offer:1");
        await rig.Store.DeclineAsync(Character, Agent, Rig.Start, "decline:1");
        Assert.NotNull((await rig.Store.ReadAsync(Character, Agent))!.DeclineUntil);

        await rig.Store.ResetAsync(Character, Agent, Rig.Start, "reset:1");

        Assert.NotNull(await rig.Store.OfferAsync(Character, Agent, Courier, Rig.Start, "offer:2"));
    }

    [PgFact]
    public async Task A_reset_of_a_character_with_no_mission_is_refused_and_writes_nothing()
    {
        await using var rig = await Rig.Create();

        var refused = await Assert.ThrowsAsync<AgentMissionRefusedException>(() =>
            rig.Store.ResetAsync(Character, Agent, Rig.Start, "reset:1"));

        Assert.Equal("not_held", refused.Reason);
        Assert.Empty(await rig.JournalAsync(Character, Agent));
        Assert.Empty(rig.Changes);
    }
}

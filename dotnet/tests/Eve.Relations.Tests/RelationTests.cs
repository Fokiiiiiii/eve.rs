namespace Eve.Relations.Tests;

public class RelationTests
{
    private const long Pilot = 90000001, Other = 90000002, Corp = 98000001, Corp2 = 98000002, Alliance = 99000001, Faction = 500001;

    [PgFact]
    public async Task A_standing_is_set_clamped_read_back_and_a_write_that_changes_nothing_reports_nothing()
    {
        await using var rig = await Rig.Create();
        var change = await rig.Set("standing", Pilot, Corp, 3.5, "agent mission");
        Assert.Equal((null, 3.5), (change!.OldValue, change.NewValue));
        Assert.Equal(3.5, await rig.Relations.ValueAsync("standing", Pilot, Corp));
        Assert.Null(await rig.Set("standing", Pilot, Corp, 3.5));
        Assert.Equal(10, (await rig.Set("standing", Pilot, Corp, 99))!.NewValue);   // clamped to the kind's range
        Assert.Equal(0, await rig.Relations.ValueAsync("standing", Pilot, Other));  // the fallback
        var history = await rig.Relations.HistoryAsync("standing", Pilot);
        Assert.Equal(2, history.Count);   // newest first; the write that changed nothing left no line
        Assert.Equal((3.5, 10.0), (history[0].OldValue, history[0].NewValue));
        Assert.Equal((null, 3.5), (history[1].OldValue, history[1].NewValue));
        Assert.Equal("agent mission", history[1].Reason);
    }

    [PgFact]
    public async Task Adjust_adds_to_the_value_within_the_range_and_keeps_the_rest()
    {
        await using var rig = await Rig.Create();
        await rig.Set("security_status", Pilot, RelationStore.World, 0, attrs: new { note = "kept" });
        await rig.Commit((c, t) => rig.Relations.AdjustAsync(c, t, "security_status", Pilot, RelationStore.World, -4.25, "killed a pilot"));
        await rig.Commit((c, t) => rig.Relations.AdjustAsync(c, t, "security_status", Pilot, RelationStore.World, -9, "killed a pilot"));
        var now = (await rig.Relations.GetAsync("security_status", Pilot, RelationStore.World))!;
        Assert.Equal(-10, now.Value);
        Assert.Contains("kept", now.AttrsJson);
        // a relation that did not exist starts from the given value
        await rig.Commit((c, t) => rig.Relations.AdjustAsync(c, t, "standing", Pilot, Corp, 1.5, start: 2));
        Assert.Equal(3.5, await rig.Relations.ValueAsync("standing", Pilot, Corp));
    }

    [PgFact]
    public async Task A_one_per_subject_kind_replaces_the_object_and_the_chain_follows_affiliation()
    {
        await using var rig = await Rig.Create();
        await rig.Set("affiliation.corporation", Pilot, Corp, 0, "joined");
        await rig.Set("affiliation.alliance", Corp, Alliance, 0);
        await rig.Set("affiliation.faction", Corp, Faction, 0);
        var affiliation = await rig.Relations.AffiliationOfAsync(Pilot);
        Assert.Equal((Corp, Alliance, Faction), (affiliation.Corporation, affiliation.Alliance, affiliation.Faction));
        Assert.Equal([Pilot, Corp, Alliance, Faction], await rig.Relations.ChainOfAsync(Pilot));

        await rig.Set("affiliation.corporation", Pilot, Corp2, 0, "moved");
        Assert.Equal([Corp2], (await rig.Relations.FromAsync("affiliation.corporation", Pilot)).Select(r => r.Object));
        Assert.Empty(await rig.Relations.ToAsync("affiliation.corporation", Corp));
        var history = await rig.Relations.HistoryAsync("affiliation.corporation", Pilot);
        Assert.Contains(history, h => h.Object == Corp && h.NewValue is null && h.Reason == "replaced");
        Assert.Equal([Pilot, Corp2], await rig.Relations.ChainOfAsync(Pilot));
    }

    [PgFact]
    public async Task The_best_standing_is_found_along_the_chains_of_both_sides()
    {
        await using var rig = await Rig.Create();
        await rig.Set("standing", Corp, Other, -5);
        await rig.Set("standing", Alliance, Other, 2);
        await rig.Set("standing", Pilot, Corp2, 9);
        Assert.Equal(2, await rig.Relations.BestAsync("standing", [Pilot, Corp, Alliance], [Other]));
        Assert.Null(await rig.Relations.BestAsync("standing", [Pilot], [Other]));
    }

    [PgFact]
    public async Task Changes_are_published_through_the_event_bus_and_a_rolled_back_change_is_not()
    {
        await using var rig = await Rig.Create();
        var seen = new List<string>();
        rig.Bus.Subscribe("watch", ["relation.*"], (ctx, _) => { seen.Add($"{ctx.Event.Topic}:{ctx.Event.Key}"); return Task.CompletedTask; });
        await rig.Bus.PumpAsync();

        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await rig.Relations.SetAsync(connection, transaction, "standing", Pilot, Corp, 1);
            await transaction.RollbackAsync();
        }
        Assert.Null(await rig.Relations.GetAsync("standing", Pilot, Corp));

        await rig.Set("standing", Pilot, Corp, 1);
        await rig.Set("standing", Pilot, Corp, 2);
        await rig.Commit((c, t) => rig.Relations.EndAsync(c, t, "standing", Pilot, Corp, "reset"));
        await rig.Scheduler.RunDueAsync();
        Assert.Equal(["relation.standing.changed:standing:90000001", "relation.standing.changed:standing:90000001", "relation.standing.ended:standing:90000001"], seen);
    }

    [PgFact]
    public async Task A_war_ends_by_itself_when_its_time_comes_and_moving_the_end_moves_the_job()
    {
        await using var rig = await Rig.Create();
        var ended = new List<string?>();
        rig.Bus.Subscribe("w", ["relation.war.ended"], (ctx, _) => { ended.Add(ctx.Event.Payload<Dictionary<string, object?>>()!["reason"]?.ToString()); return Task.CompletedTask; });
        await rig.Bus.PumpAsync();
        var t = rig.Now;
        await rig.Set("war", Corp, Corp2, 0, "declared", new { mutual = false }, until: t + 1000);
        Assert.True(await rig.Relations.AtWarAsync([Corp], [Pilot, Corp2]));   // either side may be the aggressor
        Assert.True(await rig.Relations.AtWarAsync([Corp2], [Corp]));
        Assert.False(await rig.Relations.AtWarAsync([Corp], [Other]));

        // the end is moved later: the first job is replaced, so nothing happens at the first time
        await rig.Set("war", Corp, Corp2, 0, "extended", until: t + 5000);
        rig.Now = t + 1500;
        await rig.Scheduler.RunDueAsync();
        Assert.True(await rig.Relations.HoldsAsync("war", Corp, Corp2));
        rig.Now = t + 5000;
        await rig.Scheduler.RunDueAsync();
        Assert.False(await rig.Relations.HoldsAsync("war", Corp, Corp2));
        await rig.Scheduler.RunDueAsync();
        Assert.Equal(["expired"], ended);
        Assert.Contains(await rig.Relations.HistoryAsync("war", Corp), h => h.Reason == "expired" && h.NewValue is null);
    }

    [PgFact]
    public async Task A_war_that_is_ended_by_hand_cancels_its_job_and_an_unknown_kind_is_refused()
    {
        await using var rig = await Rig.Create();
        await rig.Set("war", Corp, Corp2, 0, until: rig.Now + 1000);
        await rig.Commit((c, t) => rig.Relations.EndAsync(c, t, "war", Corp, Corp2, "surrendered"));
        Assert.Empty(await rig.Scheduler.ListAsync("relations.expire"));
        await Assert.ThrowsAsync<ArgumentException>(() => rig.Set("friendship", Pilot, Other, 1));
        rig.Relations.Define(new RelationKind("friendship") { Min = 0, Max = 1 });
        Assert.Equal(1, (await rig.Set("friendship", Pilot, Other, 5))!.NewValue);
    }

    [PgFact]
    public async Task Relations_can_be_listed_from_and_to_an_owner()
    {
        await using var rig = await Rig.Create();
        await rig.Set("standing", Pilot, Corp, 1);
        await rig.Set("standing", Pilot, Corp2, 2);
        await rig.Set("standing", Other, Corp, 3);
        Assert.Equal([1.0, 2.0], (await rig.Relations.FromAsync("standing", Pilot)).Select(r => r.Value));
        Assert.Equal([Pilot, Other], (await rig.Relations.ToAsync("standing", Corp)).Select(r => r.Subject).OrderByDescending(x => x == Pilot));
    }
}

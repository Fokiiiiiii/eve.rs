using Npgsql;

namespace Eve.Relations.Tests;

public class RelationTests
{
    private const long Pilot = 90000001, Other = 90000002, Corp = 98000001, Corp2 = 98000002, Corp3 = 98000003, Alliance = 99000001, Faction = 500001;
    private const string CorporationAffiliation = "affiliation.corporation";

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
    public async Task History_returns_the_persisted_attributes_without_reconstructing_them()
    {
        await using var rig = await Rig.Create();
        await rig.Set("security_status", Pilot, RelationStore.World, -0.25, "aggression", new
        {
            eventTypeID = 379,
            otherOwnerID = Other,
            otherTypeID = 587,
            locationID = 30_100_001,
            modification = -0.025,
        });

        var history = (await rig.Relations.HistoryAsync("security_status", Pilot)).Single();
        Assert.True(history.Attrs.HasValue);
        var attrs = history.Attrs.Value;
        Assert.Equal(379, attrs.GetProperty("eventTypeID").GetInt32());
        Assert.Equal(Other, attrs.GetProperty("otherOwnerID").GetInt64());
        Assert.Equal(587, attrs.GetProperty("otherTypeID").GetInt32());
        Assert.Equal(30_100_001, attrs.GetProperty("locationID").GetInt64());
        Assert.Equal(-0.025, attrs.GetProperty("modification").GetDouble());
        Assert.Equal(-0.25, history.NewValue);

        var json = System.Text.Json.JsonSerializer.Serialize(history);
        using var serialized = System.Text.Json.JsonDocument.Parse(json);
        var serializedAttrs = serialized.RootElement.GetProperty(nameof(RelationHistoryEntry.Attrs));
        Assert.Equal(379, serializedAttrs.GetProperty("eventTypeID").GetInt32());

        var legacy = new RelationHistoryEntry(history.Id, history.Kind, history.Subject, history.Object,
            history.OldValue, history.NewValue, history.Reason, history.AtMs);
        Assert.Null(legacy.Attrs);
        using var legacySerialized = System.Text.Json.JsonDocument.Parse(
            System.Text.Json.JsonSerializer.Serialize(legacy));
        Assert.False(legacySerialized.RootElement.TryGetProperty(nameof(RelationHistoryEntry.Attrs), out _));
    }

    [PgFact]
    public async Task History_records_war_attribute_changes_that_keep_the_same_value()
    {
        await using var rig = await Rig.Create();
        var events = new List<RelationChange>();
        rig.Bus.Subscribe("war-history", ["relation.war.changed", "relation.war.ended"], (ctx, _) =>
        {
            events.Add(ctx.Event.Payload<RelationChange>()!);
            return Task.CompletedTask;
        });
        await rig.Bus.PumpAsync();

        var firstUntil = rig.Now + 10_000;
        var secondUntil = rig.Now + 20_000;
        await rig.Set("war", Corp, Corp2, 0, "declared", new { mutual = false }, firstUntil);
        await rig.Set("war", Corp, Corp2, 0, "accepted", new { mutual = true }, firstUntil);

        var history = await rig.Relations.HistoryAsync("war", Corp, Corp2);
        Assert.Equal(2, history.Count);
        Assert.False(history[0].OldAttrs!.Value.GetProperty("mutual").GetBoolean());
        Assert.True(history[0].NewAttrs!.Value.GetProperty("mutual").GetBoolean());
        Assert.True(history[0].Attrs!.Value.GetProperty("mutual").GetBoolean());
        Assert.Equal(firstUntil, history[0].OldUntilMs);
        Assert.Equal(firstUntil, history[0].NewUntilMs);

        await rig.Set("war", Corp, Corp2, 0, "extended", until: secondUntil);
        history = await rig.Relations.HistoryAsync("war", Corp, Corp2);
        Assert.True(history[0].OldAttrs!.Value.GetProperty("mutual").GetBoolean());
        Assert.True(history[0].NewAttrs!.Value.GetProperty("mutual").GetBoolean());
        Assert.Equal(firstUntil, history[0].OldUntilMs);
        Assert.Equal(secondUntil, history[0].NewUntilMs);

        await rig.Commit((connection, transaction) => rig.Relations.AdjustAsync(
            connection, transaction, "war", Corp, Corp2, 1, "adjusted"));
        history = await rig.Relations.HistoryAsync("war", Corp, Corp2);
        Assert.True(history[0].OldAttrs!.Value.GetProperty("mutual").GetBoolean());
        Assert.True(history[0].NewAttrs!.Value.GetProperty("mutual").GetBoolean());
        Assert.Equal(secondUntil, history[0].OldUntilMs);
        Assert.Equal(secondUntil, history[0].NewUntilMs);

        await rig.Commit((connection, transaction) => rig.Relations.EndAsync(
            connection, transaction, "war", Corp, Corp2, "ended"));
        history = await rig.Relations.HistoryAsync("war", Corp, Corp2);
        Assert.Null(history[0].NewValue);
        Assert.True(history[0].OldAttrs!.Value.GetProperty("mutual").GetBoolean());
        Assert.Null(history[0].NewAttrs);
        Assert.Null(history[0].Attrs);
        Assert.Equal(secondUntil, history[0].OldUntilMs);
        Assert.Null(history[0].NewUntilMs);

        await rig.Bus.PumpAsync();
        var endedEvent = Assert.Single(events, change => change.NewValue is null);
        Assert.True(endedEvent.OldAttrs!.Value.GetProperty("mutual").GetBoolean());
        Assert.Null(endedEvent.NewAttrs);
        Assert.Equal(secondUntil, endedEvent.OldUntilMs);
        Assert.Null(endedEvent.NewUntilMs);
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
    public async Task Concurrent_first_sets_to_a_many_relation_serialize_and_keep_history()
    {
        await using var rig = await Rig.Create();

        await using (var firstConnection = await rig.Source.OpenConnectionAsync())
        await using (var firstTransaction = await firstConnection.BeginTransactionAsync())
        {
            await rig.Relations.SetAsync(firstConnection, firstTransaction, "standing", Pilot, Other, 1,
                "first set");

            await using var secondConnection = await rig.Source.OpenConnectionAsync();
            await using var secondTransaction = await secondConnection.BeginTransactionAsync();
            var secondPid = await BackendPidAsync(secondConnection, secondTransaction);
            using var secondCancellation = new CancellationTokenSource();
            var secondWrite = rig.Relations.SetAsync(secondConnection, secondTransaction, "standing", Pilot, Other, 2,
                "second set", cancellationToken: secondCancellation.Token);
            var waiting = await IsWaitingForAdvisoryLockAsync(rig, secondPid, secondWrite);
            if (!waiting)
            {
                secondCancellation.Cancel();
                try { await secondWrite; } catch (OperationCanceledException) { }
            }
            Assert.True(waiting, "a concurrent first SetAsync should wait for the relation's transaction lock");

            await firstTransaction.CommitAsync();
            await secondWrite;
            await secondTransaction.CommitAsync();
        }

        Assert.Equal(2, await rig.Relations.ValueAsync("standing", Pilot, Other));
        Assert.Equal(
            [(1.0, 2.0, "second set"), ((double?)null, 1.0, "first set")],
            (await rig.Relations.HistoryAsync("standing", Pilot, Other))
                .Select(entry => (entry.OldValue, entry.NewValue, entry.Reason)));

    }

    [PgFact]
    public async Task Concurrent_first_adjustments_to_a_many_relation_serialize_and_accumulate()
    {
        await using var rig = await Rig.Create();
        const long adjustedObject = Other + 1;
        await using (var firstConnection = await rig.Source.OpenConnectionAsync())
        await using (var firstTransaction = await firstConnection.BeginTransactionAsync())
        {
            await rig.Relations.AdjustAsync(firstConnection, firstTransaction, "standing", Pilot, adjustedObject, 1.5,
                "first adjustment");

            await using var secondConnection = await rig.Source.OpenConnectionAsync();
            await using var secondTransaction = await secondConnection.BeginTransactionAsync();
            var secondPid = await BackendPidAsync(secondConnection, secondTransaction);
            using var secondCancellation = new CancellationTokenSource();
            var secondWrite = rig.Relations.AdjustAsync(secondConnection, secondTransaction, "standing", Pilot,
                adjustedObject, 2.5, "second adjustment", cancellationToken: secondCancellation.Token);
            var waiting = await IsWaitingForAdvisoryLockAsync(rig, secondPid, secondWrite);
            if (!waiting)
            {
                secondCancellation.Cancel();
                try { await secondWrite; } catch (OperationCanceledException) { }
            }
            Assert.True(waiting, "a concurrent first AdjustAsync should wait for the relation's transaction lock");

            await firstTransaction.CommitAsync();
            await secondWrite;
            await secondTransaction.CommitAsync();
        }

        Assert.Equal(4, await rig.Relations.ValueAsync("standing", Pilot, adjustedObject));
        Assert.Equal(
            [(1.5, 4.0, "second adjustment"), ((double?)null, 1.5, "first adjustment")],
            (await rig.Relations.HistoryAsync("standing", Pilot, adjustedObject))
                .Select(entry => (entry.OldValue, entry.NewValue, entry.Reason)));
    }

    [PgFact]
    public async Task Concurrent_first_affiliation_assignments_are_serialized_with_matching_history_events_and_expiry()
    {
        await using var rig = await Rig.Create();
        var events = await WatchAffiliationEventsAsync(rig);
        var firstUntil = rig.Now + 1_000;
        var secondUntil = rig.Now + 2_000;

        await using var firstConnection = await rig.Source.OpenConnectionAsync();
        await using var firstTransaction = await firstConnection.BeginTransactionAsync();
        await rig.Relations.SetAsync(firstConnection, firstTransaction, CorporationAffiliation, Pilot, Corp, 0,
            "first", untilMs: firstUntil);

        await using var secondConnection = await rig.Source.OpenConnectionAsync();
        await using var secondTransaction = await secondConnection.BeginTransactionAsync();
        var secondPid = await BackendPidAsync(secondConnection, secondTransaction);
        using var secondCancellation = new CancellationTokenSource();
        var secondWrite = rig.Relations.SetAsync(secondConnection, secondTransaction, CorporationAffiliation, Pilot, Corp2, 0,
            "second", untilMs: secondUntil, cancellationToken: secondCancellation.Token);
        var waiting = await IsWaitingForAdvisoryLockAsync(rig, secondPid, secondWrite);
        if (!waiting)
        {
            secondCancellation.Cancel();
            try { await secondWrite; } catch (OperationCanceledException) { }
        }
        Assert.True(waiting, "the second write should wait for the subject's transaction lock");

        await firstTransaction.CommitAsync();
        await secondWrite;
        await secondTransaction.CommitAsync();
        await rig.Bus.PumpAsync();

        Assert.Equal([Corp2], (await rig.Relations.FromAsync(CorporationAffiliation, Pilot)).Select(r => r.Object));
        var affiliation = await rig.Relations.AffiliationOfAsync(Pilot);
        Assert.Equal((long?)Corp2, affiliation.Corporation);
        Assert.Equal([Pilot, Corp2], await rig.Relations.ChainOfAsync(Pilot));
        Assert.Equal(
            [(Corp2, (double?)null, (double?)0, "second"), (Corp, (double?)0, (double?)null, "replaced"), (Corp, (double?)null, (double?)0, "first")],
            (await rig.Relations.HistoryAsync(CorporationAffiliation, Pilot)).Select(h => (h.Object, h.OldValue, h.NewValue, h.Reason)));
        Assert.Equal(
            [
                ($"relation.{CorporationAffiliation}.changed", $"{CorporationAffiliation}:{Pilot}", Corp.ToString(), "first"),
                ($"relation.{CorporationAffiliation}.ended", $"{CorporationAffiliation}:{Pilot}", Corp.ToString(), "replaced"),
                ($"relation.{CorporationAffiliation}.changed", $"{CorporationAffiliation}:{Pilot}", Corp2.ToString(), "second"),
            ], events);
        await AssertOnlyExpiryJobAsync(rig, CorporationAffiliation, Pilot, Corp2, secondUntil);
    }

    [PgFact]
    public async Task Concurrent_replacements_serialize_the_affiliation_chain_history_events_and_expiry()
    {
        await using var rig = await Rig.Create();
        var events = await WatchAffiliationEventsAsync(rig);
        var firstUntil = rig.Now + 1_000;
        var secondUntil = rig.Now + 2_000;
        var thirdUntil = rig.Now + 3_000;
        await rig.Set(CorporationAffiliation, Pilot, Corp, 0, "initial", until: firstUntil);

        await using var firstConnection = await rig.Source.OpenConnectionAsync();
        await using var firstTransaction = await firstConnection.BeginTransactionAsync();
        await rig.Relations.SetAsync(firstConnection, firstTransaction, CorporationAffiliation, Pilot, Corp2, 0,
            "first replacement", untilMs: secondUntil);

        await using var secondConnection = await rig.Source.OpenConnectionAsync();
        await using var secondTransaction = await secondConnection.BeginTransactionAsync();
        var secondPid = await BackendPidAsync(secondConnection, secondTransaction);
        using var secondCancellation = new CancellationTokenSource();
        var secondWrite = rig.Relations.SetAsync(secondConnection, secondTransaction, CorporationAffiliation, Pilot, Corp3, 0,
            "second replacement", untilMs: thirdUntil, cancellationToken: secondCancellation.Token);
        var waiting = await IsWaitingForAdvisoryLockAsync(rig, secondPid, secondWrite);
        if (!waiting)
        {
            secondCancellation.Cancel();
            try { await secondWrite; } catch (OperationCanceledException) { }
        }
        Assert.True(waiting, "the second replacement should wait for the subject's transaction lock");

        await firstTransaction.CommitAsync();
        await secondWrite;
        await secondTransaction.CommitAsync();
        await rig.Bus.PumpAsync();

        Assert.Equal([Corp3], (await rig.Relations.FromAsync(CorporationAffiliation, Pilot)).Select(r => r.Object));
        var affiliation = await rig.Relations.AffiliationOfAsync(Pilot);
        Assert.Equal((long?)Corp3, affiliation.Corporation);
        Assert.Equal([Pilot, Corp3], await rig.Relations.ChainOfAsync(Pilot));
        Assert.Equal(
            [
                (Corp3, (double?)null, (double?)0, "second replacement"),
                (Corp2, (double?)0, (double?)null, "replaced"),
                (Corp2, (double?)null, (double?)0, "first replacement"),
                (Corp, (double?)0, (double?)null, "replaced"),
                (Corp, (double?)null, (double?)0, "initial"),
            ],
            (await rig.Relations.HistoryAsync(CorporationAffiliation, Pilot)).Select(h => (h.Object, h.OldValue, h.NewValue, h.Reason)));
        Assert.Equal(
            [
                ($"relation.{CorporationAffiliation}.changed", $"{CorporationAffiliation}:{Pilot}", Corp.ToString(), "initial"),
                ($"relation.{CorporationAffiliation}.ended", $"{CorporationAffiliation}:{Pilot}", Corp.ToString(), "replaced"),
                ($"relation.{CorporationAffiliation}.changed", $"{CorporationAffiliation}:{Pilot}", Corp2.ToString(), "first replacement"),
                ($"relation.{CorporationAffiliation}.ended", $"{CorporationAffiliation}:{Pilot}", Corp2.ToString(), "replaced"),
                ($"relation.{CorporationAffiliation}.changed", $"{CorporationAffiliation}:{Pilot}", Corp3.ToString(), "second replacement"),
            ], events);
        await AssertOnlyExpiryJobAsync(rig, CorporationAffiliation, Pilot, Corp3, thirdUntil);
    }

    [PgFact]
    public async Task A_rolled_back_affiliation_replacement_preserves_the_committed_relation_and_retry_is_consistent()
    {
        await using var rig = await Rig.Create();
        var events = await WatchAffiliationEventsAsync(rig);
        var firstUntil = rig.Now + 1_000;
        var secondUntil = rig.Now + 2_000;
        await rig.Set(CorporationAffiliation, Pilot, Corp, 0, "initial", until: firstUntil);

        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await rig.Relations.SetAsync(connection, transaction, CorporationAffiliation, Pilot, Corp2, 0,
                "failed replacement", untilMs: secondUntil);
            await transaction.RollbackAsync();
        }

        Assert.Equal([Corp], (await rig.Relations.FromAsync(CorporationAffiliation, Pilot)).Select(r => r.Object));
        Assert.Equal((long?)Corp, (await rig.Relations.AffiliationOfAsync(Pilot)).Corporation);
        Assert.Equal([Pilot, Corp], await rig.Relations.ChainOfAsync(Pilot));
        Assert.Equal([(Corp, (double?)null, (double?)0, "initial")],
            (await rig.Relations.HistoryAsync(CorporationAffiliation, Pilot)).Select(h => (h.Object, h.OldValue, h.NewValue, h.Reason)));
        await AssertOnlyExpiryJobAsync(rig, CorporationAffiliation, Pilot, Corp, firstUntil);
        await rig.Bus.PumpAsync();
        Assert.Equal(
            [($"relation.{CorporationAffiliation}.changed", $"{CorporationAffiliation}:{Pilot}", Corp.ToString(), "initial")], events);

        await rig.Set(CorporationAffiliation, Pilot, Corp2, 0, "retry", until: secondUntil);
        await rig.Bus.PumpAsync();

        Assert.Equal([Corp2], (await rig.Relations.FromAsync(CorporationAffiliation, Pilot)).Select(r => r.Object));
        Assert.Equal((long?)Corp2, (await rig.Relations.AffiliationOfAsync(Pilot)).Corporation);
        Assert.Equal([Pilot, Corp2], await rig.Relations.ChainOfAsync(Pilot));
        Assert.Equal(
            [(Corp2, (double?)null, (double?)0, "retry"), (Corp, (double?)0, (double?)null, "replaced"), (Corp, (double?)null, (double?)0, "initial")],
            (await rig.Relations.HistoryAsync(CorporationAffiliation, Pilot)).Select(h => (h.Object, h.OldValue, h.NewValue, h.Reason)));
        Assert.Equal(
            [
                ($"relation.{CorporationAffiliation}.changed", $"{CorporationAffiliation}:{Pilot}", Corp.ToString(), "initial"),
                ($"relation.{CorporationAffiliation}.ended", $"{CorporationAffiliation}:{Pilot}", Corp.ToString(), "replaced"),
                ($"relation.{CorporationAffiliation}.changed", $"{CorporationAffiliation}:{Pilot}", Corp2.ToString(), "retry"),
            ], events);
        await AssertOnlyExpiryJobAsync(rig, CorporationAffiliation, Pilot, Corp2, secondUntil);
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
    public async Task A_characters_standing_toward_an_owner_reads_owner_to_character_and_the_corporations_value()
    {
        await using var rig = await Rig.Create();
        await rig.Set("affiliation.corporation", Pilot, Corp, 0);
        await rig.Set("standing", Faction, Corp, 4);
        await rig.Set("standing", Faction, Pilot, 2);
        await rig.Set("standing", Pilot, Corp, 9); // legacy direction must not be read as the effective standing

        Assert.Equal(4, await rig.Relations.StandingTowardAsync(Pilot, Faction));
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
        await rig.Bus.PumpAsync();
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
        await rig.Bus.PumpAsync();
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


    [PgFact]
    public async Task Affiliation_reconciliation_batches_are_bounded_idempotent_and_publish_changed_and_ended()
    {
        await using var rig = await Rig.Create();
        var seen = new List<string>();
        rig.Bus.Subscribe("affiliation-reconcile", ["relation.affiliation.corporation.*"],
            (ctx, _) => { seen.Add(ctx.Event.Topic); return Task.CompletedTask; });
        await rig.Bus.PumpAsync();

        var kind = Eve.Relations.RelationKinds.CorporationMember.Name;
        var initial = new Dictionary<long, long?> { [Pilot] = Corp, [Other] = Corp2 };
        await rig.Relations.ReconcileOnePerSubjectBatchAsync(kind, initial, "startup affiliation backfill");
        await rig.Set("standing", Pilot, Faction, 2);
        var listed = await rig.Relations.ListAsync(kind);
        Assert.Equal(2, listed.Count);
        Assert.All(listed, relation => Assert.Equal(kind, relation.Kind));
        await rig.Bus.PumpAsync();
        Assert.Equal(["relation.affiliation.corporation.changed", "relation.affiliation.corporation.changed"], seen);

        await rig.Relations.ReconcileOnePerSubjectBatchAsync(kind, initial, "startup affiliation backfill");
        await rig.Bus.PumpAsync();
        Assert.Equal(2, seen.Count);

        var updated = new Dictionary<long, long?> { [Pilot] = Corp2, [Other] = null };
        await rig.Relations.ReconcileOnePerSubjectBatchAsync(kind, updated, "repair affiliation graph");
        await rig.Bus.PumpAsync();
        Assert.Equal(
            ["relation.affiliation.corporation.changed", "relation.affiliation.corporation.changed",
             "relation.affiliation.corporation.ended", "relation.affiliation.corporation.changed",
             "relation.affiliation.corporation.ended"], seen);
        var remaining = Assert.Single(await rig.Relations.ListAsync(kind));
        Assert.Equal(Pilot, remaining.Subject);
        Assert.Equal(Corp2, remaining.Object);

        var tooMany = Enumerable.Range(1, Eve.Relations.RelationStore.ReconciliationBatchSize + 1)
            .ToDictionary(value => (long)value, value => (long?)Corp);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            rig.Relations.ReconcileOnePerSubjectBatchAsync(kind, tooMany, "over-sized batch"));
    }

    private static async Task<List<(string Topic, string? Key, string? Object, string? Reason)>> WatchAffiliationEventsAsync(Rig rig)
    {
        var seen = new List<(string Topic, string? Key, string? Object, string? Reason)>();
        rig.Bus.Subscribe("affiliation-race", ["relation.affiliation.corporation.*"], (ctx, _) =>
        {
            var payload = ctx.Event.Payload<Dictionary<string, object?>>()!;
            seen.Add((ctx.Event.Topic, ctx.Event.Key, payload["object"]?.ToString(), payload["reason"]?.ToString()));
            return Task.CompletedTask;
        });
        await rig.Bus.PumpAsync();
        return seen;
    }

    private static async Task<int> BackendPidAsync(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        await using var command = new NpgsqlCommand("SELECT pg_backend_pid()", connection, transaction);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<bool> IsWaitingForAdvisoryLockAsync(Rig rig, int waitingPid, Task pendingWrite)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var monitor = await rig.Source.OpenConnectionAsync(timeout.Token);
        try
        {
            while (true)
            {
                if (pendingWrite.IsCompleted)
                    return false;

                await using var command = new NpgsqlCommand("""
                    SELECT EXISTS (
                        SELECT 1 FROM pg_stat_activity
                        WHERE pid = $1 AND wait_event_type = 'Lock' AND wait_event = 'advisory'
                    )
                    """, monitor);
                command.Parameters.AddWithValue(waitingPid);
                if ((bool)(await command.ExecuteScalarAsync(timeout.Token))!)
                    return true;
                await Task.Delay(10, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            return false;
        }
    }

    private static async Task AssertOnlyExpiryJobAsync(Rig rig, string kind, long subject, long obj, long runAtMs)
    {
        var job = Assert.Single(await rig.Scheduler.ListAsync(RelationStore.ExpireKind, "pending"));
        Assert.Equal($"{kind}|{subject}|{obj}", job.Key);
        Assert.Equal(runAtMs, job.RunAtMs);
    }
}

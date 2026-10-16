using System.Text.Json;
using Eve.Content;
using Eve.Events;
using Eve.Scheduler;

namespace Eve.Content.Tests;

public sealed class AbyssalContentTests
{
    private const long Creator = 1001;
    private const long Member = 1002;
    private const long CreatorShip = 2001;
    private const long MemberShip = 2002;
    private const long FleetId = 3001;

    [Fact]
    public void Client_game_mode_and_server_content_type_are_distinct_contracts()
    {
        Assert.Equal(1, (int)AbyssalGameMode.SoloCruiser);
        Assert.Equal(2, (int)AbyssalGameMode.ThreeFrigates);
        Assert.Equal(3, (int)AbyssalGameMode.TwoDestroyers);
        Assert.Equal(4, (int)AbyssalContentType.TwoPlayerDestroyers);
        Assert.Equal(AbyssalContentType.SoloCruiser,
            AbyssalContentRuntime.ContentTypeForGameMode(AbyssalGameMode.SoloCruiser));
        Assert.Equal(AbyssalContentType.FrigateFleet,
            AbyssalContentRuntime.ContentTypeForGameMode(AbyssalGameMode.ThreeFrigates));
        Assert.Equal(AbyssalContentType.TwoPlayerDestroyers,
            AbyssalContentRuntime.ContentTypeForGameMode(AbyssalGameMode.TwoDestroyers));
        Assert.NotEqual((int)AbyssalGameMode.TwoDestroyers, (int)AbyssalContentType.TwoPlayerDestroyers);
    }

    [PgFact]
    public async Task Room_clear_unlocks_each_gate_and_final_exit_waits_for_all_return_receipts()
    {
        await using var rig = await Rig.Create();
        var abyssal = new AbyssalContentRuntime(rig.Content, rig.Bus, rig.Scheduler);
        var transitionEvents = new List<JsonElement>();
        var locationExitedEvents = new List<JsonElement>();
        var returnEvents = new List<JsonElement>();
        var finishedEvents = new List<JsonElement>();
        rig.Bus.Subscribe("test.abyssal.transitions", ["abyssal.room.transition.requested"], (context, _) =>
        {
            transitionEvents.Add(context.Event.Json().Clone());
            return Task.CompletedTask;
        });
        rig.Bus.Subscribe("test.abyssal.location-exited", ["abyssal.location.exited"], (context, _) =>
        {
            locationExitedEvents.Add(context.Event.Json().Clone());
            return Task.CompletedTask;
        });
        rig.Bus.Subscribe("test.abyssal.returns", ["abyssal.participant.return_requested"], (context, _) =>
        {
            returnEvents.Add(context.Event.Json().Clone());
            return Task.CompletedTask;
        });
        rig.Bus.Subscribe("test.abyssal.finished", ["abyssal.participant.content_finished"], (context, _) =>
        {
            finishedEvents.Add(context.Event.Json().Clone());
            return Task.CompletedTask;
        });
        await rig.Ready();

        var started = await rig.Commit((connection, transaction) =>
            abyssal.StartAsync(connection, transaction, StartRequest(8000, Plan("dark"))));
        var instanceId = started.State!.InstanceId;
        await rig.Commit((connection, transaction) =>
            abyssal.EnterAsync(connection, transaction, EnterRequest(instanceId, Creator, CreatorShip, 8100)));
        await rig.Commit((connection, transaction) =>
            abyssal.EnterAsync(connection, transaction, EnterRequest(instanceId, Member, MemberShip, 8101)));

        for (var locationId = 1; locationId <= 3; locationId++)
        {
            var firstEntityId = 90000 + locationId * 10;
            await rig.Commit((connection, transaction) =>
                abyssal.PrepareRoomSpawnAsync(connection, transaction,
                    new AbyssalRoomSpawnRequest(instanceId, locationId,
                        Entities(locationId, firstEntityId, firstEntityId + 1))));

            var firstKill = await rig.Commit((connection, transaction) =>
                abyssal.NpcDestroyedAsync(connection, transaction,
                    new AbyssalNpcDestroyedRequest(instanceId, locationId, firstEntityId)));
            Assert.False(firstKill.State!.Data.Rooms[locationId - 1].Cleared);
            var duplicateKill = await rig.Commit((connection, transaction) =>
                abyssal.NpcDestroyedAsync(connection, transaction,
                    new AbyssalNpcDestroyedRequest(instanceId, locationId, firstEntityId)));
            Assert.Equal(AbyssalOperationStatus.AlreadyApplied, duplicateKill.Status);

            await Assert.ThrowsAsync<AbyssalRuleException>(() => rig.Commit((connection, transaction) =>
                abyssal.ActivateRoomGateAsync(connection, transaction,
                    new AbyssalRoomGateActivationRequest(instanceId, locationId, 93000 + locationId, Creator))));
            var lastKill = await rig.Commit((connection, transaction) =>
                abyssal.NpcDestroyedAsync(connection, transaction,
                    new AbyssalNpcDestroyedRequest(instanceId, locationId, firstEntityId + 1)));
            Assert.True(lastKill.State!.Data.Rooms[locationId - 1].Cleared);

            if (locationId < 3)
            {
                var moved = await rig.Commit((connection, transaction) =>
                    abyssal.ActivateRoomGateAsync(connection, transaction,
                        new AbyssalRoomGateActivationRequest(instanceId, locationId, 93000 + locationId, Creator)));
                Assert.Equal(locationId + 1, moved.State!.Data.CurrentRoomIndex);
                Assert.All(moved.State.Data.Participants.Where(p => p.Status is AbyssalParticipantStatus.Active or AbyssalParticipantStatus.Disconnected),
                    p => Assert.Equal(locationId + 1, p.CurrentRoomIndex));
            }
            else
            {
                var exit = await rig.Commit((connection, transaction) =>
                    abyssal.EndGateActivationAsync(connection, transaction,
                        new AbyssalEndGateActivationRequest(instanceId, locationId, 94003, Creator)));
                Assert.All(exit.State!.Data.Participants, p => Assert.Equal(AbyssalParticipantStatus.Returning, p.Status));
                Assert.Null(exit.State.ExpiresAtMs);
                Assert.Empty(await rig.Scheduler.ListAsync(ContentRuntime.ExpireKind));

                var firstReceipt = await rig.Commit((connection, transaction) =>
                    abyssal.RecordOriginReturnAsync(connection, transaction,
                        new AbyssalReturnReceiptRequest(instanceId, Creator)));
                Assert.Equal("active", firstReceipt.State!.ContentState);
                var finalReceipt = await rig.Commit((connection, transaction) =>
                    abyssal.RecordOriginReturnAsync(connection, transaction,
                        new AbyssalReturnReceiptRequest(instanceId, Member)));
                Assert.Equal("completed", finalReceipt.State!.ContentState);
            }
        }

        await rig.Drain();
        Assert.Equal(2, transitionEvents.Count);
        Assert.Equal(new[] { 2, 3 }, transitionEvents.Select(e => e.GetProperty("toLocationId").GetInt32()));
        Assert.Equal(new[] { "environment-1", "environment-2" },
            transitionEvents.Select(e => e.GetProperty("fromEnvironmentProfileId").GetString()));
        Assert.Equal(new[] { "environment-2", "environment-3" },
            transitionEvents.Select(e => e.GetProperty("toEnvironmentProfileId").GetString()));
        Assert.All(transitionEvents, e =>
        {
            Assert.Equal(7100001, e.GetProperty("spaceLocationId").GetInt32());
            Assert.Equal(2, e.GetProperty("recipients").GetArrayLength());
        });
        Assert.Equal(6, locationExitedEvents.Count);
        Assert.All(locationExitedEvents, e =>
            Assert.Equal("environment-" + e.GetProperty("locationId").GetInt32(),
                e.GetProperty("environmentProfileId").GetString()));
        Assert.Equal(new long[] { Creator, Member }, returnEvents.Select(e => e.GetProperty("recipientCharacterId").GetInt64()).Order());
        Assert.All(returnEvents, e => Assert.Equal(started.State.Data.ContentId, e.GetProperty("contentId").GetString()));
        Assert.Equal(new long[] { Creator, Member },
            finishedEvents.Select(e => e.GetProperty("recipientCharacterId").GetInt64()).Order());
        Assert.All(finishedEvents, e => Assert.Equal(started.State.Data.ContentId, e.GetProperty("contentId").GetString()));
        Assert.Empty(await rig.Scheduler.ListAsync(ContentRuntime.ExpireKind));
    }

    [PgFact]
    public async Task Concurrent_deploy_retries_for_one_trace_return_one_persisted_run()
    {
        await using var rig = await Rig.Create();
        var abyssal = new AbyssalContentRuntime(rig.Content, rig.Bus, rig.Scheduler);
        await rig.Ready();

        var requests = Enumerable.Range(0, 6)
            .Select(index => StartRequest(7999, Plan(index % 2 == 0 ? "dark" : "gamma")))
            .ToArray();
        var results = await Task.WhenAll(requests.Select(request =>
            rig.Commit((connection, transaction) => abyssal.StartAsync(connection, transaction, request))));

        Assert.Single(results.Select(result => result.State.InstanceId).Distinct());
        Assert.Single(results, result => result.Status == AbyssalOperationStatus.Applied);
        Assert.All(results, result => Assert.Equal(results[0].State.Data.ContentId, result.State.Data.ContentId));
        Assert.All(results, result => Assert.Equal(results[0].State.Data.Plan.WeatherType, result.State.Data.Plan.WeatherType));

        await rig.Drain();
        Assert.Single(rig.Seen, topic => topic == "content.abyssal.started");
    }

        [PgFact]
    public async Task Run_deadline_expires_the_content_without_admin_returning_survivors()
        {
            await using var rig = await Rig.Create();
            var abyssal = new AbyssalContentRuntime(rig.Content, rig.Bus, rig.Scheduler);
            var deathRequests = new List<JsonElement>();
            var returnEvents = new List<JsonElement>();
            rig.Bus.Subscribe("test.abyssal.expiry", ["abyssal.participant.return_requested"], (context, _) =>
            {
                returnEvents.Add(context.Event.Json().Clone());
                return Task.CompletedTask;
            });
            rig.Bus.Subscribe("test.abyssal.expiry-death", ["abyssal.participant.death_settlement_requested"], (context, _) =>
            {
                deathRequests.Add(context.Event.Json().Clone());
                return Task.CompletedTask;
            });
            await rig.Ready();

            var started = await rig.Commit((connection, transaction) =>
                abyssal.StartAsync(connection, transaction, StartRequest(7998, Plan("dark"))));
            var entered = await rig.Commit((connection, transaction) =>
                abyssal.EnterAsync(connection, transaction, EnterRequest(started.State.InstanceId, Creator, CreatorShip, 8100)));
            rig.Now = entered.State.Data.RunExpiresAtMs!.Value;

            var expired = await rig.Commit((connection, transaction) =>
                abyssal.ExpireIfDueAsync(connection, transaction, started.State.InstanceId));
            Assert.Equal(AbyssalOperationStatus.Applied, expired.Status);
            Assert.Equal("expired", expired.State.ContentState);
            Assert.Empty(await rig.Scheduler.ListAsync(ContentRuntime.ExpireKind));

            var participant = expired.State.Data.Participants.Single();
            Assert.NotNull(participant.DeathSettlementRequestedAtMs);
            Assert.Equal(AbyssalDeathSettlementCause.RunExpired, participant.DeathSettlementCause);
            Assert.Null(participant.DeathSettlementReceiptAtMs);

            await rig.Drain();
            Assert.Empty(returnEvents);
            Assert.Single(deathRequests);
            Assert.Equal(Creator, deathRequests.Single().GetProperty("recipientCharacterId").GetInt64());
            Assert.Equal((int)AbyssalDeathSettlementCause.RunExpired,
                deathRequests.Single().GetProperty("cause").GetInt32());
            Assert.Equal(JsonValueKind.Null, deathRequests.Single().GetProperty("wreckTypeId").ValueKind);
        }

        [PgFact]
    public async Task Timeout_death_notifications_follow_the_pod_clone_settlement_receipt()
        {
            await using var rig = await Rig.Create();
            var abyssal = new AbyssalContentRuntime(rig.Content, rig.Bus, rig.Scheduler);
            var sequence = new List<string>();
            var deathRequests = new List<JsonElement>();
            var locationExited = new List<JsonElement>();
            var finished = new List<JsonElement>();
            rig.Bus.Subscribe("test.abyssal.timeout-death-request", ["abyssal.participant.death_settlement_requested"], (context, _) =>
            {
                deathRequests.Add(context.Event.Json().Clone());
                return Task.CompletedTask;
            });
            rig.Bus.Subscribe("test.abyssal.timeout-exit", ["abyssal.location.exited"], (context, _) =>
            {
                sequence.Add("location-exited");
                locationExited.Add(context.Event.Json().Clone());
                return Task.CompletedTask;
            });
            rig.Bus.Subscribe("test.abyssal.timeout-finished", ["abyssal.participant.content_finished"], (context, _) =>
            {
                sequence.Add("content-finished");
                finished.Add(context.Event.Json().Clone());
                return Task.CompletedTask;
            });
            await rig.Ready();

            var started = await rig.Commit((connection, transaction) =>
                abyssal.StartAsync(connection, transaction, StartRequest(7997, Plan("dark"))));
            var instanceId = started.State!.InstanceId;
            var entered = await rig.Commit((connection, transaction) =>
                abyssal.EnterAsync(connection, transaction, EnterRequest(instanceId, Creator, CreatorShip, 8100)));
            rig.Now = entered.State!.Data.RunExpiresAtMs!.Value;

            var expired = await rig.Commit((connection, transaction) =>
                abyssal.ExpireIfDueAsync(connection, transaction, instanceId));
            Assert.Equal("expired", expired.State.ContentState);
            Assert.Equal(AbyssalDeathSettlementCause.RunExpired,
                expired.State.Data.Participants.Single().DeathSettlementCause);
            await rig.Drain();
            var settlementKey = deathRequests.Single().GetProperty("settlementKey").GetString()!;

            var destroyed = await rig.Commit((connection, transaction) =>
                abyssal.ParticipantDestroyedAsync(connection, transaction,
                    new AbyssalParticipantDestroyedRequest(instanceId, Creator, CreatorShip, "timeout")));
            Assert.Equal("expired", destroyed.State.ContentState);
            Assert.Equal(AbyssalParticipantStatus.Destroyed, destroyed.State.Data.Participants.Single().Status);

            var settled = await rig.Commit((connection, transaction) =>
                abyssal.RecordParticipantDestructionSettlementAsync(connection, transaction,
                    new AbyssalParticipantDestructionSettlementRequest(instanceId, Creator, CreatorShip, settlementKey)));
            Assert.Equal(AbyssalOperationStatus.Applied, settled.Status);
            Assert.Equal("expired", settled.State.ContentState);

            await rig.Drain();
            Assert.Equal(new[] { "location-exited", "content-finished" }, sequence);
            Assert.Equal(Creator, locationExited.Single().GetProperty("recipientCharacterId").GetInt64());
            Assert.Equal(Creator, finished.Single().GetProperty("recipientCharacterId").GetInt64());
            Assert.Equal(started.State.Data.ContentId, finished.Single().GetProperty("contentId").GetString());
            Assert.NotNull(settled.State.Data.Participants.Single().DeathSettlementReceiptAtMs);
        }

    [PgFact]
    public async Task Start_is_idempotent_by_trace_and_keeps_the_first_selected_plan()
    {
        await using var rig = await Rig.Create();
        var abyssal = new AbyssalContentRuntime(rig.Content, rig.Bus, rig.Scheduler);
        await rig.Ready();

        var request = StartRequest(8001, Plan("dark"));
        var first = await rig.Commit(async (connection, transaction) =>
        {
            var conflict = await abyssal.FindAdmissionConflictAsync(
                connection, transaction, request.CreatorCharacterId, request.FleetId, request.TraceItemId);
            Assert.Null(conflict);
            return await abyssal.StartAsync(connection, transaction, request);
        });
        var retry = await rig.Commit((connection, transaction) =>
            abyssal.StartAsync(connection, transaction, request with { Plan = Plan("gamma") }));

        Assert.Equal(AbyssalOperationStatus.Applied, first.Status);
        Assert.Equal(AbyssalOperationStatus.AlreadyApplied, retry.Status);
        Assert.Equal(first.State!.InstanceId, retry.State!.InstanceId);
        Assert.Equal(first.State.Data.ContentId, retry.State.Data.ContentId);
        Assert.Equal(7100001, first.State.Data.SpaceLocationId);
        Assert.Equal(7100001, first.State.Data.Plan.SpaceLocationId);
        Assert.Empty(first.State.Data.Participants);
        Assert.Equal(1, first.State.Data.Rooms[0].LocationId);
        Assert.Equal(AbyssalGameMode.ThreeFrigates, first.State.Data.GameModeId);
        Assert.Equal(AbyssalContentType.FrigateFleet, first.State.Data.ContentType);
        Assert.Equal("dark", retry.State.Data.Plan.WeatherType);
        Assert.Null(retry.State.Data.RunStartedAtMs);
        Assert.Null(retry.State.Data.RunExpiresAtMs);
        Assert.Equal(request.TraceExpiresAtMs, (await rig.Content.GetAsync(first.State.InstanceId))!.ExpiresMs);
    }

    [PgFact]
    public async Task Transaction_bound_lookup_returns_typed_scene_and_participant_state_without_side_effects()
    {
        await using var rig = await Rig.Create();
        var abyssal = new AbyssalContentRuntime(rig.Content, rig.Bus, rig.Scheduler);
        var locationEvents = new List<JsonElement>();
        var deadlineEvents = new List<JsonElement>();
        rig.Bus.Subscribe("test.abyssal.lookup-location", ["abyssal.location.entered"], (context, _) =>
        {
            locationEvents.Add(context.Event.Json().Clone());
            return Task.CompletedTask;
        });
        rig.Bus.Subscribe("test.abyssal.lookup-deadline", ["abyssal.deadline.added"], (context, _) =>
        {
            deadlineEvents.Add(context.Event.Json().Clone());
            return Task.CompletedTask;
        });
        await rig.Ready();

        var started = await rig.Commit((connection, transaction) =>
            abyssal.StartAsync(connection, transaction, StartRequest(8009, Plan("dark"))));
        var instanceId = started.State!.InstanceId;
        var entered = await rig.Commit((connection, transaction) =>
            abyssal.EnterAsync(connection, transaction, EnterRequest(instanceId, Creator, CreatorShip, 8110)));
        await rig.Drain();
        await rig.Commit((connection, transaction) =>
            abyssal.DisconnectAsync(connection, transaction, instanceId, Creator));

        var before = (await rig.Content.GetAsync(instanceId))!;
        var jobsBefore = await rig.Scheduler.ListAsync(ContentRuntime.ExpireKind);
        var locationEventCount = locationEvents.Count;
        var deadlineEventCount = deadlineEvents.Count;
        var (byTrace, participant) = await rig.Commit(async (connection, transaction) =>
        {
            var run = await abyssal.FindByTraceItemIdAsync(connection, transaction, 8009);
            var binding = await abyssal.FindActiveForParticipantAsync(connection, transaction, Creator, CreatorShip);
            return (run, binding);
        });

        Assert.Equal(instanceId, byTrace!.InstanceId);
        Assert.Equal(entered.State!.Data.ContentId, byTrace.Data.ContentId);
        Assert.NotNull(participant);
        Assert.Equal(byTrace.Data.ContentId, participant.Run.Data.ContentId);
        Assert.Equal(7100001, participant.Run.Data.SpaceLocationId);
        Assert.Equal(1, participant.Participant.CurrentRoomIndex);
        Assert.Equal(AbyssalParticipantStatus.Disconnected, participant.Participant.Status);
        Assert.Equal(8110, participant.Participant.Origin.SolarSystemId);
        Assert.Equal(60003760 + Creator, participant.Participant.Origin.LocationId);
        Assert.Equal(1, participant.Run.Data.Rooms[0].LocationId);
        Assert.Null(await rig.Commit((connection, transaction) =>
            abyssal.FindActiveForParticipantAsync(connection, transaction, Creator, CreatorShip + 1)));

        var after = (await rig.Content.GetAsync(instanceId))!;
        var jobsAfter = await rig.Scheduler.ListAsync(ContentRuntime.ExpireKind);
        Assert.Equal(before.DataJson, after.DataJson);
        Assert.Equal(before.ExpiresMs, after.ExpiresMs);
        Assert.Equal(jobsBefore, jobsAfter);
        Assert.Equal(locationEventCount, locationEvents.Count);
        Assert.Equal(deadlineEventCount, deadlineEvents.Count);
    }

    [PgFact]
    public async Task First_entry_sets_one_absolute_deadline_and_join_reconnect_replay_it_without_extending()
    {
        await using var rig = await Rig.Create();
        var abyssal = new AbyssalContentRuntime(rig.Content, rig.Bus, rig.Scheduler);
        var locationEvents = new List<JsonElement>();
        var deadlineEvents = new List<JsonElement>();
        rig.Bus.Subscribe("test.abyssal.location", ["abyssal.location.entered"], (context, _) =>
        {
            locationEvents.Add(context.Event.Json().Clone());
            return Task.CompletedTask;
        });
        rig.Bus.Subscribe("test.abyssal.deadline", ["abyssal.deadline.added"], (context, _) =>
        {
            deadlineEvents.Add(context.Event.Json().Clone());
            return Task.CompletedTask;
        });
        await rig.Ready();

        var started = await rig.Commit((connection, transaction) =>
            abyssal.StartAsync(connection, transaction, StartRequest(8002, Plan("electrical"))));
        var first = await rig.Commit((connection, transaction) =>
            abyssal.EnterAsync(connection, transaction, EnterRequest(started.State!.InstanceId, Creator, CreatorShip, 8101)));
        var deadline = rig.Now + AbyssalContentRuntime.RunDurationMs;
        var scheduledExpiry = Assert.Single(await rig.Scheduler.ListAsync(ContentRuntime.ExpireKind));
        Assert.Equal(deadline, scheduledExpiry.RunAtMs);
        Assert.Equal(deadline, first.State!.Data.RunExpiresAtMs);
        Assert.Equal(rig.Now, first.State.Data.RunStartedAtMs);

        rig.Now += 30_000;
        var joined = await rig.Commit((connection, transaction) =>
            abyssal.EnterAsync(connection, transaction, EnterRequest(started.State.InstanceId, Member, MemberShip, 8102)));
        Assert.Equal(deadline, joined.State!.Data.RunExpiresAtMs);

        await rig.Commit((connection, transaction) =>
            abyssal.DisconnectAsync(connection, transaction, started.State.InstanceId, Member));
        rig.Now += 45_000;
        var reconnected = await rig.Commit((connection, transaction) =>
            abyssal.ReconnectAsync(connection, transaction, Member, MemberShip));
        Assert.Equal(deadline, reconnected.State!.Data.RunExpiresAtMs);
        Assert.Equal(1, reconnected.State.Data.Participants.Single(p => p.CharacterId == Member).CurrentRoomIndex);
        var replayedExpiry = Assert.Single(await rig.Scheduler.ListAsync(ContentRuntime.ExpireKind));
        Assert.Equal(scheduledExpiry.Id, replayedExpiry.Id);
        Assert.Equal(deadline, replayedExpiry.RunAtMs);

        await rig.Drain();
        Assert.Equal(new long[] { Creator, Member, Member },
            deadlineEvents.Select(e => e.GetProperty("recipientCharacterId").GetInt64()));
        Assert.All(deadlineEvents, e => Assert.Equal(deadline, e.GetProperty("expiresAtMs").GetInt64()));
        Assert.All(locationEvents, e =>
        {
            Assert.Equal(started.State.Data.ContentId, e.GetProperty("contentId").GetString());
            Assert.Equal(1, e.GetProperty("locationId").GetInt32());
            Assert.Equal(7100001, e.GetProperty("spaceLocationId").GetInt32());
            Assert.Equal(1, e.GetProperty("difficultyTier").GetInt32());
            Assert.Equal("environment-1", e.GetProperty("environmentProfileId").GetString());
        });
        Assert.Equal(Creator, locationEvents[0].GetProperty("recipientCharacterId").GetInt64());
        Assert.Equal(Member, locationEvents[1].GetProperty("recipientCharacterId").GetInt64());
    }

    [PgFact]
    public async Task Room_spawn_reservation_is_saved_before_outbox_and_retries_keep_the_same_entity_ids()
    {
        await using var rig = await Rig.Create();
        var abyssal = new AbyssalContentRuntime(rig.Content, rig.Bus, rig.Scheduler);
        var spawnEvents = new List<JsonElement>();
        rig.Bus.Subscribe("test.abyssal.spawn", ["abyssal.room.spawn_requested"], (context, _) =>
        {
            spawnEvents.Add(context.Event.Json().Clone());
            return Task.CompletedTask;
        });
        await rig.Ready();

        var started = await rig.Commit((connection, transaction) =>
            abyssal.StartAsync(connection, transaction, StartRequest(8003, Plan("dark"))));
        await rig.Commit((connection, transaction) =>
            abyssal.EnterAsync(connection, transaction, EnterRequest(started.State!.InstanceId, Creator, CreatorShip, 8201)));

        var reserved = Entities(1, 90001, 90002);
        var prepared = await rig.Commit((connection, transaction) =>
            abyssal.PrepareRoomSpawnAsync(connection, transaction,
                new AbyssalRoomSpawnRequest(started.State!.InstanceId, 1, reserved)));
        var duplicate = await rig.Commit((connection, transaction) =>
            abyssal.PrepareRoomSpawnAsync(connection, transaction,
                new AbyssalRoomSpawnRequest(started.State.InstanceId, 1, Entities(1, 91001, 91002))));

        Assert.Equal(AbyssalOperationStatus.Applied, prepared.Status);
        Assert.Equal(AbyssalOperationStatus.AlreadyApplied, duplicate.Status);
        Assert.Equal(new long[] { 90001, 90002 },
            duplicate.State!.Data.Rooms[0].Entities.Select(e => e.EntityId));

        await rig.Drain();
        Assert.Single(spawnEvents);
        Assert.Equal(7100001, spawnEvents[0].GetProperty("spaceLocationId").GetInt32());
        Assert.Equal(1, spawnEvents[0].GetProperty("locationId").GetInt32());
        Assert.Equal(new long[] { 90001, 90002 },
            spawnEvents[0].GetProperty("entities").EnumerateArray()
                .Select(e => e.GetProperty("entityId").GetInt64()));
    }

        [PgFact]
    public async Task One_participant_death_or_return_does_not_finish_the_other_participants_run()
        {
            await using var rig = await Rig.Create();
            var abyssal = new AbyssalContentRuntime(rig.Content, rig.Bus, rig.Scheduler);
            var deathRequests = new List<JsonElement>();
            var finishedEvents = new List<JsonElement>();
            rig.Bus.Subscribe("test.abyssal.death-request", ["abyssal.participant.death_settlement_requested"], (context, _) =>
            {
                deathRequests.Add(context.Event.Json().Clone());
                return Task.CompletedTask;
            });
            rig.Bus.Subscribe("test.abyssal.death-finish", ["abyssal.participant.content_finished"], (context, _) =>
            {
                finishedEvents.Add(context.Event.Json().Clone());
                return Task.CompletedTask;
            });
            await rig.Ready();

            var started = await rig.Commit((connection, transaction) =>
                abyssal.StartAsync(connection, transaction, StartRequest(8004, Plan("exotic"))));
            var instanceId = started.State!.InstanceId;
            await rig.Commit((connection, transaction) =>
                abyssal.EnterAsync(connection, transaction, EnterRequest(instanceId, Creator, CreatorShip, 8301)));
            await rig.Commit((connection, transaction) =>
                abyssal.EnterAsync(connection, transaction, EnterRequest(instanceId, Member, MemberShip, 8302)));

            var death = await rig.Commit((connection, transaction) =>
                abyssal.ParticipantDestroyedAsync(connection, transaction,
                    new AbyssalParticipantDestroyedRequest(instanceId, Creator, CreatorShip, "ship-destroyed")));
            Assert.Equal(AbyssalOperationStatus.Applied, death.Status);
            Assert.Equal("active", death.State!.ContentState);
            Assert.Equal(AbyssalParticipantStatus.Destroyed,
                death.State.Data.Participants.Single(p => p.CharacterId == Creator).Status);
            Assert.Equal(AbyssalParticipantStatus.Active,
                death.State.Data.Participants.Single(p => p.CharacterId == Member).Status);
            var deathRequestData = death.State.Data.Participants.Single(p => p.CharacterId == Creator);
            Assert.NotNull(deathRequestData.DeathSettlementRequestedAtMs);
            Assert.Equal(AbyssalDeathSettlementCause.ShipDestroyed, deathRequestData.DeathSettlementCause);
            Assert.Null(deathRequestData.DeathSettlementReceiptAtMs);

            await rig.Drain();
            var request = deathRequests.Single();
            Assert.Equal(Creator, request.GetProperty("recipientCharacterId").GetInt64());
            var deathSettlement = await rig.Commit((connection, transaction) =>
                abyssal.RecordParticipantDestructionSettlementAsync(connection, transaction,
                    new AbyssalParticipantDestructionSettlementRequest(instanceId, Creator, CreatorShip,
                        request.GetProperty("settlementKey").GetString()!)));
            Assert.Equal(AbyssalOperationStatus.Applied, deathSettlement.Status);
            Assert.Equal("active", deathSettlement.State.ContentState);
            var returnRequest = await rig.Commit((connection, transaction) =>
                abyssal.ReturnParticipantAsync(connection, transaction,
                    new AbyssalParticipantReturnRequest(instanceId, Member, "individual-return")));
            Assert.Equal("active", returnRequest.State.ContentState);
            var memberReturn = await rig.Commit((connection, transaction) =>
                abyssal.RecordOriginReturnAsync(connection, transaction,
                    new AbyssalReturnReceiptRequest(instanceId, Member)));
            Assert.Equal(AbyssalOperationStatus.Applied, memberReturn.Status);
            Assert.Equal("abandoned", memberReturn.State.ContentState);

            await rig.Drain();
            Assert.Equal(new long[] { Creator, Member },
                finishedEvents.Select(e => e.GetProperty("recipientCharacterId").GetInt64()).Order());
            Assert.All(finishedEvents, e => Assert.Equal(started.State.Data.ContentId, e.GetProperty("contentId").GetString()));
        }

    [PgFact]
    public async Task Server_restart_does_not_spawn_an_entered_room_again()
    {
        await using var rig = await Rig.Create();
        var abyssal = new AbyssalContentRuntime(rig.Content, rig.Bus, rig.Scheduler);
        var spawnEvents = new List<JsonElement>();
        rig.Bus.Subscribe("test.abyssal.no-respawn", ["abyssal.room.spawn_requested"], (context, _) =>
        {
            spawnEvents.Add(context.Event.Json().Clone());
            return Task.CompletedTask;
        });
        await rig.Ready();

        var started = await rig.Commit((connection, transaction) =>
            abyssal.StartAsync(connection, transaction, StartRequest(8007, Plan("gamma"))));
        var instanceId = started.State.InstanceId;
        await rig.Commit((connection, transaction) =>
            abyssal.EnterAsync(connection, transaction, EnterRequest(instanceId, Creator, CreatorShip, 8401)));
        await rig.Commit((connection, transaction) =>
            abyssal.PrepareRoomSpawnAsync(connection, transaction,
                new AbyssalRoomSpawnRequest(instanceId, 1, Entities(1, 90071, 90072))));

        await rig.Commit((connection, transaction) =>
            abyssal.RecoverAfterServerRestartAsync(connection, transaction, instanceId));
        await rig.Drain();

        Assert.Single(spawnEvents);
    }

    [PgFact]
    public async Task Server_restart_requests_each_participant_return_to_their_saved_origin()
    {
        await using var rig = await Rig.Create();
        var abyssal = new AbyssalContentRuntime(rig.Content, rig.Bus, rig.Scheduler);
        var returns = new List<JsonElement>();
        rig.Bus.Subscribe("test.abyssal.return", ["abyssal.participant.return_requested"], (context, _) =>
        {
            returns.Add(context.Event.Json().Clone());
            return Task.CompletedTask;
        });
        await rig.Ready();

        var started = await rig.Commit((connection, transaction) =>
            abyssal.StartAsync(connection, transaction, StartRequest(8005, Plan("gamma"))));
        var instanceId = started.State!.InstanceId;
        await rig.Commit((connection, transaction) =>
            abyssal.EnterAsync(connection, transaction, EnterRequest(instanceId, Creator, CreatorShip, 8401)));
        await rig.Commit((connection, transaction) =>
            abyssal.EnterAsync(connection, transaction, EnterRequest(instanceId, Member, MemberShip, 8402)));

        var recovered = await rig.Commit((connection, transaction) =>
            abyssal.RecoverAfterServerRestartAsync(connection, transaction, instanceId));
        Assert.Equal(AbyssalOperationStatus.Applied, recovered.Status);
        await rig.Drain();

        Assert.Equal(new long[] { Creator, Member },
            returns.Select(e => e.GetProperty("recipientCharacterId").GetInt64()).Order());
        Assert.Equal(new long[] { 8401, 8402 },
            returns.Select(e => e.GetProperty("origin").GetProperty("solarSystemId").GetInt64()).Order());
        Assert.All(returns, e => Assert.Equal(started.State.Data.ContentId, e.GetProperty("contentId").GetString()));
        Assert.DoesNotContain(rig.Seen, topic => topic == "abyssal.room.spawn_requested");
    }

    [PgFact]
    public async Task Data_and_expiry_job_update_roll_back_together()
    {
        await using var rig = await Rig.Create();
        var abyssal = new AbyssalContentRuntime(rig.Content, rig.Bus, rig.Scheduler);
        await rig.Ready();

        var started = await rig.Commit((connection, transaction) =>
            abyssal.StartAsync(connection, transaction, StartRequest(8006, Plan("dark"))));
        var instanceId = started.State!.InstanceId;
        var before = (await rig.Content.GetAsync(instanceId))!;
        var beforeJobs = await rig.Scheduler.ListAsync(ContentRuntime.ExpireKind);
        var newExpiry = before.ExpiresMs!.Value + 120_000;

        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            Assert.True(await rig.Content.UpdateDataAndDeadlineAsync(connection, transaction, instanceId,
                new { marker = "rolled-back" }, newExpiry));
            await transaction.RollbackAsync();
        }

        var after = (await rig.Content.GetAsync(instanceId))!;
        var afterJobs = await rig.Scheduler.ListAsync(ContentRuntime.ExpireKind);
        Assert.Equal(before.DataJson, after.DataJson);
        Assert.Equal(before.ExpiresMs, after.ExpiresMs);
        Assert.Equal(beforeJobs.Single().RunAtMs, afterJobs.Single().RunAtMs);
        Assert.Equal(beforeJobs.Single().Id, afterJobs.Single().Id);
    }


    [PgFact]
    public async Task Shared_scheduler_expiry_commits_timeout_death_requests_in_the_content_failure_transaction()
    {
        await using var rig = await Rig.Create();
        var abyssal = new AbyssalContentRuntime(rig.Content, rig.Bus, rig.Scheduler);
        var deathRequests = new List<JsonElement>();
        var returnRequests = new List<JsonElement>();
        rig.Bus.Subscribe("test.abyssal.scheduler-death",
            ["abyssal.participant.death_settlement_requested"], (context, _) =>
            {
                deathRequests.Add(context.Event.Json().Clone());
                return Task.CompletedTask;
            });
        rig.Bus.Subscribe("test.abyssal.scheduler-return",
            ["abyssal.participant.return_requested"], (context, _) =>
            {
                returnRequests.Add(context.Event.Json().Clone());
                return Task.CompletedTask;
            });
        await rig.Ready();

        var started = await rig.Commit((connection, transaction) =>
            abyssal.StartAsync(connection, transaction, StartRequest(7901, Plan("dark"))));
        var instanceId = started.State!.InstanceId;
        var first = await rig.Commit((connection, transaction) =>
            abyssal.EnterAsync(connection, transaction, EnterRequest(instanceId, Creator, CreatorShip, 8100)));
        await rig.Commit((connection, transaction) =>
            abyssal.EnterAsync(connection, transaction, EnterRequest(instanceId, Member, MemberShip, 8200)));

        rig.Now = first.State!.Data.RunExpiresAtMs!.Value;
        Assert.True(await rig.Scheduler.RunDueAsync() > 0);

        var persisted = await rig.Content.GetAsync(instanceId);
        Assert.Equal("expired", persisted!.State);
        var participants = persisted.Data<AbyssalRunData>()!.Participants;
        Assert.All(participants, participant =>
        {
            Assert.NotNull(participant.DeathSettlementRequestedAtMs);
            Assert.Equal(AbyssalDeathSettlementCause.RunExpired, participant.DeathSettlementCause);
            Assert.Null(participant.DeathSettlementReceiptAtMs);
        });

        await rig.Drain();
        Assert.Equal(new long[] { Creator, Member },
            deathRequests.Select(item => item.GetProperty("recipientCharacterId").GetInt64()).Order());
        Assert.Empty(returnRequests);
    }

    [PgFact]
    public async Task Restart_at_due_deadline_expires_and_queues_timeout_deaths_instead_of_safe_return()
    {
        await using var rig = await Rig.Create();
        var abyssal = new AbyssalContentRuntime(rig.Content, rig.Bus, rig.Scheduler);
        var deathRequests = new List<JsonElement>();
        var returnRequests = new List<JsonElement>();
        rig.Bus.Subscribe("test.abyssal.restart-expiry-death",
            ["abyssal.participant.death_settlement_requested"], (context, _) =>
            {
                deathRequests.Add(context.Event.Json().Clone());
                return Task.CompletedTask;
            });
        rig.Bus.Subscribe("test.abyssal.restart-expiry-return",
            ["abyssal.participant.return_requested"], (context, _) =>
            {
                returnRequests.Add(context.Event.Json().Clone());
                return Task.CompletedTask;
            });
        await rig.Ready();

        var started = await rig.Commit((connection, transaction) =>
            abyssal.StartAsync(connection, transaction, StartRequest(7902, Plan("dark"))));
        var instanceId = started.State!.InstanceId;
        var entered = await rig.Commit((connection, transaction) =>
            abyssal.EnterAsync(connection, transaction, EnterRequest(instanceId, Creator, CreatorShip, 8100)));
        await rig.Commit((connection, transaction) =>
            abyssal.EnterAsync(connection, transaction, EnterRequest(instanceId, Member, MemberShip, 8200)));
        rig.Now = entered.State!.Data.RunExpiresAtMs!.Value;

        var recovered = await rig.Commit((connection, transaction) =>
            abyssal.RecoverAfterServerRestartAsync(connection, transaction, instanceId));
        Assert.Equal("expired", recovered.State!.ContentState);
        Assert.All(recovered.State.Data.Participants, participant =>
        {
            Assert.Equal(AbyssalDeathSettlementCause.RunExpired, participant.DeathSettlementCause);
            Assert.NotNull(participant.DeathSettlementRequestedAtMs);
        });

        await rig.Drain();
        Assert.Equal(2, deathRequests.Count);
        var firstKeys = deathRequests.Select(item => item.GetProperty("settlementKey").GetString()).ToArray();
        Assert.Empty(returnRequests);

        var terminalRecovery = await rig.Commit((connection, transaction) =>
            abyssal.RecoverAfterServerRestartAsync(connection, transaction, instanceId));
        Assert.Equal("expired", terminalRecovery.State!.ContentState);
        await rig.Drain();
        Assert.Equal(4, deathRequests.Count);
        Assert.All(deathRequests.Skip(2), item =>
            Assert.Contains(item.GetProperty("settlementKey").GetString(), firstKeys));
        Assert.Empty(returnRequests);
    }

    [PgFact]
    public async Task Restart_replays_pending_individual_ship_death_and_returns_only_living_survivor()
    {
        await using var rig = await Rig.Create();
        var abyssal = new AbyssalContentRuntime(rig.Content, rig.Bus, rig.Scheduler);
        var deathRequests = new List<JsonElement>();
        var returnRequests = new List<JsonElement>();
        rig.Bus.Subscribe("test.abyssal.death-recovery",
            ["abyssal.participant.death_settlement_requested"], (context, _) =>
            {
                deathRequests.Add(context.Event.Json().Clone());
                return Task.CompletedTask;
            });
        rig.Bus.Subscribe("test.abyssal.return-recovery",
            ["abyssal.participant.return_requested"], (context, _) =>
            {
                returnRequests.Add(context.Event.Json().Clone());
                return Task.CompletedTask;
            });
        await rig.Ready();

        var started = await rig.Commit((connection, transaction) =>
            abyssal.StartAsync(connection, transaction, StartRequest(7903, Plan("dark"))));
        var instanceId = started.State!.InstanceId;
        await rig.Commit((connection, transaction) =>
            abyssal.EnterAsync(connection, transaction, EnterRequest(instanceId, Creator, CreatorShip, 8100)));
        await rig.Commit((connection, transaction) =>
            abyssal.EnterAsync(connection, transaction, EnterRequest(instanceId, Member, MemberShip, 8200)));

        var request = new AbyssalParticipantDestroyedRequest(instanceId, Creator, CreatorShip, "ship-destroyed", 671000);
        var destroyed = await rig.Commit((connection, transaction) =>
            abyssal.ParticipantDestroyedAsync(connection, transaction, request));
        Assert.Equal("active", destroyed.State!.ContentState);
        Assert.Equal(671000, destroyed.State.Data.Participants.Single(item => item.CharacterId == Creator).DeathSettlementWreckTypeId);

        await rig.Drain();
        var original = Assert.Single(deathRequests);
        var originalKey = original.GetProperty("settlementKey").GetString();
        Assert.Equal(671000, original.GetProperty("wreckTypeId").GetInt32());

        var duplicate = await rig.Commit((connection, transaction) =>
            abyssal.ParticipantDestroyedAsync(connection, transaction, request));
        Assert.Equal(AbyssalOperationStatus.AlreadyApplied, duplicate.Status);
        var conflictingDuplicate = await Assert.ThrowsAsync<AbyssalRuleException>(() =>
            rig.Commit((connection, transaction) =>
                abyssal.ParticipantDestroyedAsync(connection, transaction, request with { WreckTypeId = 671001 })));
        Assert.Equal(AbyssalRuleCode.InvalidRequest, conflictingDuplicate.Code);
        await rig.Drain();
        Assert.Single(deathRequests);

        var recovered = await rig.Commit((connection, transaction) =>
            abyssal.RecoverAfterServerRestartAsync(connection, transaction, instanceId));
        Assert.Equal("active", recovered.State!.ContentState);
        await rig.Drain();

        Assert.Equal(2, deathRequests.Count);
        Assert.All(deathRequests, item =>
        {
            Assert.Equal(originalKey, item.GetProperty("settlementKey").GetString());
            Assert.Equal(671000, item.GetProperty("wreckTypeId").GetInt32());
        });
        Assert.Equal(new long[] { Member },
            returnRequests.Select(item => item.GetProperty("recipientCharacterId").GetInt64()));
        Assert.Equal(AbyssalParticipantStatus.Destroyed,
            recovered.State.Data.Participants.Single(item => item.CharacterId == Creator).Status);
        Assert.Null(recovered.State.Data.Participants.Single(item => item.CharacterId == Member).DeathSettlementRequestedAtMs);
    }

    [PgFact]
    public async Task Restart_replays_all_dead_settlements_without_returning_anyone()
    {
        await using var rig = await Rig.Create();
        var abyssal = new AbyssalContentRuntime(rig.Content, rig.Bus, rig.Scheduler);
        var deathRequests = new List<JsonElement>();
        var returnRequests = new List<JsonElement>();
        rig.Bus.Subscribe("test.abyssal.all-dead",
            ["abyssal.participant.death_settlement_requested"], (context, _) =>
            {
                deathRequests.Add(context.Event.Json().Clone());
                return Task.CompletedTask;
            });
        rig.Bus.Subscribe("test.abyssal.all-dead-return",
            ["abyssal.participant.return_requested"], (context, _) =>
            {
                returnRequests.Add(context.Event.Json().Clone());
                return Task.CompletedTask;
            });
        await rig.Ready();

        var started = await rig.Commit((connection, transaction) =>
            abyssal.StartAsync(connection, transaction, StartRequest(7904, Plan("dark"))));
        var instanceId = started.State!.InstanceId;
        await rig.Commit((connection, transaction) =>
            abyssal.EnterAsync(connection, transaction, EnterRequest(instanceId, Creator, CreatorShip, 8100)));
        await rig.Commit((connection, transaction) =>
            abyssal.EnterAsync(connection, transaction, EnterRequest(instanceId, Member, MemberShip, 8200)));
        await rig.Commit((connection, transaction) =>
            abyssal.ParticipantDestroyedAsync(connection, transaction,
                new AbyssalParticipantDestroyedRequest(instanceId, Creator, CreatorShip, "ship-destroyed")));
        var failed = await rig.Commit((connection, transaction) =>
            abyssal.ParticipantDestroyedAsync(connection, transaction,
                new AbyssalParticipantDestroyedRequest(instanceId, Member, MemberShip, "ship-destroyed")));
        Assert.Equal("failed", failed.State!.ContentState);
        await rig.Drain();
        Assert.Equal(2, deathRequests.Count);

        var recovered = await rig.Commit((connection, transaction) =>
            abyssal.RecoverAfterServerRestartAsync(connection, transaction, instanceId));
        Assert.Equal("failed", recovered.State!.ContentState);
        await rig.Drain();

        Assert.Equal(4, deathRequests.Count);
        Assert.Equal(2, deathRequests.Select(item => item.GetProperty("settlementKey").GetString()).Distinct().Count());
        Assert.Equal(2, deathRequests.GroupBy(item => item.GetProperty("recipientCharacterId").GetInt64()).Count());
        Assert.Empty(returnRequests);
    }

    [PgFact]
    public async Task Duplicate_death_settlement_receipt_does_not_duplicate_exit_or_finished_events()
    {
        await using var rig = await Rig.Create();
        var abyssal = new AbyssalContentRuntime(rig.Content, rig.Bus, rig.Scheduler);
        var deathRequests = new List<JsonElement>();
        var locationExited = new List<JsonElement>();
        var finished = new List<JsonElement>();
        rig.Bus.Subscribe("test.abyssal.receipt-request",
            ["abyssal.participant.death_settlement_requested"], (context, _) =>
            {
                deathRequests.Add(context.Event.Json().Clone());
                return Task.CompletedTask;
            });
        rig.Bus.Subscribe("test.abyssal.receipt-exit", ["abyssal.location.exited"], (context, _) =>
        {
            locationExited.Add(context.Event.Json().Clone());
            return Task.CompletedTask;
        });
        rig.Bus.Subscribe("test.abyssal.receipt-finished", ["abyssal.participant.content_finished"], (context, _) =>
        {
            finished.Add(context.Event.Json().Clone());
            return Task.CompletedTask;
        });
        await rig.Ready();

        var started = await rig.Commit((connection, transaction) =>
            abyssal.StartAsync(connection, transaction, StartRequest(7905, Plan("dark"))));
        var instanceId = started.State!.InstanceId;
        await rig.Commit((connection, transaction) =>
            abyssal.EnterAsync(connection, transaction, EnterRequest(instanceId, Creator, CreatorShip, 8100)));
        await rig.Commit((connection, transaction) =>
            abyssal.ParticipantDestroyedAsync(connection, transaction,
                new AbyssalParticipantDestroyedRequest(instanceId, Creator, CreatorShip, "ship-destroyed")));
        await rig.Drain();
        var request = deathRequests.Single();
        var settlementKey = request.GetProperty("settlementKey").GetString()!;

        var receiptRequest = new AbyssalParticipantDestructionSettlementRequest(
            instanceId, Creator, CreatorShip, settlementKey);
        var first = await rig.Commit((connection, transaction) =>
            abyssal.RecordParticipantDestructionSettlementAsync(connection, transaction, receiptRequest));
        var duplicate = await rig.Commit((connection, transaction) =>
            abyssal.RecordParticipantDestructionSettlementAsync(connection, transaction, receiptRequest));
        Assert.Equal(AbyssalOperationStatus.Applied, first.Status);
        Assert.Equal(AbyssalOperationStatus.AlreadyApplied, duplicate.Status);

        await rig.Drain();
        Assert.Single(locationExited);
        Assert.Single(finished);
        Assert.Equal(started.State.Data.ContentId, finished.Single().GetProperty("contentId").GetString());
    }



    [PgFact]
    public async Task Blueprint_copy_run_counts_are_validated_persisted_and_reused_in_the_spawn_outbox()
    {
        await using var rig = await Rig.Create();
        var abyssal = new AbyssalContentRuntime(rig.Content, rig.Bus, rig.Scheduler);
        var spawnEvents = new List<JsonElement>();
        rig.Bus.Subscribe("test.abyssal.blueprint-spawn", ["abyssal.room.spawn_requested"], (context, _) =>
        {
            spawnEvents.Add(context.Event.Json().Clone());
            return Task.CompletedTask;
        });
        await rig.Ready();

        var plan = Plan("dark");
        var rooms = plan.Rooms.ToArray();
        rooms[0] = rooms[0] with
        {
            Loot = [new AbyssalLootPlan(70001, 2)
            {
                IsBlueprintCopy = true,
                BlueprintRunsPerCopy = [2, 5],
            }],
        };
        plan = plan with { Rooms = rooms };

        var started = await rig.Commit((connection, transaction) =>
            abyssal.StartAsync(connection, transaction, StartRequest(7906, plan)));
        var savedLoot = Assert.Single(started.State!.Data.Plan.Rooms[0].Loot);
        Assert.True(savedLoot.IsBlueprintCopy);
        Assert.Equal(new[] { 2, 5 }, savedLoot.BlueprintRunsPerCopy);

        var instanceId = started.State.InstanceId;
        await rig.Commit((connection, transaction) =>
            abyssal.EnterAsync(connection, transaction, EnterRequest(instanceId, Creator, CreatorShip, 8100)));
        var reservation = new AbyssalRoomSpawnRequest(instanceId, 1, Entities(1, 91001, 91002));
        var prepared = await rig.Commit((connection, transaction) =>
            abyssal.PrepareRoomSpawnAsync(connection, transaction, reservation));
        Assert.Equal(AbyssalOperationStatus.Applied, prepared.Status);
        var retried = await rig.Commit((connection, transaction) =>
            abyssal.PrepareRoomSpawnAsync(connection, transaction, reservation));
        Assert.Equal(AbyssalOperationStatus.AlreadyApplied, retried.Status);

        await rig.Drain();
        var spawn = Assert.Single(spawnEvents);
        var loot = Assert.Single(spawn.GetProperty("loot").EnumerateArray());
        Assert.True(loot.GetProperty("isBlueprintCopy").GetBoolean());
        Assert.Equal(new[] { 2, 5 },
            loot.GetProperty("blueprintRunsPerCopy").EnumerateArray().Select(value => value.GetInt32()));

        var invalidRooms = Plan("dark").Rooms.ToArray();
        invalidRooms[0] = invalidRooms[0] with
        {
            Loot = [new AbyssalLootPlan(70002, 2)
            {
                IsBlueprintCopy = true,
                BlueprintRunsPerCopy = [4],
            }],
        };
        var invalidPlan = Plan("dark") with { Rooms = invalidRooms };
        await Assert.ThrowsAsync<AbyssalRuleException>(() =>
            rig.Commit((connection, transaction) =>
                abyssal.StartAsync(connection, transaction, StartRequest(7907, invalidPlan))));

        var nonBlueprintRooms = Plan("dark").Rooms.ToArray();
        nonBlueprintRooms[0] = nonBlueprintRooms[0] with
        {
            Loot = [new AbyssalLootPlan(70003, 1)
            {
                BlueprintRunsPerCopy = [3],
            }],
        };
        var nonBlueprintPlan = Plan("dark") with { Rooms = nonBlueprintRooms };
        await Assert.ThrowsAsync<AbyssalRuleException>(() =>
            rig.Commit((connection, transaction) =>
                abyssal.StartAsync(connection, transaction, StartRequest(7908, nonBlueprintPlan))));
    }


    [PgFact]
    public async Task An_unentered_deployment_conflicts_for_its_creator_or_fleet_but_same_trace_retries()
    {
        await using var rig = await Rig.Create();
        var abyssal = new AbyssalContentRuntime(rig.Content, rig.Bus, rig.Scheduler);
        await rig.Ready();

        var firstRequest = StartRequest(8012, Plan("dark"));
        var first = await rig.Commit((connection, transaction) =>
            abyssal.StartAsync(connection, transaction, firstRequest));
        Assert.Empty(first.State!.Data.Participants);

        var sameTrace = await rig.Commit((connection, transaction) =>
            abyssal.FindAdmissionConflictAsync(
                connection, transaction, Creator, FleetId, firstRequest.TraceItemId));
        Assert.Null(sameTrace);

        var creatorConflict = await rig.Commit((connection, transaction) =>
            abyssal.FindAdmissionConflictAsync(connection, transaction, Creator, FleetId, 8013));
        Assert.Equal(first.State.InstanceId, creatorConflict!.InstanceId);
        var creatorRetry = await Assert.ThrowsAsync<AbyssalRuleException>(() =>
            rig.Commit((connection, transaction) =>
                abyssal.StartAsync(connection, transaction, StartRequest(8013, Plan("gamma")))));
        Assert.Equal(AbyssalRuleCode.AdmissionConflict, creatorRetry.Code);

        var otherPilotRequest = StartRequest(8014, Plan("exotic")) with
        {
            CreatorCharacterId = Member,
            CreatorShipId = MemberShip,
        };
        var fleetConflict = await rig.Commit((connection, transaction) =>
            abyssal.FindAdmissionConflictAsync(
                connection, transaction, otherPilotRequest.CreatorCharacterId, FleetId, otherPilotRequest.TraceItemId));
        Assert.Equal(first.State.InstanceId, fleetConflict!.InstanceId);
        var fleetRetry = await Assert.ThrowsAsync<AbyssalRuleException>(() =>
            rig.Commit((connection, transaction) => abyssal.StartAsync(connection, transaction, otherPilotRequest)));
        Assert.Equal(AbyssalRuleCode.AdmissionConflict, fleetRetry.Code);

        var sameTraceRetry = await rig.Commit((connection, transaction) =>
            abyssal.StartAsync(connection, transaction, firstRequest with { Plan = Plan("gamma") }));
        Assert.Equal(AbyssalOperationStatus.AlreadyApplied, sameTraceRetry.Status);
        Assert.Equal(first.State.InstanceId, sameTraceRetry.State!.InstanceId);
    }

    [PgFact]
    public async Task Concurrent_different_creators_in_one_fleet_reserve_only_one_unentered_deployment()
    {
        await using var rig = await Rig.Create();
        var abyssal = new AbyssalContentRuntime(rig.Content, rig.Bus, rig.Scheduler);
        await rig.Ready();

        var requests = new[]
        {
            StartRequest(8015, Plan("dark")),
            StartRequest(8016, Plan("gamma")) with
            {
                CreatorCharacterId = Member,
                CreatorShipId = MemberShip,
            },
        };

        async Task<(AbyssalStartRequest Request, AbyssalOperationResult? Result, AbyssalRuleException? Error)> TryStart(
            AbyssalStartRequest request)
        {
            try
            {
                var result = await rig.Commit((connection, transaction) =>
                    abyssal.StartAsync(connection, transaction, request));
                return (request, result, null);
            }
            catch (AbyssalRuleException exception)
            {
                return (request, null, exception);
            }
        }

        var outcomes = await Task.WhenAll(requests.Select(TryStart));
        var winner = Assert.Single(outcomes, outcome => outcome.Result?.Status == AbyssalOperationStatus.Applied);
        var loser = Assert.Single(outcomes, outcome => outcome.Error is not null);
        Assert.Equal(AbyssalRuleCode.AdmissionConflict, loser.Error!.Code);
        Assert.Empty(winner.Result!.State!.Data.Participants);

        var conflict = await rig.Commit((connection, transaction) =>
            abyssal.FindAdmissionConflictAsync(connection, transaction,
                loser.Request.CreatorCharacterId, FleetId, 8017));
        Assert.Equal(winner.Result.State.InstanceId, conflict!.InstanceId);
    }

    private static AbyssalStartRequest StartRequest(long traceItemId, AbyssalRunPlan plan) =>
        new(
            traceItemId,
            Creator,
            CreatorShip,
            FleetId,
            AbyssalGameMode.ThreeFrigates,
            60001,
            30000142,
            60003760,
            new AbyssalPosition(10, 20, 30),
            2_000_000,
            plan);

    [Fact]
    public void Legacy_room_and_entity_constructors_keep_empty_layout_defaults()
    {
        var room = RoomPlan(1);
        var entity = new AbyssalSpaceEntity(90001, 50001, AbyssalEntityRole.Cloud, new AbyssalPosition(1, 2, 3));
        var spawnEvent = new AbyssalRoomSpawnRequestedEvent(
            1, Guid.NewGuid().ToString("D"), 7100001, 1, 0, "dark", "profile", [], []);

        Assert.Empty(room.EnvironmentObjects);
        Assert.Empty(room.Artifacts);
        Assert.Null(entity.PlanKey);
        Assert.Empty(spawnEvent.EnvironmentObjects);
        Assert.Empty(spawnEvent.Artifacts);

        const string legacyRoomJson = """{"locationId":1,"npcSpawns":[],"loot":[],"objectives":[],"environmentProfileId":"profile"}""";
        var legacyRoom = JsonSerializer.Deserialize<AbyssalRoomPlan>(
            legacyRoomJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(legacyRoom);
        Assert.Empty(legacyRoom.EnvironmentObjects);
        Assert.Empty(legacyRoom.Artifacts);

        const string legacyEntityJson = """{"entityId":90002,"typeId":50001,"role":0,"position":{"x":1,"y":2,"z":3}}""";
        var legacyEntity = JsonSerializer.Deserialize<AbyssalSpaceEntity>(
            legacyEntityJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(legacyEntity);
        Assert.Null(legacyEntity.PlanKey);

        const string legacyEventJson = """{"instanceId":1,"contentId":"00000000-0000-0000-0000-000000000001","spaceLocationId":7100001,"locationId":1,"tier":0,"weatherType":"dark","environmentProfileId":"profile","entities":[],"loot":[]}""";
        var legacyEvent = JsonSerializer.Deserialize<AbyssalRoomSpawnRequestedEvent>(
            legacyEventJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(legacyEvent);
        Assert.Empty(legacyEvent.EnvironmentObjects);
        Assert.Empty(legacyEvent.Artifacts);

        const string legacyEnvironmentObjectJson = """{"key":"legacy","typeId":47440,"localPosition":{"x":1,"y":2,"z":3},"rotation":{"x":0,"y":0,"z":0},"role":"cloud","sourceAuthority":"legacy-fixture"}""";
        var legacyEnvironmentObject = JsonSerializer.Deserialize<AbyssalEnvironmentObjectPlan>(
            legacyEnvironmentObjectJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(legacyEnvironmentObject);
        Assert.True(legacyEnvironmentObject.MechanicsEnabled);
        Assert.Null(legacyEnvironmentObject.PresentationRadiusMeters);
    }

    [PgFact]
    public async Task Saved_layout_snapshot_is_validated_and_reused_by_the_room_spawn_outbox()
    {
        await using var rig = await Rig.Create();
        var abyssal = new AbyssalContentRuntime(rig.Content, rig.Bus, rig.Scheduler);
        var spawnEvents = new List<JsonElement>();
        rig.Bus.Subscribe("test.abyssal.layout", ["abyssal.room.spawn_requested"], (context, _) =>
        {
            spawnEvents.Add(context.Event.Json().Clone());
            return Task.CompletedTask;
        });
        await rig.Ready();

        var environmentObject = new AbyssalEnvironmentObjectPlan(
            "dark-r1-object-a",
            47437,
            new AbyssalPosition(12, 34, 56),
            new AbyssalRotation(0, 0, 1.25),
            "cloud",
            "evejs:2feb89c293284f61a4e95234f255468dd3a71ff9")
        {
            MechanicsEnabled = false,
        };
        var mainArtifactLoot = new List<AbyssalLootPlan>
        {
            new AbyssalLootPlan(21001, 2)
            {
                IsBlueprintCopy = true,
                BlueprintRunsPerCopy = [3, 5],
            },
        };
        var mainArtifact = new AbyssalArtifactPlan(
            "dark-r1-main-cache",
            17918,
            AbyssalArtifactKind.MainCache,
            new AbyssalPosition(0, 0, 43000),
            mainArtifactLoot,
            "retail_final",
            "evejs:lootTables");
        var sideArtifact = new AbyssalArtifactPlan(
            "dark-r1-extraction",
            17918,
            AbyssalArtifactKind.ExtractionNode,
            new AbyssalPosition(0, 12000, 0),
            [new AbyssalLootPlan(21002, 3)],
            "profile",
            "evejs:extractionNodeProfile");
        var environmentObjects = new List<AbyssalEnvironmentObjectPlan> { environmentObject };
        var rooms = Plan("dark").Rooms.ToArray();
        rooms[0] = rooms[0] with
        {
            Loot = [],
            EnvironmentObjects = environmentObjects,
            Artifacts = [mainArtifact, sideArtifact],
        };
        var plan = Plan("dark") with { Rooms = rooms };

        var started = await rig.Commit((connection, transaction) =>
            abyssal.StartAsync(connection, transaction, StartRequest(8021, plan)));
        environmentObjects.Clear();
        mainArtifactLoot.Clear();
        var entered = await rig.Commit((connection, transaction) =>
            abyssal.EnterAsync(connection, transaction, EnterRequest(started.State!.InstanceId, Creator, CreatorShip, 8221)));
        Assert.Equal(AbyssalOperationStatus.Applied, entered.Status);
        Assert.Equal(environmentObject, started.State!.Data.Plan.Rooms[0].EnvironmentObjects.Single());
        Assert.False(started.State.Data.Plan.Rooms[0].EnvironmentObjects.Single().MechanicsEnabled);
        Assert.Equal(new[] { mainArtifact.Key, sideArtifact.Key }, started.State.Data.Plan.Rooms[0].Artifacts.Select(artifact => artifact.Key));

        var entities = Entities(1, 90101, 90102).Concat(
        [
            new AbyssalSpaceEntity(90103, environmentObject.TypeId, AbyssalEntityRole.Cloud, new AbyssalPosition(12, 34, 56))
            {
                PlanKey = environmentObject.Key,
            },
            new AbyssalSpaceEntity(90104, mainArtifact.TypeId, AbyssalEntityRole.Container, new AbyssalPosition(0, 0, 43000))
            {
                PlanKey = mainArtifact.Key,
            },
            new AbyssalSpaceEntity(90105, sideArtifact.TypeId, AbyssalEntityRole.Container, new AbyssalPosition(0, 12000, 0))
            {
                PlanKey = sideArtifact.Key,
            },
        ]).ToArray();

        var missingKeyEntities = entities.ToArray();
        missingKeyEntities[2] = missingKeyEntities[2] with { PlanKey = null };
        var missingKey = await Assert.ThrowsAsync<AbyssalRuleException>(() =>
            rig.Commit((connection, transaction) => abyssal.PrepareRoomSpawnAsync(connection, transaction,
                new AbyssalRoomSpawnRequest(started.State.InstanceId, 1, missingKeyEntities))));
        Assert.Equal(AbyssalRuleCode.RoomSpawnMismatch, missingKey.Code);

        var wrongTypeEntities = entities.ToArray();
        wrongTypeEntities[2] = wrongTypeEntities[2] with { TypeId = environmentObject.TypeId + 1 };
        var mismatch = await Assert.ThrowsAsync<AbyssalRuleException>(() =>
            rig.Commit((connection, transaction) => abyssal.PrepareRoomSpawnAsync(connection, transaction,
                new AbyssalRoomSpawnRequest(started.State.InstanceId, 1, wrongTypeEntities))));
        Assert.Equal(AbyssalRuleCode.RoomSpawnMismatch, mismatch.Code);

        var prepared = await rig.Commit((connection, transaction) => abyssal.PrepareRoomSpawnAsync(
            connection, transaction, new AbyssalRoomSpawnRequest(started.State.InstanceId, 1, entities)));
        var retry = await rig.Commit((connection, transaction) => abyssal.PrepareRoomSpawnAsync(
            connection, transaction, new AbyssalRoomSpawnRequest(started.State.InstanceId, 1, Entities(1, 91101, 91102))));

        Assert.Equal(AbyssalOperationStatus.Applied, prepared.Status);
        Assert.Equal(AbyssalOperationStatus.AlreadyApplied, retry.Status);
        Assert.False(retry.State!.Data.Plan.Rooms[0].EnvironmentObjects.Single().MechanicsEnabled);
        Assert.Equal(new[] { mainArtifact.Key, sideArtifact.Key }, retry.State.Data.Plan.Rooms[0].Artifacts.Select(artifact => artifact.Key));
        Assert.Equal(new[] { "dark-r1-object-a", "dark-r1-main-cache", "dark-r1-extraction" },
            retry.State.Data.Rooms[0].Entities.Where(entity => entity.PlanKey is not null).Select(entity => entity.PlanKey));

        await rig.Drain();
        var spawnEvent = Assert.Single(spawnEvents);
        Assert.Empty(spawnEvent.GetProperty("loot").EnumerateArray());
        Assert.Equal("dark-r1-object-a", spawnEvent.GetProperty("environmentObjects")[0].GetProperty("key").GetString());
        Assert.Equal("evejs:2feb89c293284f61a4e95234f255468dd3a71ff9",
            spawnEvent.GetProperty("environmentObjects")[0].GetProperty("sourceAuthority").GetString());
        Assert.False(spawnEvent.GetProperty("environmentObjects")[0].GetProperty("mechanicsEnabled").GetBoolean());
        Assert.Equal(2, spawnEvent.GetProperty("artifacts").GetArrayLength());
        Assert.Equal(2, spawnEvent.GetProperty("artifacts")[0].GetProperty("loot")[0].GetProperty("count").GetInt32());
        Assert.Equal(new[] { 3, 5 },
            spawnEvent.GetProperty("artifacts")[0].GetProperty("loot")[0].GetProperty("blueprintRunsPerCopy")
                .EnumerateArray().Select(run => run.GetInt32()));
        Assert.Equal("dark-r1-extraction",
            spawnEvent.GetProperty("artifacts")[1].GetProperty("key").GetString());
        Assert.Equal(3, spawnEvent.GetProperty("artifacts")[1].GetProperty("loot")[0].GetProperty("count").GetInt32());
        Assert.Equal("dark-r1-main-cache",
            spawnEvent.GetProperty("entities").EnumerateArray()
                .Single(entity => entity.TryGetProperty("planKey", out var key) && key.GetString() == "dark-r1-main-cache")
                .GetProperty("planKey").GetString());
    }

    [PgFact]
    public async Task Presentation_radius_is_persisted_and_replayed_without_changing_type_radius()
    {
        await using var rig = await Rig.Create();
        var abyssal = new AbyssalContentRuntime(rig.Content, rig.Bus, rig.Scheduler);
        var spawnEvents = new List<JsonElement>();
        rig.Bus.Subscribe("test.abyssal.presentation-radius", ["abyssal.room.spawn_requested"], (context, _) =>
        {
            spawnEvents.Add(context.Event.Json().Clone());
            return Task.CompletedTask;
        });
        await rig.Ready();

        var boundary = new AbyssalEnvironmentObjectPlan(
            "spawn-boundary-47465",
            47465,
            new AbyssalPosition(10, 20, 30),
            new AbyssalRotation(0, 0, 0),
            "boundary",
            "source-fixture")
        {
            PresentationRadiusMeters = 75000,
        };
        var perimeter = new AbyssalEnvironmentObjectPlan(
            "spawn-perimeter-54196",
            54196,
            new AbyssalPosition(40, 50, 60),
            new AbyssalRotation(0, 0, 0),
            "perimeter",
            "source-fixture")
        {
            PresentationRadiusMeters = 7500,
        };
        var rooms = Plan("dark").Rooms.ToArray();
        rooms[0] = rooms[0] with { EnvironmentObjects = [boundary, perimeter] };
        var plan = Plan("dark") with { Rooms = rooms };

        var started = await rig.Commit((connection, transaction) =>
            abyssal.StartAsync(connection, transaction, StartRequest(8030, plan)));
        Assert.Equal(new[] { 75000d, 7500d },
            started.State!.Data.Plan.Rooms[0].EnvironmentObjects
                .Select(item => item.PresentationRadiusMeters!.Value));

        await rig.Commit((connection, transaction) =>
            abyssal.EnterAsync(connection, transaction, EnterRequest(started.State.InstanceId, Creator, CreatorShip, 8230)));
        var entities = Entities(1, 93001, 93002).Concat(
        [
            new AbyssalSpaceEntity(93003, boundary.TypeId, AbyssalEntityRole.Cloud, boundary.LocalPosition)
            {
                PlanKey = boundary.Key,
            },
            new AbyssalSpaceEntity(93004, perimeter.TypeId, AbyssalEntityRole.Cloud, perimeter.LocalPosition)
            {
                PlanKey = perimeter.Key,
            },
        ]).ToArray();

        var prepared = await rig.Commit((connection, transaction) => abyssal.PrepareRoomSpawnAsync(
            connection, transaction, new AbyssalRoomSpawnRequest(started.State.InstanceId, 1, entities)));
        var retry = await rig.Commit((connection, transaction) => abyssal.PrepareRoomSpawnAsync(
            connection, transaction, new AbyssalRoomSpawnRequest(started.State.InstanceId, 1, Entities(1, 93101, 93102))));

        Assert.Equal(AbyssalOperationStatus.Applied, prepared.Status);
        Assert.Equal(AbyssalOperationStatus.AlreadyApplied, retry.Status);
        Assert.Equal(new[] { 75000d, 7500d },
            retry.State!.Data.Plan.Rooms[0].EnvironmentObjects
                .Select(item => item.PresentationRadiusMeters!.Value));

        await rig.Drain();
        var spawnEvent = Assert.Single(spawnEvents);
        Assert.Equal(new[] { 75000d, 7500d },
            spawnEvent.GetProperty("environmentObjects").EnumerateArray()
                .Select(item => item.GetProperty("presentationRadiusMeters").GetDouble()));
    }

    [PgFact]
    public async Task Invalid_environment_layout_and_duplicate_main_loot_ownership_are_rejected()
    {
        await using var rig = await Rig.Create();
        var abyssal = new AbyssalContentRuntime(rig.Content, rig.Bus, rig.Scheduler);
        await rig.Ready();

        var environmentObject = new AbyssalEnvironmentObjectPlan(
            "gamma-r2-object",
            47437,
            new AbyssalPosition(1, 2, 3),
            new AbyssalRotation(0, 0, 0),
            "cloud",
            "evejs:environmental-catalog");
        var mainArtifact = new AbyssalArtifactPlan(
            "gamma-r2-main",
            17918,
            AbyssalArtifactKind.MainCache,
            new AbyssalPosition(0, 0, 43000),
            [new AbyssalLootPlan(22001, 1)],
            "retail_final",
            "evejs:lootTables");
        var rooms = Plan("gamma").Rooms.ToArray();
        rooms[1] = rooms[1] with
        {
            Loot = [],
            EnvironmentObjects = [environmentObject],
            Artifacts = [mainArtifact],
        };

        var duplicateKeyRooms = rooms.ToArray();
        duplicateKeyRooms[1] = duplicateKeyRooms[1] with
        {
            Artifacts = [mainArtifact with { Key = environmentObject.Key }],
        };
        var duplicateKeyPlan = Plan("gamma") with { Rooms = duplicateKeyRooms };
        var duplicateKey = await Assert.ThrowsAsync<AbyssalRuleException>(() =>
            rig.Commit((connection, transaction) => abyssal.StartAsync(connection, transaction, StartRequest(8022, duplicateKeyPlan))));
        Assert.Equal(AbyssalRuleCode.InvalidRequest, duplicateKey.Code);

        var duplicateLootRooms = rooms.ToArray();
        duplicateLootRooms[1] = duplicateLootRooms[1] with { Loot = [new AbyssalLootPlan(22002, 1)] };
        var duplicateLootPlan = Plan("gamma") with { Rooms = duplicateLootRooms };
        var duplicateLoot = await Assert.ThrowsAsync<AbyssalRuleException>(() =>
            rig.Commit((connection, transaction) => abyssal.StartAsync(connection, transaction, StartRequest(8023, duplicateLootPlan))));
        Assert.Equal(AbyssalRuleCode.InvalidRequest, duplicateLoot.Code);

        var invalidBlueprintRooms = rooms.ToArray();
        invalidBlueprintRooms[1] = invalidBlueprintRooms[1] with
        {
            Artifacts =
            [
                mainArtifact with
                {
                    Loot =
                    [
                        new AbyssalLootPlan(22003, 2)
                        {
                            IsBlueprintCopy = true,
                            BlueprintRunsPerCopy = [0, 5],
                        },
                    ],
                },
            ],
        };
        var invalidBlueprintPlan = Plan("gamma") with { Rooms = invalidBlueprintRooms };
        var invalidBlueprint = await Assert.ThrowsAsync<AbyssalRuleException>(() =>
            rig.Commit((connection, transaction) => abyssal.StartAsync(connection, transaction, StartRequest(8024, invalidBlueprintPlan))));
        Assert.Equal(AbyssalRuleCode.InvalidRequest, invalidBlueprint.Code);

        var invalidRotationRooms = rooms.ToArray();
        invalidRotationRooms[1] = invalidRotationRooms[1] with
        {
            EnvironmentObjects = [environmentObject with { Rotation = new AbyssalRotation(double.NaN, 0, 0) }],
        };
        var invalidRotationPlan = Plan("gamma") with { Rooms = invalidRotationRooms };
        var invalidRotation = await Assert.ThrowsAsync<AbyssalRuleException>(() =>
            rig.Commit((connection, transaction) => abyssal.StartAsync(connection, transaction, StartRequest(8025, invalidRotationPlan))));
        Assert.Equal(AbyssalRuleCode.InvalidRequest, invalidRotation.Code);

        var negativeRadiusRooms = rooms.ToArray();
        negativeRadiusRooms[1] = negativeRadiusRooms[1] with
        {
            EnvironmentObjects = [environmentObject with { PresentationRadiusMeters = -0.01 }],
        };
        var negativeRadiusPlan = Plan("gamma") with { Rooms = negativeRadiusRooms };
        var negativeRadius = await Assert.ThrowsAsync<AbyssalRuleException>(() =>
            rig.Commit((connection, transaction) => abyssal.StartAsync(connection, transaction, StartRequest(8026, negativeRadiusPlan))));
        Assert.Equal(AbyssalRuleCode.InvalidRequest, negativeRadius.Code);

        var nonFiniteRadiusRooms = rooms.ToArray();
        nonFiniteRadiusRooms[1] = nonFiniteRadiusRooms[1] with
        {
            EnvironmentObjects = [environmentObject with { PresentationRadiusMeters = double.PositiveInfinity }],
        };
        var nonFiniteRadiusPlan = Plan("gamma") with { Rooms = nonFiniteRadiusRooms };
        var nonFiniteRadius = await Assert.ThrowsAsync<AbyssalRuleException>(() =>
            rig.Commit((connection, transaction) => abyssal.StartAsync(connection, transaction, StartRequest(8027, nonFiniteRadiusPlan))));
        Assert.Equal(AbyssalRuleCode.InvalidRequest, nonFiniteRadius.Code);
    }

    private static AbyssalRunPlan Plan(string weather) =>
        new(
            1,
            weather,
            6001,
            3,
            [
                RoomPlan(1),
                RoomPlan(2),
                RoomPlan(3),
            ])
        {
            SpaceLocationId = 7100001,
        };

    private static AbyssalRoomPlan RoomPlan(int locationId) =>
        new(
            locationId,
            [new AbyssalNpcSpawnPlan(50001 + locationId, 2)],
            [new AbyssalLootPlan(70001 + locationId, 1)],
            [new AbyssalObjectivePlan($"clear-{locationId}", [50001 + locationId])],
            $"environment-{locationId}")
        {
            NebulaGraphicId = 6000 + locationId,
        };

    private static AbyssalEnterRequest EnterRequest(long instanceId, long characterId, long shipId, long originSystemId) =>
        new(instanceId, new AbyssalParticipantAdmission(
            characterId,
            shipId,
            603,
            "frigate",
            FleetId,
            new AbyssalParticipantOrigin(
                originSystemId,
                60003760 + characterId,
                new AbyssalPosition(characterId, 0, 0),
                new AbyssalPosition(1, 0, 0),
                30)));

    private static IReadOnlyList<AbyssalSpaceEntity> Entities(int locationId, long firstId, long secondId) =>
    [
        new(firstId, 50001 + locationId, AbyssalEntityRole.Npc, new AbyssalPosition(1, 2, 3)),
        new(secondId, 50001 + locationId, AbyssalEntityRole.Npc, new AbyssalPosition(4, 5, 6)),
    ];
}

using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using Eve.Characters;
using Eve.Content;
using Eve.Data;
using Eve.Dogma;
using Eve.Events;
using Eve.Fitting;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Sde;
using Eve.Services;
using Eve.Services.Core;
using Eve.Services.Space;
using Eve.Ships;
using Eve.Skills;
using Eve.Space;
using Eve.Universe;
using Npgsql;
using Xunit;

namespace Eve.Host.Tests;

public sealed class AbyssalHostFlowTests
{
    [RealAbyssalSimPgFact]
    public async Task Real_sim_cleanup_confirms_live_npc_environment_and_reward_scene_removal_with_another_run_intact()
    {
        var binary = Environment.GetEnvironmentVariable("EVE_ABYSSAL_SIM_BINARY")!;
        var start = new System.Diagnostics.ProcessStartInfo(binary)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true,
        };
        start.Environment["EVE_SIM_LISTEN"] = "127.0.0.1:0";
        start.Environment["EVE_SDE_DIR"] = Environment.GetEnvironmentVariable("EVE_ABYSSAL_SIM_SDE")
            ?? throw new InvalidOperationException("Set EVE_ABYSSAL_SIM_SDE to the pinned test SDE.");
        using var process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("The test simulation did not start.");
        try
        {
            string? endpoint = null;
            using var ready = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            while (await process.StandardError.ReadLineAsync(ready.Token) is { } line)
                if (line.StartsWith("eve-sim: listening on ", StringComparison.Ordinal))
                { endpoint = line["eve-sim: listening on ".Length..]; break; }
            Assert.NotNull(endpoint);
            var stderr = process.StandardError.ReadToEndAsync();
            var stdout = process.StandardOutput.ReadToEndAsync();
            await using var simulation = SimClient.ToTcpSocket(endpoint);
            var messages = new ConcurrentQueue<SimMessage>();
            simulation.Received += messages.Enqueue;
            simulation.Start();
            using var connected = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (!simulation.IsConnected) await Task.Delay(20, connected.Token);
            await using var rig = await Rig.Create(simulation);
            rig.Now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var run = await rig.CreateRun();
            var other = await rig.CreateRun();
            await rig.Materialize(run, 1);
            await rig.Materialize(other, 1);
            // A genuine nonempty Sim scene cannot pass the strict legacy empty-scene remover.
            Assert.NotNull(await simulation.RemoveAbyssalSceneAsync(new SimRemoveAbyssalScene(900_001,
                new SimSceneKey(run.Data.SpaceLocationId, run.InstanceId, 1))));
            var npcId = run.Data.Rooms[0].Entities.First(entity => entity.Role == AbyssalEntityRole.Npc).EntityId;
            var before = await simulation.GetAbyssalParticipantPoseAsync(new SimGetAbyssalParticipantPose(900_003,
                new SimSceneKey(run.Data.SpaceLocationId, run.InstanceId, 1), npcId));
            Assert.Equal("ABYSSAL_RESIDENT_NOT_A_PARTICIPANT", before.Error);
            await rig.Recover(run.InstanceId);
            await rig.Drain();
            Assert.Empty(rig.Problems);
            var after = await simulation.GetAbyssalParticipantPoseAsync(new SimGetAbyssalParticipantPose(900_004,
                new SimSceneKey(run.Data.SpaceLocationId, run.InstanceId, 1), npcId));
            Assert.Equal("ABYSSAL_RESIDENT_SHIP_NOT_FOUND", after.Error);
            Assert.DoesNotContain(messages, message => message is SimDestroyed);
            foreach (var entity in run.Data.Rooms[0].Entities) Assert.Null(await rig.Items.GetAsync(entity.EntityId));
            Assert.Null(await rig.Items.GetAsync(run.Data.TraceItemId));
            Assert.NotNull(await simulation.RemoveAbyssalSceneAsync(new SimRemoveAbyssalScene(900_002,
                new SimSceneKey(other.Data.SpaceLocationId, other.InstanceId, 1))));
            foreach (var entity in other.Data.Rooms[0].Entities) Assert.NotNull(await rig.Items.GetAsync(entity.EntityId));
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
    }

    [PgFact]
    public async Task Terminal_cleanup_removes_only_its_scenes_reservations_and_unclaimed_rewards_and_replays_safely()
    {
        await using var rig = await Rig.Create();
        var run = await rig.CreateRun();
        var other = await rig.CreateRun();
        await rig.Materialize(run, 1);
        await rig.Materialize(other, 1);
        var ownScope = new RunSpaceItemScope(Guid.Parse(run.Data.ContentId), run.InstanceId, 1,
            run.Data.SpaceLocationId, run.Data.CreatorCharacterId);
        var scopedCan = await rig.Ships.CreateCanAsync(run.Data.CreatorCharacterId, run.Data.SpaceLocationId,
            runSpaceScope: ownScope);
        var otherCan = await rig.Ships.CreateCanAsync(other.Data.CreatorCharacterId, other.Data.SpaceLocationId,
            runSpaceScope: new RunSpaceItemScope(Guid.Parse(other.Data.ContentId), other.InstanceId, 1,
                other.Data.SpaceLocationId, other.Data.CreatorCharacterId));
        long canCargo = 0;
        long unknownItem = 0;
        long nestedCan = 0;
        long nestedCargo = 0;
        long claimedCanCargo = 0;
        var corporationId = (await rig.Characters.GetAsync(run.Data.CreatorCharacterId))!.CorporationId;
        await rig.Transaction(async (c, tx) =>
        {
            canCargo = await rig.Items.CreateAsync(c, tx, new NewItem(run.Data.FilamentTypeId, run.Data.CreatorCharacterId, scopedCan.Id, 0, 2));
            nestedCan = await rig.Items.CreateAsync(c, tx, new NewItem(CharacterShips.CargoContainerTypeId, corporationId,
                scopedCan.Id, 0, 1, Singleton: true));
            nestedCargo = await rig.Items.CreateAsync(c, tx, new NewItem(run.Data.FilamentTypeId, other.Data.CreatorCharacterId, nestedCan, 0, 2));
            claimedCanCargo = await rig.Items.CreateAsync(c, tx, new NewItem(run.Data.FilamentTypeId, other.Data.CreatorCharacterId, nestedCan, 0, 2));
            await rig.Items.MoveAsync(c, tx, claimedCanCargo, other.Data.CreatorShipId, ItemFlags.Cargo);
            unknownItem = await rig.Items.CreateAsync(c, tx, new NewItem(run.Data.FilamentTypeId, run.Data.CreatorCharacterId,
                run.Data.SpaceLocationId, CharacterShips.InSpaceFlag, 1));
        });
        var cache = run.Data.Rooms[0].Entities.First(value => value.Role == AbyssalEntityRole.Container);
        var destroyed = new AbyssalCacheDestroyedEvent(run.InstanceId, run.Data.ContentId, 1, cache.EntityId, cache.TypeId,
            [cache.Position.X, cache.Position.Y, cache.Position.Z], [0, 1, 0]);
        await rig.Publish(AbyssalCacheSettlementProcessor.DestroyedTopic, destroyed, "cache-before-cleanup");
        await rig.Drain();
        var receipt = System.Text.Json.JsonSerializer.Deserialize<AbyssalCacheReceipt>((await rig.Items.GetAsync(cache.EntityId))!.CustomInfo!,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        var claimed = receipt.LootItemIds.First();
        await rig.Transaction((c, tx) => rig.Items.MoveAsync(c, tx, claimed, run.Data.CreatorShipId, ItemFlags.Cargo));
        var owned = run.Data.Rooms.SelectMany(room => room.Entities).Select(value => value.EntityId)
            .Append(run.Data.TraceItemId).Concat(receipt.LootItemIds.Where(id => id != claimed)).Append(receipt.WreckItemId!.Value)
            .Append(scopedCan.Id).Append(canCargo).Append(nestedCan).Append(nestedCargo).ToArray();
        rig.Simulator.BlockedCleanupScene = new SimSceneKey(run.Data.SpaceLocationId, run.InstanceId, 2);
        await rig.Recover(run.InstanceId);
        await rig.Drain();
        Assert.NotNull(await rig.Items.GetAsync(cache.EntityId)); // A later scene refusal rolls back all inventory cleanup.
        Assert.NotNull(await rig.Items.GetAsync(run.Data.TraceItemId));
        rig.Simulator.BlockedCleanupScene = null;
        rig.Now += 10_000;
        await rig.Drain();
        Assert.Contains(rig.Simulator.LiveScenes, scene => scene.InstanceId == other.InstanceId);
        foreach (var entity in other.Data.Rooms[0].Entities) Assert.NotNull(await rig.Items.GetAsync(entity.EntityId));
        Assert.NotNull(await rig.Items.GetAsync(otherCan.Id));
        Assert.NotNull(await rig.Items.GetAsync(unknownItem)); // Same hidden system and owner is not run ownership proof.
        Assert.Equal(other.Data.CreatorShipId, (await rig.Items.GetAsync(claimedCanCargo))!.LocationId);
        foreach (var id in owned) Assert.Null(await rig.Items.GetAsync(id));
        Assert.NotNull(await rig.Items.GetAsync(claimed));
        Assert.DoesNotContain(rig.Simulator.LiveScenes, scene => scene.InstanceId == run.InstanceId);
        await rig.Invoke("ReconcileAbyssalStartupAsync", CancellationToken.None);
        await rig.Drain();
        // Startup recovery returns the second run, so use a new live run to check late replay isolation.
        var surviving = await rig.CreateRun();
        await rig.Materialize(surviving, 1);
        await rig.Publish(CharacterServicesComposition.AbyssalCleanupTopic, new AbyssalCleanupRequestedEvent(run.InstanceId, run.Data.ContentId), "cleanup-replay");
        await rig.Publish(AbyssalCacheSettlementProcessor.DestroyedTopic, destroyed, "late-cache");
        await rig.Drain();
        Assert.Contains(rig.Simulator.LiveScenes, scene => scene.InstanceId == surviving.InstanceId);
        foreach (var entity in surviving.Data.Rooms[0].Entities) Assert.NotNull(await rig.Items.GetAsync(entity.EntityId));
        Assert.Equal(run.Data.CreatorShipId, (await rig.Items.GetAsync(claimed))!.LocationId);
    }

    [PgFact]
    public async Task Expiry_cleanup_waits_for_real_death_receipt_then_removes_all_three_scenes()
    {
        await using var rig = await Rig.Create();
        var run = await rig.CreateRun();
        await rig.Materialize(run, 1);
        rig.Simulator.RemovalError = "TEST_DEATH_CLEANUP_PENDING";
        rig.Now = run.Data.RunExpiresAtMs!.Value + 1;
        await rig.Transaction(async (c, tx) => await rig.Runtime.ExpireIfDueAsync(c, tx, run.InstanceId));
        await rig.Drain();
        Assert.Null(Assert.Single((await rig.State(run.InstanceId)).Participants).DeathSettlementReceiptAtMs);
        Assert.Empty(rig.Simulator.RemovedScenes);
        foreach (var entity in run.Data.Rooms[0].Entities) Assert.NotNull(await rig.Items.GetAsync(entity.EntityId));
        rig.Simulator.RemovalError = null;
        rig.Now += 10_000;
        await rig.Recover(run.InstanceId);
        await rig.Drain();
        Assert.NotNull(Assert.Single((await rig.State(run.InstanceId)).Participants).DeathSettlementReceiptAtMs);
        Assert.Equal(3, rig.Simulator.RemovedScenes.Where(scene => scene.InstanceId == run.InstanceId).Distinct().Count());
        foreach (var entity in run.Data.Rooms[0].Entities) Assert.Null(await rig.Items.GetAsync(entity.EntityId));
        Assert.Null(await rig.Items.GetAsync(run.Data.TraceItemId));
        Assert.DoesNotContain(rig.Simulator.LiveScenes, scene => scene.InstanceId == run.InstanceId);
        Assert.DoesNotContain(await rig.Items.OwnedByAsync(run.Data.CreatorCharacterId), item => item.LocationId == run.Data.SpaceLocationId);
    }

    [PgFact]
    public async Task Late_room_spawn_entry_and_cache_intents_are_acknowledged_only_for_the_matching_settled_run()
    {
        await using var rig = await Rig.Create();
        var run = await rig.CreateRun();
        AbyssalRoomSpawnRequestedEvent? spawn = null;
        await rig.Transaction(async (c, tx) => spawn = await rig.Reservation.PrepareAsync(c, tx, run, 1));
        var entered = new AbyssalLocationEnteredEvent(run.InstanceId, run.Data.ContentId, run.Data.Tier,
            run.Data.SpaceLocationId, run.Data.CreatorCharacterId, 1, run.Data.Rooms[0].NebulaGraphicId, run.Data.Plan.Rooms[0].EnvironmentProfileId);
        await rig.Recover(run.InstanceId);
        await rig.Drain();
        var session = new ClientSession(new RecordingSink(rig.Order));
        session.Set("charid", new PyLong(run.Data.CreatorCharacterId));
        rig.Sessions.Enter(run.Data.CreatorCharacterId, session); // No Pilot remains after full restart.
        rig.Problems.Clear();
        await rig.Publish(AbyssalContentRuntime.RoomSpawnRequestedTopic, spawn!, "old-spawn-replay");
        await rig.Publish(AbyssalContentRuntime.LocationEnteredTopic, entered, "old-entry-replay");
        await rig.Drain();
        Assert.Empty(rig.Problems);
        Assert.DoesNotContain(rig.Simulator.LiveScenes, scene => scene.InstanceId == run.InstanceId);
        foreach (var entity in run.Data.Rooms[0].Entities) Assert.Null(await rig.Items.GetAsync(entity.EntityId));
        var removalsBefore = rig.Simulator.RemovedScenes.Count;
        await rig.Publish(CharacterServicesComposition.AbyssalCleanupTopic,
            new AbyssalCleanupRequestedEvent(run.InstanceId, run.Data.ContentId, 1), "late-partial-cleanup");
        await rig.Drain();
        Assert.Equal(new[] { 1, 2, 3 }, rig.Simulator.RemovedScenes.Skip(removalsBefore).Select(scene => scene.RoomIndex));
        Assert.Empty(rig.Problems);
        // A changed UUID is an invalid intent, even though the referenced run is terminal.
        await rig.Publish(AbyssalContentRuntime.RoomSpawnRequestedTopic, spawn! with { ContentId = Guid.NewGuid().ToString("D") }, "bad-spawn");
        await rig.Drain();
        Assert.Contains(rig.Problems, problem => problem.Contains("does not match its persisted snapshot"));
    }

    [PgFact]
    public async Task Departed_room_cleanup_preserves_the_current_room_and_its_scoped_container()
    {
        await using var rig = await Rig.Create();
        var run = await rig.CreateRun();
        await rig.Materialize(run, 1);
        run = await rig.AdvanceToFinalRoom(run);
        await rig.Materialize(run, 3);
        var currentCan = await rig.Ships.CreateCanAsync(run.Data.CreatorCharacterId, run.Data.SpaceLocationId,
            runSpaceScope: new RunSpaceItemScope(Guid.Parse(run.Data.ContentId), run.InstanceId, 3,
                run.Data.SpaceLocationId, run.Data.CreatorCharacterId));
        await rig.Publish(CharacterServicesComposition.AbyssalCleanupTopic,
            new AbyssalCleanupRequestedEvent(run.InstanceId, run.Data.ContentId), "departed-rooms");
        await rig.Drain();
        foreach (var entity in run.Data.Rooms.Take(2).SelectMany(room => room.Entities))
            Assert.Null(await rig.Items.GetAsync(entity.EntityId));
        foreach (var entity in run.Data.Rooms[2].Entities)
            Assert.NotNull(await rig.Items.GetAsync(entity.EntityId));
        Assert.NotNull(await rig.Items.GetAsync(currentCan.Id));
        Assert.Contains(new SimSceneKey(run.Data.SpaceLocationId, run.InstanceId, 3), rig.Simulator.LiveScenes);
        Assert.DoesNotContain(rig.Simulator.RemovedScenes, scene => scene.RoomIndex == 3);
        Assert.Empty(rig.Problems);
    }

    [PgFact]
    public async Task Inflight_materialization_serializes_with_terminal_recovery_and_cannot_resurrect_the_scene()
    {
        await using var rig = await Rig.Create();
        var run = await rig.CreateRun();
        rig.Simulator.SceneEnsureStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Simulator.AllowSceneEnsure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var materializing = rig.Materialize(run, 1);
        await rig.Simulator.SceneEnsureStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var recoveryStarted = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var recovery = rig.Transaction(async (c, tx) =>
        {
            await using var pid = new NpgsqlCommand("SELECT pg_backend_pid()", c, tx);
            recoveryStarted.SetResult((int)(await pid.ExecuteScalarAsync())!);
            await rig.Runtime.RecoverAfterServerRestartAsync(c, tx, run.InstanceId);
        });
        try
        {
            var backend = await recoveryStarted.Task;
            var blocked = false;
            for (var attempt = 0; attempt < 250 && !recovery.IsCompleted; attempt++)
            {
                await using var command = rig.Source.CreateCommand("SELECT cardinality(pg_blocking_pids($1)) > 0");
                command.Parameters.AddWithValue(backend);
                if ((bool)(await command.ExecuteScalarAsync())!) { blocked = true; break; }
                await Task.Delay(20);
            }
            Assert.True(blocked, "Terminal recovery must wait for the in-flight content transaction.");
        }
        finally { rig.Simulator.AllowSceneEnsure.TrySetResult(); }
        await Task.WhenAll(materializing, recovery);
        await rig.Drain();
        Assert.DoesNotContain(rig.Simulator.LiveScenes, scene => scene.InstanceId == run.InstanceId);
        foreach (var entity in run.Data.Rooms[0].Entities) Assert.Null(await rig.Items.GetAsync(entity.EntityId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Materialize(run, 1));
        Assert.DoesNotContain(rig.Simulator.LiveScenes, scene => scene.InstanceId == run.InstanceId);
    }

    [PgFact]
    public async Task Active_invalid_spawn_intent_is_refused_without_allocating_a_future_room()
    {
        await using var rig = await Rig.Create();
        var run = await rig.CreateRun();
        await rig.Drain();
        var plan = run.Data.Plan.Rooms[1];
        var invalid = new AbyssalRoomSpawnRequestedEvent(run.InstanceId, run.Data.ContentId, run.Data.SpaceLocationId,
            2, run.Data.Tier, run.Data.WeatherType, plan.EnvironmentProfileId, [], plan.Loot)
        { EnvironmentObjects = plan.EnvironmentObjects, Artifacts = plan.Artifacts };
        await rig.Publish(AbyssalContentRuntime.RoomSpawnRequestedTopic, invalid, "invalid-future-room");
        await rig.Drain();
        Assert.Contains(rig.Problems, problem => problem.Contains("does not match its persisted snapshot"));
        Assert.DoesNotContain(rig.Simulator.LiveScenes, scene => scene.InstanceId == run.InstanceId && scene.RoomIndex == 2);
        Assert.False((await rig.State(run.InstanceId)).Rooms[1].SpawnPrepared);
    }

    [PgFact]
    public async Task Unentered_trace_expiry_cleans_already_absent_scenes_without_touching_its_source_ship()
    {
        await using var rig = await Rig.Create();
        var run = await rig.CreateRun(enter: false);
        rig.Now = run.Data.TraceExpiresAtMs + 1;
        await rig.Transaction(async (c, tx) => await rig.Runtime.ExpireIfDueAsync(c, tx, run.InstanceId));
        await rig.Drain();
        Assert.Null(await rig.Items.GetAsync(run.Data.TraceItemId));
        Assert.NotNull(await rig.Items.GetAsync(run.Data.CreatorShipId));
        Assert.Equal(3, rig.Simulator.RemovedScenes.Distinct().Count());
        Assert.Empty(rig.Problems);
    }

    [PgFact]
    public async Task Deployment_and_entrance_retries_after_private_handoff_and_reconnect_use_the_existing_receipt()
    {
        await using var rig = await Rig.Create();
        var setup = await rig.CreateRun(enter: false);
        rig.Now = setup.Data.TraceExpiresAtMs + 1;
        await rig.Transaction(async (c, tx) => await rig.Runtime.ExpireIfDueAsync(c, tx, setup.InstanceId));
        await rig.Drain();
        var session = await rig.ConnectPublic(setup);
        rig.Simulator.Pose = [10, 20, 30];
        long filament = 0;
        await rig.Transaction(async (c, tx) => filament = await rig.Items.CreateAsync(c, tx,
            new NewItem(setup.Data.FilamentTypeId, setup.Data.CreatorCharacterId, setup.Data.CreatorShipId, ItemFlags.Cargo, 2)));
        var deploy = new ServiceCall { Session = session, Method = "AbyssalEntranceDeployment", Args = [new PyLong(filament), new PyInt(1)] };
        await rig.Service.CallAsync(deploy);
        await rig.Drain();
        AbyssalParticipantRunBinding? binding = null;
        await rig.Transaction(async (c, tx) => binding = await rig.Runtime.FindActiveForParticipantAsync(c, tx, setup.Data.CreatorCharacterId, setup.Data.CreatorShipId));
        Assert.NotNull(binding);
        var run = binding.Run;
        Assert.Equal(run.Data.SpaceLocationId, session.GetInt("solarsystemid"));
        Assert.Equal(1, (await rig.Items.GetAsync(filament))!.Quantity);
        var inspections = rig.Simulator.PublicPoseQueries;
        var moves = rig.Simulator.Moves;
        await rig.Service.CallAsync(deploy);
        await rig.Service.CallAsync(new ServiceCall { Session = session, Method = "AbyssalEntranceGateActivation", Args = [new PyLong(run.Data.TraceItemId)] });
        await rig.Drain();
        Assert.Equal(inspections, rig.Simulator.PublicPoseQueries);
        Assert.Equal(moves, rig.Simulator.Moves);
        Assert.Equal(1, (await rig.Items.GetAsync(filament))!.Quantity);
        await rig.Disconnect(session);
        var reconnected = await rig.Connect(run, sessionOffset: 1000);
        await rig.Service.CallAsync(new ServiceCall { Session = reconnected, Method = deploy.Method, Args = deploy.Args });
        await rig.Service.CallAsync(new ServiceCall { Session = reconnected, Method = "AbyssalEntranceGateActivation", Args = [new PyLong(run.Data.TraceItemId)] });
        await rig.Drain();
        Assert.Equal(inspections, rig.Simulator.PublicPoseQueries);
        Assert.Equal(moves, rig.Simulator.Moves);
        Assert.Equal(1, (await rig.Items.GetAsync(filament))!.Quantity);
    }

    [PgFact]
    public async Task Room_gate_retry_after_actual_transfer_and_reconnect_keeps_one_transition_and_rejects_foreign_markers()
    {
        await using var rig = await Rig.Create();
        var run = await rig.CreateRun();
        await rig.Drain();
        var session = await rig.Connect(run);
        await rig.Transaction(async (c, tx) =>
        {
            foreach (var npc in run.Data.Rooms[0].Entities.Where(entity => entity.Role == AbyssalEntityRole.Npc))
                await rig.Runtime.NpcDestroyedAsync(c, tx, new AbyssalNpcDestroyedRequest(run.InstanceId, 1, npc.EntityId));
        });
        var gate = run.Data.Rooms[0].Entities.Single(entity => entity.Role == AbyssalEntityRole.Gate);
        rig.Simulator.Pose = [gate.Position.X, gate.Position.Y, gate.Position.Z];
        var request = new ServiceCall { Session = session, Method = "AbyssalGateActivation", Args = [new PyLong(gate.EntityId)] };
        await rig.Service.CallAsync(request);
        await rig.Drain();
        Assert.Equal(2, (await rig.State(run.InstanceId)).CurrentRoomIndex);
        Assert.Equal(1, rig.Simulator.Moves);
        await rig.Service.CallAsync(request);
        await rig.Drain();
        Assert.Equal(1, rig.Simulator.Moves);
        Assert.Single((await rig.State(run.InstanceId)).GateTransitions);
        await rig.Disconnect(session);
        var reconnected = await rig.Connect(run, sessionOffset: 1000);
        await rig.Service.CallAsync(new ServiceCall { Session = reconnected, Method = request.Method, Args = request.Args });
        await rig.Drain();
        Assert.Equal(1, rig.Simulator.Moves);
        await Assert.ThrowsAsync<MachoWrappedException>(() => rig.Service.CallAsync(new ServiceCall
        { Session = reconnected, Method = request.Method, Args = [new PyLong(gate.EntityId + 10_000_000)] }).AsTask());
        Assert.Single((await rig.State(run.InstanceId)).GateTransitions);
    }

    [PgFact]
    public async Task Committed_consumption_precedes_trace_publication_when_rpc_and_outbox_race()
    {
        await using var rig = await Rig.Create();
        var run = await rig.CreateRun();
        var participant = Assert.Single(run.Data.Participants);
        await rig.Drain();
        var session = new ClientSession(new RecordingSink(rig.Order)) { SessionId = 44, ClientId = 45 };
        session.Set("charid", new PyLong(participant.CharacterId));
        rig.Sessions.Enter(participant.CharacterId, session);
        var before = new Item(123, run.Data.FilamentTypeId, participant.CharacterId, participant.ShipId, ItemFlags.Cargo, 1, false, null);
        var traceRow = (await rig.Sde.ReadAsync("types", [AbyssalTraceFactory.TraceTypeId])).Single().Doc;
        var trace = AbyssalTraceFactory.Create(checked((int)participant.Origin.SolarSystemId), run.Data.TraceItemId,
            (int)run.Data.GameModeId, run.Data.FilamentTypeId, run.Data.Tier, run.Data.TraceExpiresAtMs,
            traceRow.GetProperty("radius").GetDouble(), [10, 20, 30], [0, 0, 1]);
        var request = new AbyssalTracePublishRequestedEvent(trace,
            new AbyssalFilamentConsumedEvent(run.Data.ContentId, participant.CharacterId, before, before with { LocationId = 6 }, 1979, 22));
        await rig.Publish(AbyssalRunAdmission.TracePublishRequestedTopic, request, "test-committed-deployment");
        await Task.WhenAll(rig.Drain(), rig.Invoke("PublishAbyssalDeploymentAsync", request, (ulong?)44, CancellationToken.None));
        Assert.Equal("OnItemChange", rig.Order[0]);
        Assert.Equal(1, rig.Order.Count(value => value == "OnItemChange"));
        Assert.Equal(2, rig.Order.Count(value => value == "TracePublish"));
        Assert.Empty(rig.Problems);
    }

    [PgFact]
    public async Task Offline_restart_return_confirms_absence_and_commits_ship_character_pose_and_receipt()
    {
        await using var rig = await Rig.Create();
        var run = await rig.CreateRun();
        await rig.Recover(run.InstanceId);
        await rig.Drain();

        var state = await rig.State(run.InstanceId);
        var participant = Assert.Single(state.Participants);
        Assert.True(participant.Status == AbyssalParticipantStatus.Returned, string.Join("\n", rig.Problems));
        Assert.NotNull(participant.ReturnedAtMs);
        Assert.Equal(participant.Origin.SolarSystemId, (await rig.Characters.GetAsync(participant.CharacterId))!.SolarSystemId);
        var ship = (await rig.Items.GetAsync(participant.ShipId))!;
        Assert.Equal((participant.CharacterId, participant.Origin.LocationId, CharacterShips.InSpaceFlag),
            (ship.OwnerId, ship.LocationId, ship.Flag));
        var restore = (await rig.SpaceItems.GetAsync(ship.Id))!;
        Assert.Equal(participant.Origin.SolarSystemId, restore.SolarSystemId);
        Assert.Equal(new[] { participant.Origin.ShipPosition.X, participant.Origin.ShipPosition.Y, participant.Origin.ShipPosition.Z }, restore.Position);
        Assert.Single(rig.Simulator.Removals);
        Assert.Equal(0, rig.Simulator.Moves);
        Assert.Empty(rig.Problems);
    }

    [PgFact]
    public async Task Failed_space_detach_keeps_origin_receipt_and_authority_uncommitted_until_retry()
    {
        await using var rig = await Rig.Create();
        var run = await rig.CreateRun();
        rig.Simulator.RemovalError = "TEST_DETACH_FAILURE";
        await rig.Recover(run.InstanceId);
        await rig.Drain();
        var pending = Assert.Single((await rig.State(run.InstanceId)).Participants);
        Assert.Equal(AbyssalParticipantStatus.Returning, pending.Status);
        Assert.Null(pending.ReturnedAtMs);
        Assert.Equal(run.Data.SpaceLocationId, (await rig.Items.GetAsync(pending.ShipId))!.LocationId);
        rig.Simulator.RemovalError = null;
        rig.Now += 10_000;
        await rig.Drain();
        Assert.Equal(AbyssalParticipantStatus.Returned, Assert.Single((await rig.State(run.InstanceId)).Participants).Status);
    }

    [PgFact]
    public async Task Startup_selects_expired_and_failed_unsettled_runs_and_preserves_lethal_outcomes()
    {
        await using var rig = await Rig.Create();
        var failed = await rig.CreateRun();
        var expired = await rig.CreateRun();
        var p = Assert.Single(failed.Data.Participants);
        await rig.Transaction(async (c, tx) =>
            await rig.Runtime.ParticipantDestroyedAsync(c, tx,
                new AbyssalParticipantDestroyedRequest(failed.InstanceId, p.CharacterId, p.ShipId, "test-destruction")));
        rig.Now = expired.Data.RunExpiresAtMs!.Value + 1;
        await rig.Transaction(async (c, tx) => await rig.Runtime.ExpireIfDueAsync(c, tx, expired.InstanceId));
        await rig.Transaction(async (c, tx) =>
        {
            var rows = await new AbyssalStartupReadModel(rig.Foundations.Content, rig.Foundations.ContentSchema).ReadAsync(c, tx);
            Assert.Contains(rows, row => row.Id == failed.InstanceId && row.State == "failed");
            Assert.Contains(rows, row => row.Id == expired.InstanceId && row.State == "expired");
        });
        await rig.Invoke("ReconcileAbyssalStartupAsync", CancellationToken.None);
        foreach (var id in new[] { failed.InstanceId, expired.InstanceId })
        {
            var state = await rig.State(id);
            Assert.DoesNotContain(state.Participants, value => value.ReturnReason == "server-restart");
            Assert.All(state.Participants, value => Assert.NotNull(value.DeathSettlementKey));
        }
    }

    [PgFact]
    public async Task Timeout_death_uses_real_ship_capsule_authority_and_restart_retry_keeps_one_rookie_ship()
    {
        foreach (var failCleanup in new[] { false, true })
        {
            await using var rig = await Rig.Create();
            var run = await rig.CreateRun();
            var original = Assert.Single(run.Data.Participants);
            rig.Now = run.Data.RunExpiresAtMs!.Value + 1;
            rig.Simulator.RemovalError = failCleanup ? "TEST_CLEANUP_FAILURE" : null;
            await rig.Transaction(async (c, tx) => await rig.Runtime.ExpireIfDueAsync(c, tx, run.InstanceId));
            await rig.Drain();
            var first = Assert.Single((await rig.State(run.InstanceId)).Participants);
            var character = (await rig.Characters.GetAsync(original.CharacterId))!;
            var rookieId = character.ShipId!.Value;
            Assert.NotEqual(original.ShipId, rookieId);
            Assert.Null(await rig.Items.GetAsync(original.ShipId));
            if (!failCleanup)
                Assert.True(first.Status == AbyssalParticipantStatus.Destroyed, string.Join(Environment.NewLine, rig.Problems));
            Assert.Equal(AbyssalDeathSettlementCause.RunExpired, first.DeathSettlementCause);
            Assert.Null(first.ReturnedAtMs);
            if (failCleanup)
            {
                Assert.Null(first.DeathSettlementReceiptAtMs);
                rig.Simulator.RemovalError = null;
                rig.Now += 10_000;
                await rig.Recover(run.InstanceId);
                await rig.Drain();
            }
            var final = Assert.Single((await rig.State(run.InstanceId)).Participants);
            Assert.True(final.DeathSettlementReceiptAtMs is not null, string.Join(Environment.NewLine, rig.Problems));
            Assert.Equal(rookieId, (await rig.Characters.GetAsync(original.CharacterId))!.ShipId);
            Assert.Equal(0, rig.Simulator.Moves);
            Assert.Equal(failCleanup ? 2 : 1, rig.Simulator.Removals.Count);
        }
    }

    [PgFact]
    public async Task Far_encounter_and_origin_conduits_are_rejected_through_host_rpc_without_mutation()
    {
        await using var rig = await Rig.Create();
        foreach (var final in new[] { false, true })
        {
            var run = await rig.CreateRun();
            if (final) run = await rig.AdvanceToFinalRoom(run);
            var session = await rig.Connect(run);
            rig.Simulator.Pose = [1e30, 0, 0];
            var marker = run.Data.Rooms.Single(room => room.LocationId == run.Data.CurrentRoomIndex).Entities
                .Single(entity => entity.Role == AbyssalEntityRole.Gate).EntityId;
            var before = (await rig.Foundations.Content.GetAsync(run.InstanceId))!.DataJson;
            await Assert.ThrowsAsync<MachoWrappedException>(async () => await rig.Service.CallAsync(new ServiceCall
            {
                Session = session,
                Method = final ? "AbyssalEndGateActivation" : "AbyssalGateActivation",
                Args = [new PyLong(marker)],
            }));
            Assert.Equal(before, (await rig.Foundations.Content.GetAsync(run.InstanceId))!.DataJson);
        }
        Assert.Equal(0, rig.Simulator.Moves);
    }

    [PgFact]
    public async Task Final_exit_serializes_notices_session_handoff_and_finished_and_retry_does_not_repeat_exit()
    {
        await using var rig = await Rig.Create();
        var run = await rig.AdvanceToFinalRoom(await rig.CreateRun());
        await rig.Drain();
        var session = await rig.Connect(run);
        var gate = run.Data.Rooms.Single(room => room.LocationId == 3).Entities.Single(entity => entity.Role == AbyssalEntityRole.Gate);
        rig.Simulator.Pose = [gate.Position.X, gate.Position.Y, gate.Position.Z];
        rig.Order.Clear();
        await rig.Service.CallAsync(new ServiceCall { Session = session, Method = "AbyssalEndGateActivation", Args = [new PyLong(gate.EntityId)] });
        await rig.Service.CallAsync(new ServiceCall { Session = session, Method = "AbyssalEndGateActivation", Args = [new PyLong(gate.EntityId)] });
        Assert.Empty(rig.Order); // Both commits leave notification ordering to the same durable subscriber.
        await rig.Drain();
        Assert.Equal(new[] { "OnExitingDungeon", "OnAbyssalLocationExited", "SessionChange", "SimArrival", "OnAbyssalContentFinished" }, rig.Order);
        Assert.Equal(AbyssalParticipantStatus.Returned, Assert.Single((await rig.State(run.InstanceId)).Participants).Status);
        Assert.Empty(rig.Problems);
    }

    [PgFact]
    public async Task Cache_rewards_require_durable_destruction_and_duplicate_events_do_not_duplicate_loot()
    {
        await using var rig = await Rig.Create();
        var run = await rig.CreateRun();
        var cache = run.Data.Rooms[0].Entities.Single(entity => entity.Role == AbyssalEntityRole.Container &&
            run.Data.Plan.Rooms[0].Artifacts.Any(artifact => artifact.Kind == AbyssalArtifactKind.MainCache && artifact.Key == entity.PlanKey));
        Assert.Equal(0, await rig.Contents(cache.EntityId));
        await rig.EnsureScene(run, 1);
        var request = new AbyssalCacheDestroyedEvent(run.InstanceId, run.Data.ContentId, 1, cache.EntityId,
            cache.TypeId, [cache.Position.X, cache.Position.Y, cache.Position.Z], [0, 0, 1]);
        await rig.Publish(AbyssalCacheSettlementProcessor.DestroyedTopic, request, "first");
        await rig.Drain();
        var meta = System.Text.Json.JsonSerializer.Deserialize<AbyssalCacheReceipt>((await rig.Items.GetAsync(cache.EntityId))!.CustomInfo!,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        Assert.NotNull(meta.DestroyedAtMs);
        Assert.NotNull(meta.WreckItemId);
        Assert.Equal(cache.TypeId == 47951 ? 48120 : 49692, (await rig.Items.GetAsync(meta.WreckItemId.Value))!.TypeId);
        var count = await rig.Contents(meta.WreckItemId.Value);
        Assert.Equal(run.Data.Plan.Rooms[0].Artifacts.Single(value => value.Key == cache.PlanKey).Loot
            .Sum(value => value.IsBlueprintCopy ? value.Count : value.Count > 0 ? 1 : 0), count);
        await rig.Publish(AbyssalCacheSettlementProcessor.DestroyedTopic, request, "duplicate");
        await rig.Drain();
        Assert.Equal(count, await rig.Contents(meta.WreckItemId.Value));
        Assert.Single(rig.Simulator.Wrecks);
        Assert.Empty(rig.Problems);
    }

    [PgFact]
    public async Task Room_materialization_publishes_saved_artifacts_environment_and_npcs_with_stable_scene_keys()
    {
        await using var rig = await Rig.Create();
        var run = await rig.CreateRun();
        await rig.Materialize(run, 1);
        var room = run.Data.Rooms[0];
        var plan = run.Data.Plan.Rooms[0];
        Assert.Equal(plan.Artifacts.Count, rig.Simulator.Caches.Count);
        Assert.Equal(plan.EnvironmentObjects.Count, rig.Simulator.EnvironmentObjects.Count);
        Assert.Equal(plan.NpcSpawns.Sum(value => value.Count), rig.Simulator.Spawns.Count);
        Assert.Empty(rig.Simulator.Wrecks);
        foreach (var artifact in plan.Artifacts)
        {
            var reserved = room.Entities.Single(value => value.PlanKey == artifact.Key);
            var published = rig.Simulator.Caches.Single(value => value.Entity.ShipId == reserved.EntityId);
            Assert.Equal((run.InstanceId, 1, artifact.TypeId), (published.Scene.InstanceId, published.Scene.RoomIndex, published.Entity.TypeId));
            Assert.Equal(new[] { reserved.Position.X, reserved.Position.Y, reserved.Position.Z }, published.Position);
            Assert.Equal(0, await rig.Contents(reserved.EntityId));
        }
        foreach (var environment in plan.EnvironmentObjects)
        {
            var reserved = room.Entities.Single(value => value.PlanKey == environment.Key);
            var published = rig.Simulator.EnvironmentObjects.Single(value => value.Object.Body.ItemId == reserved.EntityId);
            Assert.Equal(environment.TypeId, published.Object.Body.TypeId);
            Assert.Equal(new[] { 0d, 1d, 0d }, published.Object.Body.Direction);
            if (environment.PresentationRadiusMeters is { } radius)
                Assert.Equal(radius, published.Object.Body.Radius);
            Assert.Equal(new[] { reserved.Position.X, reserved.Position.Y, reserved.Position.Z }, published.Object.Body.Position);
        }
        await rig.Materialize(run, 1);
        Assert.Equal(plan.Artifacts.Count, rig.Simulator.Caches.Select(value => value.Entity.ShipId).Distinct().Count());
        Assert.Equal(plan.EnvironmentObjects.Count, rig.Simulator.EnvironmentObjects.Select(value => value.Object.Body.ItemId).Distinct().Count());
    }

    [PgFact]
    public async Task Cache_destruction_after_deadline_suppresses_rewards()
    {
        await using var rig = await Rig.Create();
        var run = await rig.CreateRun();
        var cache = run.Data.Rooms[0].Entities.Single(entity => entity.Role == AbyssalEntityRole.Container &&
            run.Data.Plan.Rooms[0].Artifacts.Any(artifact => artifact.Kind == AbyssalArtifactKind.MainCache && artifact.Key == entity.PlanKey));
        await rig.Drain();
        rig.Now = run.Data.RunExpiresAtMs!.Value + 1;
        await rig.Publish(AbyssalCacheSettlementProcessor.DestroyedTopic,
            new AbyssalCacheDestroyedEvent(run.InstanceId, run.Data.ContentId, 1, cache.EntityId, cache.TypeId,
                [cache.Position.X, cache.Position.Y, cache.Position.Z], [0, 0, 1]), "expired");
        await rig.Drain();
        var meta = System.Text.Json.JsonSerializer.Deserialize<AbyssalCacheReceipt>((await rig.Items.GetAsync(cache.EntityId))!.CustomInfo!,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        Assert.True(meta.Suppressed);
        Assert.Null(meta.WreckItemId);
        Assert.Empty(rig.Simulator.Wrecks);
        Assert.Equal(0, await rig.Contents(cache.EntityId));
        Assert.Empty(rig.Problems);
    }

    private sealed class Rig : IAsyncDisposable
    {
        public required NpgsqlDataSource Source { get; init; }
        public required Foundations Foundations { get; init; }
        public required PgCharacterStore Characters { get; init; }
        public required PgItemStore Items { get; init; }
        public required PgSpaceItemStore SpaceItems { get; init; }
        public required AbyssalContentRuntime Runtime { get; init; }
        public required AbyssalStaticCatalog Catalog { get; init; }
        public required CreationCatalog Creation { get; init; }
        public required StationCatalog Stations { get; init; }
        public required SdeReader Sde { get; init; }
        public required AbyssalRoomSpawnReservation Reservation { get; init; }
        public required CharacterServicesComposition Host { get; init; }
        public required SpaceSessions Space { get; init; }
        public required FlowSim Simulator { get; init; }
        public required CharacterSessions Sessions { get; init; }
        public required CharacterShips Ships { get; init; }
        public required AbyssalMgrService Service { get; init; }
        public required string[] Schemas { get; init; }
        public List<string> Order { get; } = [];
        public List<string> Problems { get; } = [];
        public long Now { get; set; } = 1_790_000_000_000;

        public static async Task<Rig> Create(ISimulation? realSimulation = null)
        {
            var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
            var cs = new NpgsqlConnectionStringBuilder(PgConnection.ForTest(url));
            Assert.Equal("eve_rs_test", cs.Database);
            var source = NpgsqlDataSource.Create(cs.ConnectionString);
            var suffix = Guid.NewGuid().ToString("N");
            var schemas = Enumerable.Range(0, 11).Select(index => $"abyss_hf_{index}_{suffix}").ToArray();
            await PgCharacterStore.MigrateAsync(source, schemas[0]);
            await PgItemStore.MigrateAsync(source, schemas[1]);
            await PgSkillStore.MigrateAsync(source, schemas[2]);
            await PgModuleStateStore.MigrateAsync(source, schemas[3]);
            await PgSpaceItemStore.MigrateAsync(source, schemas[4]);
            await Eve.Crimewatch.PgCrimewatchStore.MigrateAsync(source, schemas[10]);
            await using (var fleet = source.CreateCommand($"""
                CREATE SCHEMA {schemas[9]};
                CREATE TABLE {schemas[9]}.fleet_members (
                    fleet_id bigint, character_id bigint UNIQUE, wing_id bigint, squad_id bigint,
                    role smallint, job smallint, accepts_fleet_warp boolean, accepts_conduit_jumps boolean,
                    accepts_fleet_regroups boolean, ship_type_id bigint);
                """)) await fleet.ExecuteNonQueryAsync();
            Rig? rig = null;
            var mapping = new Dictionary<string, string>
            {
                ["EVE_SCHEDULER_SCHEMA"] = schemas[5], ["EVE_EVENTS_SCHEMA"] = schemas[6],
                ["EVE_RELATIONS_SCHEMA"] = schemas[7], ["EVE_CONTENT_SCHEMA"] = schemas[8],
            };
            var foundations = await Foundations.CreateAsync(source, () => rig?.Now ?? 1_790_000_000_000,
                (key, fallback) => mapping.GetValueOrDefault(key, fallback), text => rig?.Problems.Add(text));
            var sde = new SdeReader(source);
            var statics = await DogmaStatic.LoadAsync(sde);
            var characters = new PgCharacterStore(source, schemas[0]);
            var items = new PgItemStore(source, schemas[1]);
            var spaces = new PgSpaceItemStore(source, schemas[4]);
            var itemTypes = new ItemTypeCatalog(sde);
            var creation = await CreationCatalog.LoadAsync(sde, (long?)null);
            var stations = await StationCatalog.LoadAsync(sde);
            var systems = await SolarSystemCatalog.LoadAsync(sde);
            var catalog = await AbyssalStaticCatalog.LoadAsync(sde);
            var ships = new CharacterShips(source, statics, characters, items,
                new PgSkillStore(source, schemas[2]), new PgModuleStateStore(source, schemas[3]));
            var simulation = DispatchProxy.Create<ISimulation, FlowSim>();
            var fake = (FlowSim)(object)simulation;
            fake.Inner = realSimulation;
            var sessions = new CharacterSessions();
            var space = new SpaceSessions(simulation, ships, statics, stations, systems,
                () => rig?.Now ?? 1_790_000_000_000, spaceItems: spaces);
            space.AbyssalEnvironmentEffects = (_, _) => [];
            space.RookieShipOfRace = raceId => creation.Race(raceId)?.ShipTypeId;
            var runtime = new AbyssalContentRuntime(foundations.Content, foundations.Events, foundations.Scheduler);
            var loot = new AbyssalLootMaterializer(items, itemTypes, await AbyssalLootMaterializer.LoadBlueprintCategoryIdAsync(sde));
            var reservation = new AbyssalRoomSpawnReservation(sde, items, itemTypes, catalog, runtime);
            var host = (CharacterServicesComposition)RuntimeHelpers.GetUninitializedObject(typeof(CharacterServicesComposition));
            void Field(string name, object value) => typeof(CharacterServicesComposition).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(host, value);
            Field("source", source); Field("foundations", foundations); Field("sde", sde);
            Field("characters", characters); Field("items", items); Field("itemTypes", itemTypes);
            Field("spaceItemStore", spaces); Field("spaceSessions", space); Field("ships", ships); Field("dogma", statics);
            Field("catalog", creation); Field("stations", stations); Field("characterSessions", sessions);
            Field("abyssalRuntime", runtime); Field("abyssalCatalog", catalog);
            Field("fleetStore", new FleetStore(source, schemas[9]));
            Field("crimewatchStore", new Eve.Crimewatch.PgCrimewatchStore(source, schemas[10]));
            Field("dungeonReadModel", new PersistenceDungeonReadModel(
                (table, ct) => sde.ReadAsync(table, cancellationToken: ct), foundations.Content.ActiveByKindAsync,
                (_, _) => Task.FromResult(new System.Text.Json.Nodes.JsonObject()), () => rig!.Now));
            Field("abyssalDeathSettlement", new AbyssalDeathSettlementProcessor(ships, characters, items, itemTypes, creation, stations));
            Field("abyssalPublicationLock", new SemaphoreSlim(1, 1));
            Field("abyssalRoomSpawnReservation", reservation);
            Field("abyssalNotifications", new AbyssalNotificationAdapter(sessions));
            Field("abyssalCacheSettlement", new AbyssalCacheSettlementProcessor(items, itemTypes, sde,
                foundations.Content, foundations.Events, loot, () => rig!.Now));
            Field("abyssalDogmaEffectsByContentId", new ConcurrentDictionary<string, IReadOnlyList<EffectDef>>(StringComparer.Ordinal));
            var deploymentRules = await AbyssalDeploymentEligibility.LoadAsync(sde, itemTypes);
            Field("abyssalDeploymentEligibility", deploymentRules);
            Field("abyssalTraceRadius", (await sde.ReadAsync("types", [AbyssalTraceFactory.TraceTypeId])).Single().Doc.GetProperty("radius").GetDouble());
            Field("abyssalRunAdmission", new AbyssalRunAdmission(source, characters, items, itemTypes, catalog, runtime,
                foundations.Events, new AbyssalFilamentConsumer(items, itemTypes), deploymentRules,
                typeof(CharacterServicesComposition).GetMethod("ReadAbyssalDeploymentEligibilityAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .CreateDelegate<Func<AbyssalRunAdmissionRequest, CancellationToken, Task<AbyssalParticipantPoseResult>>>(host),
                await AbyssalLootMaterializer.LoadBlueprintCategoryIdAsync(sde),
                (await sde.ReadAsync("types", [AbyssalTraceFactory.TraceTypeId])).Single().Doc.GetProperty("radius").GetDouble(), () => rig!.Now));
            host.RegisterAbyssalReturnAndCacheHandlers();
            var handler = typeof(CharacterServicesComposition).GetMethod("HandleAbyssalRpcAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .CreateDelegate<Func<ServiceCall, CancellationToken, ValueTask<PyValue>>>(host);
            rig = new Rig
            {
                Source = source, Foundations = foundations, Characters = characters, Items = items, SpaceItems = spaces,
                Runtime = runtime, Catalog = catalog, Creation = creation, Stations = stations, Sde = sde,
                Reservation = reservation, Host = host, Space = space, Simulator = fake, Sessions = sessions,
                Service = new AbyssalMgrService(handler), Ships = ships, Schemas = schemas,
            };
            fake.Order = rig.Order;
            space.FindAbyssalReconnectAsync = typeof(CharacterServicesComposition).GetMethod("FindAbyssalReconnectAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)!.CreateDelegate<Func<ClientSession, long, long, CancellationToken, Task<AbyssalReconnectBinding?>>>(host);
            space.AbyssalParticipantDisconnectedAsync = typeof(CharacterServicesComposition).GetMethod("PersistAbyssalDisconnectAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)!.CreateDelegate<Func<AbyssalParticipantDisconnectedNotice, CancellationToken, ValueTask>>(host);
            await foundations.Content.AttachAsync();
            await foundations.Events.PumpAsync();
            rig.Problems.Clear();
            return rig;
        }

        public async Task<AbyssalRunState> CreateRun(bool enter = true)
        {
            const int origin = 30000142;
            var system = (await Sde.ReadAsync("mapSolarSystems", [origin])).Single().Doc;
            var race = Creation.Races.First();
            var bloodline = Creation.Bloodlines.First(value => value.RaceId == race.Id);
            var school = CreationCatalog.Schools.First(value => value.RaceId == race.Id);
            var home = Stations.Get(Creation.StarterFor(school.CorporationId)!.StationId)!;
            var type = Catalog.AllowedShipTypeIdsByMode[AbyssalGameMode.SoloCruiser].Order().First();
            var filament = Catalog.FilamentsByTypeId.Values.First(value => value.Tier == 0);
            var shipRadius = (await Sde.ReadAsync("types", [type])).Single().Doc.GetProperty("radius").GetDouble();
            AbyssalRunState? run = null;
            await Transaction(async (c, tx) =>
            {
                var character = await Characters.InsertAsync(c, tx, new NewCharacter(1, "Host abyss " + Guid.NewGuid().ToString("N")[..12],
                    0, race.Id, bloodline.Id, 1, school.Id, CreationCatalog.CharacterTypeId(bloodline.Id), school.CorporationId,
                    null, home.Id, origin, system.GetProperty("constellationID").GetInt32(), system.GetProperty("regionID").GetInt32(), null, null));
                var ship = await Items.CreateAsync(c, tx, new NewItem(type, character, origin, CharacterShips.InSpaceFlag, 1, Singleton: true));
                await Characters.SetShipAsync(c, tx, character, ship);
                await Characters.SetStationAsync(c, tx, character, 0);
                var trace = await Items.CreateAsync(c, tx, new NewItem(AbyssalTraceFactory.TraceTypeId, 1, origin, CharacterShips.InSpaceFlag, 1, Singleton: true));
                var contentId = Guid.NewGuid().ToString("D");
                var hidden = Catalog.AbyssalSystemsById.Keys.Order().First();
                var plan = Catalog.CreatePlan(filament.TypeId, AbyssalGameMode.SoloCruiser, trace, contentId, hidden);
                plan = await AbyssalLootPlanResolver.FreezeBlueprintCopiesAsync(plan, new ItemTypeCatalog(Sde),
                    await AbyssalLootMaterializer.LoadBlueprintCategoryIdAsync(Sde));
                var started = await Runtime.StartAsync(c, tx, new AbyssalStartRequest(trace, character, ship, null,
                    AbyssalGameMode.SoloCruiser, filament.TypeId, origin, origin, new AbyssalPosition(10, 20, 30), Now + 1_800_000, plan));
                await Items.SetCustomInfoAsync(c, tx, trace,
                    System.Text.Json.JsonSerializer.Serialize(new AbyssalTracePoseSnapshot(1, [10, 20, 30], [0, 0, 1]),
                        new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
                if (!enter) { run = started.State; return; }
                var entered = await Runtime.EnterAsync(c, tx, new AbyssalEnterRequest(started.State.InstanceId,
                    new AbyssalParticipantAdmission(character, ship, type, "cruiser", null,
                        new AbyssalParticipantOrigin(origin, origin, new AbyssalPosition(10, 20, 30), new AbyssalPosition(0, 0, 1), shipRadius))));
                await Reservation.PrepareAsync(c, tx, entered.State, 1);
                var hiddenRow = (await Sde.ReadAsync("mapSolarSystems", [hidden])).Single().Doc;
                await Characters.SetLocationAsync(c, tx, character, hidden, hiddenRow.GetProperty("constellationID").GetInt32(), hiddenRow.GetProperty("regionID").GetInt32());
                await Items.MoveAsync(c, tx, ship, hidden, CharacterShips.InSpaceFlag);
                run = await Runtime.FindByTraceItemIdAsync(c, tx, trace);
            });
            return run!;
        }

        public async Task<AbyssalRunState> AdvanceToFinalRoom(AbyssalRunState run)
        {
            for (var room = 1; room <= 3; room++)
            {
                await Transaction(async (c, tx) =>
                {
                    foreach (var npc in run.Data.Rooms.Single(value => value.LocationId == room).Entities.Where(value => value.Role == AbyssalEntityRole.Npc))
                        run = (await Runtime.NpcDestroyedAsync(c, tx, new AbyssalNpcDestroyedRequest(run.InstanceId, room, npc.EntityId))).State;
                    if (room < 3)
                    {
                        var gate = run.Data.Rooms.Single(value => value.LocationId == room).Entities.Single(value => value.Role == AbyssalEntityRole.Gate);
                        run = (await Runtime.ActivateRoomGateAsync(c, tx,
                            new AbyssalRoomGateActivationRequest(run.InstanceId, room, gate.EntityId, run.Data.CreatorCharacterId))).State;
                        await Reservation.PrepareAsync(c, tx, run, room + 1);
                        run = (await Runtime.FindByTraceItemIdAsync(c, tx, run.Data.TraceItemId))!;
                    }
                });
            }
            return run;
        }

        public async Task<ClientSession> Connect(AbyssalRunState run, long sessionOffset = 100)
        {
            var p = Assert.Single(run.Data.Participants);
            var session = new ClientSession(new RecordingSink(Order)) { SessionId = p.CharacterId + sessionOffset, ClientId = p.CharacterId + sessionOffset + 100 };
            session.Set("charid", new PyLong(p.CharacterId)); session.Set("shipid", new PyLong(p.ShipId));
            session.Set("solarsystemid", new PyInt(run.Data.SpaceLocationId));
            session.Set("solarsystemid2", new PyInt(run.Data.SpaceLocationId));
            session.Set("locationid", new PyInt(run.Data.SpaceLocationId));
            Sessions.Enter(p.CharacterId, session);
            await Space.OnCharacterEnteredAsync(session, p.CharacterId, CancellationToken.None);
            Order.Clear();
            return session;
        }

        public async Task<ClientSession> ConnectPublic(AbyssalRunState run)
        {
            var character = (await Characters.GetAsync(run.Data.CreatorCharacterId))!;
            var ship = (await Items.GetAsync(run.Data.CreatorShipId))!;
            var type = (await new ItemTypeCatalog(Sde).GetAsync(ship.TypeId))!;
            var radius = (await Sde.ReadAsync("types", [ship.TypeId])).Single().Doc.GetProperty("radius").GetDouble();
            await Transaction((c, tx) => SpaceItems.SaveAsync(c, tx, new SpaceItem(ship.Id, character.SolarSystemId,
                [10, 20, 30], [0, 0, 1], radius, type.GroupId, 0, character.Id, character.CorporationId, 0, 0, long.MaxValue)));
            var session = new ClientSession(new RecordingSink(Order)) { SessionId = character.Id + 100, ClientId = character.Id + 200 };
            session.Set("charid", new PyLong(character.Id)); session.Set("shipid", new PyLong(ship.Id));
            session.Set("solarsystemid", new PyInt(character.SolarSystemId)); session.Set("solarsystemid2", new PyInt(character.SolarSystemId));
            session.Set("locationid", new PyInt(character.SolarSystemId));
            Sessions.Enter(character.Id, session);
            await Space.OnCharacterEnteredAsync(session, character.Id, CancellationToken.None);
            return session;
        }

        public async Task Disconnect(ClientSession session) =>
            await (Task)typeof(SpaceSessions).GetMethod("OnSessionClosedAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Space, [session])!;

        public async Task Transaction(Func<NpgsqlConnection, NpgsqlTransaction, Task> action)
        {
            await using var c = await Source.OpenConnectionAsync();
            await using var tx = await c.BeginTransactionAsync();
            await action(c, tx);
            await tx.CommitAsync();
        }
        public Task Materialize(AbyssalRunState run, int ordinal) => Transaction((c, tx) =>
            Invoke("PrepareAndMaterializeAbyssalRoomAsync", c, tx, run, ordinal, CancellationToken.None));
        public async Task EnsureScene(AbyssalRunState run, int room)
        {
            var center = await Reservation.ReadSystemCenterAsync(run.Data.SpaceLocationId, CancellationToken.None);
            Assert.Null(await Space.EnsureAbyssalSceneAsync(new SimAbyssalSceneSpec(
                new SimSceneKey(run.Data.SpaceLocationId, run.InstanceId, room),
                new SimAbyssalEnvironmentProfile([], []),
                new SimAbyssalBoundaryProfile([center.X, center.Y, center.Z], 75000, 1000,
                    [new SimAbyssalBoundaryDamagePoint(0, 10.55), new SimAbyssalBoundaryDamagePoint(75000, 778.55)], 15000),
                DateTimeOffset.FromUnixTimeMilliseconds(run.Data.RunExpiresAtMs!.Value).ToFileTime()), []));
        }
        public Task Recover(long id) => Transaction(async (c, tx) => await Runtime.RecoverAfterServerRestartAsync(c, tx, id));
        public async Task<AbyssalRunData> State(long id) => (await Foundations.Content.GetAsync(id))!.Data<AbyssalRunData>()!;
        public Task Publish(string topic, object payload, string key) => Transaction(async (c, tx) => await Foundations.Events.PublishAsync(c, tx, topic, payload, key));
        public async Task Drain()
        {
            for (var attempt = 0; attempt < 30; attempt++)
                if ((await Foundations.Events.PumpAsync()).Delivered == 0) break;
        }
        public async Task<long> Contents(long id)
        {
            await using var command = Source.CreateCommand($"SELECT count(*) FROM {Schemas[1]}.items WHERE location_id=$1");
            command.Parameters.AddWithValue(id);
            return (long)(await command.ExecuteScalarAsync())!;
        }
        public async Task Invoke(string method, params object[] args)
        {
            var result = typeof(CharacterServicesComposition).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Host, args);
            await (Task)result!;
        }
        public async ValueTask DisposeAsync()
        {
            Space.Dispose();
            await Foundations.DisposeAsync();
            foreach (var schema in Schemas)
            {
                await using var command = Source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
                await command.ExecuteNonQueryAsync();
            }
            await Source.DisposeAsync();
        }
    }

    public class FlowSim : DispatchProxy
    {
        public ISimulation? Inner { get; set; }
        public List<SimRemoveAbyssalParticipant> Removals { get; } = [];
        public List<SimSceneKey> RemovedScenes { get; } = [];
        public HashSet<SimSceneKey> LiveScenes { get; } = [];
        public SimSceneKey? BlockedCleanupScene { get; set; }
        public TaskCompletionSource? SceneEnsureStarted { get; set; }
        public TaskCompletionSource? AllowSceneEnsure { get; set; }
        private static async Task<string?> AwaitSceneEnsureAsync(Task task) { await task; return null; }
        public List<SimPlaceWreck> Wrecks { get; } = [];
        public List<SimEnsureAbyssalRewardCache> Caches { get; } = [];
        public List<SimEnsureAbyssalEnvironmentObject> EnvironmentObjects { get; } = [];
        public List<SimSpawn> Spawns { get; } = [];
        public List<string> Order { get; set; } = [];
        public string? RemovalError { get; set; }
        public IReadOnlyList<double> Pose { get; set; } = [0, 0, 0];
        public int Moves { get; private set; }
        public int PublicPoseQueries { get; private set; }
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(method);
            if (Inner is not null) return method.Invoke(Inner, args);
            if (method.Name == "get_IsConnected") return true;
            if (method.Name.StartsWith("add_") || method.Name.StartsWith("remove_")) return null;
            if (method.Name == "RemoveAbyssalParticipantAsync")
            {
                var request = (SimRemoveAbyssalParticipant)args![0]!;
                Removals.Add(request);
                return Task.FromResult(new SimRemoveAbyssalParticipantReply(request.RequestId, true, RemovalError));
            }
            if (method.Name == "GetAbyssalParticipantPoseAsync")
            {
                var request = (SimGetAbyssalParticipantPose)args![0]!;
                if (request.Scene.InstanceId == 0) PublicPoseQueries++;
                return Task.FromResult(new SimGetAbyssalParticipantPoseReply(request.RequestId, request.Scene, request.ShipId, Pose, [0, 0, 1], null)
                    { Deployment = request.Scene.InstanceId == 0 ? new SimAbyssalDeploymentSnapshot(false, false, false, false, false, []) : null });
            }
            if (method.Name is "MoveAbyssalParticipantAsync" or "MoveAbyssalParticipantInOrderAsync")
            {
                Moves++;
                var move = (SimMoveAbyssalParticipant)args![0]!;
                if (method.Name == "MoveAbyssalParticipantInOrderAsync")
                    ((Action<SimMoveAbyssalParticipantReply>)args[1]!)(new SimMoveAbyssalParticipantReply(move.RequestId, null));
                Order.Add("SimArrival");
                return Task.FromResult<string?>(null);
            }
            if (method.Name == "EnsureAbyssalSceneAsync")
            {
                LiveScenes.Add(((SimEnsureAbyssalScene)args![0]!).Spec.Scene);
                SceneEnsureStarted?.TrySetResult();
                return AllowSceneEnsure is { } allowed ? AwaitSceneEnsureAsync(allowed.Task) : Task.FromResult<string?>(null);
            }
            if (method.Name == "RemoveAbyssalSceneAsync")
            {
                var scene = ((SimRemoveAbyssalScene)args![0]!).Scene;
                if (BlockedCleanupScene == scene) return Task.FromResult<string?>("ABYSSAL_SCENE_OCCUPIED");
                LiveScenes.Remove(scene);
                RemovedScenes.Add(scene);
                return Task.FromResult<string?>(null);
            }
            if (method.Name == "RemoveAbyssalTraceAsync")
            {
                var request = (SimRemoveAbyssalTrace)args![0]!;
                return Task.FromResult(new SimRemoveAbyssalTraceReply(request.RequestId, SimAbyssalTraceStatus.AlreadyAbsent, 0, null));
            }
            if (method.Name == "EnsureAbyssalTracePublishedAsync")
            {
                var trace = (SimEnsureAbyssalTracePublished)args![0]!;
                Order.Add("TracePublish");
                return Task.FromResult(new SimEnsureAbyssalTracePublishedReply(trace.RequestId, SimAbyssalTraceStatus.Created, 1, true, null));
            }
            if (method.Name == "CommandAsync")
                return Task.FromResult(new CommandReply(true, 0, null) { Position = Pose, Direction = [0, 0, 1] });
            if (method.Name == "SpawnAsync")
            {
                var spawn = (SimSpawn)args![0]!;
                Spawns.Add(spawn);
                return Task.FromResult(new SimSpawnReply(spawn.RequestId, null, null));
            }
            if (method.Name == "EnsureAbyssalRewardCacheAsync")
            {
                Caches.Add((SimEnsureAbyssalRewardCache)args![0]!);
                return Task.FromResult<string?>(null);
            }
            if (method.Name == "EnsureAbyssalEnvironmentObjectAsync")
            {
                EnvironmentObjects.Add((SimEnsureAbyssalEnvironmentObject)args![0]!);
                return Task.FromResult<string?>(null);
            }
            if (method.Name == "EnsureAbyssalLootContainerAsync")
            {
                Wrecks.Add(((SimEnsureAbyssalLootContainer)args![0]!).Container);
                return Task.FromResult<string?>(null);
            }
            if (method.ReturnType == typeof(Task<string>)) return Task.FromResult<string?>(null);
            if (method.ReturnType == typeof(Task)) return Task.CompletedTask;
            if (method.ReturnType == typeof(void)) return null;
            throw new NotSupportedException(method.Name);
        }
    }

    private sealed class RecordingSink(List<string> order) : IPacketSink
    {
        public void Send(PyValue packet)
        {
            if (packet is not PyObject obj || obj.Args is not PyTuple tuple) return;
            var fields = tuple.Items;
            if (fields[0] is PyInt { Value: MachoMessage.SessionChangeNotification }) { order.Add("SessionChange"); return; }
            if (fields[0] is not PyInt { Value: MachoMessage.Notification }) return;
            var address = MachoAddress.Decode(fields[2]);
            if (address.BroadcastId is "OnItemChange" or "OnExitingDungeon" or "OnAbyssalLocationExited" or "OnAbyssalContentFinished")
                order.Add(address.BroadcastId);
        }
    }
}

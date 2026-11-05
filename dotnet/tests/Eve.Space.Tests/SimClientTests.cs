using System.Net.Sockets;

namespace Eve.Space.Tests;

public class SimClientTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    private static SimShip Ship() => new(9988400001939, 140000342, 601, 237, 6, "Ibis", 140000342, 1000006, 0, 0, 0, 47, 1163000, 339.25, 4.0185, 3, 860000, 29700, 660, 28, 4, 149.5, 55.25, []);

    private static SimEnter Enter(ulong session = 7) => new(session, SimSceneKey.SolarSystem(30100032), EnterHow.Undock, 60015249, Ship());

    private static async Task<T> Within<T>(Task<T> task)
    {
        using var timeout = new CancellationTokenSource(Patience);
        return await task.WaitAsync(timeout.Token);
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "gave up waiting");
            await Task.Delay(10);
        }
    }

    private static async Task<(FakeSimulation Sim, SimClient Client)> ConnectedAsync(
        Func<SimMessage, IEnumerable<SimMessage>>? reply = null,
        Action<string>? log = null,
        Action? afterConnectionCheck = null)
    {
        var sim = new FakeSimulation { Reply = reply ?? (_ => []) };
        sim.Listen();
        var client = SimClient.ToUnixSocket(sim.Path, log, afterConnectionCheck);
        client.RetryAfter = TimeSpan.FromMilliseconds(20);
        client.Start();
        await Until(() => client.IsConnected);
        return (sim, client);
    }

    [Fact]
    public async Task The_host_simulation_client_retries_until_a_slow_listener_starts()
    {
        await using var sim = new FakeSimulation();
        await using var client = SimClient.ToUnixSocket(sim.Path);
        client.RetryAfter = TimeSpan.FromMilliseconds(20);

        client.Start();
        await Task.Delay(100);
        Assert.False(client.IsConnected);

        sim.Listen();
        await Until(() => client.IsConnected);
        Assert.Contains(sim.HeardNow(), message => message is SimHello { Version: SimCodec.ProtocolVersion });
    }

    [Fact]
    public async Task It_says_hello_and_is_connected_once_the_simulation_answers()
    {
        var (sim, client) = await ConnectedAsync();
        await using var _ = sim;
        await using var __ = client;
        Assert.Equal(new SimHello(SimCodec.ProtocolVersion), sim.HeardNow().Single());
    }

    [Fact]
    public async Task An_enter_is_done_when_the_simulation_says_so_and_refused_with_its_reason_otherwise()
    {
        var (sim, client) = await ConnectedAsync(message => message is SimEnter { SessionId: 7 } e ? [new SimEnterReply(e.SessionId, null)] : message is SimEnter e2 ? [new SimEnterReply(e2.SessionId, "UNKNOWN_STATION 1")] : []);
        await using var _ = sim;
        await using var __ = client;

        Assert.Null(await Within(client.EnterAsync(Enter(7))));
        Assert.Equal("UNKNOWN_STATION 1", await Within(client.EnterAsync(Enter(8))));
        Assert.Equal(2, sim.HeardNow().OfType<SimEnter>().Count());
    }

    [Fact]
    public async Task Cutoff_replies_reject_mismatched_identity_even_when_request_id_matches()
    {
        var (sim, client) = await ConnectedAsync(message => message switch
        {
            SimRegisterTournamentCutoff request => [new SimRegisterTournamentCutoffReply(request.RequestId, request.Scene, request.MatchId,
                request.RequestId == 191 ? request.Generation + 1 : request.Generation, request.Start, request.Deadline, SimTournamentCutoffStatus.Pending, "")],
            SimReadTournamentCutoff request => [new SimReadTournamentCutoffReply(request.RequestId,
                request.RequestId == 192 ? SimSceneKey.SolarSystem(30_000_143) : request.Scene, request.MatchId, request.Generation, request.Start, request.Deadline,
                SimTournamentCutoffStatus.Unavailable, [], "")],
            SimReleaseTournamentCutoff request => [new SimReleaseTournamentCutoffReply(request.RequestId, request.Scene, request.MatchId, request.Generation,
                request.Start, request.RequestId == 193 ? request.Deadline + 1 : request.Deadline, SimTournamentCutoffStatus.Pending, "")],
            _ => [],
        });
        await using var _ = sim; await using var __ = client;
        var scene = SimSceneKey.SolarSystem(30_000_142);
        await Assert.ThrowsAsync<SimLinkException>(() => Within(client.RegisterTournamentCutoffAsync(new SimRegisterTournamentCutoff(191,scene,2,3,1000,2000,[new(101,201)]))));
        await Assert.ThrowsAsync<SimLinkException>(() => Within(client.ReadTournamentCutoffAsync(new SimReadTournamentCutoff(192,scene,2,3,1000,2000))));
        await Assert.ThrowsAsync<SimLinkException>(() => Within(client.ReleaseTournamentCutoffAsync(new SimReleaseTournamentCutoff(193,scene,2,3,1000,2000))));
        Assert.Equal(SimTournamentCutoffStatus.Unavailable, (await Within(client.ReadTournamentCutoffAsync(new SimReadTournamentCutoff(194,scene,2,3,1000,2000)))).Status);
    }

    [Fact]
    public async Task Metrics_replies_reject_forged_identity_or_roster()
    {
        var (sim,client)=await ConnectedAsync(message => message switch
        {
            SimRegisterTournamentMetrics r => [new SimRegisterTournamentMetricsReply(r.RequestId,r.Scene,r.MatchId,r.Generation+1,r.Start,r.Roster,SimTournamentMetricsStatus.Success,"")],
            SimReadTournamentMetrics r => [new SimReadTournamentMetricsReply(r.RequestId,r.Scene,r.MatchId,r.Generation,r.Start,[new(101,999,0)],SimTournamentMetricsStatus.Unavailable,"",null)],
            SimReadTournamentMetricsV2 r => [new SimReadTournamentMetricsV2Reply(r.RequestId,r.Scene,r.MatchId,r.Generation,r.Start,[new(101,999,0)],SimTournamentMetricsStatus.Unavailable,"",null)],
            SimReleaseTournamentMetrics r => [new SimReleaseTournamentMetricsReply(r.RequestId,SimSceneKey.SolarSystem(30000143),r.MatchId,r.Generation,r.Start,r.Roster,SimTournamentMetricsStatus.Success,"")],
            _ => [],
        });
        await using var _=sim; await using var __=client;
        var scene=SimSceneKey.SolarSystem(30000142); SimTournamentMetricsPilot[] roster=[new(101,201,0)];
        await Assert.ThrowsAsync<SimLinkException>(()=>Within(client.RegisterTournamentMetricsAsync(new(201,scene,3,4,1000,roster))));
        await Assert.ThrowsAsync<SimLinkException>(()=>Within(client.ReadTournamentMetricsAsync(new(202,scene,3,4,1000,roster))));
        await Assert.ThrowsAsync<SimLinkException>(()=>Within(client.ReadTournamentMetricsV2Async(new(204,scene,3,4,1000,roster))));
        await Assert.ThrowsAsync<SimLinkException>(()=>Within(client.ReleaseTournamentMetricsAsync(new(203,scene,3,4,1000,roster))));
    }

    [Fact]
    public async Task Metrics_v2_client_preserves_known_zero_fighter_members()
    {
        var (sim, client) = await ConnectedAsync(message => message switch
        {
            SimReadTournamentMetricsV2 r => [new SimReadTournamentMetricsV2Reply(r.RequestId,r.Scene,r.MatchId,r.Generation,r.Start,r.Roster,SimTournamentMetricsStatus.Success,"",
                new SimTournamentMetricsSample(r.Start,null,[new SimTournamentMetricsPilotSample(r.Roster[0],[],[],[new(401,801,r.Roster[0].ShipId,1,SimTournamentCompanionKind.Fighter,0)],SimTournamentMetricsEhp.Unknown,null)]))],
            _ => [],
        });
        await using var _ = sim; await using var __ = client;
        var scene = SimSceneKey.SolarSystem(30000142); SimTournamentMetricsPilot[] roster = [new(101,201,0)];
        var reply = await Within(client.ReadTournamentMetricsV2Async(new(205,scene,3,4,1000,roster)));
        Assert.Equal(0, reply.Sample!.Pilots[0].Companions[0].Members);
    }

    [Fact]
    public async Task Scene_body_reply_rejects_forged_scene_and_item_even_when_request_id_matches()
    {
        var (sim, client) = await ConnectedAsync(message => message switch
        {
            SimGetSceneBody request => [new SimGetSceneBodyReply(request.RequestId,
                request.RequestId == 91 ? SimSceneKey.SolarSystem(30_000_143) : request.Scene,
                request.RequestId == 92 ? request.ItemId + 1 : request.ItemId,
                10645, 310, 2, [1, 2, 3], null)],
            _ => [],
        });
        await using var _ = sim;
        await using var __ = client;
        var scene = SimSceneKey.SolarSystem(30_000_142);
        await Assert.ThrowsAsync<SimLinkException>(() => Within(client.GetSceneBodyAsync(new SimGetSceneBody(91, scene, 123))));
        await Assert.ThrowsAsync<SimLinkException>(() => Within(client.GetSceneBodyAsync(new SimGetSceneBody(92, scene, 123))));
        var valid = await Within(client.GetSceneBodyAsync(new SimGetSceneBody(93, scene, 123)));
        Assert.Equal(123, valid.ItemId);
        Assert.Equal(scene, valid.Scene);
    }

    [Fact]
    public async Task Abyssal_scene_move_and_rebind_requests_wait_for_their_correlated_replies()
    {
        var (sim, client) = await ConnectedAsync(message => message switch
        {
            SimEnsureAbyssalScene request => [new SimEnsureAbyssalSceneReply(request.RequestId, null)],
            SimMoveAbyssalParticipant request => [new SimMoveAbyssalParticipantReply(request.RequestId, "DESTINATION_NOT_READY")],
            SimRebindAbyssalSession request => [new SimRebindAbyssalSessionReply(request.RequestId, null)],
            SimRemoveAbyssalScene request => [new SimRemoveAbyssalSceneReply(request.RequestId, null)],
            _ => [],
        });
        await using var _ = sim;
        await using var __ = client;
        var scene = new SimSceneKey(31_000_201, 42, 2);
        var profile = new SimAbyssalSceneSpec(
            scene,
            new SimAbyssalEnvironmentProfile([], []),
            new SimAbyssalBoundaryProfile([0.0, 0.0, 0.0], 75_000.0, 1_000,
                [new SimAbyssalBoundaryDamagePoint(0.0, 12.5)]),
            134_351_527_136_350_000);

        Assert.Null(await Within(client.EnsureAbyssalSceneAsync(new SimEnsureAbyssalScene(101, profile))));
        Assert.Equal("DESTINATION_NOT_READY", await Within(client.MoveAbyssalParticipantAsync(new SimMoveAbyssalParticipant(
            102, 9988400001939, new SimSceneKey(31_000_200, 42, 1), scene, [1.0, 2.0, 3.0], [0.0, 0.0, 1.0]))));
        Assert.Null(await Within(client.RebindAbyssalSessionAsync(new SimRebindAbyssalSession(103, 8, 9988400001939, scene))));
        Assert.Null(await Within(client.RemoveAbyssalSceneAsync(new SimRemoveAbyssalScene(104, scene))));
        Assert.Equal([101UL, 102UL, 103UL, 104UL], sim.HeardNow().Skip(1).Select(message => message switch
        {
            SimEnsureAbyssalScene ensure => ensure.RequestId,
            SimMoveAbyssalParticipant move => move.RequestId,
            SimRebindAbyssalSession rebind => rebind.RequestId,
            SimRemoveAbyssalScene remove => remove.RequestId,
            _ => 0UL,
        }));
    }

    [Fact]
    public async Task A_dock_is_accepted_with_the_time_refused_with_the_reason_or_answered_with_the_distance_the_ship_now_goes()
    {
        var (sim, client) = await ConnectedAsync(message => message switch
        {
            SimCommand { SessionId: 7, Code: SimCommands.Dock } c => [new SimAccepted(c.SessionId, c.Code, 134351527136350000)],
            SimCommand { SessionId: 9, Code: SimCommands.Dock } c => [new SimApproaching(c.SessionId, c.Code, 15974.6)],
            SimCommand { Code: SimCommands.Dock } c => [new SimRefused(c.SessionId, c.Code, "DOCKING_APPROACH_REQUIRED")],
            _ => [],
        });
        await using var _ = sim;
        await using var __ = client;

        Assert.Equal(new CommandReply(true, 134351527136350000, null), await Within(client.CommandAsync(7, SimCommands.Dock, [60015249])));
        Assert.Equal(new CommandReply(false, 0, "DOCKING_APPROACH_REQUIRED"), await Within(client.CommandAsync(8, SimCommands.Dock, [60015249])));
        Assert.Equal(new CommandReply(false, 0, null, 15974.6), await Within(client.CommandAsync(9, SimCommands.Dock, [60015249])));
        var dock = sim.HeardNow().OfType<SimCommand>().First();
        Assert.Equal([60015249d], dock.Args);
    }

    [Fact]
    public async Task A_command_that_is_waited_for_is_answered_by_its_own_code_and_a_movement_that_is_not_is_handed_on()
    {
        var (sim, client) = await ConnectedAsync(message => message switch
        {
            SimCommand { Code: SimCommands.WarpTo, SessionId: 7 } c => [new SimAccepted(c.SessionId, c.Code, 5)],
            SimCommand { Code: SimCommands.WarpTo } c => [new SimRefused(c.SessionId, c.Code, "TARGET_NOT_FOUND")],
            SimCommand { Code: SimCommands.Stop } c => [new SimAccepted(c.SessionId, c.Code, 6)],
            _ => [],
        });
        await using var _ = sim;
        await using var __ = client;
        var heard = new List<SimMessage>();
        client.Received += heard.Add;

        Assert.Equal(new CommandReply(true, 5, null), await Within(client.CommandAsync(7, SimCommands.WarpTo, [40491233, 0])));
        Assert.Equal(new CommandReply(false, 0, "TARGET_NOT_FOUND"), await Within(client.CommandAsync(8, SimCommands.WarpTo, [1, 0])));
        Assert.Equal([40491233d, 0d], sim.HeardNow().OfType<SimCommand>().First().Args);

        // a stop nobody waits for: its acceptance reaches the listener; a waited one does not
        client.Command(7, SimCommands.Stop);
        await Until(() => heard.Count == 1);
        Assert.Equal(new SimAccepted(7, SimCommands.Stop, 6), heard.Single());
    }

    [Fact]
    public async Task A_jump_is_answered_with_where_the_ship_is_going_and_the_arrival_is_handed_on()
    {
        var (sim, client) = await ConnectedAsync(message => message switch
        {
            SimCommand { Code: SimCommands.Jump, SessionId: 7 } c => [new SimJumping(c.SessionId, c.Code, 30100034, 50016839)],
            SimCommand { Code: SimCommands.Jump } c => [new SimRefused(c.SessionId, c.Code, "TOO_FAR_FROM_STARGATE")],
            _ => [],
        });
        await using var _ = sim;
        await using var __ = client;
        var heard = new List<SimMessage>();
        client.Received += heard.Add;

        Assert.Equal(new CommandReply(true, 0, null, null, 30100034, 50016839), await Within(client.CommandAsync(7, SimCommands.Jump, [50016630, 50016839])));
        Assert.Equal(new CommandReply(false, 0, "TOO_FAR_FROM_STARGATE"), await Within(client.CommandAsync(8, SimCommands.Jump, [50016630, 0])));
        Assert.Equal([50016630d, 50016839d], sim.HeardNow().OfType<SimCommand>().First().Args);
        Assert.Empty(heard);

        // the arrival is nobody's answer: it reaches the listener, and so does a jump nobody waits for
        await sim.SayAsync(new SimJumped(7, 30100032, 50016630, 30100034, 50016839));
        await sim.SayAsync(new SimJumping(9, SimCommands.Jump, 30100034, 50016839));
        await Until(() => heard.Count == 1);
        Assert.Equal(new SimJumped(7, 30100032, 50016630, 30100034, 50016839), heard.Single());
    }

    [Fact]
    public async Task A_targeting_order_is_answered_with_what_is_locked_or_why_it_is_refused_and_an_answer_nobody_waits_for_is_dropped()
    {
        var (sim, client) = await ConnectedAsync(message => message switch
        {
            SimCommand { Code: SimCommands.AddTarget, SessionId: 7 } c => [new SimTargets(c.SessionId, c.Code, true, [60015249])],
            SimCommand { Code: SimCommands.AddTarget } c => [new SimTargetRefused(c.SessionId, c.Code, "TARGET_OUT_OF_RANGE", 92885, 29700, 12345.5)],
            SimCommand { Code: SimCommands.GetTargets } c => [new SimTargets(c.SessionId, c.Code, false, [1, 2])],
            SimCommand { Code: SimCommands.ClearTargets } c => [new SimRefused(c.SessionId, c.Code, "NO_SHIP_IN_SPACE")],
            _ => [],
        });
        await using var _ = sim;
        await using var __ = client;
        var heard = new List<SimMessage>();
        client.Received += heard.Add;

        var going = await Within(client.CommandAsync(7, SimCommands.AddTarget, [9988400002151]));
        Assert.Equal((true, true), (going.Accepted, going.Pending));
        Assert.Equal([60015249L], going.Targets);
        var refused = await Within(client.CommandAsync(8, SimCommands.AddTarget, [60015249]));
        Assert.Equal((false, "TARGET_OUT_OF_RANGE", 92885, 29700d, 12345.5), (refused.Accepted, refused.Reason, refused.TargetType, refused.DesiredRange, refused.ActualDistance));
        var held = await Within(client.CommandAsync(7, SimCommands.GetTargets, []));
        Assert.Equal((true, false), (held.Accepted, held.Pending));
        Assert.Equal([1L, 2L], held.Targets);
        Assert.Equal(new CommandReply(false, 0, "NO_SHIP_IN_SPACE"), await Within(client.CommandAsync(7, SimCommands.ClearTargets, [])));
        Assert.Empty(heard);

        // an answer nobody waits for is not handed on as news
        await sim.SayAsync(new SimTargets(9, SimCommands.AddTarget, true, []));
        await sim.SayAsync(new SimTargetRefused(9, SimCommands.AddTarget, "TARGET_SELF", 0, 0, 0));
        await sim.SayAsync(new SimBind(9));
        await Until(() => heard.Count == 1);
        Assert.IsType<SimBind>(heard.Single());
    }

    [Fact]
    public async Task An_effect_is_given_to_the_simulation_and_answered_under_its_own_code()
    {
        var effect = new SimEffect(9988400001940, 21857, 6731, "effects.Afterburner", 10000, 5, 1000, 0, new SimNumbers(1663000, 522.848918, 52), null, [new SimChange(9988400001940, 20, 60)], []);
        var (sim, client) = await ConnectedAsync(message => message switch
        {
            SimActivate { SessionId: 7 } a => [new SimAccepted(a.SessionId, SimCommands.Activate, 42)],
            SimActivate a => [new SimRefused(a.SessionId, SimCommands.Activate, "ALREADY_ACTIVE")],
            _ => [],
        });
        await using var _ = sim;
        await using var __ = client;
        var heard = new List<SimMessage>();
        client.Received += heard.Add;

        Assert.Equal(new CommandReply(true, 42, null), await Within(client.ActivateAsync(7, effect)));
        Assert.Equal(new CommandReply(false, 0, "ALREADY_ACTIVE"), await Within(client.ActivateAsync(8, effect)));
        // the simulation was given the effect whole
        var asked = sim.HeardNow().OfType<SimActivate>().First();
        Assert.Equal(("effects.Afterburner", 522.848918, 1000), (asked.Effect.Fx, asked.Effect.On!.MaxVelocity, asked.Effect.Repeat));
        Assert.Equal([(9988400001940, 20, 60.0)], asked.Effect.OnChanges.Select(c => (c.ItemId, c.AttributeId, c.Value)));
        Assert.Empty(heard);
    }

    [Fact]
    public async Task What_the_simulation_says_on_its_own_is_handed_on_and_what_answers_a_request_is_not()
    {
        var (sim, client) = await ConnectedAsync(message => message is SimEnter e ? [new SimEnterReply(e.SessionId, null)] : []);
        await using var _ = sim;
        await using var __ = client;
        var heard = new List<SimMessage>();
        client.Received += heard.Add;

        await Within(client.EnterAsync(Enter()));
        await sim.SayAsync(new SimNotify(7, "DoDestinyUpdate", "clientID", new byte[] { 1, 2, 3 }));
        await sim.SayAsync(new SimRefused(7, SimCommands.Stop, "NO_SHIP_IN_SPACE"));
        await sim.SayAsync(new SimDocked(7, 60015249, SimCondition.Full));
        await sim.SayAsync(new SimAccepted(7, SimCommands.FollowBall, 134351527136350000)); // a movement is accepted: nobody waits for that
        await Until(() => heard.Count == 4);

        Assert.IsType<SimNotify>(heard[0]);
        Assert.Equal(new SimRefused(7, SimCommands.Stop, "NO_SHIP_IN_SPACE"), heard[1]);
        Assert.Equal(new SimDocked(7, 60015249, SimCondition.Full), heard[2]);
        Assert.Equal(new SimAccepted(7, SimCommands.FollowBall, 134351527136350000), heard[3]);
    }

    [Fact]
    public async Task Tether_policy_reaches_the_peer_and_the_state_change_reaches_the_host()
    {
        const long structureId = 1_040_000_000_001;
        var (sim, client) = await ConnectedAsync(message => message is SimTetherPolicy policy
            ? [new SimTetherChanged(policy.SessionId, 0, policy.Candidates[0].StructureId, true)] : []);
        await using var _ = sim;
        await using var __ = client;
        var heard = new System.Collections.Concurrent.ConcurrentQueue<SimMessage>();
        client.Received += heard.Enqueue;
        client.SetTetherPolicy(new(7, 1000, 3000, [new(structureId, 10000)]));
        await Until(() => heard.Count == 1);

        var policy = Assert.Single(sim.HeardNow().OfType<SimTetherPolicy>());
        Assert.Equal((7UL, 1000UL, 3000UL), (policy.SessionId, policy.BlockedForMs, policy.LeaseMs));
        Assert.Equal(new SimTetherCandidate(structureId, 10000), Assert.Single(policy.Candidates));
        Assert.Equal(new SimTetherChanged(7, 0, structureId, true), Assert.Single(heard));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Structure_control_waits_for_the_actual_simulation_outcome(bool accepted)
    {
        var (sim, client) = await ConnectedAsync(message => message is SimControlStructure control
            ? accepted ? [new SimAccepted(control.SessionId, SimCommands.ControlStructure, 123)]
                : [new SimRefused(control.SessionId, SimCommands.ControlStructure, "NO_STRUCTURE")] : []);
        await using var disposeSim = sim;
        await using var disposeClient = client;
        var request = new SimControlStructure(7, 30000142, 1040000000000001, 90000001, true, 1);
        Assert.Equal(accepted ? null : "NO_STRUCTURE", await client.ControlStructureAsync(request));
        Assert.Equal(request, Assert.Single(sim.HeardNow().OfType<SimControlStructure>()));
    }

    [Fact]
    public async Task Commands_reach_the_simulation_in_the_order_they_were_given()
    {
        var (sim, client) = await ConnectedAsync();
        await using var _ = sim;
        await using var __ = client;

        client.Bind(7);
        client.UpdateState(7);
        client.Command(7, SimCommands.SetSpeedFraction, 0.5);
        client.Command(7, SimCommands.GotoDirection, 1, 0, 0);
        client.Command(7, SimCommands.Stop);
        client.ShipChanged(7, Ship());
        client.Leave(7, LeaveWhy.LoggedOff);
        await Until(() => sim.HeardNow().Count == 8);

        var heard = sim.HeardNow().Skip(1).ToArray();
        Assert.Equal([typeof(SimBind), typeof(SimUpdateState), typeof(SimCommand), typeof(SimCommand), typeof(SimCommand), typeof(SimShipChanged), typeof(SimLeave)], heard.Select(m => m.GetType()));
        Assert.Equal([0.5], ((SimCommand)heard[2]).Args);
        Assert.Equal([1d, 0d, 0d], ((SimCommand)heard[3]).Args);
        Assert.Equal(LeaveWhy.LoggedOff, ((SimLeave)heard[6]).Why);
    }

    [Fact]
    public async Task Placement_waits_for_outbound_queue_room_without_dropping_the_link()
    {
        var allowReading = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var sim = new FakeSimulation { PauseAfterHello = allowReading.Task };
        sim.Listen();
        await using var client = new SimClient(async cancellationToken =>
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified) { SendBufferSize = 1024 };
            try
            {
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(sim.Path), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        });
        client.RetryAfter = TimeSpan.FromMilliseconds(20);
        client.Start();
        await Until(() => client.IsConnected);

        const int count = 10_000;
        var expected = Enumerable.Range(0, count).Select(index => index + 1_000L).ToArray();
        var origin = new double[] { 0, 0, 0 };
        var sending = Task.Run(async () =>
        {
            foreach (var id in expected)
                Assert.True(await client.PlaceDeadspacePocketAsync(new(id, 30_000_142, "Deadspace Pocket", origin, 75_000)));
        });

        await Task.Delay(100);
        var waitedForRoom = !sending.IsCompleted;
        var stayedConnected = client.IsConnected;
        allowReading.TrySetResult(true);
        await sending.WaitAsync(Patience);
        await Until(() => sim.HeardNow().OfType<SimPlaceDeadspacePocket>().Count() == count);

        Assert.True(waitedForRoom);
        Assert.True(stayedConnected);
        Assert.Equal(expected, sim.HeardNow().OfType<SimPlaceDeadspacePocket>().Select(pocket => pocket.DeployableId));
    }

    [Fact]
    public async Task A_request_when_the_simulation_is_not_there_is_refused_at_once()
    {
        await using var client = SimClient.ToUnixSocket(Path.Combine(Path.GetTempPath(), $"nobody-{Guid.NewGuid():N}.sock"));
        client.RetryAfter = TimeSpan.FromMilliseconds(20);
        client.Start();
        Assert.False(client.IsConnected);
        await Assert.ThrowsAsync<SimUnavailableException>(() => client.EnterAsync(Enter()));
        await Assert.ThrowsAsync<SimUnavailableException>(() => client.CommandAsync(7, SimCommands.Dock, [1]));
        client.Bind(7); // said to nobody: nothing happens
    }

    [Fact]
    public async Task Every_abyssal_reply_map_fails_when_the_link_breaks()
    {
        var (sim, client) = await ConnectedAsync();
        await using var _ = sim;
        await using var __ = client;
        var scene = new SimSceneKey(31_000_201, 42, 1);
        var trace = new SimAbyssalTrace(
            31_000_201, 8_400_000_489, 48_964, 1_991, 2, 90_001, 500.0,
            [1.0, 2.0, 3.0], [4.0, 5.0, 6.0], [0.0, 0.0, 1.0], [7.0, 8.0, 9.0],
            0.5, 2, 56_132, 0, 1_800_000_000_000);
        var requests = new Task[]
        {
            ExpectUnavailable(client.EnsureAbyssalTracePublishedAsync(new(1, 7, trace))),
            ExpectUnavailable(client.RemoveAbyssalTraceAsync(new(2, scene.LocationId, trace.ItemId))),
            ExpectUnavailable(client.RemoveAbyssalParticipantAsync(new(3, scene, 8_400_000_193))),
            ExpectUnavailable(client.GetAbyssalParticipantPoseAsync(new(4, scene, 8_400_000_193))),
        };

        await Until(() => sim.HeardNow().Count(message => message is
            SimEnsureAbyssalTracePublished or SimRemoveAbyssalTrace or
            SimRemoveAbyssalParticipant or SimGetAbyssalParticipantPose) == requests.Length);
        sim.Drop();
        await Task.WhenAll(requests);

        static async Task ExpectUnavailable<T>(Task<T> request) =>
            await Assert.ThrowsAsync<SimUnavailableException>(() => Within(request));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task A_link_break_after_the_connection_check_cannot_strand_a_request(int requestKind)
    {
        using var continueRegistration = new ManualResetEventSlim();
        var checkedConnection = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var linkBroken = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (sim, client) = await ConnectedAsync(
            log: message =>
            {
                if (message.StartsWith("the simulation link:", StringComparison.Ordinal))
                    linkBroken.TrySetResult(true);
            },
            afterConnectionCheck: () =>
            {
                checkedConnection.TrySetResult(true);
                if (!continueRegistration.Wait(Patience))
                    throw new TimeoutException("request registration was not released");
            });
        await using var _ = sim;
        await using var __ = client;
        var scene = SimSceneKey.SolarSystem(30_000_142);
        Task request = Task.Run(async () =>
        {
            switch (requestKind)
            {
                case 0:
                    await client.EnterAsync(Enter());
                    break;
                case 1:
                    await client.RemoveTournamentBeaconAsync(new(2, scene, 8_400_000_193));
                    break;
                default:
                    await client.AdjustChargeAsync(new(3, 7, 101, 34, new SimChargeOp.Remove(1)));
                    break;
            }
        });

        try
        {
            await checkedConnection.Task.WaitAsync(Patience);
            sim.Drop();
            await linkBroken.Task.WaitAsync(Patience);
            continueRegistration.Set();
            await Assert.ThrowsAsync<SimUnavailableException>(() => request.WaitAsync(Patience));
        }
        finally
        {
            continueRegistration.Set();
        }
    }

    [Fact]
    public async Task When_the_link_breaks_what_was_waiting_fails_and_the_loss_is_reported_and_it_connects_again()
    {
        var (sim, client) = await ConnectedAsync(); // never answers an enter
        await using var _ = sim;
        await using var __ = client;
        var lost = 0;
        client.Lost += () => Interlocked.Increment(ref lost);

        var waiting = client.EnterAsync(Enter());
        await Until(() => sim.HeardNow().OfType<SimEnter>().Any());
        sim.Drop();

        await Assert.ThrowsAsync<SimUnavailableException>(() => Within(waiting));
        await Until(() => lost == 1);
        await Until(() => client.IsConnected && sim.Connections == 2);
        Assert.Equal(1, lost);
    }

    [Fact]
    public async Task A_simulation_that_speaks_another_version_is_not_connected_to()
    {
        var sim = new FakeSimulation { AckVersion = SimCodec.ProtocolVersion + 1 };
        await using var _ = sim;
        sim.Listen();
        var log = new System.Collections.Concurrent.ConcurrentQueue<string>();
        await using var client = SimClient.ToUnixSocket(sim.Path, log.Enqueue);
        client.RetryAfter = TimeSpan.FromMilliseconds(20);
        client.Start();
        await Until(() => sim.Connections >= 2);
        Assert.False(client.IsConnected);
        Assert.Contains(log, line => line.Contains("HelloAck") || line.Contains("hello"));
    }

    [Fact]
    public async Task Only_one_request_of_a_kind_waits_per_session()
    {
        var (sim, client) = await ConnectedAsync(); // never answers
        await using var _ = sim;
        await using var __ = client;
        var first = client.EnterAsync(Enter());
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.EnterAsync(Enter()));
        Assert.False(first.IsCompleted);
    }

    [Fact]
    public async Task A_pre_cancelled_request_is_canceled_without_reaching_the_simulation()
    {
        var (sim, client) = await ConnectedAsync();
        await using var _ = sim;
        await using var __ = client;
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var scene = new SimSceneKey(31_000_201, 42, 2);
        var profile = new SimAbyssalSceneSpec(
            scene,
            new SimAbyssalEnvironmentProfile([], []),
            new SimAbyssalBoundaryProfile([0.0, 0.0, 0.0], 75_000.0, 1_000,
                [new SimAbyssalBoundaryDamagePoint(0.0, 12.5)]),
            134_351_527_136_350_000);

        var enterCancellation = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.EnterAsync(Enter(), canceled.Token));
        var abyssalCancellation = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.EnsureAbyssalSceneAsync(
            new SimEnsureAbyssalScene(101, profile), canceled.Token));
        var chargeCancellation = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.AdjustChargeAsync(
            new SimAdjustCharge(102, 7, 9988400001939, 34, new SimChargeOp.Add(10, 5)), cancellationToken: canceled.Token));
        Assert.Equal(canceled.Token, enterCancellation.CancellationToken);
        Assert.Equal(canceled.Token, abyssalCancellation.CancellationToken);
        Assert.Equal(canceled.Token, chargeCancellation.CancellationToken);

        Assert.DoesNotContain(sim.HeardNow(), message => message is SimEnter or SimEnsureAbyssalScene or SimAdjustCharge);
    }

    [Fact]
    public async Task Cancellation_after_the_connection_check_prevents_enqueue()
    {
        using var canceled = new CancellationTokenSource();
        using var continueEnqueue = new ManualResetEventSlim();
        var checkedConnection = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (sim, client) = await ConnectedAsync(afterConnectionCheck: () =>
        {
            checkedConnection.TrySetResult(true);
            if (!continueEnqueue.Wait(Patience)) throw new TimeoutException("request enqueue was not released");
        });
        await using var _ = sim;
        await using var __ = client;

        var request = Task.Run(() => client.EnterAsync(Enter(), canceled.Token));
        try
        {
            await checkedConnection.Task.WaitAsync(Patience);
            canceled.Cancel();
            continueEnqueue.Set();

            var cancellation = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request.WaitAsync(Patience));
            Assert.Equal(canceled.Token, cancellation.CancellationToken);
            Assert.DoesNotContain(sim.HeardNow(), message => message is SimEnter);
        }
        finally
        {
            continueEnqueue.Set();
        }
    }

    [Fact]
    public async Task Cancellation_at_the_enqueue_boundary_keeps_the_accepted_send_and_cancels_the_wait()
    {
        using var canceled = new CancellationTokenSource();
        var seen = 0;
        var (sim, client) = await ConnectedAsync(message =>
        {
            if (message is not SimEnter enter) return [];
            if (Interlocked.Increment(ref seen) == 1)
            {
                canceled.Cancel();
                return [];
            }
            return [new SimEnterReply(enter.SessionId, null)];
        });
        await using var _ = sim;
        await using var __ = client;

        var cancellation = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.EnterAsync(Enter(), canceled.Token));
        Assert.Equal(canceled.Token, cancellation.CancellationToken);
        Assert.Single(sim.HeardNow().OfType<SimEnter>());

        Assert.Null(await Within(client.EnterAsync(Enter())));
        Assert.Equal(2, sim.HeardNow().OfType<SimEnter>().Count());
    }

    [Fact]
    public async Task A_reply_after_cancellation_is_ignored_before_a_retry_reuses_the_key()
    {
        var (sim, client) = await ConnectedAsync();
        await using var _ = sim;
        await using var __ = client;
        using var canceled = new CancellationTokenSource();
        var heardAfterReply = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Received += message =>
        {
            if (message is SimBind { SessionId: 7 }) heardAfterReply.TrySetResult(true);
        };

        var first = client.EnterAsync(Enter(), canceled.Token);
        await Until(() => sim.HeardNow().OfType<SimEnter>().Count() == 1);
        canceled.Cancel();
        var cancellation = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Equal(canceled.Token, cancellation.CancellationToken);
        await sim.SayAsync(new SimEnterReply(7, "CANCELLED_REQUEST"));
        await sim.SayAsync(new SimBind(7));
        await Within(heardAfterReply.Task);

        var retry = client.EnterAsync(Enter());
        await Until(() => sim.HeardNow().OfType<SimEnter>().Count() == 2);
        await sim.SayAsync(new SimEnterReply(7, "RETRY_REPLY"));
        Assert.Equal("RETRY_REPLY", await Within(retry));
    }

    [Fact]
    public void Late_cancellation_cleanup_keeps_retries_with_the_same_key_in_every_pending_map()
    {
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        var answeredKey = (Session: 7UL, Code: SimCommands.Dock);
        var oldAnswer = new TaskCompletionSource<CommandReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        var retryAnswer = new TaskCompletionSource<CommandReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        AssertLateCancellationLeavesRetry(
            new System.Collections.Concurrent.ConcurrentDictionary<(ulong Session, byte Code), TaskCompletionSource<CommandReply>>(),
            answeredKey, oldAnswer, retryAnswer, oldAnswer, new CommandReply(true, 1, null), canceled.Token);

        var abyssalKey = (ReplyType: typeof(SimEnsureAbyssalSceneReply), RequestId: 101UL);
        var oldAbyssal = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var retryAbyssal = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        AssertLateCancellationLeavesRetry(
            new System.Collections.Concurrent.ConcurrentDictionary<(Type ReplyType, ulong RequestId), TaskCompletionSource<string?>>(),
            abyssalKey, oldAbyssal, retryAbyssal, oldAbyssal, null, canceled.Token);

        var oldCharge = new TaskCompletionSource<SimChargeAdjusted>(TaskCreationOptions.RunContinuationsAsynchronously);
        var retryCharge = new TaskCompletionSource<SimChargeAdjusted>(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldChargePending = (Completion: oldCharge, InOrder: (Action<SimChargeAdjusted>?)null);
        var retryChargePending = (Completion: retryCharge, InOrder: (Action<SimChargeAdjusted>?)null);
        AssertLateCancellationLeavesRetry(
            new System.Collections.Concurrent.ConcurrentDictionary<ulong, (TaskCompletionSource<SimChargeAdjusted> Completion, Action<SimChargeAdjusted>? InOrder)>(),
            102UL, oldChargePending, retryChargePending, oldCharge, new SimChargeAdjusted(102, true, 4, 5), canceled.Token);
    }

    [Fact]
    public async Task Charge_adjustment_completes_from_its_correlated_reply()
    {
        var request = new SimAdjustCharge(201, 7, 9988400001939, 34, new SimChargeOp.Add(10, 5));
        var reply = new SimChargeAdjusted(request.RequestId, true, 4, 9);
        var (sim, client) = await ConnectedAsync(message => message is SimAdjustCharge sent && sent.RequestId == request.RequestId ? [reply] : []);
        await using var _ = sim;
        await using var __ = client;
        var ordered = new List<SimChargeAdjusted>();

        Assert.Equal(reply, await Within(client.AdjustChargeAsync(request, ordered.Add)));
        Assert.Equal([reply], ordered);
        Assert.Equal(request, Assert.Single(sim.HeardNow().OfType<SimAdjustCharge>()));
    }

    private static void AssertLateCancellationLeavesRetry<TKey, TValue, T>(
        System.Collections.Concurrent.ConcurrentDictionary<TKey, TValue> pending,
        TKey key,
        TValue original,
        TValue retry,
        TaskCompletionSource<T> originalCompletion,
        T reply,
        CancellationToken cancellationToken) where TKey : notnull
    {
        Assert.True(pending.TryAdd(key, original));
        Assert.True(pending.TryRemove(key, out var replied));
        Assert.Equal(original, replied);
        Assert.True(originalCompletion.TrySetResult(reply));
        Assert.True(pending.TryAdd(key, retry));

        SimClient.CancelPending(new object(), pending, key, original, originalCompletion, cancellationToken);

        Assert.Equal(retry, pending[key]);
        Assert.Equal(reply, originalCompletion.Task.GetAwaiter().GetResult());
    }

    [Fact]
    public async Task A_full_outbound_queue_fails_pending_requests_and_breaks_the_link()
    {
        var stream = new BlockingWriteStream();
        await using var client = new SimClient(_ => Task.FromResult<Stream>(stream));
        client.RetryAfter = TimeSpan.FromMinutes(1);
        var lost = 0;
        client.Lost += () => Interlocked.Increment(ref lost);
        client.Start();
        await Until(() => client.IsConnected);

        var pending = new List<Task<string?>> { client.EnterAsync(Enter(100)) };
        await stream.WriteStarted.Task.WaitAsync(Patience);

        var oversizedCandidates = Enumerable.Repeat(new SimTetherCandidate(1, 1), ushort.MaxValue).ToArray();
        Assert.Throws<ArgumentException>(() => client.SetTetherPolicy(new(7, 0, 0, oversizedCandidates)));
        Assert.True(client.IsConnected);

        for (var i = 0; i < SimClient.OutboundQueueCapacity; i++)
            pending.Add(client.EnterAsync(Enter((ulong)(101 + i))));
        Assert.All(pending, request => Assert.False(request.IsCompleted));

        var overflow = client.EnterAsync(Enter((ulong)(101 + SimClient.OutboundQueueCapacity)));
        Assert.True(overflow.IsCompleted);
        var overload = await Assert.ThrowsAsync<SimUnavailableException>(() => Within(overflow));
        Assert.Contains("outbound queue is full", overload.Message);
        client.Bind(200); // one-way sends stay quiet after the link is marked unavailable
        await Assert.ThrowsAsync<SimUnavailableException>(() => Within(client.EnterAsync(Enter(200))));
        foreach (var request in pending)
            await Assert.ThrowsAsync<SimUnavailableException>(() => Within(request));

        await Until(() => !client.IsConnected && Volatile.Read(ref lost) == 1);
    }

    [Fact]
    public async Task A_one_way_send_overflow_breaks_the_link_without_throwing()
    {
        var stream = new BlockingWriteStream();
        await using var client = new SimClient(_ => Task.FromResult<Stream>(stream));
        client.RetryAfter = TimeSpan.FromMinutes(1);
        var lost = 0;
        client.Lost += () => Interlocked.Increment(ref lost);
        client.Start();
        await Until(() => client.IsConnected);

        client.Bind(200);
        await stream.WriteStarted.Task.WaitAsync(Patience);
        for (var i = 0; i < SimClient.OutboundQueueCapacity; i++)
            client.Bind((ulong)(201 + i));

        var error = Record.Exception(() => client.Bind((ulong)(201 + SimClient.OutboundQueueCapacity)));

        Assert.Null(error);
        await Until(() => !client.IsConnected && Volatile.Read(ref lost) == 1);
    }

    private sealed class BlockingWriteStream : Stream
    {
        private readonly byte[] _helloAck = SimCodec.Encode(new SimHelloAck(SimCodec.ProtocolVersion, 4));
        private int _helloAckOffset;
        private int _writeCount;

        public TaskCompletionSource WriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_helloAckOffset < _helloAck.Length)
            {
                var count = Math.Min(buffer.Length, _helloAck.Length - _helloAckOffset);
                _helloAck.AsMemory(_helloAckOffset, count).CopyTo(buffer);
                _helloAckOffset += count;
                return count;
            }
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _writeCount) == 1)
                return;
            WriteStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Flush() { }
    }
}

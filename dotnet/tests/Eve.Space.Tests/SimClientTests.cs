namespace Eve.Space.Tests;

public class SimClientTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    private static SimShip Ship() => new(9988400001939, 140000342, 601, 237, 6, "Ibis", 140000342, 1000006, 0, 0, 0, 47, 1163000, 339.25, 4.0185, 3, 860000, 29700, 660, 28, 4, 149.5, 55.25, []);

    private static SimEnter Enter(ulong session = 7) => new(session, 30100032, EnterHow.Undock, 60015249, Ship());

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

    private static async Task<(FakeSimulation Sim, SimClient Client)> ConnectedAsync(Func<SimMessage, IEnumerable<SimMessage>>? reply = null)
    {
        var sim = new FakeSimulation { Reply = reply ?? (_ => []) };
        sim.Listen();
        var client = SimClient.ToUnixSocket(sim.Path);
        client.RetryAfter = TimeSpan.FromMilliseconds(20);
        client.Start();
        await Until(() => client.IsConnected);
        return (sim, client);
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
    public async Task A_request_that_is_given_up_stops_waiting()
    {
        var (sim, client) = await ConnectedAsync();
        await using var _ = sim;
        await using var __ = client;
        using var give_up = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.EnterAsync(Enter(), give_up.Token));
        // the session can ask again: the abandoned request left nothing behind
        var again = client.EnterAsync(Enter());
        Assert.False(again.IsCompleted);
    }
}

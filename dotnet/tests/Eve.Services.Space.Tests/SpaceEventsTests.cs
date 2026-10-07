using Eve.Characters;
using Eve.Macho;
using Eve.Marshal;
using Eve.Space;

namespace Eve.Services.Space.Tests;

/// <summary>What the host tells the other domains (missions, insurance, kill records, corporations): an NPC was destroyed, a ship was lost, a pilot's affiliation changed.</summary>
public class SpaceEventsTests
{
    private static readonly double[] Place = [1000.5, -20.25, 300];
    private static readonly double[] Facing = [0, 1, 0];
    private static readonly SimWreckKind FrigateWreck = new(26506, "Caldari Frigate Wreck", 3117);

    private const int Ibis = 601;

    private static async Task<(Rig Rig, Created Character, ClientSession Session)> InSpaceAsync(Rig? shared = null, string name = "Space Tester", int account = 1, long sessionId = 1, int shipTypeId = Rig.Cockroach)
    {
        var rig = shared ?? await Rig.CreateAsync();
        var character = await rig.NewCharacterAsync(name, account, shipTypeId);
        var session = await rig.SessionAsync(character, sessionId);
        var oid = await rig.BindShipAsync(session);
        await rig.ShipCallAsync(session, "Undock", oid, new PyLong(session.GetInt("shipid")!.Value), PyBool.False);
        await rig.BindBeyonceAsync(session);
        Events.Of(session);
        return (rig, character, session);
    }

    private static async Task<SimSpawn> SpawnAsync(Rig rig, ClientSession session)
    {
        var report = await rig.Space.SpawnNpcsAsync(session, "Blood Visionary", 1, CancellationToken.None);
        Assert.Null(report.Error);
        return rig.Sim.Heard.OfType<SimSpawn>().Last();
    }

    private static SimDestroyed NpcDestroyed(SimSpawn spawn, long killerShip) =>
        new(1UL << 62, spawn.Ship.ShipId, 4_300_000_000_000_001, Place, Facing, 47, killerShip);

    private static SimDestroyed ShipDestroyed(ClientSession session, long shipId, long killerShip) =>
        new((ulong)session.SessionId, shipId, 0, Place, Facing, 47, killerShip, FrigateWreck);

    // ── an NPC was destroyed ─────────────────────────────────────────────────

    [PgFact]
    public async Task An_npc_destroyed_by_a_pilot_is_raised_with_the_pilots_character_what_it_was_and_where()
    {
        var (rig, c, session) = await InSpaceAsync();
        await using var _ = rig;
        var kills = new List<NpcKill>();
        rig.Space.NpcDestroyed += kills.Add;
        var spawn = await SpawnAsync(rig, session);

        rig.Sim.Say(NpcDestroyed(spawn, c.ShipId));
        await Rig.UntilAsync(() => kills.Count == 1);

        var kill = Assert.Single(kills);
        Assert.Equal((c.CharacterId, spawn.Ship.TypeId, (long)session.GetInt("solarsystemid")!.Value, spawn.Ship.ShipId), (kill.FinalBlowCharacterId, kill.NpcTypeId, (long)kill.SolarSystemId, kill.ShipId));
        Assert.Equal("Blood Visionary", kill.Profile.Name);
    }

    [PgFact]
    public async Task An_npc_nobody_dealt_the_last_blow_to_is_raised_with_no_character_and_a_handler_that_fails_does_not_stop_the_others_or_the_bounty()
    {
        var (rig, c, session) = await InSpaceAsync();
        await using var _ = rig;
        var kills = new List<NpcKill>();
        rig.Space.NpcDestroyed += _ => throw new InvalidOperationException("a handler that fails");
        rig.Space.NpcDestroyed += kills.Add;
        var first = await SpawnAsync(rig, session);
        var second = await SpawnAsync(rig, session);

        rig.Sim.Say(NpcDestroyed(first, 0));
        // the last blow was another NPC's
        rig.Sim.Say(NpcDestroyed(second, first.Ship.ShipId));
        await Rig.UntilAsync(() => kills.Count == 2);

        Assert.All(kills, kill => Assert.Equal(0, kill.FinalBlowCharacterId));
        Assert.Contains(rig.Log, line => line.Contains("a handler of NpcDestroyed failed: a handler that fails"));
        // a ship the host did not spawn is not an NPC of ours
        rig.Sim.Say(new SimDestroyed(1UL << 62, 4_200_000_000_000_001, 0, Place, Facing, 47, c.ShipId));
        await Task.Delay(300);
        Assert.Equal(2, kills.Count);
    }

    [PgFact]
    public async Task The_kill_is_raised_after_the_bounty_was_recorded()
    {
        var (rig, c, session) = await InSpaceAsync();
        await using var _ = rig;
        var seen = new List<int>();
        rig.Space.NpcDestroyed += _ => seen.Add(rig.Bounties.PendingAsync().Result.Count);
        var spawn = await SpawnAsync(rig, session);

        rig.Sim.Say(NpcDestroyed(spawn, c.ShipId));
        await Rig.UntilAsync(() => seen.Count == 1);

        Assert.Equal([1], seen);
    }

    // ── a ship was lost ──────────────────────────────────────────────────────

    [PgFact]
    public async Task A_ship_destroyed_by_an_npc_is_raised_as_lost_with_its_type_and_the_cause()
    {
        var (rig, c, session) = await InSpaceAsync(shipTypeId: Ibis);
        await using var _ = rig;
        var lost = new List<ShipLoss>();
        rig.Space.ShipLost += lost.Add;
        var spawn = await SpawnAsync(rig, session);
        var type = (await rig.Items.GetAsync(c.ShipId))!.TypeId;

        rig.Sim.Say(ShipDestroyed(session, c.ShipId, spawn.Ship.ShipId));
        await Rig.UntilAsync(() => lost.Count == 1);

        Assert.Equal(new ShipLoss(c.CharacterId, c.ShipId, type, ShipLossCause.Npc, (int)session.GetInt("solarsystemid")!.Value, 0), Assert.Single(lost));
    }

    [PgFact]
    public async Task A_ship_nobody_is_known_to_have_destroyed_is_lost_for_an_unknown_cause_and_self_destruction_says_so()
    {
        var (rig, c, session) = await InSpaceAsync(shipTypeId: Ibis);
        await using var _ = rig;
        var lost = new List<ShipLoss>();
        rig.Space.ShipLost += lost.Add;

        var asked = rig.Space.SelfDestructAsync(session, CancellationToken.None);
        await Rig.UntilAsync(() => rig.Sim.Heard.OfType<SimDestroy>().Any());
        rig.Sim.Say(ShipDestroyed(session, c.ShipId, 0));
        await asked;

        Assert.Equal(ShipLossCause.SelfDestruct, Assert.Single(lost).Cause);

        // the capsule the pilot flies now is destroyed too: a capsule is not a ship that is lost
        rig.Sim.Say(ShipDestroyed(session, session.GetInt("shipid")!.Value, 0));
        await Task.Delay(500);
        Assert.Single(lost);
    }

    [PgFact]
    public async Task A_ship_destroyed_by_another_pilot_names_the_killers_character_and_a_handler_that_fails_does_not_stop_the_loss_being_told()
    {
        var (rig, victim, victimSession) = await InSpaceAsync(shipTypeId: Ibis);
        await using var _ = rig;
        var (_, killer, killerSession) = await InSpaceAsync(rig, "Killer Pilot", 2, 2);
        var lost = new List<ShipLoss>();
        rig.Space.ShipLost += _ => throw new InvalidOperationException("an insurance that failed");
        rig.Space.ShipLost += lost.Add;

        rig.Sim.Say(ShipDestroyed(victimSession, victim.ShipId, killer.ShipId));
        await Rig.UntilAsync(() => lost.Count == 1);

        var loss = Assert.Single(lost);
        Assert.Equal((victim.CharacterId, ShipLossCause.Player, killer.CharacterId), (loss.CharacterId, loss.Cause, loss.KillerCharacterId));
        Assert.Contains(rig.Log, line => line.Contains("a handler of ShipLost failed: an insurance that failed"));
        // and the client was still told: the pilot is in a capsule
        Assert.Contains(Events.Of(victimSession, clear: false), e => e.Name == "OnItemChange");
    }

    // ── a pilot's affiliation changed ────────────────────────────────────────

    [PgFact]
    public async Task A_pilot_whose_corporation_changed_has_its_ship_given_to_the_simulation_again_with_the_new_affiliation()
    {
        var (rig, c, session) = await InSpaceAsync();
        await using var _ = rig;
        var heard = rig.Sim.Heard.Count;

        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await rig.Characters.SetAffiliationAsync(connection, transaction, c.CharacterId, 98_000_001, 99_000_001, null);
            await transaction.CommitAsync();
        }
        await rig.Space.AffiliationChangedAsync(c.CharacterId);

        var changed = Assert.Single(rig.Sim.Heard.Skip(heard).OfType<SimShipChanged>());
        Assert.Equal(((ulong)session.SessionId, c.ShipId, 98_000_001, 99_000_001), (changed.SessionId, changed.Ship.ShipId, changed.Ship.CorporationId, changed.Ship.AllianceId));
    }

    [PgFact]
    public async Task A_character_that_does_not_fly_a_ship_in_space_is_nothing_to_the_simulation()
    {
        var rig = await Rig.CreateAsync();
        await using var _ = rig;
        var docked = await rig.NewCharacterAsync("Docked One", 2);
        await rig.SessionAsync(docked, 2);
        var heard = rig.Sim.Heard.Count;

        await rig.Space.AffiliationChangedAsync(docked.CharacterId);
        await rig.Space.AffiliationChangedAsync(123456);

        Assert.Empty(rig.Sim.Heard.Skip(heard));
    }

    // ── the same things on the shared events bus ─────────────────────────────

    private static async Task<List<(string Topic, string Json)>> AnnouncedAsync(Rig rig, int count)
    {
        for (var i = 0; i < 400; i++)
        {
            await rig.Bus!.PumpAsync();
            lock (rig.Announced)
            {
                if (rig.Announced.Count >= count)
                {
                    return [.. rig.Announced];
                }
            }
            await Task.Delay(10);
        }
        throw new Xunit.Sdk.XunitException("the event never came");
    }

    [PgFact]
    public async Task A_kill_is_announced_on_the_events_bus_with_the_pilot_the_type_and_the_place()
    {
        var (rig, c, session) = await InSpaceAsync();
        await using var _ = rig;
        await rig.UseBusAsync();
        var spawn = await SpawnAsync(rig, session);
        var system = (long)session.GetInt("solarsystemid")!.Value;

        rig.Sim.Say(NpcDestroyed(spawn, c.ShipId));
        var (topic, json) = (await AnnouncedAsync(rig, 1))[0];

        Assert.Equal("npc.killed", topic);
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(
            (c.CharacterId, (long)spawn.Ship.TypeId, system, spawn.Ship.ShipId),
            (root.GetProperty("characterId").GetInt64(), root.GetProperty("typeId").GetInt64(), root.GetProperty("solarSystemId").GetInt64(), root.GetProperty("shipId").GetInt64()));
        Assert.False(string.IsNullOrEmpty(root.GetProperty("profile").GetString()));
    }

    [PgFact]
    public async Task A_lost_ship_is_announced_on_the_events_bus_with_its_cause()
    {
        var (rig, c, session) = await InSpaceAsync(shipTypeId: Ibis);
        await using var _ = rig;
        await rig.UseBusAsync();
        var spawn = await SpawnAsync(rig, session);
        var type = (await rig.Items.GetAsync(c.ShipId))!.TypeId;

        rig.Sim.Say(ShipDestroyed(session, c.ShipId, spawn.Ship.ShipId));
        var (topic, json) = (await AnnouncedAsync(rig, 1))[0];

        Assert.Equal("ship.lost", topic);
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal((c.CharacterId, c.ShipId, (long)type, "Npc"), (root.GetProperty("characterId").GetInt64(), root.GetProperty("shipId").GetInt64(), root.GetProperty("shipTypeId").GetInt64(), root.GetProperty("cause").GetString()));
    }

    [Fact]
    public async Task Transition_runtime_persists_before_simulation_session_and_notification()
    {
        using var timers = new Eve.Scheduler.LocalTimers();
        var runtime = new Eve.Services.Space.TransitionRuntime(timers);
        var order = new List<string>();

        var result = await runtime.ExecuteAsync(
            _ =>
            {
                order.Add("persist");
                return Task.FromResult(true);
            },
            committed => committed,
            (_, _) =>
            {
                order.Add("simulation");
                return Task.CompletedTask;
            },
            (_, _) =>
            {
                order.Add("session");
                return Task.FromResult(true);
            },
            (_, _) =>
            {
                order.Add("notification");
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.True(result);
        Assert.Equal(new[] { "persist", "simulation", "session", "notification" }, order);
    }

    [Fact]
    public async Task Transition_runtime_does_not_apply_side_effects_when_persistence_did_not_commit()
    {
        using var timers = new Eve.Scheduler.LocalTimers();
        var runtime = new Eve.Services.Space.TransitionRuntime(timers);
        var order = new List<string>();

        var result = await runtime.ExecuteAsync(
            _ =>
            {
                order.Add("persist");
                return Task.FromResult(false);
            },
            committed => committed,
            (_, _) =>
            {
                order.Add("simulation");
                return Task.CompletedTask;
            },
            (_, _) =>
            {
                order.Add("session");
                return Task.FromResult(true);
            },
            (_, _) =>
            {
                order.Add("notification");
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.False(result);
        Assert.Equal(new[] { "persist" }, order);
    }

    [PgFact]
    public async Task Transition_runtime_session_refusal_suppresses_notification_and_wakes_the_event_bus()
    {
        var (rig, _, _) = await InSpaceAsync();
        await using var rigCleanup = rig;

        var suffix = Guid.NewGuid().ToString("N")[..12];
        var schedulerSchema = $"sc_transition_{suffix}";
        var eventsSchema = $"ev_transition_{suffix}";
        Eve.Events.EventBus? events = null;

        try
        {
            await Eve.Scheduler.DurableScheduler.MigrateAsync(rig.Source, schedulerSchema);
            await Eve.Events.EventBus.MigrateAsync(rig.Source, eventsSchema);
            var scheduler = new Eve.Scheduler.DurableScheduler(rig.Source, () => rig.Now, schema: schedulerSchema);
            var bus = new Eve.Events.EventBus(
                rig.Source,
                scheduler,
                new Eve.Events.EventBusOptions { PollMs = 60_000 },
                eventsSchema);
            events = bus;

            var delivered = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            bus.Subscribe("transition-runtime-test", ["transition.runtime"], (context, _) =>
            {
                delivered.TrySetResult(context.Event.Topic);
                return Task.CompletedTask;
            });
            await bus.PumpAsync();
            await bus.StartAsync();

            using var timers = new Eve.Scheduler.LocalTimers();
            var runtime = new Eve.Services.Space.TransitionRuntime(timers, bus);
            var order = new List<string>();
            var result = await runtime.ExecuteAsync(
                async token =>
                {
                    order.Add("persist");
                    await using var connection = await rig.Source.OpenConnectionAsync(token);
                    await using var transaction = await connection.BeginTransactionAsync(token);
                    await bus.PublishAsync(
                        connection,
                        transaction,
                        "transition.runtime",
                        new { transitionId = 1 },
                        "transition-runtime-test",
                        token);
                    await transaction.CommitAsync(token);
                    return true;
                },
                committed => committed,
                (_, _) =>
                {
                    order.Add("simulation");
                    return Task.CompletedTask;
                },
                (_, _) =>
                {
                    order.Add("session");
                    return Task.FromResult(false);
                },
                (_, _) =>
                {
                    order.Add("notification");
                    return Task.CompletedTask;
                },
                CancellationToken.None);

            Assert.True(result);
            Assert.Equal(new[] { "persist", "simulation", "session" }, order);
            Assert.Equal("transition.runtime", await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            if (events is not null)
                await events.StopAsync();

            await using (var dropEvents = rig.Source.CreateCommand($"DROP SCHEMA IF EXISTS {eventsSchema} CASCADE"))
                await dropEvents.ExecuteNonQueryAsync();
            await using (var dropScheduler = rig.Source.CreateCommand($"DROP SCHEMA IF EXISTS {schedulerSchema} CASCADE"))
                await dropScheduler.ExecuteNonQueryAsync();
        }
    }
}

using Eve.Data;
using Eve.Events;
using Eve.Scheduler;
using Eve.Services.Space.Entosis;
using Npgsql;

namespace Eve.Services.Space.Tests;

/// <summary>
/// a capture is moved at each settled cycle and completes as a job, a defender pauses it, a link's end stops it, and a restart reads the stored progress. An owned
/// </summary>
public sealed class EntosisCaptureServiceTests
{
    private const long Hub = 1_040_000_000_902;
    private const int System = 30_100_032;
    private const long Start = 1_700_000_000_000;
    private const long Character = 140_000_342;
    private const long Corporation = 98_000_010;

    private sealed class Clock
    {
        public long Now = Start;
    }

    private sealed class Holder
    {
        public EntosisHubReading? Value { get; set; }
    }

    private sealed class Harness : IAsyncDisposable
    {
        public required NpgsqlDataSource Source { get; init; }
        public required string Schema { get; init; }
        public required string SchedulerSchema { get; init; }
        public required Clock Clock { get; init; }
        public required Holder Reading { get; init; }
        public required DurableScheduler Scheduler { get; init; }
        public required EntosisCaptureService Service { get; init; }
        public required string EventsSchema { get; init; }
        public required EventBus Bus { get; init; }
        public required List<object> Notices { get; init; }

        public async ValueTask DisposeAsync()
        {
            await using (var command = Source.CreateCommand($"DROP SCHEMA IF EXISTS \"{Schema}\" CASCADE; DROP SCHEMA IF EXISTS \"{SchedulerSchema}\" CASCADE; DROP SCHEMA IF EXISTS \"{EventsSchema}\" CASCADE"))
            {
                await command.ExecuteNonQueryAsync();
            }
            await Source.DisposeAsync();
        }
    }

    private static async Task<Harness> CreateAsync(
        EntosisHubReading reading, Func<EntosisCapturedHub, CancellationToken, Task<EntosisHandover>>? handover = null,
        Func<int, CancellationToken, Task<IReadOnlyList<int>>>? constellation = null, Func<int, CancellationToken, Task<int>>? constellationOf = null,
        Func<EntosisDestroyedHub, CancellationToken, Task<bool>>? destroy = null)
    {
        var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var schema = $"ent_{suffix}";
        var schedulerSchema = $"sch_{suffix}";
        var eventsSchema = $"evt_{suffix}";
        await EntosisCaptureService.MigrateAsync(source, schema);
        await DurableScheduler.MigrateAsync(source, schedulerSchema);
        await EventBus.MigrateAsync(source, eventsSchema);
        var clock = new Clock();
        var holder = new Holder { Value = reading };
        var scheduler = new DurableScheduler(source, () => clock.Now, null, schedulerSchema);

        var bus = new EventBus(source, scheduler, null, eventsSchema, _ => { });
        var notices = new List<object>();
        bus.Subscribe("test.entosis-notices",
            [EntosisCaptureService.CaptureStartedTopic, EntosisCaptureService.ReinforcedTopic, EntosisCaptureService.StageStartedTopic, EntosisCaptureService.HubDestroyedTopic],
            (context, _) =>
            {
                notices.Add(context.Event.Topic switch
                {
                    var topic when topic == EntosisCaptureService.CaptureStartedTopic => context.Event.Payload<EntosisCaptureStarted>()!,
                    var topic when topic == EntosisCaptureService.StageStartedTopic => context.Event.Payload<CommandStageStarted>()!,
                    var topic when topic == EntosisCaptureService.HubDestroyedTopic => context.Event.Payload<HubDestroyed>()!,
                    _ => context.Event.Payload<EntosisHubReinforced>()!,
                });
                return Task.CompletedTask;
            });
        await bus.PumpAsync();
        var service = new EntosisCaptureService(
            source, scheduler, () => clock.Now, (_, _) => Task.FromResult<EntosisHubReading?>(holder.Value), handover, schema: schema, events: bus,
            constellationSystems: constellation, constellationOf: constellationOf, destroyHub: destroy);
        service.Register();
        return new Harness
        {
            Source = source, Schema = schema, SchedulerSchema = schedulerSchema, EventsSchema = eventsSchema, Clock = clock, Reading = holder,
            Scheduler = scheduler, Service = service, Bus = bus, Notices = notices,
        };
    }

    private static EntosisHubReading Neutral() => new(Hub, System, null, 1);

    private static EntosisHubReading Owned(long alliance = 7) => new(Hub, System, alliance, 1, 22);

    private static Task<EntosisCaptureService.SettleResult> SettleAsync(Harness harness, long module, long alliance, long hub = Hub) =>
        harness.Service.SettleCycleAsync(module, Character, Corporation, alliance, hub, System, CancellationToken.None);

    [PgFact]
    public async Task A_link_warms_up_on_its_first_cycle_and_then_scores_and_completes_as_a_durable_job()
    {
        await using var harness = await CreateAsync(Neutral());

        Assert.True((await SettleAsync(harness, 1, 5)).Accepted, "the warm-up cycle is accepted");
        Assert.Equal(0, (await harness.Service.ReadAsync(Hub, CancellationToken.None))!.Progress);

        harness.Clock.Now = Start + EntosisRules.CycleMs;
        Assert.True((await SettleAsync(harness, 1, 5)).Accepted);
        var moved = await harness.Service.ReadAsync(Hub, CancellationToken.None);
        Assert.Equal(EntosisRules.CycleMs / EntosisRules.BaseCaptureMs, moved!.Progress, 9);
        Assert.Equal(EntosisPhase.Capturing, moved.Phase);

        // Recreate both the service and scheduler over PostgreSQL to model a process restart.
        await using var restartedScheduler = new DurableScheduler(harness.Source, () => harness.Clock.Now, null, harness.SchedulerSchema);
        var restarted = new EntosisCaptureService(
            harness.Source, restartedScheduler, () => harness.Clock.Now,
            (_, _) => Task.FromResult<EntosisHubReading?>(Neutral()), schema: harness.Schema);
        restarted.Register();
        Assert.Equal(moved, await restarted.ReadAsync(Hub, CancellationToken.None));

        // The capture completion job is also recovered by the new scheduler.
        var job = await restartedScheduler.FindAsync(EntosisCaptureService.CaptureDueKind, Hub.ToString());
        Assert.NotNull(job);
        Assert.InRange(job!.RunAtMs, Start + (long)EntosisRules.BaseCaptureMs - 2, Start + (long)EntosisRules.BaseCaptureMs + 2);

        harness.Clock.Now = job.RunAtMs;
        await restartedScheduler.RunDueAsync();
        var captured = await restarted.ReadAsync(Hub, CancellationToken.None);
        Assert.Equal(EntosisPhase.Captured, captured!.Phase);
        Assert.Equal(1, captured.Progress);

        // A captured hub refuses the next cycle by name.
        var refused = await SettleAsync(harness, 1, 5);
        Assert.False(refused.Accepted);
        Assert.Equal("ENTOSIS_CAPTURE_COMPLETE", refused.Reason);
    }

    [PgFact]
    public async Task A_defender_with_the_attacker_pauses_the_capture_and_a_replayed_cycle_changes_nothing()
    {
        await using var harness = await CreateAsync(Owned());

        await SettleAsync(harness, 1, 5);
        harness.Clock.Now = Start + EntosisRules.CycleMs;
        await SettleAsync(harness, 1, 5);
        var before = (await harness.Service.ReadAsync(Hub, CancellationToken.None))!.Progress;
        Assert.True(before > 0);

        Assert.True((await SettleAsync(harness, 2, 7)).Accepted);
        harness.Clock.Now = Start + 2 * EntosisRules.CycleMs;
        await SettleAsync(harness, 1, 5);
        await SettleAsync(harness, 2, 7);
        harness.Clock.Now = Start + 3 * EntosisRules.CycleMs;
        await SettleAsync(harness, 1, 5);
        await SettleAsync(harness, 2, 7);
        var paused = await harness.Service.ReadAsync(Hub, CancellationToken.None);
        Assert.Equal(before, paused!.Progress, 9);
        Assert.Equal(EntosisPhase.Capturing, paused.Phase);

        // the same cycle settled again at the same time changes nothing
        Assert.True((await SettleAsync(harness, 2, 7)).Accepted);
        Assert.Equal(paused.Progress, (await harness.Service.ReadAsync(Hub, CancellationToken.None))!.Progress, 12);
    }

    [PgFact]
    public async Task A_link_that_ends_moves_the_capture_to_its_end_and_stops_counting()
    {
        await using var harness = await CreateAsync(Neutral());
        await SettleAsync(harness, 1, 5);
        harness.Clock.Now = Start + EntosisRules.CycleMs;
        await SettleAsync(harness, 1, 5);

        harness.Clock.Now = Start + EntosisRules.CycleMs + 100_000;
        await harness.Service.LinkEndedAsync(1, CancellationToken.None);

        var ended = await harness.Service.ReadAsync(Hub, CancellationToken.None);
        Assert.Equal((EntosisRules.CycleMs + 100_000) / EntosisRules.BaseCaptureMs, ended!.Progress, 9);
        Assert.Empty(await harness.Service.LinksAsync(Hub, CancellationToken.None));
        Assert.Null(await harness.Scheduler.FindAsync(EntosisCaptureService.CaptureDueKind, Hub.ToString()));

        // nothing counts while no link is there
        harness.Clock.Now = Start + 3 * EntosisRules.CycleMs;
        await harness.Service.LinkEndedAsync(1, CancellationToken.None);
        Assert.Equal(ended.Progress, (await harness.Service.ReadAsync(Hub, CancellationToken.None))!.Progress, 12);
    }

    [PgFact]
    public async Task A_cycle_at_a_system_whose_hub_is_another_structure_is_refused_by_name_and_stores_nothing()
    {
        await using var harness = await CreateAsync(Neutral());

        var refused = await SettleAsync(harness, 1, 5, hub: Hub + 1);

        Assert.False(refused.Accepted);
        Assert.Equal("ENTOSIS_TARGET_NOT_SOV_HUB", refused.Reason);
        Assert.Null(await harness.Service.ReadAsync(Hub, CancellationToken.None));
    }

    /// <summary>The attacker warms up, scores for a cycle, and its completion job runs: the capture is complete.</summary>
    private static async Task CompleteCaptureAsync(Harness harness)
    {
        await SettleAsync(harness, 1, 5);
        harness.Clock.Now = Start + EntosisRules.CycleMs;
        await SettleAsync(harness, 1, 5);
        var job = await harness.Scheduler.FindAsync(EntosisCaptureService.CaptureDueKind, Hub.ToString());
        harness.Clock.Now = job!.RunAtMs;
        await harness.Scheduler.RunDueAsync();
    }

    [PgFact]
    public async Task A_captured_neutral_hub_is_handed_over_to_its_capturer_by_the_sovereignty_claim()
    {
        var claims = new List<EntosisCapturedHub>();
        await using var harness = await CreateAsync(Neutral(), (captured, _) =>
        {
            claims.Add(captured);
            return Task.FromResult(EntosisHandover.Claimed);
        });

        await CompleteCaptureAsync(harness);

        var claimed = await harness.Service.ReadAsync(Hub, CancellationToken.None);
        Assert.Equal((EntosisPhase.Idle, 0d), (claimed!.Phase, claimed.Progress));
        Assert.Equal("claimed", await harness.Service.HandoverAsync(Hub, CancellationToken.None));
        Assert.Empty(await harness.Service.LinksAsync(Hub, CancellationToken.None));
        var claim = Assert.Single(claims);
        Assert.Equal((Hub, System, Corporation, 5L), (claim.HubId, claim.SolarSystemId, claim.CorporationId, claim.AllianceId));
    }

    [PgFact]
    public async Task A_neutral_hub_another_alliance_claimed_first_is_recorded_as_superseded()
    {
        await using var harness = await CreateAsync(Neutral(), (_, _) => Task.FromResult(EntosisHandover.Superseded));

        await CompleteCaptureAsync(harness);

        Assert.Equal("superseded", await harness.Service.HandoverAsync(Hub, CancellationToken.None));
        Assert.Equal(EntosisPhase.Idle, (await harness.Service.ReadAsync(Hub, CancellationToken.None))!.Phase);
    }

    [PgFact]
    public async Task An_owned_hub_completes_into_reinforcement_with_its_timer_and_the_owner_is_told_once()
    {
        var calls = 0;
        await using var harness = await CreateAsync(Owned(), (_, _) =>
        {
            calls++;
            return Task.FromResult(EntosisHandover.Claimed);
        });

        await CompleteCaptureAsync(harness);
        var completedMs = harness.Clock.Now;

        var reinforced = await harness.Service.ReadAsync(Hub, CancellationToken.None);
        Assert.Equal(EntosisPhase.Reinforced, reinforced!.Phase);
        Assert.Equal("owned", await harness.Service.HandoverAsync(Hub, CancellationToken.None));
        Assert.Equal(0, calls);

        await harness.Bus.PumpAsync();
        var started = Assert.Single(harness.Notices.OfType<EntosisCaptureStarted>());
        Assert.Equal((Hub, System, 7L, 1L), (started.HubId, started.SolarSystemId, started.DefenderAllianceId, started.ModuleId));
        var notice = Assert.Single(harness.Notices.OfType<EntosisHubReinforced>());
        Assert.Equal((Hub, System, 7L), (notice.HubId, notice.SolarSystemId, notice.DefenderAllianceId));
        Assert.True(notice.ReinforcementExitMs >= completedMs + EntosisWindow.ReinforcementMs, "not before 48 hours");
        Assert.True(EntosisWindow.IsOpen(22, 1, notice.ReinforcementExitMs), "inside the owner's window");

        // The reinforced hub refuses cycles before its exit, and replayed notices stay idempotent.
        Assert.Equal("SOVEREIGNTY_HUB_ALREADY_REINFORCED", (await SettleAsync(harness, 1, 5)).Reason);
        await harness.Bus.PumpAsync();
        Assert.Equal(2, harness.Notices.Count);

        // The reinforcement exit is stored as a durable job and resumes through a new scheduler.
        var campaignJob = await harness.Scheduler.FindAsync(EntosisCaptureService.CampaignStartKind, Hub.ToString());
        Assert.NotNull(campaignJob);
        Assert.Equal(notice.ReinforcementExitMs, campaignJob!.RunAtMs);
        await using var restartedScheduler = new DurableScheduler(harness.Source, () => harness.Clock.Now, null, harness.SchedulerSchema);
        var restarted = new EntosisCaptureService(
            harness.Source, restartedScheduler, () => harness.Clock.Now,
            (_, _) => Task.FromResult<EntosisHubReading?>(Owned()), schema: harness.Schema);
        restarted.Register();

        harness.Clock.Now = campaignJob.RunAtMs;
        Assert.Equal(1, await restartedScheduler.RunDueAsync());
        Assert.Equal("active", await ScalarTextAsync(harness, "SELECT phase FROM {schema}.campaigns"));
        Assert.Equal("4", await ScalarTextAsync(harness, "SELECT COUNT(*) FROM {schema}.nodes"));
        Assert.Equal("reinforced", await ScalarTextAsync(harness, "SELECT phase FROM {schema}.hubs"));
    }

    [PgFact]
    public async Task A_cycle_outside_the_owners_vulnerability_window_is_refused_by_name_and_stores_nothing()
    {

        await using var harness = await CreateAsync(new(Hub, System, 7, 1, 12));

        var refused = await SettleAsync(harness, 1, 5);

        Assert.False(refused.Accepted);
        Assert.Equal("SOVEREIGNTY_TARGET_NOT_VULNERABLE", refused.Reason);
        Assert.Null(await harness.Service.ReadAsync(Hub, CancellationToken.None));
        await harness.Bus.PumpAsync();
        Assert.Empty(harness.Notices);
    }

    [PgFact]
    public async Task A_neutral_capture_announces_nothing_to_an_owner()
    {
        await using var harness = await CreateAsync(Neutral(), (_, _) => Task.FromResult(EntosisHandover.Claimed));

        await CompleteCaptureAsync(harness);
        await harness.Bus.PumpAsync();

        Assert.Empty(harness.Notices);
    }

    [PgFact]
    public async Task A_failed_handover_stays_pending_and_a_retry_job_writes_it_later()
    {
        var attempts = 0;
        await using var harness = await CreateAsync(Neutral(), (_, _) =>
        {
            attempts++;
            if (attempts == 1)
            {
                throw new InvalidOperationException("the sovereignty write was refused");
            }
            return Task.FromResult(EntosisHandover.Claimed);
        });

        await CompleteCaptureAsync(harness);

        Assert.Equal("pending", await harness.Service.HandoverAsync(Hub, CancellationToken.None));
        var retry = await harness.Scheduler.FindAsync(EntosisCaptureService.HandoverDueKind, Hub.ToString());
        Assert.NotNull(retry);
        Assert.Equal(harness.Clock.Now + EntosisCaptureService.HandoverRetryMs, retry!.RunAtMs);

        harness.Clock.Now = retry.RunAtMs;
        await harness.Scheduler.RunDueAsync();

        Assert.Equal(2, attempts);
        Assert.Equal("claimed", await harness.Service.HandoverAsync(Hub, CancellationToken.None));
    }

    /// <summary>The start of the day of <see cref="Start"/> (UTC): the test's clock is 22:13 that day.</summary>
    private const long Day = Start / EntosisWindow.DayMs * EntosisWindow.DayMs;

    [PgFact]
    public async Task An_owners_custom_window_takes_effect_after_the_next_window_and_the_default_applies_until_then()
    {
        await using var harness = await CreateAsync(Owned());
        var answer = await harness.Service.SetHubWindowAsync(Hub, System, new EntosisWindowSetting(10, 2), CancellationToken.None);
        Assert.True(answer.Accepted, answer.Reason);
        Assert.Equal(Day + 31 * EntosisWindow.HourMs, answer.EffectiveFromMs);

        // until then the default window (13:00 to 07:00, centred on 22:00) still applies: 02:00 is inside it
        harness.Clock.Now = Day + 26 * EntosisWindow.HourMs;
        Assert.True((await SettleAsync(harness, 1, 5)).Accepted, "the default window is still in force");
    }

    [PgFact]
    public async Task A_custom_window_refuses_cycles_outside_it_once_in_force_and_stores_nothing_for_them()
    {
        await using var harness = await CreateAsync(Owned());
        Assert.True((await harness.Service.SetHubWindowAsync(Hub, System, new EntosisWindowSetting(10, 2), CancellationToken.None)).Accepted);

        harness.Clock.Now = Day + 50 * EntosisWindow.HourMs;
        Assert.Equal("SOVEREIGNTY_TARGET_NOT_VULNERABLE", (await SettleAsync(harness, 1, 5)).Reason);
        Assert.Null(await harness.Service.ReadAsync(Hub, CancellationToken.None));

        harness.Clock.Now = Day + 34 * EntosisWindow.HourMs;
        Assert.True((await SettleAsync(harness, 1, 5)).Accepted);
    }

    [PgFact]
    public async Task A_captured_hub_inherits_the_default_window_of_its_new_owner()
    {
        await using var harness = await CreateAsync(Neutral(), (_, _) => Task.FromResult(EntosisHandover.Claimed));
        Assert.True((await harness.Service.SetAllianceWindowAsync(5, new EntosisWindowSetting(10, 2), CancellationToken.None)).Accepted);

        // the capture: a warm-up, a cycle, then the completion job, as the neutral capture tests run it
        Assert.True((await SettleAsync(harness, 1, 5)).Accepted);
        harness.Clock.Now = Start + EntosisRules.CycleMs;
        Assert.True((await SettleAsync(harness, 1, 5)).Accepted);
        var job = await harness.Scheduler.FindAsync(EntosisCaptureService.CaptureDueKind, Hub.ToString());
        harness.Clock.Now = job!.RunAtMs;
        await harness.Service.CaptureDueAsync(Hub, CancellationToken.None);
        Assert.Equal("claimed", await harness.Service.HandoverAsync(Hub, CancellationToken.None));

        await using var command = harness.Source.CreateCommand(
            $"SELECT centre_hour, length_hours FROM \"{harness.Schema}\".hub_windows WHERE hub_id = {Hub}");
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "the claimed hub has the alliance's window");
        Assert.Equal(10, reader.GetInt16(0));
        Assert.Equal(2, reader.GetInt16(1));
    }

    [PgFact]
    public async Task A_window_change_is_refused_for_a_neutral_hub_and_for_a_setting_out_of_range()
    {
        await using var neutral = await CreateAsync(Neutral());
        Assert.Equal("SOVEREIGNTY_HUB_NOT_OWNED", (await neutral.Service.SetHubWindowAsync(Hub, System, new EntosisWindowSetting(10, 2), CancellationToken.None)).Reason);
        await using var owned = await CreateAsync(Owned());
        Assert.Equal("SOVEREIGNTY_VULNERABILITY_WINDOW_INVALID", (await owned.Service.SetHubWindowAsync(Hub, System, new EntosisWindowSetting(24, 2), CancellationToken.None)).Reason);
    }

    /// <summary>A reinforced owned hub whose reinforcement ends at <paramref name="exitMs"/> (the state a completed owned capture leaves).</summary>
    private static async Task InsertReinforcedHubAsync(Harness harness, long exitMs)
    {
        await using var command = harness.Source.CreateCommand(
            $"INSERT INTO \"{harness.Schema}\".hubs (hub_id, solar_system_id, progress, checkpoint_ms, activity_defense_multiplier, defender_alliance_id, phase, "
            + $"prime_hour, reinforcement_exit_ms, handover) VALUES ({Hub}, {System}, 1, {Start}, 1, 7, 'reinforced', 22, {exitMs}, 'owned')");
        await command.ExecuteNonQueryAsync();
    }

    private static Func<int, CancellationToken, Task<IReadOnlyList<int>>> Constellation =>
        (_, _) => Task.FromResult<IReadOnlyList<int>>([System, System + 1, System + 2]);

    private static async Task<string?> ScalarTextAsync(Harness harness, string sql)
    {
        await using var command = harness.Source.CreateCommand(sql.Replace("{schema}", $"\"{harness.Schema}\""));
        return (await command.ExecuteScalarAsync())?.ToString();
    }

    /// <summary>Any live node of the stage (the rows are the stage's; the constellation spreads them over the systems).</summary>
    private static async Task<long> AnyNodeAsync(Harness harness) =>
        long.Parse((await ScalarTextAsync(harness, "SELECT node_id FROM {schema}.nodes ORDER BY node_id LIMIT 1"))!, global::System.Globalization.CultureInfo.InvariantCulture);

    private static Task<EntosisCaptureService.SettleResult> NodeSettleAsync(Harness harness, long node, long module, long alliance) =>
        harness.Service.SettleCycleAsync(module, Character, Corporation, alliance, node, System, CancellationToken.None);

    /// <summary>A node is captured by one side in two cycles: the warm-up, then a cycle of 5 minutes (the base time is 4 minutes at ADM 1).</summary>
    private static async Task CaptureNodeAsync(Harness harness, long node, long alliance)
    {
        var at = harness.Clock.Now;
        Assert.True((await NodeSettleAsync(harness, node, 1, alliance)).Accepted, "the warm-up");
        harness.Clock.Now = at + EntosisRules.CycleMs;
        Assert.True((await NodeSettleAsync(harness, node, 1, alliance)).Accepted, "the capturing cycle");
    }

    [PgFact]
    public async Task A_stage_starts_at_the_reinforcement_exit_with_four_nodes_in_the_constellation()
    {
        await using var harness = await CreateAsync(Owned(), constellation: Constellation);
        await InsertReinforcedHubAsync(harness, Start);
        harness.Clock.Now = Start;
        await harness.Service.CampaignStartAsync(Hub, CancellationToken.None);

        Assert.Equal("4", await ScalarTextAsync(harness, "SELECT COUNT(*) FROM {schema}.nodes"));
        Assert.Equal("active", await ScalarTextAsync(harness, "SELECT phase FROM {schema}.campaigns"));
        // the defender is told that the stage has started (V24.01 161 is written from this event)
        await harness.Bus.PumpAsync();
        Assert.Equal(7L, harness.Notices.OfType<CommandStageStarted>().Single().DefenderAllianceId);
        Assert.Equal("reinforced", await ScalarTextAsync(harness, "SELECT phase FROM {schema}.hubs"));
        Assert.NotNull(await harness.Scheduler.FindAsync(EntosisCaptureService.NodeSpawnKind, Hub.ToString()));
        // the stage is started once: a second start leaves it as it is
        await harness.Service.CampaignStartAsync(Hub, CancellationToken.None);
        Assert.Equal("4", await ScalarTextAsync(harness, "SELECT COUNT(*) FROM {schema}.nodes"));
    }

    [PgFact]
    public async Task An_attackers_node_capture_moves_the_tally_and_a_new_node_takes_its_place()
    {
        await using var harness = await CreateAsync(Owned(), constellation: Constellation);
        await InsertReinforcedHubAsync(harness, Start);
        harness.Clock.Now = Start;
        await harness.Service.CampaignStartAsync(Hub, CancellationToken.None);
        var node = await AnyNodeAsync(harness);

        harness.Clock.Now = Start + 600_000;
        await CaptureNodeAsync(harness, node, alliance: 5);

        Assert.Equal("0", await ScalarTextAsync(harness, $"SELECT COUNT(*) FROM {{schema}}.nodes WHERE node_id = {node}"));
        Assert.Equal("4", await ScalarTextAsync(harness, "SELECT COUNT(*) FROM {schema}.nodes"));
        Assert.Equal("1", await ScalarTextAsync(harness, "SELECT attacker_captures FROM {schema}.campaigns"));
        Assert.Equal("active", await ScalarTextAsync(harness, "SELECT phase FROM {schema}.campaigns"));
    }

    [PgFact]
    public async Task Nine_attacker_captures_destroy_the_hub_and_end_the_stage()
    {
        await using var harness = await CreateAsync(Owned(), constellation: Constellation);
        await InsertReinforcedHubAsync(harness, Start);
        harness.Clock.Now = Start;
        await harness.Service.CampaignStartAsync(Hub, CancellationToken.None);

        for (var capture = 0; capture < 9; capture++)
        {
            harness.Clock.Now = Start + 3_600_000L * (capture + 1);
            await CaptureNodeAsync(harness, await AnyNodeAsync(harness), alliance: 5);
        }

        Assert.Equal("destroyed", await ScalarTextAsync(harness, "SELECT phase FROM {schema}.campaigns"));
        Assert.Equal("destroyed", await ScalarTextAsync(harness, "SELECT phase FROM {schema}.hubs"));
        Assert.Equal("0", await ScalarTextAsync(harness, "SELECT COUNT(*) FROM {schema}.nodes"));
        // the stage is over: a node's cycle is refused, and the hub is refused by name
        var settled = await NodeSettleAsync(harness, 1, 1, 5);
        Assert.False(settled.Accepted);
    }

    [PgFact]
    public async Task Six_defender_captures_defend_the_hub_which_is_invulnerable_until_the_next_window()
    {
        await using var harness = await CreateAsync(Owned(), constellation: Constellation);
        await InsertReinforcedHubAsync(harness, Start);
        harness.Clock.Now = Start;
        await harness.Service.CampaignStartAsync(Hub, CancellationToken.None);

        for (var capture = 0; capture < 6; capture++)
        {
            harness.Clock.Now = Start + 3_600_000L * (capture + 1);
            await CaptureNodeAsync(harness, await AnyNodeAsync(harness), alliance: 7);
        }

        Assert.Equal("defended", await ScalarTextAsync(harness, "SELECT phase FROM {schema}.campaigns"));
        Assert.Equal("idle", await ScalarTextAsync(harness, "SELECT phase FROM {schema}.hubs"));

        var until = long.Parse((await ScalarTextAsync(harness, "SELECT invulnerable_until_ms FROM {schema}.hubs"))!, global::System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(Day + 37 * EntosisWindow.HourMs, until);
        harness.Clock.Now = Start + 3_600_000L * 7;
        Assert.Equal("SOVEREIGNTY_TARGET_NOT_VULNERABLE", (await SettleAsync(harness, 9, 5)).Reason);
    }

    [PgFact]
    public async Task A_destroyed_hub_is_lost_by_the_destruction_job_which_is_retried_until_the_records_write_it()
    {
        var attempts = 0;
        await using var harness = await CreateAsync(Owned(), constellation: Constellation, destroy: (hub, _) =>
        {
            Assert.Equal(Hub, hub.HubId);
            attempts++;
            if (attempts == 1)
            {
                throw new InvalidOperationException("the sovereignty table is busy");
            }
            return Task.FromResult(true);
        });
        await InsertReinforcedHubAsync(harness, Start);
        harness.Clock.Now = Start;
        await harness.Service.CampaignStartAsync(Hub, CancellationToken.None);
        for (var capture = 0; capture < 9; capture++)
        {
            harness.Clock.Now = Start + 3_600_000L * (capture + 1);
            await CaptureNodeAsync(harness, await AnyNodeAsync(harness), alliance: 5);
        }
        Assert.Equal("destroy-pending", await ScalarTextAsync(harness, "SELECT handover FROM {schema}.hubs"));
        Assert.NotNull(await harness.Scheduler.FindAsync(EntosisCaptureService.HubDestroyDueKind, Hub.ToString()));

        // the first write fails: the hub stays pending and the job is tried again
        await harness.Service.HubDestroyDueAsync(Hub, CancellationToken.None);
        Assert.Equal(1, attempts);
        Assert.Equal("destroy-pending", await ScalarTextAsync(harness, "SELECT handover FROM {schema}.hubs"));

        await harness.Service.HubDestroyDueAsync(Hub, CancellationToken.None);
        Assert.Equal(2, attempts);
        Assert.Equal("destroyed", await ScalarTextAsync(harness, "SELECT handover FROM {schema}.hubs"));
        await harness.Bus.PumpAsync();
        var destroyed = harness.Notices.OfType<HubDestroyed>().Single();
        Assert.Equal((System, 7L), (destroyed.SolarSystemId, destroyed.DefenderAllianceId));
        // once written, a repeat does nothing
        await harness.Service.HubDestroyDueAsync(Hub, CancellationToken.None);
        Assert.Equal(2, attempts);
    }
}

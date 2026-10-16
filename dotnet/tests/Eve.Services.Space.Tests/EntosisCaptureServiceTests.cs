using Eve.Data;
using Eve.Events;
using Eve.Scheduler;
using Eve.Services.Space.Entosis;
using Npgsql;

namespace Eve.Services.Space.Tests;

/// <summary>
/// ST-9 c, e, the durable capture (PostgreSQL, the real store and the real durable scheduler; the clock is the test's): the first cycle of a link is its warm-up,
/// a capture is moved at each settled cycle and completes as a job, a defender pauses it, a link's end stops it, and a restart reads the stored progress. An owned
/// hub is refused outside its owner's window, completes into a reinforcement, and the owner is told by events (ST-9 e).
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
        EntosisHubReading reading, Func<EntosisCapturedHub, CancellationToken, Task<EntosisHandover>>? handover = null)
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
        // the owner's notices: the capture's events are collected by a subscriber (started once the first pump has run)
        var bus = new EventBus(source, scheduler, null, eventsSchema, _ => { });
        var notices = new List<object>();
        bus.Subscribe("test.entosis-notices", [EntosisCaptureService.CaptureStartedTopic, EntosisCaptureService.ReinforcedTopic], (context, _) =>
        {
            notices.Add(context.Event.Topic == EntosisCaptureService.CaptureStartedTopic
                ? context.Event.Payload<EntosisCaptureStarted>()!
                : context.Event.Payload<EntosisHubReinforced>()!);
            return Task.CompletedTask;
        });
        await bus.PumpAsync();
        var service = new EntosisCaptureService(
            source, scheduler, () => clock.Now, (_, _) => Task.FromResult<EntosisHubReading?>(holder.Value), handover, schema: schema, events: bus);
        service.Register();
        return new Harness
        {
            Source = source, Schema = schema, SchedulerSchema = schedulerSchema, EventsSchema = eventsSchema, Clock = clock, Reading = holder,
            Scheduler = scheduler, Service = service, Bus = bus, Notices = notices,
        };
    }

    private static EntosisHubReading Neutral() => new(Hub, System, null, 1);

    /// <summary>An owned hub whose owner's prime hour is 22:00 UTC: its window at ADM 1 is 13:00 to 07:00, and the test's clock (22:13 UTC) is inside it.</summary>
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

        // restart-safe: a new service over the same tables reads the same progress
        var restarted = new EntosisCaptureService(harness.Source, harness.Scheduler, () => harness.Clock.Now, (_, _) => Task.FromResult<EntosisHubReading?>(Neutral()), schema: harness.Schema);
        Assert.Equal(moved, await restarted.ReadAsync(Hub, CancellationToken.None));

        // the capture completes at Start + 595 s: its completion is a durable job for the hub
        var job = await harness.Scheduler.FindAsync(EntosisCaptureService.CaptureDueKind, Hub.ToString());
        Assert.NotNull(job);
        Assert.InRange(job!.RunAtMs, Start + (long)EntosisRules.BaseCaptureMs - 2, Start + (long)EntosisRules.BaseCaptureMs + 2);

        harness.Clock.Now = job.RunAtMs;
        await harness.Scheduler.RunDueAsync();
        var captured = await harness.Service.ReadAsync(Hub, CancellationToken.None);
        Assert.Equal(EntosisPhase.Captured, captured!.Phase);
        Assert.Equal(1, captured.Progress);

        // a captured hub refuses the next cycle by name
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

        // the owner's defender link warms up at the same time: its warm-up moves nothing, and from then on both links are there
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

        // the claim ends the capture: the hub is idle under its new owner, and the links of the capture are gone
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

        // the reinforcement lasts 48 hours at the earliest, then it ends inside a window of the owner (the wiki)
        await harness.Bus.PumpAsync();
        var started = Assert.Single(harness.Notices.OfType<EntosisCaptureStarted>());
        Assert.Equal((Hub, System, 7L, 1L), (started.HubId, started.SolarSystemId, started.DefenderAllianceId, started.ModuleId));
        var notice = Assert.Single(harness.Notices.OfType<EntosisHubReinforced>());
        Assert.Equal((Hub, System, 7L), (notice.HubId, notice.SolarSystemId, notice.DefenderAllianceId));
        Assert.True(notice.ReinforcementExitMs >= completedMs + EntosisWindow.ReinforcementMs, "not before 48 hours");
        Assert.True(EntosisWindow.IsOpen(22, 1, notice.ReinforcementExitMs), "inside the owner's window");

        // the reinforced hub refuses the next cycle by name, and a replayed pump delivers nothing twice
        Assert.Equal("SOVEREIGNTY_HUB_ALREADY_REINFORCED", (await SettleAsync(harness, 1, 5)).Reason);
        await harness.Bus.PumpAsync();
        Assert.Equal(2, harness.Notices.Count);
    }

    [PgFact]
    public async Task A_cycle_outside_the_owners_vulnerability_window_is_refused_by_name_and_stores_nothing()
    {
        // the owner's window is centred on 12:00 UTC (03:00 to 21:00 at ADM 1); the clock is 22:13 UTC
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

        // 02:00 is inside the default window, but the owner's window (09:00 to 11:00) is in force from 07:00 the next day: refused, nothing is stored
        harness.Clock.Now = Day + 50 * EntosisWindow.HourMs;
        Assert.Equal("SOVEREIGNTY_TARGET_NOT_VULNERABLE", (await SettleAsync(harness, 1, 5)).Reason);
        Assert.Null(await harness.Service.ReadAsync(Hub, CancellationToken.None));

        // 10:00 of the day after the change is inside the owner's window: accepted
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
}

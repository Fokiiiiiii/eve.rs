using Eve.Data;
using Eve.Universe;
using Npgsql;

namespace Eve.Crimewatch.Tests;

public class AggressionTests
{
    private const long Now = 1_000_000_000;
    private const long System = 30100032;

    private static CombatState Flagged(Flag flag, long until) => CombatState.Idle with { Flag = flag, FlagUntilMs = until };

    [Fact]
    public void An_idle_pilot_is_told_all_five_timers_idle_with_no_expiry()
    {
        var timers = CombatState.Idle.TimersAt(Now);
        Assert.Equal([new TimerView(100, null), new TimerView(200, null), new TimerView(400, null), new TimerView(300, null), new TimerView(500, null)],
            new[] { timers.Weapons, timers.Pvp, timers.Npc, timers.Criminal, timers.Disapproval });
    }

    [Fact]
    public void Shooting_a_pilot_in_high_security_starts_the_timers_and_makes_a_criminal_for_fifteen_minutes()
    {
        // what the reference told the aggressor: weapons 102 for a minute, pvp 202 and criminal 303 for fifteen
        var outcome = Aggression.Record(CombatState.Idle, CombatState.Idle, SecurityClass.SafeSec, System, false, false, Now);
        Assert.Equal(AggressionReason.Criminal, outcome.Reason);
        Assert.True(outcome.IsCriminalOffense);
        var timers = outcome.Attacker.TimersAt(Now);
        Assert.Equal(new TimerView(102, Now + 60_000), timers.Weapons);
        Assert.Equal(new TimerView(202, Now + 900_000), timers.Pvp);
        Assert.Equal(new TimerView(303, Now + 900_000), timers.Criminal);
        Assert.Equal(new TimerView(400, null), timers.Npc);
        Assert.Equal(System, outcome.Attacker.SolarSystemId);
    }

    [Theory]
    [InlineData(SecurityClass.HighSec)]
    [InlineData(SecurityClass.SafeSec)]
    public void Every_high_security_class_is_a_crime(SecurityClass where) =>
        Assert.Equal(AggressionReason.Criminal, Aggression.Record(CombatState.Idle, CombatState.Idle, where, System, false, false, Now).Reason);

    [Fact]
    public void Shooting_a_ship_in_low_security_makes_a_suspect_and_a_capsule_a_criminal()
    {
        var ship = Aggression.Record(CombatState.Idle, CombatState.Idle, SecurityClass.LowSec, System, false, false, Now);
        Assert.Equal(AggressionReason.Suspect, ship.Reason);
        Assert.Equal(new TimerView(304, Now + 900_000), ship.Attacker.TimersAt(Now).Criminal);
        var capsule = Aggression.Record(CombatState.Idle, CombatState.Idle, SecurityClass.LowSec, System, true, false, Now);
        Assert.Equal(AggressionReason.Criminal, capsule.Reason);
        Assert.Equal(new TimerView(303, Now + 900_000), capsule.Attacker.TimersAt(Now).Criminal);
    }

    [Fact]
    public void In_null_security_there_is_no_crime_but_the_timers_run()
    {
        var outcome = Aggression.Record(CombatState.Idle, CombatState.Idle, SecurityClass.ZeroSec, System, true, false, Now);
        Assert.Equal(AggressionReason.NoCrime, outcome.Reason);
        Assert.Equal(Flag.None, outcome.Attacker.FlagAt(Now));
        Assert.Equal(new TimerView(102, Now + 60_000), outcome.Attacker.TimersAt(Now).Weapons);
        Assert.Equal(new TimerView(202, Now + 900_000), outcome.Attacker.TimersAt(Now).Pvp);
    }

    [Theory]
    [InlineData(Flag.Criminal, SecurityClass.HighSec)]
    [InlineData(Flag.Suspect, SecurityClass.HighSec)]
    [InlineData(Flag.Criminal, SecurityClass.LowSec)]
    [InlineData(Flag.Suspect, SecurityClass.LowSec)]
    public void A_target_that_is_flagged_may_be_shot_without_a_crime_and_the_timers_still_run(Flag target, SecurityClass where)
    {
        var outcome = Aggression.Record(CombatState.Idle, Flagged(target, Now + 60_000), where, System, false, false, Now);
        Assert.Equal(AggressionReason.TargetFlagged, outcome.Reason);
        Assert.Equal(Flag.None, outcome.Attacker.FlagAt(Now));
        Assert.Equal(Now + 60_000, outcome.Attacker.WeaponsUntilMs);
    }

    [Fact]
    public void A_flag_that_has_run_out_protects_nobody()
    {
        var outcome = Aggression.Record(CombatState.Idle, Flagged(Flag.Criminal, Now), SecurityClass.HighSec, System, false, false, Now);
        Assert.Equal(AggressionReason.Criminal, outcome.Reason);
    }

    [Fact]
    public void What_the_law_allows_is_no_crime_in_any_class()
    {
        foreach (var where in Enum.GetValues<SecurityClass>())
        {
            var outcome = Aggression.Record(CombatState.Idle, CombatState.Idle, where, System, false, true, Now);
            Assert.Equal(AggressionReason.Legal, outcome.Reason);
            Assert.Equal(Now + 60_000, outcome.Attacker.WeaponsUntilMs);
            Assert.Equal(Flag.None, outcome.Attacker.FlagAt(Now));
        }
    }

    [Fact]
    public void A_timer_that_runs_longer_is_kept_and_a_new_offence_sets_the_kind_of_the_flag_as_the_reference_does()
    {
        var criminal = Aggression.Record(CombatState.Idle, CombatState.Idle, SecurityClass.HighSec, System, false, false, Now).Attacker;
        // a minute later in low security the criminal shoots a ship: the reference makes it a suspect (setNonTheftPenalty), the timer runs from the new offence
        var later = Now + 60_000;
        var again = Aggression.Record(criminal, CombatState.Idle, SecurityClass.LowSec, System, false, false, later);
        Assert.Equal(AggressionReason.Suspect, again.Reason);
        Assert.Equal(Flag.Suspect, again.Attacker.FlagAt(later));
        Assert.Equal(later + 900_000, again.Attacker.FlagUntilMs);
        // a suspect that commits a crime is made a criminal
        var suspect = Aggression.Record(CombatState.Idle, CombatState.Idle, SecurityClass.LowSec, System, false, false, Now).Attacker;
        Assert.Equal(Flag.Criminal, Aggression.Record(suspect, CombatState.Idle, SecurityClass.HighSec, System, false, false, later).Attacker.FlagAt(later));
        // a weapons timer that runs longer than a fresh one is kept
        var long_ = CombatState.Idle with { WeaponsUntilMs = Now + 600_000 };
        Assert.Equal(Now + 600_000, Aggression.Record(long_, CombatState.Idle, SecurityClass.ZeroSec, System, false, false, Now).Attacker.WeaponsUntilMs);
    }

    [Fact]
    public void The_next_end_is_the_first_timer_to_run_out_and_a_pilot_with_nothing_running_has_none()
    {
        var state = Aggression.Record(CombatState.Idle, CombatState.Idle, SecurityClass.HighSec, System, false, false, Now).Attacker;
        Assert.Equal(Now + 60_000, state.NextEndAfter(Now));
        Assert.Equal(Now + 900_000, state.NextEndAfter(Now + 60_000));
        Assert.Null(state.NextEndAfter(Now + 900_000));
        Assert.True(state.IsActive(Now));
        Assert.False(state.IsActive(Now + 900_000));
        Assert.False(CombatState.Idle.IsActive(Now));
        Assert.True(state.HasWeaponsTimer(Now + 59_999));
        Assert.False(state.HasWeaponsTimer(Now + 60_000));
    }

    [PgFact]
    public async Task What_is_kept_survives_the_process_is_read_back_by_system_and_a_change_is_one_step()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var schema = "cw_" + Guid.NewGuid().ToString("N")[..12];
        await PgCrimewatchStore.MigrateAsync(source, schema);
        try
        {
            var store = new PgCrimewatchStore(source, schema);
            Assert.Equal(CombatState.Idle, await store.StateOfAsync(1));
            var (before, after) = await store.UpdateAsync(1, s => Aggression.Record(s, CombatState.Idle, SecurityClass.HighSec, System, false, false, Now).Attacker, Now);
            Assert.Equal(CombatState.Idle, before);
            Assert.Equal(Flag.Criminal, after.Flag);
            Assert.Equal(after, await store.StateOfAsync(1));
            await store.UpdateAsync(2, s => Aggression.Record(s, CombatState.Idle, SecurityClass.LowSec, System, false, false, Now).Attacker, Now);
            await store.UpdateAsync(3, s => Aggression.Record(s, CombatState.Idle, SecurityClass.ZeroSec, 30000142, false, false, Now).Attacker, Now);
            var (criminals, suspects) = await store.FlaggedInAsync(System, Now);
            Assert.Equal([1L], criminals);
            Assert.Equal([2L], suspects);
            Assert.Empty((await store.FlaggedInAsync(System, Now + 900_000)).Criminals);   // a flag that has run out is not told
            Assert.Empty((await store.FlaggedInAsync(30000142, Now)).Criminals);           // pilot 3 is no crime: nobody is flagged there
            Assert.Equal([1L, 2L, 3L], (await store.ActiveAsync(Now)).Select(a => a.CharacterId).Order());
            Assert.Equal([1L, 2L, 3L], (await store.ActiveAsync(Now + 60_000)).Select(a => a.CharacterId).Order());   // the weapons timers are over, the pvp ones run
            Assert.Empty(await store.ActiveAsync(Now + 900_000));
        }
        finally
        {
            await using var connection = await source.OpenConnectionAsync();
            await Sql.ExecuteAsync(connection, null, $"DROP SCHEMA {schema} CASCADE", CancellationToken.None);
        }
    }


    [PgFact]
    public async Task Kill_rights_persist_once_per_pair_and_can_be_consumed()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var schema = "cw_" + Guid.NewGuid().ToString("N")[..12];
        await PgCrimewatchStore.MigrateAsync(source, schema);
        try
        {
            var store = new PgCrimewatchStore(source, schema);
            var created = await store.CreateKillRightAsync(2, 1, System, Now);
            Assert.NotNull(created);
            Assert.Null(await store.CreateKillRightAsync(2, 1, System, Now + 1));

            var rights = await store.KillRightsOfAsync(2);
            var right = Assert.Single(rights);
            Assert.Equal((created, 2L, 1L, System, Now),
                ((long?)right.KillRightId, right.OwnerCharacterId, right.TargetCharacterId, right.SolarSystemId, right.CreatedAtUnixMs));

            Assert.False(await store.MarkKillRightUsedAsync(3, created.Value, Now + 2));
            Assert.True(await store.MarkKillRightUsedAsync(2, created.Value, Now + 2));
            Assert.False(await store.MarkKillRightUsedAsync(2, created.Value, Now + 3));
            Assert.Empty(await store.KillRightsOfAsync(2));

            var replacement = await store.CreateKillRightAsync(2, 1, System, Now + 4);
            Assert.NotNull(replacement);
            Assert.NotEqual(created.Value, replacement.Value);
            Assert.Equal(replacement.Value, Assert.Single(await store.KillRightsOfAsync(2)).KillRightId);
        }
        finally
        {
            await using var connection = await source.OpenConnectionAsync();
            await Sql.ExecuteAsync(connection, null, $"DROP SCHEMA {schema} CASCADE", CancellationToken.None);
        }
    }

    [PgFact]
    public async Task Two_changes_of_one_character_at_once_both_count()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var schema = "cw_" + Guid.NewGuid().ToString("N")[..12];
        await PgCrimewatchStore.MigrateAsync(source, schema);
        try
        {
            var store = new PgCrimewatchStore(source, schema);
            // each change moves the weapons timer on by a minute: whichever goes second sees the work of the first
            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => store.UpdateAsync(1, s => s with { WeaponsUntilMs = s.WeaponsUntilMs + 60_000 }, Now)));
            Assert.Equal(8 * 60_000, (await store.StateOfAsync(1)).WeaponsUntilMs);
        }
        finally
        {
            await using var connection = await source.OpenConnectionAsync();
            await Sql.ExecuteAsync(connection, null, $"DROP SCHEMA {schema} CASCADE", CancellationToken.None);
        }
    }
}

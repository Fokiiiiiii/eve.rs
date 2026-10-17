using Npgsql;

namespace Eve.FactionWarfare.Tests;

public sealed class FwComplexRulesTests
{
    [Fact]
    public void Sizes_match_the_wiki_rank_size_table()
    {
        Assert.Equal(300, FwComplexes.SpecOf(ComplexSize.Scout).CaptureSeconds);
        Assert.Equal(600, FwComplexes.SpecOf(ComplexSize.Small).CaptureSeconds);
        Assert.Equal(900, FwComplexes.SpecOf(ComplexSize.Medium).CaptureSeconds);
        Assert.Equal(900, FwComplexes.SpecOf(ComplexSize.Large).CaptureSeconds);
        Assert.Equal(1, FwComplexes.SpecOf(ComplexSize.Scout).MaxHullSize);
        Assert.Equal(5, FwComplexes.SpecOf(ComplexSize.Large).MaxHullSize);
        Assert.Equal(50, FwComplexes.SpecOf(ComplexSize.Scout).VictoryPoints);
        Assert.Equal(250, FwComplexes.SpecOf(ComplexSize.Large).VictoryPoints);
    }

    [Fact]
    public void The_timer_advances_only_for_a_side_alone_in_the_complex_and_one_tick_at_most()
    {
        const long tick = 10_000;
        Assert.Equal(10_000, FwComplexes.Advance(0, attackersPresent: true, defendersPresent: false, elapsedMs: 10_000, tickMs: tick));
        Assert.Equal(-10_000, FwComplexes.Advance(0, attackersPresent: false, defendersPresent: true, elapsedMs: 10_000, tickMs: tick));
        Assert.Equal(0, FwComplexes.Advance(0, attackersPresent: true, defendersPresent: true, elapsedMs: 10_000, tickMs: tick));
        Assert.Equal(0, FwComplexes.Advance(0, attackersPresent: false, defendersPresent: false, elapsedMs: 10_000, tickMs: tick));
        // a restart after an hour credits one tick only
        Assert.Equal(10_000, FwComplexes.Advance(0, attackersPresent: true, defendersPresent: false, elapsedMs: 3_600_000, tickMs: tick));
    }

    [Fact]
    public void A_complex_resolves_at_its_capture_time_on_either_side()
    {
        var scout = FwComplexes.SpecOf(ComplexSize.Scout);
        Assert.Null(FwComplexes.Resolved(299_999, scout));
        Assert.True(FwComplexes.Resolved(300_000, scout));
        Assert.Null(FwComplexes.Resolved(-299_999, scout));
        Assert.False(FwComplexes.Resolved(-300_000, scout));
    }
}

/// <summary>The complex ticker on PostgreSQL: spawns, capture by either side, the victory points, and the respawn time.</summary>
public sealed class FwComplexTickerTests
{
    private static readonly long SystemId = FwWarzones.SystemsOf(FwWarzones.AmarrMinmatar)[0];

    [PgFact]
    public async Task Attackers_capture_a_scout_complex_after_its_timer_and_gain_its_victory_points()
    {
        await using var rig = await FwRig.CreateAsync();
        await rig.OccupyWarzoneAsync(FwWarzones.AmarrMinmatar, FwFactions.AmarrEmpire);
        var attackers = FwFactions.OccupationEnemyOf(FwFactions.AmarrEmpire);
        // the first tick spawns the complexes (no time has passed for them yet), so a 300 s timer needs 31 ticks
        var resolved = await TicksAsync(rig, 31, [attackers]);
        var scout = Assert.Single(resolved, r => r.Size == ComplexSize.Scout && r.SolarSystemId == SystemId);
        Assert.True(scout.AttackersWon);
        Assert.Equal(50, scout.VictoryPoints);
        Assert.Equal(50, scout.AttackerPoints);
        await using var connection = await rig.Source.OpenConnectionAsync();
        Assert.Equal(50, (await rig.Store.SystemAsync(connection, null, SystemId, false))!.AttackerPoints);
    }

    [PgFact]
    public async Task Defenders_capture_subtracts_the_points_and_the_complex_respawns_after_thirty_minutes()
    {
        await using var rig = await FwRig.CreateAsync();
        await rig.OccupyWarzoneAsync(FwWarzones.AmarrMinmatar, FwFactions.AmarrEmpire);
        var resolved = await TicksAsync(rig, 31, [FwFactions.AmarrEmpire]);
        var scout = Assert.Single(resolved, r => r.Size == ComplexSize.Scout && r.SolarSystemId == SystemId);
        Assert.False(scout.AttackersWon);
        Assert.Equal(0, scout.AttackerPoints);

        // the scout complex of the system is not back before thirty minutes after its capture
        long closedAt = rig.Now;
        var none = await TicksAsync(rig, 179, []);
        Assert.Empty(none);
        Assert.True(await OpenSizeAsync(rig, SystemId, ComplexSize.Scout) is null);
        await TicksAsync(rig, 1, []);
        Assert.NotNull(await OpenSizeAsync(rig, SystemId, ComplexSize.Scout));
        Assert.True(rig.Now - closedAt >= (long)FwComplexes.Respawn.TotalMilliseconds);
    }

    /// <summary>Runs <paramref name="ticks"/> ticks of 10 s with the given war factions present; returns what resolved.</summary>
    private static async Task<List<FwComplexResolution>> TicksAsync(FwRig rig, int ticks, long[] factions)
    {
        var resolved = new List<FwComplexResolution>();
        Func<long, CancellationToken, Task<IReadOnlyList<long>>> presence = (_, _) => Task.FromResult<IReadOnlyList<long>>(factions);
        for (int i = 0; i < ticks; i++)
        {
            rig.Now += 10_000;
            await using var connection = await rig.Source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            resolved.AddRange(await FwComplexTicker.TickAsync(rig.Store, connection, transaction, rig.Now, presence, CancellationToken.None));
            await transaction.CommitAsync();
        }
        return resolved;
    }

    private static async Task<FwComplex?> OpenSizeAsync(FwRig rig, long systemId, ComplexSize size)
    {
        await using var connection = await rig.Source.OpenConnectionAsync();
        return (await rig.Store.OpenComplexesAsync(connection, null)).FirstOrDefault(c => c.SolarSystemId == systemId && c.Size == size);
    }
}

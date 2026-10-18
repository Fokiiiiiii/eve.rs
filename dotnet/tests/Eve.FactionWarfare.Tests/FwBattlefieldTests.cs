using Npgsql;

namespace Eve.FactionWarfare.Tests;

public sealed class FwBattlefieldRulesTests
{
    [Fact]
    public void The_hold_timer_gives_one_point_per_full_tick_and_carries_the_rest()
    {
        Assert.Equal((5_000L, 0), FwBattlefields.Hold(0, 5_000));
        Assert.Equal((0L, 1), FwBattlefields.Hold(15_000, 5_000));
        Assert.Equal((4_000L, 2), FwBattlefields.Hold(16_000, 28_000));
    }

    [Fact]
    public void The_battle_is_decided_at_plus_or_minus_150_and_the_score_stays_between()
    {
        Assert.Null(FwBattlefields.Decided(149));
        Assert.Null(FwBattlefields.Decided(-149));
        Assert.True(FwBattlefields.Decided(150));
        Assert.False(FwBattlefields.Decided(-150));
        Assert.Equal(150, FwBattlefields.Clamp(400));
        Assert.Equal(-150, FwBattlefields.Clamp(-151));
    }

    [Fact]
    public void Points_and_scoreboards_are_the_sde_types_of_the_occupier()
    {
        Assert.Equal(73_443, FwBattlefields.PointTypeOf(500_003, 0));
        Assert.Equal(73_860, FwBattlefields.PointTypeOf(500_004, 1));
        Assert.Equal(73_449, FwBattlefields.ScoreboardTypeOf(500_001));
        Assert.Equal("Caldari Control Zone C", FwBattlefields.NameOf(500_001, 2));
    }

    [Fact]
    public void The_outer_points_are_80_km_either_side_of_the_centre()
    {
        var origin = new double[] { 0, 0, 1e9 };
        Assert.Equal(new double[] { 0, 0, 1e9 }, FwBattlefields.PointPosition(origin, 0));
        Assert.Equal(new double[] { 80_000, 0, 1e9 }, FwBattlefields.PointPosition(origin, 1));
        Assert.Equal(new double[] { -80_000, 0, 1e9 }, FwBattlefields.PointPosition(origin, 2));
    }

    [Fact]
    public void Loyalty_points_go_to_the_first_thirty_winners_by_character_id_and_the_pool_is_150_000()
    {
        Assert.Equal(150_000, FwBattlefields.LpEach * FwBattlefields.LpParticipants);
        var recipients = FwBattlefields.LpRecipients(Enumerable.Range(1, 35).Reverse().Select(id => (long)id));
        Assert.Equal(30, recipients.Count);
        Assert.Equal(1, recipients[0]);
        Assert.Equal(30, recipients[29]);
    }

    [Fact]
    public void The_ball_ids_of_a_battlefield_do_not_overlap_the_next_battlefields()
    {
        var ids = new HashSet<long>();
        for (long battlefield = 1; battlefield <= 3; battlefield++)
        {
            for (int index = 0; index < FwBattlefields.PointCount; index++)
                Assert.True(ids.Add(FwBattlefields.PointId(battlefield, index)));
            Assert.True(ids.Add(FwBattlefields.ScoreboardId(battlefield)));
        }
    }
}

/// <summary>The battlefield ticker on PostgreSQL: the spawn, the capture of the points by one side, the award, and the spawn gap after a win.</summary>
public sealed class FwBattlefieldTickerTests
{
    private static readonly long Amarr = FwFactions.AmarrEmpire;
    private static readonly long Minmatar = FwFactions.OccupationEnemyOf(FwFactions.AmarrEmpire);

    [PgFact]
    public async Task One_side_holding_the_three_points_wins_the_battlefield_and_gets_the_awards()
    {
        await using var rig = await FwRig.CreateAsync();
        await rig.OccupyWarzoneAsync(FwWarzones.AmarrMinmatar, Amarr);
        var sites = new BattleSites();
        var rewards = new RecordingRewards();
        var first = await TickAsync(rig, sites, rewards);
        Assert.Empty(first);
        sites.ActiveSystem = (await OpenAsync(rig)).First().SolarSystemId;
        sites.Pilots = Enumerable.Range(0, 35).Select(index => new FwComplexPilot(2000 + index, Minmatar)).ToList();
        var wins = new List<FwBattlefieldWon>();
        for (int tick = 0; tick < 400 && wins.Count == 0; tick++)
            wins.AddRange(await TickAsync(rig, sites, rewards));

        var won = Assert.Single(wins);
        Assert.Equal(sites.ActiveSystem, won.SolarSystemId);
        Assert.Equal(Minmatar, won.WinnerId);
        Assert.Equal(FwBattlefields.VictoryPoints, won.VictoryPoints);
        Assert.Equal(FwBattlefields.VictoryPoints, won.AttackerPoints);
        Assert.Equal(30, won.LpRecipients);
        Assert.Equal(30, rewards.Credits.Count);
        Assert.All(rewards.Credits, credit => Assert.Equal(FwBattlefields.LpEach, credit.Amount));
        Assert.All(rewards.Credits, credit => Assert.Equal(FwFactions.MilitiaCorporationOf(Minmatar), credit.Issuer));
        Assert.Equal(Enumerable.Range(0, 30).Select(index => 2000L + index), rewards.Credits.Select(credit => credit.CharacterId));

        // the won battlefield is closed; the other stays open, and no third one comes within three hours of the win
        var open = await OpenAsync(rig);
        Assert.Single(open);
        rig.Now += 60 * 60 * 1000;
        await TickAsync(rig, sites, rewards);
        Assert.Single(await OpenAsync(rig));
        rig.Now += 3 * 60 * 60 * 1000;
        await TickAsync(rig, sites, rewards);
        Assert.Equal(2, (await OpenAsync(rig)).Count);
    }

    [PgFact]
    public async Task One_side_holding_the_points_wins_for_the_occupier_and_takes_the_points_away()
    {
        await using var rig = await FwRig.CreateAsync();
        await rig.OccupyWarzoneAsync(FwWarzones.AmarrMinmatar, Amarr);
        var sites = new BattleSites();
        var rewards = new RecordingRewards();
        await TickAsync(rig, sites, rewards);
        sites.ActiveSystem = (await OpenAsync(rig)).First().SolarSystemId;
        sites.Pilots = [new FwComplexPilot(3001, Amarr), new FwComplexPilot(3002, Amarr)];
        var wins = new List<FwBattlefieldWon>();
        for (int tick = 0; tick < 400 && wins.Count == 0; tick++)
            wins.AddRange(await TickAsync(rig, sites, rewards));

        var won = Assert.Single(wins);
        Assert.Equal(Amarr, won.WinnerId);
        Assert.Equal(-FwBattlefields.VictoryPoints, won.VictoryPoints);
        Assert.Equal(0, won.AttackerPoints);
        Assert.Equal(2, won.LpRecipients);
        Assert.All(rewards.Credits, credit => Assert.Equal(FwFactions.MilitiaCorporationOf(Amarr), credit.Issuer));
    }

    [PgFact]
    public async Task Both_sides_present_pause_every_point_and_no_score_moves()
    {
        await using var rig = await FwRig.CreateAsync();
        await rig.OccupyWarzoneAsync(FwWarzones.AmarrMinmatar, Amarr);
        var sites = new BattleSites();
        var rewards = new RecordingRewards();
        await TickAsync(rig, sites, rewards);
        sites.ActiveSystem = (await OpenAsync(rig)).First().SolarSystemId;
        sites.Pilots = [new FwComplexPilot(4001, Amarr), new FwComplexPilot(4002, Minmatar)];
        for (int tick = 0; tick < 60; tick++)
            Assert.Empty(await TickAsync(rig, sites, rewards));

        var battlefield = (await OpenAsync(rig)).First(b => b.SolarSystemId == sites.ActiveSystem);
        Assert.Equal(0, battlefield.Score);
        Assert.All(battlefield.Points, point => Assert.Equal(0, point.HoldMs));
        Assert.Empty(rewards.Credits);
    }

    private static async Task<IReadOnlyList<FwBattlefield>> OpenAsync(FwRig rig)
    {
        await using var connection = await rig.Source.OpenConnectionAsync();
        return await rig.Store.OpenBattlefieldsAsync(connection, null);
    }

    /// <summary>One tick of 10 s; returns the battlefields it won.</summary>
    private static async Task<IReadOnlyList<FwBattlefieldWon>> TickAsync(FwRig rig, IFwComplexSites sites, IFwRewards rewards)
    {
        rig.Now += 10_000;
        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var (_, wins) = await FwBattlefieldTicker.TickAsync(rig.Store, sites, rewards, connection, transaction, rig.Now, CancellationToken.None);
        await transaction.CommitAsync();
        return wins;
    }

    /// <summary>The sites of the test: the pilots are in the active system only, at any point of it.</summary>
    private sealed class BattleSites : IFwComplexSites
    {
        public long ActiveSystem { get; set; }
        public IReadOnlyList<FwComplexPilot> Pilots { get; set; } = [];

        public Task<double[]> OriginAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long solarSystemId, CancellationToken cancellationToken) =>
            Task.FromResult<double[]>([0d, 0d, 1e9]);

        public Task<IReadOnlyList<FwComplexPilot>> PilotsNearAsync(long solarSystemId, double[] at, double radius, CancellationToken cancellationToken) =>
            Task.FromResult(solarSystemId == ActiveSystem ? Pilots : (IReadOnlyList<FwComplexPilot>)[]);

        public Task<long> StartAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long solarSystemId, ComplexSize size, long complexId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("the battlefield tests start no complex site");

        public Task<bool> DefendersClearedAsync(long instanceId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public bool PilotsIn(long solarSystemId) => false;

        public Task<IReadOnlyList<FwComplexPilot>> PilotsInsideAsync(long solarSystemId, long instanceId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task EndAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long instanceId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingRewards : IFwRewards
    {
        public List<(long CharacterId, long Issuer, long Amount, string Reference)> Credits { get; } = [];

        public Task CreditLpAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long characterId, long issuerCorpId, long amount, string reference,
            CancellationToken cancellationToken)
        {
            Credits.Add((characterId, issuerCorpId, amount, reference));
            return Task.CompletedTask;
        }
    }
}

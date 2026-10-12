using Eve.Insurgency;

namespace Eve.Insurgency.Tests;

/// <summary>The campaign service on PostgreSQL: deadlines, restart, points and ambition.</summary>
public sealed class InsurgencyCampaignTests
{
    private const long Hour = 3_600_000;
    private static readonly IReadOnlyList<long> Covered = [30000001, 30000002];

    private static CampaignStart StartOf(long warzone = 1) => new(500011, warzone, 30000001, Covered);

    private static async Task<Campaign> StartCampaign(Rig rig, long warzone = 1)
    {
        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var campaign = await rig.Campaigns.StartAsync(connection, transaction, StartOf(warzone));
        await transaction.CommitAsync();
        return campaign;
    }

    private static async Task<Campaign> StartCovering(Rig rig, long warzone, IReadOnlyList<long> covered)
    {
        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var campaign = await rig.Campaigns.StartAsync(connection, transaction, new CampaignStart(500011, warzone, 30000001, covered));
        await transaction.CommitAsync();
        return campaign;
    }

    private static async Task<ScoreOutcome> Score(Rig rig, long campaignId, Side side, long system = 30000001)
    {
        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var outcome = await rig.Campaigns.ScorePointAsync(connection, transaction, campaignId, side, system);
        await transaction.CommitAsync();
        return outcome;
    }

    [PgFact]
    public async Task A_started_campaign_is_stored_and_its_deadline_job_exists()
    {
        await using var rig = await Rig.CreateAsync();
        var campaign = await StartCampaign(rig);
        var stored = await rig.Get(campaign.CampaignId);
        Assert.Equal(CampaignStates.Forecasting, stored!.State);
        Assert.Equal(Covered, stored.CoveredSolarSystemIds);
        Assert.Equal(Rig.Epoch + 48 * Hour, (await rig.Job(campaign.CampaignId))!.RunAtMs);
    }

    [PgFact]
    public async Task The_deadlines_run_the_campaign_through_forecast_to_no_winner_and_terminal()
    {
        await using var rig = await Rig.CreateAsync();
        var campaign = await StartCampaign(rig);
        await rig.RunAt(Rig.Epoch + 48 * Hour);
        Assert.Equal(CampaignStates.Active, (await rig.Get(campaign.CampaignId))!.State);
        Assert.Equal(Rig.Epoch + 7 * 24 * Hour, (await rig.Job(campaign.CampaignId))!.RunAtMs);

        await rig.RunAt(Rig.Epoch + 7 * 24 * Hour);
        Assert.Equal(CampaignStates.NoWinner, (await rig.Get(campaign.CampaignId))!.State);

        await rig.RunAt(Rig.Epoch + 9 * 24 * Hour);
        Assert.Equal(CampaignStates.Terminal, (await rig.Get(campaign.CampaignId))!.State);
        Assert.Null(await rig.Job(campaign.CampaignId));
    }

    [PgFact]
    public async Task A_restart_in_the_middle_of_the_active_state_keeps_its_expiry()
    {
        await using var rig = await Rig.CreateAsync();
        var campaign = await StartCampaign(rig);
        await rig.RunAt(Rig.Epoch + 50 * Hour);
        var before = await rig.Get(campaign.CampaignId);

        rig.Start();
        rig.Now = Rig.Epoch + 60 * Hour;
        await rig.Commit((connection, transaction) => rig.Campaigns.EnsureScheduledAsync(connection, transaction));
        var after = await rig.Get(campaign.CampaignId);
        Assert.Equal(before!.State, after!.State);
        Assert.Equal(before.MaxEndsMs, after.MaxEndsMs);
        Assert.Equal(before.StateExpiresMs, after.StateExpiresMs);
        Assert.Equal(before.PiratePointsRequired, after.PiratePointsRequired);
        Assert.Equal(CampaignStates.Active, after.State);
        Assert.Equal(Rig.Epoch + 7 * 24 * Hour, (await rig.Job(campaign.CampaignId))!.RunAtMs);
    }

    [PgFact]
    public async Task A_side_wins_when_it_reaches_its_count_and_the_ambition_moves()
    {
        await using var rig = await Rig.CreateAsync();
        var first = await StartCovering(rig, 7, [30000001, 30000002, 30000003, 30000004, 30000005]);
        await rig.RunAt(Rig.Epoch + 48 * Hour);
        // a system scores once for a side: five covered systems make the five points
        var systems = first.CoveredSolarSystemIds;
        for (var i = 0; i < 4; i++)
            Assert.Equal(ScoreOutcome.Scored, await Score(rig, first.CampaignId, Side.Pirate, system: systems[i]));
        Assert.Equal(ScoreOutcome.AlreadyScored, await Score(rig, first.CampaignId, Side.Pirate, system: systems[0]));
        Assert.Equal(ScoreOutcome.Scored, await Score(rig, first.CampaignId, Side.Pirate, system: systems[4]));
        Assert.Equal(CampaignStates.PirateWin, (await rig.Get(first.CampaignId))!.State);
        Assert.Equal(ScoreOutcome.NotActive, await Score(rig, first.CampaignId, Side.Pirate));

        var ambition = await rig.Commit((c, t) => rig.Store.AmbitionAsync(c, t, 7));
        Assert.Equal((1, 0), ambition);

        // the next campaign of the warzone needs 6 systems
        var second = await StartCampaign(rig, warzone: 7);
        Assert.Equal(6, second.PiratePointsRequired);
        Assert.Equal(5, second.AntipiratePointsRequired);
    }

    [PgFact]
    public async Task A_point_for_a_system_the_campaign_does_not_cover_is_refused()
    {
        await using var rig = await Rig.CreateAsync();
        var campaign = await StartCampaign(rig);
        await rig.RunAt(Rig.Epoch + 48 * Hour);
        Assert.Equal(ScoreOutcome.NotCovered, await Score(rig, campaign.CampaignId, Side.Pirate, system: 30009999));
        Assert.Equal(0, (await rig.Get(campaign.CampaignId))!.PiratePointsScored);
    }

    [PgFact]
    public async Task A_point_before_the_active_state_is_refused()
    {
        await using var rig = await Rig.CreateAsync();
        var campaign = await StartCampaign(rig);
        Assert.Equal(ScoreOutcome.NotActive, await Score(rig, campaign.CampaignId, Side.Antipirate));
        Assert.Equal(ScoreOutcome.UnknownCampaign, await Score(rig, 999999, Side.Antipirate));
    }
}

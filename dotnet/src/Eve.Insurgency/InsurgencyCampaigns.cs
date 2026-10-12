using Eve.Events;
using Eve.Scheduler;
using Eve.Wallet;
using Npgsql;

namespace Eve.Insurgency;

/// <summary>What a campaign start names: the pirate faction (its warzone), the origin system, the systems it covers and the FOB that holds it (0 when none yet).</summary>
public sealed record CampaignStart(int PirateFactionId, long WarzoneId, long OriginSolarSystemId, IReadOnlyList<long> CoveredSolarSystemIds, long StructureId = 0);

/// <summary>An event of a campaign state change (<see cref="InsurgencyCampaigns.CampaignChangedTopic"/>): the transition and the campaign after it.</summary>
public sealed record CampaignChanged(long CampaignId, int From, int To, long AtMs, Campaign Campaign);

/// <summary>An event of a point (<see cref="InsurgencyCampaigns.PointScoredTopic"/>): the side, the system and the campaign after the point.</summary>
public sealed record PointScored(long CampaignId, Side Side, long SolarSystemId, Campaign Campaign);

/// <summary>An event of the empire side's goal reached (<see cref="InsurgencyCampaigns.FobExposedTopic"/>): the campaign's FOB can now be attacked.</summary>
public sealed record FobExposure(long CampaignId, long StructureId);

public enum ScoreOutcome
{
    Scored,
    NotActive,
    NotCovered,
    UnknownCampaign,
    AlreadyScored,
}

/// <summary>
/// The insurgency campaigns: starts them, moves them along their timers and scores points. Every change is stored and its events are written in the same
/// transaction (the event bus delivers them after the commit). Each campaign has one durable deadline job (<see cref="JobKind"/>, key <c>campaign:{id}</c>),
/// so a restart resumes at the same deadline.
/// </summary>
public sealed class InsurgencyCampaigns
{
    public const string JobKind = "insurgency.phase";
    public const string CampaignChangedTopic = "insurgency.campaign_changed";
    public const string PointScoredTopic = "insurgency.point_scored";
    public const string FobExposedTopic = "insurgency.fob_exposed";

    private sealed record PhaseJob(long CampaignId);

    private readonly InsurgencyStore _store;
    private readonly DurableScheduler _scheduler;
    private readonly EventBus _events;
    private readonly LoyaltyPointStore? _loyalty;

    /// <summary>The campaigns. With a loyalty store, a pirate victory pays each contributor in the win's transaction (<see cref="InsurgencyRules.VictoryLp"/>).</summary>
    public InsurgencyCampaigns(InsurgencyStore store, DurableScheduler scheduler, EventBus events, LoyaltyPointStore? loyalty = null)
    {
        _store = store;
        _scheduler = scheduler;
        _events = events;
        _loyalty = loyalty;
    }

    public InsurgencyStore Store => _store;

    private static string KeyOf(long campaignId) => $"campaign:{campaignId}";

    /// <summary>Registers the deadline handler. Call it once, before the foundations start.</summary>
    public void Register() => _scheduler.Register(JobKind, (job, token) => PhaseAsync(job, token));

    /// <summary>Starts a campaign in forecasting. Its required systems are the warzone's ambition at this moment.</summary>
    public async Task<Campaign> StartAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CampaignStart start, CancellationToken cancellationToken = default)
    {
        var (pirate, antipirate) = await _store.AmbitionAsync(connection, transaction, start.WarzoneId, cancellationToken);
        var created = CampaignMachine.Start(
            start.PirateFactionId, start.WarzoneId, start.OriginSolarSystemId, start.CoveredSolarSystemIds, start.StructureId, _scheduler.Now,
            InsurgencyRules.RequiredSystems(pirate), InsurgencyRules.RequiredSystems(antipirate));
        var campaign = await _store.InsertAsync(connection, transaction, created, cancellationToken);
        await _events.PublishInTransactionAsync(connection, transaction, CampaignChangedTopic,
            new CampaignChanged(campaign.CampaignId, CampaignStates.Initial, CampaignStates.Forecasting, campaign.StartedMs, campaign), KeyOf(campaign.CampaignId), cancellationToken);
        await ScheduleAsync(connection, transaction, campaign, cancellationToken);
        return campaign;
    }

    /// <summary>
    /// A system scores a point for a side. The timers are run to now first, so a point never lands on a campaign whose deadline has passed. Accepted points are
    /// stored, announced, and may win the campaign (the ambition of both sides is then updated in the same transaction).
    /// </summary>
    public async Task<ScoreOutcome> ScorePointAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long campaignId, Side side, long solarSystemId, CancellationToken cancellationToken = default)
    {
        var current = await _store.GetAsync(connection, transaction, campaignId, true, cancellationToken);
        if (current is null)
            return ScoreOutcome.UnknownCampaign;
        var now = _scheduler.Now;
        var advanced = CampaignMachine.Advance(current, now);
        var outcome = ScoreOutcome.NotCovered;
        var step = advanced;
        if (advanced.Campaign.CoveredSolarSystemIds.Contains(solarSystemId))
        {
            if (advanced.Campaign.State != CampaignStates.Active)
                outcome = ScoreOutcome.NotActive;
            else if (!await _store.TryScorePointAsync(connection, transaction, campaignId, solarSystemId, side, now, cancellationToken))
                outcome = ScoreOutcome.AlreadyScored;
            else
            {
                var scored = CampaignMachine.Score(advanced.Campaign, side, now);
                outcome = ScoreOutcome.Scored;
                step = new MachineStep(scored.Campaign, [.. advanced.Transitions, .. scored.Transitions], scored.Accepted);
            }
        }
        if (step.Transitions.Count > 0 || outcome == ScoreOutcome.Scored)
            await _store.UpdateAsync(connection, transaction, step.Campaign, cancellationToken);
        await ApplyTransitionsAsync(connection, transaction, step, cancellationToken);
        if (outcome == ScoreOutcome.Scored)
            await _events.PublishInTransactionAsync(connection, transaction, PointScoredTopic,
                new PointScored(campaignId, side, solarSystemId, step.Campaign), KeyOf(campaignId), cancellationToken);
        if (outcome == ScoreOutcome.Scored && side == Side.Antipirate && step.Campaign.AntipiratePointsScored == step.Campaign.AntipiratePointsRequired)
            await _events.PublishInTransactionAsync(connection, transaction, FobExposedTopic,
                new FobExposure(campaignId, step.Campaign.StructureId), KeyOf(campaignId), cancellationToken);
        await ScheduleAsync(connection, transaction, step.Campaign, cancellationToken);
        return outcome;
    }

    /// <summary>
    /// The FOB of a campaign was destroyed (the structure timeline announced it): an active campaign of that FOB ends in the empire's win. Returns false
    /// when no active campaign has the FOB.
    /// </summary>
    public async Task<bool> FobDestroyedAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long structureId, CancellationToken cancellationToken = default)
    {
        var open = (await _store.OpenAsync(connection, transaction, cancellationToken)).FirstOrDefault(c => c.StructureId == structureId);
        if (open is null)
            return false;
        var current = await _store.GetAsync(connection, transaction, open.CampaignId, true, cancellationToken);
        var step = CampaignMachine.FobDestroyed(current!, _scheduler.Now);
        if (step.Transitions.Count == 0)
            return false;
        await _store.UpdateAsync(connection, transaction, step.Campaign, cancellationToken);
        await ApplyTransitionsAsync(connection, transaction, step, cancellationToken);
        await ScheduleAsync(connection, transaction, step.Campaign, cancellationToken);
        return true;
    }

    /// <summary>Makes sure every open campaign has its deadline job (after a restart). Returns how many were scheduled.</summary>
    public async Task<int> EnsureScheduledAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken = default)
    {
        var open = await _store.OpenAsync(connection, transaction, cancellationToken);
        foreach (var campaign in open)
            await ScheduleAsync(connection, transaction, campaign, cancellationToken);
        return open.Count;
    }

    private async Task PhaseAsync(JobContext job, CancellationToken cancellationToken)
    {
        var campaignId = job.Payload<PhaseJob>()!.CampaignId;
        var current = await _store.GetAsync(job.Connection, job.Transaction, campaignId, true, cancellationToken);
        if (current is null)
            return;
        var step = CampaignMachine.Advance(current, job.NowMs);
        if (step.Transitions.Count > 0)
            await _store.UpdateAsync(job.Connection, job.Transaction, step.Campaign, cancellationToken);
        await ApplyTransitionsAsync(job.Connection, job.Transaction, step, cancellationToken);
        if (step.Campaign.State != CampaignStates.Terminal)
            job.RunAgainAt(step.Campaign.StateExpiresMs);
    }

    /// <summary>
    /// Announces each transition (after the commit, through the outbox). A win also changes the ambition of both sides in the same transaction: the winner
    /// gains a level, the loser gives one back.
    /// </summary>
    private async Task ApplyTransitionsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, MachineStep step, CancellationToken cancellationToken)
    {
        foreach (var transition in step.Transitions)
        {
            if (transition.To is CampaignStates.PirateWin or CampaignStates.AntipirateWin)
            {
                var (pirate, antipirate) = await _store.AmbitionAsync(connection, transaction, step.Campaign.WarzoneId, cancellationToken);
                var pirateWon = transition.To == CampaignStates.PirateWin;
                await _store.SetAmbitionAsync(connection, transaction, step.Campaign.WarzoneId,
                    pirateWon ? InsurgencyRules.WinnerModifier(pirate) : InsurgencyRules.LoserModifier(pirate),
                    pirateWon ? InsurgencyRules.LoserModifier(antipirate) : InsurgencyRules.WinnerModifier(antipirate),
                    cancellationToken);
            }
            if (transition.To == CampaignStates.PirateWin && _loyalty is not null)
                await PayContributorsAsync(connection, transaction, step.Campaign, cancellationToken);
            await _events.PublishInTransactionAsync(connection, transaction, CampaignChangedTopic,
                new CampaignChanged(step.Campaign.CampaignId, transition.From, transition.To, transition.AtMs, step.Campaign), KeyOf(step.Campaign.CampaignId), cancellationToken);
        }
    }

    /// <summary>
    /// The victory payout (the wiki: a win pays every contributor 1,500,000 LP): one credit per contributor from the pirate corporation of the warzone, keyed by the
    /// campaign and the character, so a replayed win credits nothing.
    /// </summary>
    private async Task PayContributorsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Campaign campaign, CancellationToken cancellationToken)
    {
        var warzone = InsurgencyWarzones.All.Single(w => w.WarzoneId == campaign.WarzoneId);
        foreach (var character in await _store.ContributorsAsync(connection, transaction, campaign.CampaignId, cancellationToken))
        {
            await _loyalty!.ApplyAsync(connection, transaction, new LoyaltyHolder(LoyaltyHolderKind.Character, character), warzone.PirateCorporationId,
                InsurgencyRules.VictoryLp, "insurgency_victory", $"insurgency:{campaign.CampaignId}:{character}", cancellationToken);
        }
    }

    private Task ScheduleAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Campaign campaign, CancellationToken cancellationToken)
    {
        if (campaign.State == CampaignStates.Terminal)
            return Task.CompletedTask;
        return _scheduler.ScheduleAsync(connection, transaction, new JobSpec(JobKind, campaign.StateExpiresMs)
        {
            Key = KeyOf(campaign.CampaignId),
            Payload = new PhaseJob(campaign.CampaignId),
        }, cancellationToken);
    }
}

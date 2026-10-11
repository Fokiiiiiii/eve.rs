namespace Eve.Insurgency;

/// <summary>A campaign as the state machine and the store see it. Times are unix milliseconds.</summary>
/// <param name="StateExpiresMs">When the current state gives way: the forecast end, the active maximum, or the end of a wind-down.</param>
/// <param name="MaxEndsMs">The start plus the maximum length; the active state cannot run past it.</param>
public sealed record Campaign(
    long CampaignId,
    int PirateFactionId,
    long WarzoneId,
    long OriginSolarSystemId,
    IReadOnlyList<long> CoveredSolarSystemIds,
    long StructureId,
    int State,
    long StartedMs,
    long MaxEndsMs,
    long StateExpiresMs,
    int PiratePointsRequired,
    int AntipiratePointsRequired,
    int PiratePointsScored,
    int AntipiratePointsScored,
    long EndedMs);

/// <summary>A state change: the states before and after, and the moment it happened (the campaign's own deadline, not the time it was noticed).</summary>
public sealed record CampaignTransition(int From, int To, long AtMs);

/// <summary>The result of a step: the campaign after it, the transitions in order, and whether a point was accepted (points count only while active).</summary>
public sealed record MachineStep(Campaign Campaign, IReadOnlyList<CampaignTransition> Transitions, bool Accepted = false);

/// <summary>The campaign state machine: pure functions over <see cref="Campaign"/>; the service stores the result and publishes the transitions.</summary>
public static class CampaignMachine
{
    /// <summary>A campaign that has just started: forecasting until the forecast ends. Its required counts are fixed now (the ambition at the start).</summary>
    public static Campaign Start(
        int pirateFactionId, long warzoneId, long originSolarSystemId, IReadOnlyList<long> coveredSolarSystemIds, long structureId, long startedMs,
        int pirateRequired, int antipirateRequired) =>
        new(0, pirateFactionId, warzoneId, originSolarSystemId, coveredSolarSystemIds, structureId, CampaignStates.Forecasting, startedMs,
            startedMs + InsurgencyRules.MaxLengthMs, startedMs + InsurgencyRules.ForecastMs, pirateRequired, antipirateRequired, 0, 0, 0);

    /// <summary>
    /// Runs the timers up to <paramref name="nowMs"/>: forecasting ends in active; active ends with no winner at the maximum length; a finished campaign
    /// ends (terminal) when its wind-down ends. Every deadline that has passed is taken in order, so a late timer lands on the same states as an early one.
    /// </summary>
    public static MachineStep Advance(Campaign campaign, long nowMs)
    {
        var transitions = new List<CampaignTransition>();
        var c = campaign;
        while (true)
        {
            switch (c.State)
            {
                case CampaignStates.Forecasting when nowMs >= c.StateExpiresMs:
                {
                    var at = c.StateExpiresMs;
                    c = c with { State = CampaignStates.Active, StateExpiresMs = c.MaxEndsMs };
                    transitions.Add(new CampaignTransition(CampaignStates.Forecasting, CampaignStates.Active, at));
                    continue;
                }
                case CampaignStates.Active when nowMs >= c.MaxEndsMs:
                {
                    var at = c.MaxEndsMs;
                    c = c with { State = CampaignStates.NoWinner, EndedMs = at, StateExpiresMs = at + InsurgencyRules.WindDownMs };
                    transitions.Add(new CampaignTransition(CampaignStates.Active, CampaignStates.NoWinner, at));
                    continue;
                }
                case CampaignStates.PirateWin or CampaignStates.AntipirateWin or CampaignStates.NoWinner when nowMs >= c.StateExpiresMs:
                {
                    var at = c.StateExpiresMs;
                    transitions.Add(new CampaignTransition(c.State, CampaignStates.Terminal, at));
                    c = c with { State = CampaignStates.Terminal, StateExpiresMs = 0 };
                    continue;
                }
                default:
                    return new MachineStep(c, transitions);
            }
        }
    }

    /// <summary>
    /// A system scores a point for a side at <paramref name="nowMs"/>. Refused (no change) unless the campaign is active. The side that reaches its
    /// required count wins at once; the win starts the wind-down.
    /// </summary>
    public static MachineStep Score(Campaign campaign, Side side, long nowMs)
    {
        if (campaign.State != CampaignStates.Active)
            return new MachineStep(campaign, [], false);
        var c = side == Side.Pirate
            ? campaign with { PiratePointsScored = campaign.PiratePointsScored + 1 }
            : campaign with { AntipiratePointsScored = campaign.AntipiratePointsScored + 1 };
        if (side == Side.Pirate && c.PiratePointsScored >= c.PiratePointsRequired)
            return Won(c, CampaignStates.PirateWin, nowMs);
        if (side == Side.Antipirate && c.AntipiratePointsScored >= c.AntipiratePointsRequired)
            return Won(c, CampaignStates.AntipirateWin, nowMs);
        return new MachineStep(c, [], true);
    }

    private static MachineStep Won(Campaign c, int winState, long nowMs)
    {
        var won = c with { State = winState, EndedMs = nowMs, StateExpiresMs = nowMs + InsurgencyRules.WindDownMs };
        return new MachineStep(won, [new CampaignTransition(CampaignStates.Active, winState, nowMs)], true);
    }
}

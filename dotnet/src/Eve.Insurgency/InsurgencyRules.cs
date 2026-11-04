namespace Eve.Insurgency;

/// <summary>The campaign states, as the V24.01 client names them (pirateinsurgency/const.py:6-13).</summary>
public static class CampaignStates
{
    public const int Initial = 1;
    public const int Forecasting = 2;
    public const int Active = 3;
    public const int PirateWin = 4;
    public const int AntipirateWin = 5;
    public const int NoWinner = 6;
    public const int Cleanup = 7;
    public const int Terminal = 99;

    /// <summary>The states the client is shown: a campaign that is running or has ended and is in its wind-down.</summary>
    public static bool IsVisible(int state) => state is Forecasting or Active or PirateWin or AntipirateWin or NoWinner;

    public static bool IsEnded(int state) => state is PirateWin or AntipirateWin or NoWinner;
}

/// <summary>The two sides of a warzone: the pirates (corruption) and the empire militias (suppression).</summary>
public enum Side
{
    Pirate,
    Antipirate,
}

public static class InsurgencyRules
{
    public const long ForecastMs = 48L * 3_600_000;
    public const long MaxLengthMs = 7L * 24 * 3_600_000;
    public const long WindDownMs = 48L * 3_600_000;
    public const long ReinforcementBaseMs = 3L * 3_600_000;
    public const long ReinforcementJitterMs = 3_600_000;
    public const int BaseRequiredSystems = 5;
    public const int MaxAmbitionModifier = 5;
    public const int SpreadCorruptionLevel = 3;
    public const long MaxWinningContributionLp = 1_500_000;

    public const long VictoryLp = MaxWinningContributionLp;
    public const double WinningMultiplier = 1.0;
    public const double LosingMultiplier = 0.4;
    public const double MaxPayoutShare = 0.45;

    /// <summary>The systems a side must fully corrupt or suppress for its modifier: 5 at no ambition, 10 at the maximum.</summary>
    public static int RequiredSystems(int ambitionModifier) => BaseRequiredSystems + Math.Clamp(ambitionModifier, 0, MaxAmbitionModifier);

    /// <summary>A FOB's armor reinforcement: 3 h with a variance of 1 h, so 2 to 4 h. <paramref name="roll"/> is in [0, 1].</summary>
    public static long ReinforcementMs(double roll) =>
        ReinforcementBaseMs - ReinforcementJitterMs + (long)Math.Round(2 * ReinforcementJitterMs * Math.Clamp(roll, 0, 1));

    /// <summary>A side that wins a campaign gains one ambition level, up to the maximum.</summary>
    public static int WinnerModifier(int modifier) => Math.Min(modifier + 1, MaxAmbitionModifier);

    /// <summary>A side that loses a campaign gives back one ambition level, down to none.</summary>
    public static int LoserModifier(int modifier) => Math.Max(modifier - 1, 0);
}

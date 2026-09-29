namespace Eve.Skills;

/// <summary>How many skill points each level of a skill costs.</summary>
public static class SkillPoints
{
    public const int MaxLevel = 5;

    /// <summary>
    /// The points a skill of <paramref name="rank"/> has when trained to <paramref name="level"/>:
    /// <c>250 * rank * sqrt(32)^(level - 1)</c>, rounded (level 4 of a rank 1 skill is 45,255). Level 0 has none.
    /// </summary>
    public static long At(int rank, int level)
    {
        if (level is < 0 or > MaxLevel)
        {
            throw new ArgumentOutOfRangeException(nameof(level), level, "a skill has levels 0 to 5");
        }
        return level == 0 ? 0 : (long)Math.Round(250d * rank * Math.Pow(Math.Sqrt(32d), level - 1), MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// The points at which training counts <paramref name="level"/> of a skill of <paramref name="rank"/> reached: <c>250 * rank * 2^(2.5 * (level - 1))</c>
    /// rounded up, so level II of a rank 1 skill is 1,415 (the reference's queue and the game's tables do this; the skills a character starts with
    /// come from <see cref="At"/>). A level outside 0 to 5 counts as the nearest.
    /// </summary>
    public static long ForTraining(int rank, int level)
    {
        var bounded = Math.Clamp(level, 0, MaxLevel);
        return bounded == 0 ? 0 : (long)Math.Ceiling(rank * 250d * Math.Pow(2, 2.5 * (bounded - 1)));
    }

    /// <summary>The highest level whose training threshold <paramref name="points"/> reach.</summary>
    public static int LevelAt(int rank, long points)
    {
        for (var level = MaxLevel; level >= 1; level--)
        {
            if (points >= ForTraining(rank, level))
            {
                return level;
            }
        }
        return 0;
    }
}


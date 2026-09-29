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
}

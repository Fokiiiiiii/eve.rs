namespace Eve.Services.Core;

/// <summary>
/// The V24.01 industry arithmetic (the client's <c>industry</c> package, <c>industry/const.py</c>,
/// <c>activities/*.py</c>, <c>job.py</c>), as pure functions. Persistence feeds it SDE and ESI inputs;
/// the client's own quote values are never read here.
/// </summary>
public static class IndustryRules
{
    public const int Manufacturing = 1;
    public const int ResearchTime = 3;
    public const int ResearchMaterial = 4;
    public const int Copying = 5;
    public const int Invention = 8;
    public const int Reaction = 9;

    public const double CostPercentage = 0.02;
    public const double SccSurchargeRate = 0.04;
    public const double SccResearchDiscount = 0.5;
    public const double IndustrySecurityTaxRate = 0.05;
    public const int MaxMaterialEfficiency = 10;
    public const int MaxTimeEfficiency = 20;
    public const int StepTimeEfficiency = 2;
    public const int InventionMaterialEfficiency = 2;
    public const int InventionTimeEfficiency = 4;
    public const double InventionSkillProbability = 1.0 / 30.0;
    public const int MaxRunLengthSeconds = 2592000;
    public const int MaxRuns = 500;

    /// <summary>Research time of level i relative to level 0 (the client's RESEARCH_TIMES).</summary>
    public static readonly double[] ResearchTimes =
        [1.0, 29 / 21.0, 23 / 7.0, 39 / 5.0, 278 / 15.0, 928 / 21.0, 2200 / 21.0, 5251 / 21.0, 4163 / 7.0, 29660 / 21.0];

    /// <summary>Activities that price their blueprint with the manufacturing materials (the client's SCIENCE_ACTIVITIES).</summary>
    public static bool IsScience(int activityId) => activityId is ResearchTime or ResearchMaterial or Copying or Invention;

    /// <summary>The SDE blueprint key of an activity id (the <c>blueprints</c> table's <c>activities</c> object).</summary>
    public static string ActivityKey(int activityId) => activityId switch
    {
        Manufacturing => "manufacturing",
        ResearchTime => "research_time",
        ResearchMaterial => "research_material",
        Copying => "copying",
        Invention => "invention",
        Reaction => "reaction",
        _ => string.Empty,
    };

    /// <summary>The activity whose materials price a blueprint (the client's GetBlueprintPrice mapping).</summary>
    public static int PricingActivity(int activityId) => IsScience(activityId) ? Manufacturing : activityId;

    /// <summary>
    /// Material quantity of one material row: base x modifiers x material runs, rounded to two decimals,
    /// rounded up, and never less than one per run (the client's Job._get_materials).
    /// </summary>
    public static long MaterialQuantity(long baseQuantity, double modifier, long materialRuns, long runs)
    {
        var quantity = baseQuantity * modifier * materialRuns;
        var rounded = Math.Round(quantity, 2, MidpointRounding.AwayFromZero);
        return Math.Max((long)Math.Ceiling(rounded), runs);
    }

    /// <summary>Manufacturing material multiplier of a blueprint's material efficiency (ME 10 gives 0.90).</summary>
    public static double MaterialEfficiencyModifier(int materialEfficiency) => 1.0 - materialEfficiency / 100.0;

    /// <summary>Manufacturing time multiplier of a blueprint's time efficiency.</summary>
    public static double TimeEfficiencyModifier(int timeEfficiency) => 1.0 - timeEfficiency / 100.0;

    /// <summary>
    /// A skill's time modifier: the skill attribute (percent per level) times the level, as the client's
    /// REQUIRED_SKILL_MODIFIERS and the character skill bonuses do (1 + amount x level / 100).
    /// </summary>
    public static double SkillModifier(double attributePerLevelPercent, int level) =>
        1.0 + attributePerLevelPercent * level / 100.0;

    /// <summary>The base job cost in ISK, rounded (the client's <c>int(round(job_cost))</c>).</summary>
    public static long Round(double value) => (long)Math.Round(value, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Job cost parts, each a rounded share of the base cost: the system cost index, the facility tax, the SCC
    /// surcharge (halved for research) and the high-security industry tax (the client's Job cost properties).
    /// </summary>
    public static InstallCostParts CostParts(
        double baseCost, double costIndex, double facilityTax, bool highSecurity, int activityId, double sccModifier = 1.0)
    {
        var sccRate = Math.Max(0, SccSurchargeRate * sccModifier);
        if (activityId is ResearchTime or ResearchMaterial)
            sccRate *= SccResearchDiscount;
        var baseRounded = Round(baseCost);
        return new InstallCostParts(
            Cost: Round(baseRounded * Math.Max(0, costIndex)),
            Tax: Round(baseRounded * Math.Max(0, facilityTax)),
            Scc: Round(baseRounded * sccRate),
            SecurityTax: highSecurity ? Round(baseRounded * IndustrySecurityTaxRate) : 0);
    }

    /// <summary>The job's total ISK: the sum of its cost parts.</summary>
    public static long Total(InstallCostParts parts) => parts.Cost + parts.Tax + parts.Scc + parts.SecurityTax;

    /// <summary>Job duration in seconds: the base seconds times every modifier, rounded (the client's Job._get_time).</summary>
    public static long DurationSeconds(double baseSeconds, IEnumerable<double> modifiers)
    {
        var seconds = baseSeconds;
        foreach (var modifier in modifiers)
            seconds *= modifier;
        return Round(seconds);
    }

    /// <summary>
    /// Time of a research job from a level: the sum over its runs of the base time at each level (capped at
    /// the last level), the client's ResearchMaterial/ResearchTime.job_time.
    /// </summary>
    public static double ResearchJobSeconds(double activityTimeSeconds, int startLevel, int runs)
    {
        double total = 0;
        for (var i = 0; i < runs; i++)
            total += activityTimeSeconds * ResearchTimes[Math.Min(startLevel + i, ResearchTimes.Length - 1)];
        return total;
    }

    /// <summary>The number of ME (or TE) levels a blueprint can still research.</summary>
    public static int ResearchRunsLeft(int efficiency, int maximum, int step) => Math.Max(0, (maximum - efficiency) / step);

    /// <summary>
    /// Invention success probability: the mean base probability of the products, times one plus the sum of the
    /// invention skill levels (1/30 each), times the decryptor's probability multiplier, clamped to [0, 1].
    /// </summary>
    public static double InventionProbability(IReadOnlyList<double> productProbabilities, IEnumerable<int> skillLevels, double decryptorMultiplier)
    {
        var baseProbability = productProbabilities.Count == 0 ? 1.0 : productProbabilities.Average();
        var skillFactor = 1.0 + skillLevels.Sum(level => level * InventionSkillProbability);
        return Math.Clamp(baseProbability * skillFactor * decryptorMultiplier, 0.0, 1.0);
    }

    /// <summary>
    /// The successful invention attempts: each attempt succeeds when its roll is at most the probability.
    /// The roll sequence is fixed by the job's seed, so a retried delivery gives the same outcome.
    /// </summary>
    public static int SuccessfulAttempts(int runs, double probability, int seed)
    {
        var random = new Random(seed);
        var successes = 0;
        for (var i = 0; i < runs; i++)
            if (random.NextDouble() <= probability)
                successes++;
        return successes;
    }
}

/// <summary>The four rounded job cost parts in ISK.</summary>
public sealed record InstallCostParts(long Cost, long Tax, long Scc, long SecurityTax);

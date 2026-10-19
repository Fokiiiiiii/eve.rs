using System.Text.Json;
using static Eve.Services.Core.IndustryRules;

namespace Eve.Services.Core;

/// <summary>
/// The V24.01 industry arithmetic (the client's <c>industry</c> package, <c>industry/const.py</c>,
/// <c>activities/*.py</c>, <c>job.py</c>), as pure functions. Persistence feeds it SDE and ESI inputs;
/// the client's own quote values are never read here.
/// </summary>
public static class IndustryRules
{
    public const int Manufacturing = 1;
    /// <summary>
    /// The activities a structure runs from its online service ids (STRUCTURE_SERVICE_ID): manufacturing needs a Manufacturing
    /// service (21 to 24); each science activity needs the Laboratory (30) or its own laboratory service (31 to 34); reactions need

    /// </summary>
    public static IReadOnlySet<int> StructureActivities(IEnumerable<int> onlineServices)
    {
        var online = onlineServices as ISet<int> ?? new HashSet<int>(onlineServices);
        var activities = new HashSet<int>();
        if (online.Overlaps([21, 22, 23, 24])) activities.Add(Manufacturing);
        if (online.Overlaps([30, 31])) activities.Add(ResearchTime);
        if (online.Overlaps([30, 32])) activities.Add(ResearchMaterial);
        if (online.Overlaps([30, 33])) activities.Add(Copying);
        if (online.Overlaps([30, 34])) activities.Add(Invention);
        if (online.Overlaps([40, 41, 42, 43])) activities.Add(Reaction);
        return activities;
    }

    /// <summary>The client's flagCorpDeliveries (inventorycommon/const.py) and the corporation hangar divisions 1..7 (flagCorpSAG1 = 115).</summary>
    public const int CorporationDeliveriesFlag = 62;
    public const int CorporationHangarFirstFlag = 115;
    public const int CorporationHangarLastFlag = 121;

    /// <summary>Where a corporation's job may put its outputs: the deliveries or a corporation hangar division.</summary>
    public static bool IsCorporationOutputFlag(int flag) =>
        flag == CorporationDeliveriesFlag || flag is >= CorporationHangarFirstFlag and <= CorporationHangarLastFlag;
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
    public const double InventionLowerSkillProbability = 1.0 / 40.0;
    public const int JumpsPerDistanceLevel = 5;
    public const int MaxIndustryDistanceJumps = 25;
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
        var rounded = Math.Round(quantity, 2, MidpointRounding.ToEven);
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
    public static long Round(double value) => (long)Math.Round(value, MidpointRounding.ToEven);

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

    /// <summary>The invention skill bonus: each skill level adds 1/30, or 1/40 for a skill on the lower-probability list.</summary>
    public static double InventionSkillBonus(IEnumerable<(int Level, bool LowerProbability)> skills) =>
        skills.Sum(skill => skill.Level * (skill.LowerProbability ? InventionLowerSkillProbability : InventionSkillProbability));

    /// <summary>
    /// Invention success probability: the mean base probability of the products, times one plus the skill bonus, times the
    /// decryptor's probability multiplier, clamped to [0, 1].
    /// </summary>
    public static double InventionProbability(IReadOnlyList<double> productProbabilities, double skillBonus, double decryptorMultiplier)
    {
        var baseProbability = productProbabilities.Count == 0 ? 1.0 : productProbabilities.Average();
        return Math.Clamp(baseProbability * (1.0 + skillBonus) * decryptorMultiplier, 0.0, 1.0);
    }

    /// <summary>The jumps a facility may be from the installer: 5 per level of the distance skill, at most 25 (the client's DISTANCE_PER_LEVEL).</summary>
    public static int MaxDistanceJumps(int distanceSkillLevel) => Math.Min(MaxIndustryDistanceJumps, JumpsPerDistanceLevel * Math.Max(0, distanceSkillLevel));

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

/// <summary>The quantity a facility modifier scales: the job time, the material quantity or the job cost.</summary>
public enum IndustryBonusKind
{
    Time,
    Material,
    Cost,
}

/// <summary>
/// One facility modifier on one activity, as the client's industry Facility carries it (industry/modifiers.py). It applies to a
/// job whose product is in <see cref="CategoryId"/> or <see cref="GroupId"/> when one is set, and to every job of the activity
/// otherwise. <see cref="Source"/> is the client's industry.Reference (11 rig, 12 hull).
/// </summary>
public sealed record IndustryBonus(
    IndustryBonusKind Kind,
    int ActivityId,
    double Amount,
    int Source,
    int? CategoryId = null,
    int? GroupId = null,
    int? RigTypeId = null);

/// <summary>A structure-domain PostPercent modifier of a rig effect: the structure attribute it sets and the rig attribute it reads.</summary>
public sealed record RigModifier(int ModifiedAttributeId, int ModifyingAttributeId);

/// <summary>The SDE dogma of one fitted rig: its attributes and the structure-domain modifiers of its effects.</summary>
public sealed record RigFit(int TypeId, IReadOnlyDictionary<int, double> Attributes, IReadOnlyList<RigModifier> Modifiers);

/// <summary>
/// The facility modifiers of a structure, from the SDE: the rigs fitted in its rig slots and its own hull (role) bonuses.
/// Each rig effect names the structure attribute it changes (for example 2546, basic medium ship manufacturing material) and the
/// rig attribute that sets the amount (2594, a percentage). The structure attributes map to job targets in
/// <see cref="Targets"/>; the rig's percentage is scaled by the security band of the structure's system (attributes 2355 to 2357).
/// </summary>
public static class IndustryBonuses
{
    /// <summary>The client's industry.Reference values (industry/const.py).</summary>
    public const int SourceRig = 11;
    public const int SourceHull = 12;

    /// <summary>The structure rig slots (flagRigSlot0..7, 92 to 99).</summary>
    public const int FirstRigSlotFlag = 92;
    public const int LastRigSlotFlag = 99;

    private const int DogmaPostPercent = 6;
    private const int HiSecModifierAttribute = 2355;
    private const int LowSecModifierAttribute = 2356;
    private const int NullSecModifierAttribute = 2357;
    private const int EngineeringMaterialAttribute = 2600;
    private const int EngineeringCostAttribute = 2601;
    private const int EngineeringTimeAttribute = 2602;
    private const int RefineryReactionTimeAttribute = 2721;

    /// <summary>Rig attributes that the security band scales (the rig's time, material, cost and an extra percentage).</summary>
    private static readonly HashSet<int> SecurityScaledAttributes = [2593, 2594, 2595, 2653, 2713, 2714];

    /// <summary>The activities an engineering complex's time and cost bonuses reach (the client's HULL_MODIFIER_ACTIVITIES).</summary>
    private static readonly int[] HullActivities = [Manufacturing, ResearchTime, ResearchMaterial, Copying, Invention];

    // Ship classes of the structure target lists (SDE groups in category 6; capital and advanced components in 17).
    private static readonly int[] BasicSmallShips = [25, 420, 31];
    private static readonly int[] BasicMediumShips = [26, 419, 1201, 4902, 28, 463];
    private static readonly int[] BasicLargeShips = [27, 513, 941];
    private static readonly int[] AdvancedSmallShips = [324, 830, 831, 834, 893, 1527, 1283, 541, 1534, 1305];
    private static readonly int[] AdvancedMediumShips = [358, 832, 833, 906, 894, 540, 380, 1202, 543, 963, 954, 956, 957, 958];
    private static readonly int[] AdvancedLargeShips = [898, 900, 902];
    private static readonly int[] CapitalShips = [30, 485, 547, 659, 883, 1538, 4594, 5120];
    private static readonly int[] AdvancedComponents = [334, 332, 716, 964];
    private static readonly int[] BasicCapitalComponents = [873];
    private static readonly int[] AdvancedCapitalComponents = [913];
    private static readonly int[] StructureCategories = [23, 65, 66];
    private static readonly int[] StructureGroups = [536, 1136];
    private static readonly int[] ReactionHybridPolymers = [974];
    private static readonly int[] ReactionComposites = [428, 429, 4932];
    private static readonly int[] ReactionBiochemicals = [712, 4096];

    /// <summary>A job target: the kind and activity, limited to a product category or group when set.</summary>
    public sealed record BonusTarget(IndustryBonusKind Kind, int ActivityId, int? CategoryId = null, int? GroupId = null);

    private static BonusTarget[] ByCategory(IndustryBonusKind kind, int activityId, params int[] categories) =>
        categories.Select(category => new BonusTarget(kind, activityId, CategoryId: category)).ToArray();

    private static BonusTarget[] ByGroup(IndustryBonusKind kind, int activityId, params int[] groups) =>
        groups.Select(group => new BonusTarget(kind, activityId, GroupId: group)).ToArray();

    private static BonusTarget[] Concat(params BonusTarget[][] lists) => lists.SelectMany(list => list).ToArray();

    /// <summary>The structure attribute of each rig effect and the jobs it targets (CCP structure rig attributes).</summary>
    public static readonly IReadOnlyDictionary<int, BonusTarget[]> Targets = new Dictionary<int, BonusTarget[]>
    {
        [2538] = ByCategory(IndustryBonusKind.Material, Manufacturing, 7),
        [2539] = ByCategory(IndustryBonusKind.Time, Manufacturing, 7),
        [2540] = ByCategory(IndustryBonusKind.Material, Manufacturing, 8),
        [2541] = ByCategory(IndustryBonusKind.Time, Manufacturing, 8),
        [2542] = ByCategory(IndustryBonusKind.Material, Manufacturing, 18, 87),
        [2543] = ByCategory(IndustryBonusKind.Time, Manufacturing, 18, 87),
        [2544] = ByGroup(IndustryBonusKind.Material, Manufacturing, BasicSmallShips),
        [2545] = ByGroup(IndustryBonusKind.Time, Manufacturing, BasicSmallShips),
        [2546] = ByGroup(IndustryBonusKind.Material, Manufacturing, BasicMediumShips),
        [2547] = ByGroup(IndustryBonusKind.Time, Manufacturing, BasicMediumShips),
        [2548] = ByGroup(IndustryBonusKind.Material, Manufacturing, BasicLargeShips),
        [2549] = ByGroup(IndustryBonusKind.Time, Manufacturing, BasicLargeShips),
        [2550] = ByGroup(IndustryBonusKind.Material, Manufacturing, AdvancedSmallShips),
        [2551] = ByGroup(IndustryBonusKind.Time, Manufacturing, AdvancedSmallShips),
        [2552] = ByGroup(IndustryBonusKind.Material, Manufacturing, AdvancedMediumShips),
        [2553] = ByGroup(IndustryBonusKind.Time, Manufacturing, AdvancedMediumShips),
        [2555] = ByGroup(IndustryBonusKind.Material, Manufacturing, AdvancedLargeShips),
        [2556] = ByGroup(IndustryBonusKind.Time, Manufacturing, AdvancedLargeShips),
        [2557] = ByGroup(IndustryBonusKind.Material, Manufacturing, AdvancedComponents),
        [2558] = ByGroup(IndustryBonusKind.Time, Manufacturing, AdvancedComponents),
        [2559] = ByGroup(IndustryBonusKind.Material, Manufacturing, BasicCapitalComponents),
        [2560] = ByGroup(IndustryBonusKind.Time, Manufacturing, BasicCapitalComponents),
        [2561] = Concat(ByCategory(IndustryBonusKind.Material, Manufacturing, StructureCategories),
            ByGroup(IndustryBonusKind.Material, Manufacturing, StructureGroups)),
        [2562] = Concat(ByCategory(IndustryBonusKind.Time, Manufacturing, StructureCategories),
            ByGroup(IndustryBonusKind.Time, Manufacturing, StructureGroups)),
        [2563] = [new BonusTarget(IndustryBonusKind.Cost, Invention)],
        [2564] = [new BonusTarget(IndustryBonusKind.Time, Invention)],
        [2565] = [new BonusTarget(IndustryBonusKind.Cost, ResearchMaterial)],
        [2566] = [new BonusTarget(IndustryBonusKind.Time, ResearchMaterial)],
        [2567] = [new BonusTarget(IndustryBonusKind.Cost, ResearchTime)],
        [2568] = [new BonusTarget(IndustryBonusKind.Time, ResearchTime)],
        [2569] = [new BonusTarget(IndustryBonusKind.Cost, Copying)],
        [2570] = [new BonusTarget(IndustryBonusKind.Time, Copying)],
        [2575] = ByGroup(IndustryBonusKind.Material, Manufacturing, CapitalShips),
        [2576] = ByGroup(IndustryBonusKind.Time, Manufacturing, CapitalShips),
        [2591] = ByCategory(IndustryBonusKind.Time, Manufacturing, 6),
        [2592] = ByCategory(IndustryBonusKind.Material, Manufacturing, 6),
        [2658] = ByGroup(IndustryBonusKind.Material, Manufacturing, AdvancedCapitalComponents),
        [2659] = ByGroup(IndustryBonusKind.Time, Manufacturing, AdvancedCapitalComponents),
        [2715] = ByGroup(IndustryBonusKind.Time, Reaction, ReactionHybridPolymers),
        [2716] = ByGroup(IndustryBonusKind.Material, Reaction, ReactionHybridPolymers),
        [2717] = ByGroup(IndustryBonusKind.Time, Reaction, ReactionComposites),
        [2718] = ByGroup(IndustryBonusKind.Material, Reaction, ReactionComposites),
        [2719] = ByGroup(IndustryBonusKind.Time, Reaction, ReactionBiochemicals),
        [2720] = ByGroup(IndustryBonusKind.Material, Reaction, ReactionBiochemicals),
    };

    /// <summary>True for a structure's rig slot flag (92 to 99).</summary>
    public static bool IsRigSlot(int flag) => flag is >= FirstRigSlotFlag and <= LastRigSlotFlag;

    /// <summary>The structure attribute of the security band: high (0.45 and up), low (above 0) or null.</summary>
    public static int SecurityBandAttribute(double security) =>
        security >= 0.45 ? HiSecModifierAttribute : security > 0 ? LowSecModifierAttribute : NullSecModifierAttribute;

    /// <summary>The attribute values of an SDE typeDogma row (<c>dogmaAttributes</c>).</summary>
    public static IReadOnlyDictionary<int, double> ParseAttributes(JsonElement typeDogma)
    {
        var attributes = new Dictionary<int, double>();
        if (typeDogma.ValueKind != JsonValueKind.Object ||
            !typeDogma.TryGetProperty("dogmaAttributes", out var list) || list.ValueKind != JsonValueKind.Array)
            return attributes;
        foreach (var row in list.EnumerateArray())
        {
            var id = IntOf(row, "attributeID");
            var value = DoubleOf(row, "value");
            if (id is { } attributeId && value is { } number)
                attributes[attributeId] = number;
        }
        return attributes;
    }

    /// <summary>The effect ids of an SDE typeDogma row (<c>dogmaEffects</c>).</summary>
    public static IReadOnlyList<int> EffectIds(JsonElement typeDogma)
    {
        var ids = new List<int>();
        if (typeDogma.ValueKind == JsonValueKind.Object &&
            typeDogma.TryGetProperty("dogmaEffects", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var row in list.EnumerateArray())
                if (IntOf(row, "effectID") is { } effectId)
                    ids.Add(effectId);
        return ids;
    }

    /// <summary>
    /// A rig from its SDE rows: the typeDogma row and the dogmaEffects rows of its effects. Only the structure-domain PostPercent
    /// modifiers are kept (the rig's bonuses on the structure).
    /// </summary>
    public static RigFit ParseRig(int typeId, JsonElement typeDogma, IEnumerable<JsonElement> effects)
    {
        var modifiers = new List<RigModifier>();
        foreach (var effect in effects)
        {
            if (effect.ValueKind != JsonValueKind.Object || !effect.TryGetProperty("modifierInfo", out var info) ||
                info.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var modifier in info.EnumerateArray())
            {
                if (StringOf(modifier, "func") != "ItemModifier" || StringOf(modifier, "domain") != "structureID" ||
                    IntOf(modifier, "operation") != DogmaPostPercent)
                    continue;
                if (IntOf(modifier, "modifiedAttributeID") is { } modified &&
                    IntOf(modifier, "modifyingAttributeID") is { } modifying)
                    modifiers.Add(new RigModifier(modified, modifying));
            }
        }
        return new RigFit(typeId, ParseAttributes(typeDogma), modifiers);
    }

    /// <summary>
    /// The bonuses of one fitted rig at a system of the given security status. The rig's source attribute is scaled by its
    /// security band's multiplier when it is a security-scaled attribute; the amount is one plus that percentage.
    /// </summary>
    public static IReadOnlyList<IndustryBonus> RigBonuses(RigFit rig, double security)
    {
        var bonuses = new List<IndustryBonus>();
        foreach (var modifier in rig.Modifiers)
        {
            if (!Targets.TryGetValue(modifier.ModifiedAttributeId, out var targets))
                continue;
            var value = rig.Attributes.GetValueOrDefault(modifier.ModifyingAttributeId, 0d);
            if (SecurityScaledAttributes.Contains(modifier.ModifyingAttributeId))
                value *= rig.Attributes.GetValueOrDefault(SecurityBandAttribute(security), 1d);
            var amount = Math.Max(0, 1 + value / 100);
            if (!(amount > 0) || Math.Abs(amount - 1) <= 1e-9)
                continue;
            foreach (var target in targets)
                bonuses.Add(new IndustryBonus(target.Kind, target.ActivityId, amount, SourceRig,
                    target.CategoryId, target.GroupId, rig.TypeId));
        }
        return bonuses;
    }

    /// <summary>
    /// The hull (role) bonuses of a structure type: an engineering complex's material (2600) on manufacturing, its cost (2601) and
    /// time (2602) on the five industry activities, and a refinery's reaction time (2721). The attribute is the amount itself.
    /// </summary>
    public static IReadOnlyList<IndustryBonus> HullBonuses(IReadOnlyDictionary<int, double> attributes)
    {
        var bonuses = new List<IndustryBonus>();
        void Add(int attributeId, IndustryBonusKind kind, params int[] activities)
        {
            var amount = attributes.GetValueOrDefault(attributeId, 1d);
            if (!(amount > 0) || Math.Abs(amount - 1) <= 1e-9)
                return;
            foreach (var activityId in activities)
                bonuses.Add(new IndustryBonus(kind, activityId, amount, SourceHull));
        }
        Add(EngineeringMaterialAttribute, IndustryBonusKind.Material, Manufacturing);
        Add(EngineeringCostAttribute, IndustryBonusKind.Cost, HullActivities);
        Add(EngineeringTimeAttribute, IndustryBonusKind.Time, HullActivities);
        Add(RefineryReactionTimeAttribute, IndustryBonusKind.Time, Reaction);
        return bonuses;
    }

    /// <summary>
    /// The product of the amounts of the bonuses that apply to a job: the kind and activity, and the product's category or group
    /// where a bonus names one. Every bonus that applies multiplies, as the client's job modifiers do (no stacking penalty).
    /// </summary>
    public static double Multiplier(
        IEnumerable<IndustryBonus> bonuses, IndustryBonusKind kind, int activityId, int? categoryId, int? groupId)
    {
        double product = 1;
        foreach (var bonus in bonuses)
        {
            if (bonus.Kind != kind || bonus.ActivityId != activityId)
                continue;
            if (bonus.CategoryId is { } category && category != categoryId)
                continue;
            if (bonus.GroupId is { } group && group != groupId)
                continue;
            product *= bonus.Amount;
        }
        return product;
    }

    /// <summary>The rigs of a facility and the activities each one changes, for the client's rigModifiers (sorted, unique).</summary>
    public static IReadOnlyDictionary<int, int[]> RigActivities(IEnumerable<IndustryBonus> bonuses) =>
        bonuses.Where(bonus => bonus.RigTypeId is not null)
            .GroupBy(bonus => bonus.RigTypeId!.Value)
            .ToDictionary(group => group.Key, group => group.Select(bonus => bonus.ActivityId).Distinct().Order().ToArray());

    private static int? IntOf(JsonElement row, string name) =>
        row.ValueKind == JsonValueKind.Object && row.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;

    private static double? DoubleOf(JsonElement row, string name) =>
        row.ValueKind == JsonValueKind.Object && row.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number)
            ? number
            : null;

    private static string? StringOf(JsonElement row, string name) =>
        row.ValueKind == JsonValueKind.Object && row.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

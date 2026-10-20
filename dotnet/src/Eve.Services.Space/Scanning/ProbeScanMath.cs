namespace Eve.Services.Space.Scanning;

/// <summary>
/// The arithmetic of a probe scan. The numbers come from the SDE (the probe types' base scan range, base sensor strength, base maximum deviation and range
/// no signal strength for a ship; those come from EVE.js <c>scanMgrService</c> and <c>probeScanRuntime</c> (the reference), see DEVIATIONS "Track A7".
/// </summary>
public static class ProbeScanMath
{
    /// <summary>One astronomical unit in metres (EVE.js <c>AU_METERS</c>; the client's <c>probescanning.const.AU</c>).</summary>
    public const double AuMeters = 149_597_870_700.0;

    public const int RangeStepCount = 8;

    /// "the name is revealed at 75%", "warpable at 100%".</summary>
    public const double ResultGroup = 0.25;
    public const double ResultName = 0.75;
    public const double ResultPerfect = 1.0;

    /// <summary>A result's certainty below this is worth nothing (the client's <c>probeResultUnusable</c>, EVE.js <c>PROBE_RESULT_UNUSABLE</c>).</summary>
    public const double ResultUnusable = 0.001;

    /// <c>GetScanRangeStepsByTypeID</c>; SDE attributes 1370 and 1373).</summary>
    public static double ScanRangeMeters(double baseScanRangeAu, double rangeFactor, int rangeStep) =>
        baseScanRangeAu * Math.Pow(rangeFactor, rangeStep - 1) * AuMeters;

    /// with the strength bonus in percent (EVE.js <c>resolveProbeStrengthAtRange</c>).</summary>
    public static double StrengthAtStep(double baseSensorStrength, double rangeFactor, int rangeStep, double strengthBonusPercent) =>
        Math.Max(0, baseSensorStrength / Math.Pow(rangeFactor, rangeStep - 1) * (1 + strengthBonusPercent / 100));

    /// <summary>
    /// x 5) / 100 x (100 - Astrometric Pinpointing x 5) / 100"). <paramref name="deviationFactor"/> is the product of the skills' factors (1 at no skill).
    /// </summary>
    public static double MaxDeviationMeters(double baseMaxDeviationAu, double baseScanRangeAu, double scanRangeMeters, double deviationFactor) =>
        baseMaxDeviationAu * (scanRangeMeters / AuMeters / baseScanRangeAu) * deviationFactor * AuMeters;

    /// <summary>The factor a skill with a deviation modifier (SDE 1156, per level, in percent) puts on the deviation: 1 + modifier x level / 100.</summary>
    public static double DeviationFactor(double modifierPerLevelPercent, int level) => 1 + modifierPerLevelPercent * level / 100;

    /// <summary>The scan duration factor of a skill with a duration bonus (SDE 66, per level, in percent): 1 + bonus x level / 100 (EVE.js
    /// <c>resolveCharacterScanDurationBonusMultiplier</c>).</summary>
    public static double DurationFactor(double bonusPerLevelPercent, int level) => 1 + bonusPerLevelPercent * level / 100;

    /// <summary>
    /// The signal strength a target needs from a probe: its sensor strength over its signature radius, times 20, at least 0.25 (EVE.js
    /// <c>buildCombatTargetSite</c>: the sensor strength is the strongest of the four sensor attributes 208 to 211, the signature radius is attribute 552). A
    /// bigger ship is easier to scan, a stronger sensor harder.
    /// </summary>
    public static double RequiredStrength(double sensorStrength, double signatureRadius) =>
        Math.Max(0.25, Math.Max(1, sensorStrength) / Math.Max(1, signatureRadius) * 20);

    /// <summary>
    /// for signatures up to level III; level IV needs 72 and level V needs 104). It stands where <see cref="RequiredStrength"/> does for a ship: a probe weaker than this finds the signature
    /// only in part.
    /// </summary>
    public static double SignatureStrength(int level) => level switch
    {
        >= 5 => 104,
        4 => 72,
        _ => 42,
    };

    /// <summary>
    /// What one probe contributes to a target's result: 0 beyond the probe's range, else the distance falloff (1 - (distance / range)^2) times the probe's strength
    /// over what the target needs, at most 1 (EVE.js <c>scoreProbeCoverage</c>).
    /// </summary>
    public static double Coverage(double distanceMeters, double scanRangeMeters, double strengthAtStep, double requiredStrength)
    {
        if (scanRangeMeters <= 0 || distanceMeters > scanRangeMeters)
        {
            return 0;
        }
        var normalized = Math.Clamp(distanceMeters / scanRangeMeters, 0, 1);
        var geometry = Math.Max(0, 1 - normalized * normalized);
        // a target that needs no strength (a wormhole signature, WormholeSites.ScanStrength 0) is reached by any probe in range
        var strength = requiredStrength <= 0 ? 1 : Math.Max(0, strengthAtStep / requiredStrength);
        return Math.Clamp(geometry * strength, 0, 1);
    }

    /// <summary>
    /// The certainty of a result from the coverage of the probes that reach a target (EVE.js <c>resolveCertainty</c>): one probe gives a sphere below 25%, two a
    /// circle below 50%, three two dots below 100%, and four or more one dot, which is 100% when the strongest four are all strong.
    /// </summary>
    public static double Certainty(IReadOnlyList<double> coverages)
    {
        var scores = coverages.Where(score => double.IsFinite(score) && score > 0).OrderByDescending(score => score).ToArray();
        return scores.Length switch
        {
            0 => 0,
            1 => Math.Min(0.249, scores[0] * 0.249),
            2 => Math.Min(0.499, (scores[0] + scores[1]) / 2 * 0.5),
            3 => Math.Min(0.999, (scores[0] + scores[1] + scores[2]) / 3 * 0.9),
            _ => Math.Clamp(scores.Take(4).Average() * 1.2, 0, 1),
        };
    }

    /// <summary>
    /// How well the probes that reach a target are spread (EVE.js <c>resolveFormationGeometryQuality</c>): with fewer than four reaching, 1; with four or more, the
    /// average separation of the strongest four in units of their ranges, over 0.2, at most 1. Probes on top of each other cannot make a 100% by counting four.
    /// </summary>
    public static double FormationQuality(IReadOnlyList<(double[] Position, double ScanRangeMeters, double Coverage)> reaching)
    {
        var strongest = reaching.Where(r => r.Coverage > 0).OrderByDescending(r => r.Coverage).Take(4).ToArray();
        if (strongest.Length < 4)
        {
            return 1;
        }
        var separations = new List<double>();
        for (var i = 0; i < strongest.Length; i++)
        {
            for (var j = i + 1; j < strongest.Length; j++)
            {
                var range = Math.Max(1, (strongest[i].ScanRangeMeters + strongest[j].ScanRangeMeters) / 2);
                separations.Add(Distance(strongest[i].Position, strongest[j].Position) / range);
            }
        }
        return Math.Clamp(separations.Average() / 0.2, 0, 1);
    }

    public static double Distance(double[] a, double[] b) =>
        Math.Sqrt(Math.Pow(a[0] - b[0], 2) + Math.Pow(a[1] - b[1], 2) + Math.Pow(a[2] - b[2], 2));
}

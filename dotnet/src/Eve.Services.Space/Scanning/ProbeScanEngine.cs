namespace Eve.Services.Space.Scanning;

/// <summary>A probe as a scan sees it: where it is, the range it scans at, its strength there, the deviation its range allows, and whether it can scan ships
/// (a combat probe, SDE attribute 1413).</summary>
public sealed record ScanProbe(long ProbeId, double[] Position, double ScanRangeMeters, double StrengthAtStep, double MaxDeviationMeters, bool CombatCapable);

/// <summary>Something a probe scan can find: its id, type and scan group, where it is, the signal strength it needs, and whether it is a cosmic signature (which
/// any probe can find; a ship, drone, fighter, structure or deployable needs a combat probe).</summary>
public sealed record ScanTargetSite(
    long TargetId, int TypeId, int GroupId, int ScanGroupId, double[] Position, double RequiredStrength, bool IsSignature, int StrengthAttributeId = 0);

/// <summary>A result of a scan, as the client's rows carry it. <see cref="Perfect"/> is a result at 100%: its position is the target's own.</summary>
public sealed record ScanResultRow(
    long Id, int ScanGroupId, int GroupId, int TypeId, int StrengthAttributeId, long ItemId, double Certainty, double[] Position, double DeviationMeters,
    bool Perfect);

/// <summary>What a scan found: the results, the targets that were looked for and not reached (the client drops them), and the results at 100%.</summary>
public sealed record ProbeScanOutcome(IReadOnlyList<ScanResultRow> Results, IReadOnlyList<long> AbsentTargets);

/// <summary>
/// The result of a probe scan over the targets of a system (EVE.js <c>buildSignatureScanResults</c>, with the wiki's rules for the deviation). For every target,
/// the probes that reach it give a certainty (<see cref="ProbeScanMath.Certainty"/> times the spread of the probes); a target that no probe reaches is absent when
/// it is not a cosmic signature. A result below 100% is placed off the target by at most the probes' maximum deviation, less as the certainty grows (the wiki:
/// "the deviation of your scan will always be less than the distance shown").
/// </summary>
public static class ProbeScanEngine
{
    public static ProbeScanOutcome Compute(IReadOnlyList<ScanProbe> probes, IReadOnlyList<ScanTargetSite> targets, Random random)
    {
        var results = new List<ScanResultRow>();
        var absent = new List<long>();
        foreach (var target in targets)
        {
            var reaching = probes
                .Where(probe => target.IsSignature || probe.CombatCapable)
                .Select(probe => (probe, Coverage: ProbeScanMath.Coverage(
                    ProbeScanMath.Distance(probe.Position, target.Position), probe.ScanRangeMeters, probe.StrengthAtStep, target.RequiredStrength)))
                .Where(reach => reach.Coverage > 0)
                .ToList();
            var certainty = ProbeScanMath.Certainty(reaching.Select(reach => reach.Coverage).ToList())
                * ProbeScanMath.FormationQuality(reaching.Select(reach => (reach.probe.Position, reach.probe.ScanRangeMeters, reach.Coverage)).ToList());
            if (certainty <= 0)
            {
                if (!target.IsSignature)
                {
                    absent.Add(target.TargetId);
                }
                continue;
            }
            certainty = Math.Clamp(certainty, 0, 1);
            var strongest = reaching.OrderByDescending(reach => reach.Coverage).First().probe;
            if (certainty >= ProbeScanMath.ResultPerfect)
            {
                results.Add(Row(target, 1, target.Position, 0, true));
                continue;
            }
            // off the target by a random direction, by at most the maximum deviation, less the stronger the result
            var direction = RandomDirection(random);
            var reach = strongest.MaxDeviationMeters * (1 - certainty);
            double[] position = [target.Position[0] + direction[0] * reach, target.Position[1] + direction[1] * reach, target.Position[2] + direction[2] * reach];
            results.Add(Row(target, certainty, position, strongest.MaxDeviationMeters, false));
        }
        return new ProbeScanOutcome(results, absent);
    }

    private static ScanResultRow Row(ScanTargetSite target, double certainty, double[] position, double deviation, bool perfect) => new(
        target.TargetId, target.ScanGroupId, target.GroupId, target.TypeId, target.StrengthAttributeId, target.TargetId, certainty, position, deviation, perfect);

    /// <summary>A unit vector in a random direction (uniform on the sphere).</summary>
    private static double[] RandomDirection(Random random)
    {
        var z = random.NextDouble() * 2 - 1;
        var angle = random.NextDouble() * 2 * Math.PI;
        var ring = Math.Sqrt(1 - z * z);
        return [ring * Math.Cos(angle), z, ring * Math.Sin(angle)];
    }
}

using Eve.Services.Space.Scanning;
using Xunit;

namespace Eve.Services.Space.Tests;

/// <summary>
/// The probe scan's arithmetic: the wiki's scan range steps, probe strength and deviation tables, the skills' bonuses, the signal a target needs, the certainty
/// tiers, and the engine's results (no database).
/// </summary>
public class ProbeScanMathTests
{
    private const double Au = ProbeScanMath.AuMeters;

    [Fact]
    public void Core_probes_scan_from_a_quarter_of_an_astronomical_unit_to_32_and_combat_probes_from_half_to_64()
    {
        // the SDE: core probes 0.25 AU at a range factor of 2; combat probes 0.5 AU (wiki: "Core Scanner Probes haves a maximum scan range of 32 AU")
        Assert.Equal(0.25 * Au, ProbeScanMath.ScanRangeMeters(0.25, 2, 1), 0);
        Assert.Equal(32 * Au, ProbeScanMath.ScanRangeMeters(0.25, 2, 8), 0);
        Assert.Equal(0.5 * Au, ProbeScanMath.ScanRangeMeters(0.5, 2, 1), 0);
        Assert.Equal(64 * Au, ProbeScanMath.ScanRangeMeters(0.5, 2, 8), 0);
    }

    [Fact]
    public void A_probe_has_40_strength_at_the_base_range_and_the_wiki_minimum_of_42_with_astrometrics_one()
    {
        // wiki: "The minimum probe sensor strength is 42 (base strength of 40 ... plus 5% due to Astrometrics I)"
        Assert.Equal(40, ProbeScanMath.StrengthAtStep(40, 2, 1, 0), 9);
        Assert.Equal(42, ProbeScanMath.StrengthAtStep(40, 2, 1, 5), 9);
        // the strength halves with each range step (the base strength is given at the base scan range)
        Assert.Equal(20, ProbeScanMath.StrengthAtStep(40, 2, 2, 0), 9);
    }

    [Fact]
    public void The_largest_deviation_follows_the_wiki_table_for_core_and_combat_probes()
    {
        // wiki table, Astrometrics I: core probe (0.125 AU at 0.25 AU) 0.1188 at 0.25 AU and 1.9 at 4 AU; astrometrics five at 4 AU: 1.5
        Assert.Equal(0.1188, ProbeScanMath.MaxDeviationMeters(0.125, 0.25, 0.25 * Au, 0.95) / Au, 4);
        Assert.Equal(1.9, ProbeScanMath.MaxDeviationMeters(0.125, 0.25, 4 * Au, 0.95) / Au, 4);
        Assert.Equal(1.5, ProbeScanMath.MaxDeviationMeters(0.125, 0.25, 4 * Au, 0.75) / Au, 4);
        // a combat probe (0.25 AU at 0.5 AU) at 64 AU with astrometrics one: 30.4
        Assert.Equal(30.4, ProbeScanMath.MaxDeviationMeters(0.25, 0.5, 64 * Au, 0.95) / Au, 4);
    }

    [Fact]
    public void The_skills_change_the_deviation_and_the_duration_by_five_percent_a_level_each_as_the_wiki_and_the_SDE_give()
    {
        // SDE: Astrometrics and Astrometric Pinpointing carry maxScanDeviationModifier -5 a level; Astrometrics and Acquisition durationBonus -5 a level
        Assert.Equal(0.75, ProbeScanMath.DeviationFactor(-5, 5), 9);
        Assert.Equal(0.75, ProbeScanMath.DurationFactor(-5, 5), 9);
        Assert.Equal(1.0, ProbeScanMath.DurationFactor(0, 5), 9);
    }

    [Fact]
    public void A_probe_needs_no_more_than_the_signal_of_a_frigate_and_a_bigger_ship_is_easier()
    {
        // the Probe frigate: LADAR 9 points at a 38 m signature (SDE 208 to 211, 552): 9 / 38 x 20
        Assert.Equal(9.0 / 38 * 20, ProbeScanMath.RequiredStrength(9, 38), 9);
        Assert.True(ProbeScanMath.RequiredStrength(9, 400) < ProbeScanMath.RequiredStrength(9, 38));
        // no sensor reads as one point (EVE.js: the sensor strength is at least 1)
        Assert.Equal(1.0 / 38 * 20, ProbeScanMath.RequiredStrength(0, 38), 9);
    }

    [Fact]
    public void The_certainty_is_a_sphere_a_circle_two_dots_or_one_dot_by_the_number_of_probes_that_reach()
    {
        Assert.Equal(0.249, ProbeScanMath.Certainty([1]), 9);
        Assert.Equal(0.499, ProbeScanMath.Certainty([1, 1]), 9);
        Assert.Equal(0.9, ProbeScanMath.Certainty([1, 1, 1]), 9);
        Assert.Equal(1.0, ProbeScanMath.Certainty([1, 1, 1, 1]), 9);
        Assert.Equal(0, ProbeScanMath.Certainty([]));
    }

    [Fact]
    public void A_probe_reaches_only_within_its_range_and_less_as_it_is_further_or_weaker()
    {
        Assert.Equal(0, ProbeScanMath.Coverage(2_000, 1_000, 50, 4.7));
        Assert.Equal(1, ProbeScanMath.Coverage(0, 1_000, 50, 4.7), 9);
        // a strength of 3 against 4 needed, a quarter inside the range: 0.75 x 0.75
        Assert.Equal(0.5625, ProbeScanMath.Coverage(500, 1_000, 3, 4), 9);
        // a probe too weak for the target covers it by its strength ratio
        Assert.Equal(0.5, ProbeScanMath.Coverage(0, 1_000, 2, 4), 9);
    }
}

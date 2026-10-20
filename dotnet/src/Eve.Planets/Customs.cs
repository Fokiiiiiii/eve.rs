namespace Eve.Planets;

/// <summary>
/// Customs offices of the planets (the import and export of commodities between a launchpad and the customs office). The rates and
/// the tax formula are the client's (eve-logic <c>eve/common/script/planet/entities/spaceportPin.py</c> GetImportTax / GetExportTax, and
/// <c>eveplanet/taxRate.py</c>); the numbers are the SDE's dogma attributes 1638 (importTax), 1639 (exportTax), 1640 (importTaxMultiplier;
/// the commodity's base value per unit, EVE University wiki: R0 5 ISK, P1 400 ISK), and the wiki's office rates.
/// </summary>
public static class Customs
{
    /// <summary>The office of a planet has the id 1.2e12 + the planet id (the id range of the reference's customs offices, DEVIATIONS 16).</summary>
    public const long OfficeIdBase = 1_200_000_000_000L;
    /// <summary>The planetary customs office type (SDE type 2233 "Customs Office", group 1025 Orbital Infrastructure, category Orbital): the client's office window and planet orbitals read this group.</summary>
    public const int OfficeTypeId = 2233;
    /// <summary>The group of <see cref="OfficeTypeId"/> (the client's groupPlanetaryCustomsOffices).</summary>
    public const int OfficeGroupId = 1025;
    /// <summary>The office's hangar room in m3 (SDE types.capacity of type 2233).</summary>
    public const double OfficeCapacity = 35_000;
    /// <summary>The radius of the office's ball (SDE types.radius of type 2233).</summary>
    public const double OfficeRadius = 8000;

    /// <summary>
    /// Where a planet's office hangs in its system: two radii above the planet's centre. The place is the server's (the reference's orbit of a
    /// customs office is not in the snapshot); it is outside the planet and stays the same for every visit.
    /// </summary>
    public static double[] OfficePosition(PlanetBody body) => [body.X, body.Y, body.Z + 2 * body.Radius];
    /// <summary>Customs Code Expertise (SDE type 33467): one percent off the high-security rate per level.</summary>
    public const int CustomsCodeExpertise = 33467;
    /// <summary>The security status from which a system is high-security for the rate (EVE: 0.5 displayed; 0.45 rounds to it).</summary>
    public const double HighSecurity = 0.45;
    /// <summary>NPC office rate in high-security space, before Customs Code Expertise (wiki: 10%, the owner's share 0 for an NPC office).</summary>
    public const double NpcRateHighSecurity = 0.10;
    /// <summary>NPC office rate outside high-security space; not reduced by the skill (wiki: 17%).</summary>
    public const double NpcRateOutside = 0.17;

    public static long OfficeId(long planetId) => OfficeIdBase + planetId;

    /// <summary>The planet of an office id, or null when the id is not an office id.</summary>
    public static long? PlanetOf(long officeId) =>
        officeId > OfficeIdBase && officeId < OfficeIdBase + 1_000_000_000L ? officeId - OfficeIdBase : null;

    /// <summary>The office rate for a character in a system of this security, with its Customs Code Expertise level.</summary>
    public static double Rate(double security, int customsCodeExpertiseLevel) =>
        security >= HighSecurity ? Math.Max(0, NpcRateHighSecurity - 0.01 * Math.Clamp(customsCodeExpertiseLevel, 0, 5)) : NpcRateOutside;

    /// <summary>The five standing bands used by the V24.01 customs-office tax calculator.</summary>
    public enum StandingTier { Excellent, Good, Neutral, Bad, Terrible }

    /// <summary>Owner-configured rates by relationship; values are decimal fractions (for example, 0.05 is five percent).</summary>
    public sealed record OwnerTaxRates(
        double Corporation,
        double Alliance,
        double Excellent,
        double Good,
        double Neutral,
        double Bad,
        double Terrible)
    {
        public static OwnerTaxRates Default { get; } = new(0.05, 0.05, 0.05, 0.05, 0.05, 0.05, 0.05);

        public double ForPilot(bool sameCorporation, bool sameAlliance, double standing)
        {
            if (sameCorporation) return Corporation;
            if (sameAlliance) return Alliance;
            return TierRate(standing);
        }

        public double TierRate(double standing) => standing switch
        {
            >= 10 => Excellent,
            >= 5 => Good,
            >= 0 => Neutral,
            >= -5 => Bad,
            _ => Terrible,
        };
    }

    /// <summary>The total displayed rate: the owner-configured component plus the NPC component, capped at the published 100% maximum.</summary>
    public static double CombinedRate(double npcRate, double ownerRate) =>
        Math.Clamp(npcRate + ownerRate, 0, 1);

    /// <summary>
    /// The import and export fees of a transfer (spaceportPin.GetImportTax, GetExportTax): sum of quantity x the launchpad's rate x the
    /// commodity's multiplier, times the office rate. Imports use the launchpad's import rate, exports its export rate.
    /// </summary>
    public static (double Import, double Export) Taxes(PlanetCatalog catalog, int launchpadType, IReadOnlyDictionary<int, long> imports,
        IReadOnlyDictionary<int, long> exports, double rate)
    {
        var pad = catalog.Type(launchpadType);
        var importRate = pad.Attr(PlanetAttr.ImportTax);
        var exportRate = pad.Attr(PlanetAttr.ExportTax);
        double import = 0;
        foreach (var (type, qty) in imports) import += qty * importRate * catalog.Type(type).Attr(PlanetAttr.ImportTaxMultiplier, 1.0);
        double export = 0;
        foreach (var (type, qty) in exports) export += qty * exportRate * catalog.Type(type).Attr(PlanetAttr.ImportTaxMultiplier, 1.0);
        return (import * rate, export * rate);
    }
}

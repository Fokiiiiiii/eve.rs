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
    /// <summary>The planetary customs office type of the server's data (SDE type 35897, group 1717).</summary>
    public const int OfficeTypeId = 35897;
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

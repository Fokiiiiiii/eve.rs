using Eve.Planets;

namespace Eve.Services.Planets;

/// <summary>The six settings returned by the orbital registry, backed by the POCO or Skyhook owner.</summary>
public sealed record OrbitalSettings(
    int ReinforceHour,
    Customs.OwnerTaxRates TaxRates,
    int StandingLevel,
    bool AllowAlliance,
    bool AllowStandings,
    long? AccessGroupId);

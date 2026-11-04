namespace Eve.Services.Core;
using Eve.Marshal;
using Eve.Macho;

public enum SkyhookResourcePermission
{
    Read,
    Activate,
    Deactivate,
}

public enum SkyhookResourceResultCode
{
    Ok,
    NotFound,
    Forbidden,
    PersistenceFailure,
}

public sealed record SkyhookResourceResult<T>(SkyhookResourceResultCode Code, T? Value) where T : class;

/// <summary>Current bay stock after the Host applies the CCP maturation ramp and theft outcome.</summary>
public sealed record SkyhookReagentResource(
    long TypeId,
    long SecureStock,
    long InsecureStock);

public sealed record SkyhookTheftVulnerability(
    bool Vulnerable,
    long StartUnixSeconds,
    long EndUnixSeconds);

/// <summary>
/// A deployed skyhook backed by sovereignty persistence. Reagent configuration is resolved from
/// the current SDE and joined to the persisted stock and last-cycle state by the host adapter.
/// </summary>
public sealed record SkyhookResourceSnapshot(
    long SkyhookId,
    long TypeId,
    long SolarSystemId,
    long PlanetId,
    long CorporationId,
    long? AllianceId,
    bool ProductionActive,
    long? Workforce,
    string ResourceVersion,
    IReadOnlyList<SkyhookReagentResource> Reagents,
    SkyhookTheftVulnerability? TheftVulnerability,
    int? State = null);

public sealed record SkyhookReagentAmounts(long Secure, long Insecure);
public sealed record SkyhookWorkforce(long? Amount);
public sealed record SkyhookProductionStatus(bool Active);

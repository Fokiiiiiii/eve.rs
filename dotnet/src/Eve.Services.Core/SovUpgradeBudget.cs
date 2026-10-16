namespace Eve.Services.Core;

/// <summary>An upgrade a hub has installed, in install order, with the owner's online switch (EVE.js installedUpgrades: installation.online).</summary>
public sealed record SovUpgradeInstall(long TypeId, bool Online);

/// <summary>An upgrade's power and workforce numbers: what it needs to run and what it produces once it runs.</summary>
public sealed record SovUpgradeDefinition(long TypeId, double PowerRequired, double WorkforceRequired, double PowerProduced, double WorkforceProduced);

/// <summary>The power and workforce a system supplies to the hub's upgrades (EVE.js getHubLocalHarvest).</summary>
public sealed record SovUpgradeSupply(double Power, double Workforce);

/// <summary>
/// The sovereignty upgrade power and workforce budget (ST-17 c; EVE.js sovModernState <c>computeHubUpgradeSnapshot</c>). An upgrade with its online switch set runs
/// when the power and the workforce it needs fit what is left of the supply, in install order; a running upgrade adds what it produces. An upgrade that does not
/// fit is LOW, one whose switch is off is OFFLINE. The cynosural suppression's online state is "ONLINE", so it is derived here, not stored.
/// </summary>
public static class SovUpgradeBudget
{
    /// <summary>EVE.js POWER_STATE (sovModernState): 1 offline, 2 online, 3 low.</summary>
    public const int PowerStateOffline = 1;
    public const int PowerStateOnline = 2;
    public const int PowerStateLow = 3;

    /// <summary>
    /// The Cynosural Suppression upgrade's numbers (SDE 81619 has no power or workforce attribute; the EVE University wiki's Sovereignty Hub table gives 250
    /// power and 4 500 workforce).
    /// </summary>
    public const long CynoSuppressionTypeId = 81619;

    /// <summary>EVE.js's fallback star power (sovStaticData FALLBACK_STAR_POWER_MIN): the star power of a system without its own configuration.</summary>
    public const double FallbackStarPower = 500;

    /// <summary>
    /// The numbers of an upgrade: the wiki's where it gives them, else EVE.js's placeholder for the type (sovStaticData UPGRADE_FAKE_BY_REMAINDER: the values by
    /// type id modulo 4; EVE.js produces no power or workforce with them).
    /// </summary>
    public static SovUpgradeDefinition DefinitionOf(long typeId)
    {
        if (typeId == CynoSuppressionTypeId)
            return new SovUpgradeDefinition(typeId, 250, 4500, 0, 0);
        return (typeId % 4) switch
        {
            0 => new SovUpgradeDefinition(typeId, 100, 200, 0, 0),
            1 => new SovUpgradeDefinition(typeId, 10, 20, 0, 0),
            2 => new SovUpgradeDefinition(typeId, 30, 15, 0, 0),
            _ => new SovUpgradeDefinition(typeId, 400, 300, 0, 0),
        };
    }

    /// <summary>The power state of each installed upgrade (by type id; an upgrade is listed only with its numbers, as EVE.js).</summary>
    public static IReadOnlyDictionary<long, int> PowerStates(IReadOnlyList<SovUpgradeInstall> installed, SovUpgradeSupply supply)
    {
        var powerAvailable = supply.Power;
        var workforceAvailable = Math.Max(0, supply.Workforce);
        double powerAllocated = 0;
        double workforceAllocated = 0;
        var states = new Dictionary<long, int>();
        foreach (var install in installed)
        {
            var definition = DefinitionOf(install.TypeId);
            var state = PowerStateOffline;
            if (install.Online)
            {
                var canPower = powerAllocated + definition.PowerRequired <= powerAvailable;
                var canWorkforce = workforceAllocated + definition.WorkforceRequired <= workforceAvailable;
                if (canPower && canWorkforce)
                {
                    state = PowerStateOnline;
                    powerAllocated += definition.PowerRequired;
                    workforceAllocated += definition.WorkforceRequired;
                    powerAvailable += definition.PowerProduced;
                    workforceAvailable += definition.WorkforceProduced;
                }
                else
                {
                    state = PowerStateLow;
                }
            }
            states[install.TypeId] = state;
        }
        return states;
    }
}

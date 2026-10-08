using Eve.Data;
using Eve.Dogma;
using Eve.Services.Space.Effects;
using Eve.Sde;
using Npgsql;

namespace Eve.Services.Space.Tests;

/// <summary>The superweapon, cyno, jump portal and clone attribute constants (<see cref="ModuleAttributes"/>) name the SDE's attributes with these ids.</summary>
public class SuperWeaponAttributeIdsTests
{
    private static readonly (int Id, string Name)[] Attributes =
    [
        (ModuleAttributes.DoomsdayEnergyNeutRadius, "doomsdayEnergyNeutRadius"),
        (ModuleAttributes.DoomsdayEnergyNeutAmount, "doomsdayEnergyNeutAmount"),
        (ModuleAttributes.DoomsdayEnergyNeutSignatureRadius, "doomsdayEnergyNeutSignatureRadius"),
        (ModuleAttributes.DoomsdayEnergyNeutResistanceId, "doomsdayEnergyNeutResistanceID"),
        (ModuleAttributes.DoomsdayWarningDuration, "doomsdayWarningDuration"),
        (ModuleAttributes.DoomsdayDamageRadius, "doomsdayDamageRadius"),
        (ModuleAttributes.DoomsdayDamageDuration, "doomsdayDamageDuration"),
        (ModuleAttributes.DoomsdayDamageCycleTime, "doomsdayDamageCycleTime"),
        (ModuleAttributes.IsPointTargeted, "isPointTargeted"),
        (ModuleAttributes.DoomsdayAoeRange, "doomsdayAOERange"),
        (ModuleAttributes.DoomsdayAoeSignatureRadius, "doomsdayAOESignatureRadius"),
        (ModuleAttributes.DoomsdayAoeShape, "doomsdayAOEShape"),
        (ModuleAttributes.DoomsdayRangeIsFixed, "doomsdayRangeIsFixed"),
        (ModuleAttributes.DoomsdayImmobilityDuration, "doomsdayImmobilityDuration"),
        (ModuleAttributes.DoomsdayNoJumpOrCloakDuration, "doomsdayNoJumpOrCloakDuration"),
        (ModuleAttributes.DoomsdayAppliedDBuffDuration, "doomsdayAppliedDBuffDuration"),
        (ModuleAttributes.PanicDuration, "panicDuration"),
        (ModuleAttributes.CynosuralFieldSpawnRadius, "cynosuralFieldSpawnRadius"),
        (ModuleAttributes.JumpPortalConsumptionMassFactor, "jumpPortalConsumptionMassFactor"),
        (ModuleAttributes.JumpPortalDuration, "jumpPortalDuration"),
        (ModuleAttributes.CloneJumpCoolDown, "cloneJumpCoolDown"),
        (ModuleAttributes.IsTitanJumpPortalPassenger, "isTitanJumpPortalPassenger"),
        (ModuleAttributes.IsBlackOpsJumpPortalPassenger, "isBlackOpsJumpPortalPassenger"),
        (ModuleAttributes.IsBlackOpsJumpConduitPassenger, "isBlackOpsJumpConduitPassenger"),
        (ModuleAttributes.IsIndustrialJumpConduitPassenger, "isIndustrialJumpConduitPassenger"),
        (ModuleAttributes.IsIndustrialJumpPortalPassenger, "isIndustrialJumpPortalPassenger"),
    ];

    [PgFact]
    public async Task Every_superweapon_attribute_constant_is_the_sde_attribute_of_its_name()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var statics = await DogmaStatic.LoadAsync(new SdeReader(source));

        foreach (var (id, name) in Attributes)
        {
            Assert.Equal(name, statics.Attribute(id)?.Name);
        }
    }
}

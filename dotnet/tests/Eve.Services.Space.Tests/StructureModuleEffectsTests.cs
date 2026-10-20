using Eve.Services.Space.Effects;

namespace Eve.Services.Space.Tests;

/// <summary>
/// compressor is not a structure module (<c>CompressionHostTests</c>).
/// </summary>
public sealed class StructureModuleEffectsTests
{
    [Fact]
    public void A_structure_module_effect_is_registered_and_is_coverage()
    {
        Assert.Contains("entosisLink", ModuleEffects.Default.Names, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("entosisLink", ModuleEffects.Default.ImplementedNames, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_ship_compressor_is_coverage_and_is_not_refused_by_the_structure_blocker()
    {
        Assert.Contains("industrialItemCompression", ModuleEffects.Default.ImplementedNames, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_entosis_link_names_its_effect_and_its_reach_as_the_static_data_does()
    {
        Assert.Equal("entosisLink", EntosisLinkEffect.Name);
        Assert.Equal(54, EntosisLinkEffect.RangeAttribute);
    }

    [Fact]
    public void Structure_targeted_combat_effects_use_the_existing_projected_handlers()
    {
        string[] effects =
        [
            "structureEnergyNeutralizerFalloff",
            "structureModuleEffectStasisWebifier",
            "structureModuleEffectTargetPainter",
            "structureModuleEffectRemoteSensorDampener",
            "structureModuleEffectECM",
            "structureModuleEffectWeaponDisruption",
            "structureWarpScrambleBlockMWDWithNPCEffect",
        ];

        foreach (var effect in effects)
        {
            Assert.Contains(effect, ModuleEffects.Default.Names, StringComparer.OrdinalIgnoreCase);
            Assert.Contains(effect, ModuleEffects.Default.ImplementedNames, StringComparer.OrdinalIgnoreCase);
        }
    }
}

using Eve.Services.Space.Effects;

namespace Eve.Services.Space.Tests;

/// <summary>
/// The entosis link is registered as a handler that refuses by the structure blocker (phase 3), and is not counted as coverage: a refusal is not an implementation
/// (AGENTS rule 5). The ship compressor is not a structure module (<c>CompressionHostTests</c>).
/// </summary>
public sealed class StructureModuleEffectsTests
{
    [Fact]
    public void A_structure_module_effect_is_registered_and_is_not_coverage()
    {
        Assert.Contains("entosisLink", ModuleEffects.Default.Names, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("entosisLink", ModuleEffects.Default.ImplementedNames, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_ship_compressor_is_coverage_and_is_not_refused_by_the_structure_blocker()
    {
        Assert.Contains("industrialItemCompression", ModuleEffects.Default.ImplementedNames, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_structure_refusal_is_a_named_refusal_and_not_an_unsupported_effect()
    {
        Assert.Equal("STRUCTURES_PHASE_3", StructureModuleEffects.StructuresPhase3);
        Assert.NotEqual("UNSUPPORTED_EFFECT", StructureModuleEffects.StructuresPhase3);
    }
}

using Eve.Services.Space.Effects;

namespace Eve.Services.Space.Tests;

/// <summary>
/// The entosis link and the compressor are registered as handlers that refuse by the structure blocker (phase 3), and are not counted as coverage: a refusal
/// is not an implementation (AGENTS rule 5).
/// </summary>
public sealed class StructureModuleEffectsTests
{
    [Theory]
    [InlineData("entosisLink")]
    [InlineData("industrialItemCompression")]
    public void A_structure_module_effect_is_registered_and_is_not_coverage(string effect)
    {
        Assert.Contains(effect, ModuleEffects.Default.Names, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(effect, ModuleEffects.Default.ImplementedNames, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_structure_refusal_is_a_named_refusal_and_not_an_unsupported_effect()
    {
        Assert.Equal("STRUCTURES_PHASE_3", StructureModuleEffects.StructuresPhase3);
        Assert.NotEqual("UNSUPPORTED_EFFECT", StructureModuleEffects.StructuresPhase3);
    }
}

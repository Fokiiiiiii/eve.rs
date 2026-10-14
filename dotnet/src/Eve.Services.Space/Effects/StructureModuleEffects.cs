namespace Eve.Services.Space.Effects;

/// <summary>
/// The entosis link (<c>entosisLink</c>) targets a sovereignty hub and applies sovereignty capture rules. It remains refused until that owner and capture flow are integrated.
/// (The industrial item compressor is a ship module and has its body in <see cref="CompressionModuleEffect"/>.)
/// </summary>
internal sealed class EntosisLinkEffect() : PendingModuleEffect("entosisLink")
{
    protected override string? Refusal(ModuleEffectContext module) => StructureModuleEffects.StructuresPhase3;
}

internal static class StructureModuleEffects
{
    /// <summary>The refusal of an entosis link while the sovereignty capture owner is not wired.</summary>
    public const string StructuresPhase3 = "STRUCTURES_PHASE_3";
}

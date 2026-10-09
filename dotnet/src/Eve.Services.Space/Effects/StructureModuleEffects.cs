namespace Eve.Services.Space.Effects;

/// <summary>
/// The modules whose work is done to a structure: the entosis link (<c>entosisLink</c>, which contests a structure's sovereignty) and the industrial item
/// compression (<c>industrialItemCompression</c>, a structure's compressor). Structures are phase 3 on this server: there is no structure host and no structure
/// in space, so neither module can lock or act on the target it needs. Both are refused with <see cref="StructuresPhase3"/> before a target is asked for, and
/// neither is coverage (the gap manifest keeps them). The link's lock and range rules apply to a structure target and are not run.
/// </summary>
internal sealed class EntosisLinkEffect() : PendingModuleEffect("entosisLink")
{
    protected override string? Refusal(ModuleEffectContext module) => StructureModuleEffects.StructuresPhase3;
}

/// <summary>The compressor of a structure (see <see cref="EntosisLinkEffect"/>): refused for the same reason.</summary>
internal sealed class CompressionModuleEffect() : PendingModuleEffect("industrialItemCompression")
{
    protected override string? Refusal(ModuleEffectContext module) => StructureModuleEffects.StructuresPhase3;
}

internal static class StructureModuleEffects
{
    /// <summary>The refusal of a module whose work is on a structure: structures are phase 3, and this server has none in space.</summary>
    public const string StructuresPhase3 = "STRUCTURES_PHASE_3";
}

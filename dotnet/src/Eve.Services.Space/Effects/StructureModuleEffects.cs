namespace Eve.Services.Space.Effects;

/// <summary>
/// The module whose work is done to a structure: the entosis link (<c>entosisLink</c>, which contests a sovereignty hub). Structures are phase 3 on this server: there
/// is no structure host and no structure in space, so the module cannot lock or act on the target it needs. It is refused with <see cref="StructuresPhase3"/> before
/// a target is asked for, and is not coverage (the gap manifest keeps it). The link's lock and range rules apply to a structure target and are not run.
/// (The industrial item compressor is a ship module and has its body in <see cref="CompressionModuleEffect"/>.)
/// </summary>
internal sealed class EntosisLinkEffect() : PendingModuleEffect("entosisLink")
{
    protected override string? Refusal(ModuleEffectContext module) => StructureModuleEffects.StructuresPhase3;
}

internal static class StructureModuleEffects
{
    /// <summary>The refusal of a module whose work is on a structure: structures are phase 3, and this server has none in space.</summary>
    public const string StructuresPhase3 = "STRUCTURES_PHASE_3";
}

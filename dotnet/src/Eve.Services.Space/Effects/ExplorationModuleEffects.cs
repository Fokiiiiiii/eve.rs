namespace Eve.Services.Space.Effects;

// The hacking and ECCM module effects that have no body yet. Each is wired (fitted, onlined, registered in ModuleEffects.Default) and refused with
// PendingModuleEffect.NotImplemented; none of them cycles, takes capacitor or reports success. (The scanners have bodies: ScannerModuleEffects.cs.) The SDE rows are the published facts; the attributes listed are those
// the real body must read, on the module's values after dogma (ModuleEffectContext.Cycle reads duration and capacitor for every one of them).
//
// Shared for every effect below (SDE effect records of this build, guid as the static data gives it):
//   duration        attribute 73  (the cycle length; the effect's durationAttributeID)
//   capacitorNeed   attribute 6   (the cost of a cycle; the effect's dischargeAttributeID)
// Types are counted from the SDE join of typeDogma.dogmaEffects with dogmaEffects.name, all categories of the type (published and unpublished).

/// <summary>
/// <c>doHacking</c> (effect 1738, guid <c>effects.TargetScan</c>, category 2 activeTarget): the Data and Relic Analyzers that hack a site (23 types, 10 published:
/// Data Analyzer I and II, Relic Analyzer I and II, the integrated analyzers and the Sansha and Deathless Circle variants; unpublished: the faction variants such as
/// 'Daemon' and 'Codex', the civilian and Citizen analyzers, the QA Analyzer (72879) and the QA Cross Protocol Analyzer (33375)).
/// Reads: <c>maxRange</c> 54 (the effect's rangeAttributeID; 5000 m on Data Analyzer I), <c>accessDifficultyBonus</c> 902 (5 on Data Analyzer I), the virus
/// attributes <c>virusCoherence</c> 1909, <c>virusStrength</c> 1910 and <c>virusElementSlots</c> 1911. The hacking and archaeology skills (21718, 13278) add to
/// <c>virusCoherence</c> through dogma (<c>virusCoherenceBonus</c> 10 per level), so the body reads the live map and does not add the skill again.
/// Reference: EveJS v0.12.9 <c>server/src/services/dogma/dogmaService/moduleOperation.js</c>, the analyzer section (<c>_isAnalyzerModuleActivation</c>,
/// <c>_resolveAnalyzerVirusStats</c>, <c>_resolveAnalyzerHackingContext</c>; constants at lines 146-158) and <c>shared.js</c> (<c>ANALYZER_EFFECT_NAME</c>).
/// </summary>
internal sealed class DataAnalyzerEffect : PendingModuleEffect
{
    public DataAnalyzerEffect() : base("doHacking") { }
}

/// <summary>
/// <c>hackOrbital</c> (effect 4773, guid <c>effects.TargetScan</c>, category 2): the Data Subverter I (type 3793, unpublished, group 538 Data Miners). It has no
/// rangeAttributeID of its own; its type carries <c>maxRange</c> 54 (5000 m) and <c>accessDifficultyBonus</c> 902 (15). No EveJS reference was found for this
/// effect name; the body is unverified and must be specified from the client before it is written.
/// </summary>
internal sealed class DataSubverterEffect : PendingModuleEffect
{
    public DataSubverterEffect() : base("hackOrbital") { }
}

/// <summary>
/// <c>scanStrengthBonusPercentActivate</c> (effect 2231, guid <c>effects.ScanStrengthBonusActivate</c>, category 1 activation): the self-applied ECCMs (44 types
/// in groups 202 ECCM and 353 QA Module; none published). The SDE effect carries four <c>modifierInfo</c> entries: on the ship, the scan strengths
/// <c>scanRadarStrength</c> 208, <c>scanLadarStrength</c> 209, <c>scanMagnetometricStrength</c> 210 and <c>scanGravimetricStrength</c> 211 are changed by the
/// module's <c>scanRadarStrengthPercent</c> 1030, <c>scanLadarStrengthPercent</c> 1028, <c>scanMagnetometricStrengthPercent</c> 1029 and
/// <c>scanGravimetricStrengthPercent</c> 1027 (operation 6, PostPercent). The body may therefore be the existing dogma-modifier path (<c>DogmaModifierEffect</c>),
/// which is not done here: this pass only wires the effect.
/// Reads: <c>duration</c> and <c>capacitorNeed</c> (the shared attributes above).
/// Reference: EveJS v0.12.9 names no handler by this effect name. Its scan-strength attributes are in <c>server/src/space/modules/assistanceModuleRuntime.js</c>
/// (lines 86-100 and the modifier entries at 190-210, for the projected modules).
/// </summary>
internal sealed class ScanStrengthActivateEffect : PendingModuleEffect
{
    public ScanStrengthActivateEffect() : base("scanStrengthBonusPercentActivate") { }
}

/// <summary>
/// <c>scanStrengthTargetPercentBonus</c> (effect 2246, no guid, category 2 activeTarget): the projected ECCMs (7 types in group 289 Projected ECCM and group 1717
/// Unpublished Structure Modules; none published, the Structure ECCM Projector is a structure module). It is a target effect, so it needs a locked target like the
/// other projectors. The SDE effect carries four <c>modifierInfo</c> entries that change the target's scan strengths by the module's percent attributes, as the
/// self-applied ECCM does. The effect's rangeAttributeID is 54 (<c>maxRange</c>, 20 km on ECCM Projector I) and its falloffAttributeID is 158 (<c>falloff</c>).
/// Reads: <c>maxRange</c> 54, <c>falloff</c> 158, <c>duration</c> and <c>capacitorNeed</c> (the shared attributes above).
/// Reference: EveJS v0.12.9 <c>server/src/space/modules/assistanceModuleRuntime.js</c> is the projected-assistance path (the scan-strength attributes at lines
/// 86-100, the modifier entries at 190-210), and it names no handler by this effect name.
/// </summary>
internal sealed class ScanStrengthTargetEffect : PendingModuleEffect
{
    public ScanStrengthTargetEffect() : base("scanStrengthTargetPercentBonus") { }
}

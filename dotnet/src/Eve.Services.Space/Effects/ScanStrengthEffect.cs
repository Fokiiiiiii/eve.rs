using Eve.Dogma;
using Eve.Space;

namespace Eve.Services.Space.Effects;

/// <summary>
/// The self-applied ECCM (<c>scanStrengthBonusPercentActivate</c>, effect 2231): while it runs, the ship's four scan strengths change by the module's
/// percents, PostPercent (SDE modifiers 208 to 211 from 1027 to 1030). Its body is the dogma-modifier path (<see cref="DogmaModifierEffect.BuildModifier"/>),
/// as for the other self modifiers. Its types are unpublished in this build (group 202 ECCM, 353 QA), so it is a handler of its own and not one of the
/// published-module modifier names.
/// </summary>
internal sealed class ScanStrengthEffect : ShipEffect
{
    public override IReadOnlyCollection<string> EffectNames { get; } = ["scanStrengthBonusPercentActivate"];

    protected override SimEffect Build(ModuleEffectContext module) => DogmaModifierEffect.BuildModifier(module);
}

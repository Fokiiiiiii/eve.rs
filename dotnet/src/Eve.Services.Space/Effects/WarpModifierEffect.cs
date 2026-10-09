using Eve.Dogma;
using Eve.Space;
using static Eve.Services.Space.Effects.ModuleAttributes;

namespace Eve.Services.Space.Effects;

/// <summary>
/// The modifiers of a warp core stabilizer (<c>warpCoreStabilizerActive</c>: the ship's warp scramble status, attribute 104, a ModAdd of -2) and of an interdiction
/// nullifier (<c>interdictionNullifierBonus</c>: the ship's warp bubble immunity, attribute 1538, a PreAssign of 3120). The client is told what dogma gives them
/// (<see cref="DogmaModifierEffect"/>); the simulation gets the part it runs while the module runs: the immunity (<see cref="SimAction.WarpImmunity"/>), or the change of
/// the scramble status (<see cref="SimAction.ScrambleStatus"/>), each the number dogma gives with the module active against the number with it offline.
/// </summary>
internal sealed class WarpModifierEffect : ShipEffect
{
    public override IReadOnlyCollection<string> EffectNames { get; } = ["interdictionNullifierBonus", "warpCoreStabilizerActive"];

    protected override SimEffect Build(ModuleEffectContext module)
    {
        var shipId = module.Snapshot.Ship.Id;
        double Dogma(ItemState state, int attribute)
        {
            using var assumed = module.World.Assume(module.ModuleId, state);
            return module.World.Get(shipId, attribute);
        }
        SimAction? action = null;
        if (Dogma(ItemState.Active, WarpBubbleImmune) > 0 && Dogma(ItemState.Online, WarpBubbleImmune) <= 0)
        {
            action = new SimAction.WarpImmunity();
        }
        else if (Dogma(ItemState.Active, WarpScrambleStatus) - Dogma(ItemState.Online, WarpScrambleStatus) is var delta and not 0)
        {
            action = new SimAction.ScrambleStatus(delta);
        }
        return DogmaModifierEffect.BuildModifier(module) with { Action = action };
    }
}

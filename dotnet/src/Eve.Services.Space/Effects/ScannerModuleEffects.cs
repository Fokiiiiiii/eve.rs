using Eve.Space;
using static Eve.Services.Space.Effects.ModuleAttributes;

namespace Eve.Services.Space.Effects;

/// <summary>
/// The scanners (EVE.js <c>scannerModuleRuntime.js</c>): one cycle that reveals a locked ship to the pilot who runs it. The reach is the effect's own range
/// attribute and its falloff attribute, read from the module's values after dogma (the SDE effect records name them: a cargo scan, effect 47, has no falloff and is a
/// hard cutoff; a ship scan, effect 46, falls off through attribute 510). The simulation keeps the lock and the reach and parks the module at the cycle's end; the
/// host settles the reveal (<c>SpaceContainers.Scan.cs</c>). A scanner's effect is one cycle (attribute 1014 on the module), so it runs once.
/// </summary>
internal abstract class ScannerEffect : ShipEffect
{
    // the SDE's range attributes of the two scanner effects, used only when the effect record names none
    private const int CargoScanRange = 126;
    private const int ShipScanRange = 125;

    private readonly string _name;
    private readonly bool _cargo;

    protected ScannerEffect(string name, bool cargo)
    {
        _name = name;
        _cargo = cargo;
    }

    public sealed override IReadOnlyCollection<string> EffectNames => [_name];

    /// <summary>A scanner whose module reaches nothing is refused as unsupported, not run as a scan of nothing.</summary>
    protected override string? Refusal(ModuleEffectContext module) => Optimal(module) > 0 ? null : "UNSUPPORTED_EFFECT";

    protected override SimEffect Build(ModuleEffectContext module)
    {
        var (duration, capacitor, durationAttribute) = module.Cycle();
        var optimal = Optimal(module);
        var falloff = module.Effect.FalloffAttributeId > 0 ? Math.Max(0, module.Value(module.Effect.FalloffAttributeId, 0)) : 0;
        SimChange[] told =
        [
            new(module.ModuleId, CapacitorNeed, capacitor),
            new(module.ModuleId, durationAttribute, Three(duration)),
        ];
        return new SimEffect(
            module.ModuleId, module.Module.TypeId, module.Effect.Id, module.Effect.Guid, duration, capacitor,
            Math.Max(1, module.Repeat), Math.Max(0, module.TargetId), null, null, told, told,
            Action: new SimAction.Scan(_cargo ? SimScanKinds.Cargo : SimScanKinds.Ship, optimal, falloff));
    }

    private double Optimal(ModuleEffectContext module)
    {
        var attribute = module.Effect.RangeAttributeId > 0 ? module.Effect.RangeAttributeId : _cargo ? CargoScanRange : ShipScanRange;
        return Math.Max(0, module.Value(attribute, 0));
    }
}

/// <summary>The Cargo Scanner (<c>cargoScan</c>, effect 47): the target's hold, one container deep, at a hard cutoff of its range.</summary>
internal sealed class CargoScanEffect : ScannerEffect
{
    public CargoScanEffect() : base("cargoScan", cargo: true) { }
}

/// <summary>The Ship Scanner (<c>shipScan</c>, effect 46): the target's fitted hardware, rolled per item across the range's falloff.</summary>
internal sealed class ShipScanEffect : ScannerEffect
{
    public ShipScanEffect() : base("shipScan", cargo: false) { }
}

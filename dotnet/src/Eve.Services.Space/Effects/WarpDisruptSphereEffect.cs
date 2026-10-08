namespace Eve.Services.Space.Effects;

/// <summary>
/// The Warp Disruption Field Generator (group 899; effect <c>warpDisruptSphere</c>). Its bubble is not run yet. The ship penalties of the generator (mass, maximum
/// velocity, signature and propulsion, client Python only) have no source in the static data, so an activation is refused as
/// <see cref="PendingModuleEffect.NotImplemented"/> rather than run without them (bombs brief, decision D5). The hazard restriction (attribute 5561) is not
/// enforced either and is recorded as a deviation (decision D7). A refusal is not coverage: the effect stays in the gap manifest of <c>ModuleEffectsTests</c>.
/// </summary>
internal sealed class WarpDisruptSphereEffect : PendingModuleEffect
{
    public WarpDisruptSphereEffect() : base("warpDisruptSphere")
    {
    }
}

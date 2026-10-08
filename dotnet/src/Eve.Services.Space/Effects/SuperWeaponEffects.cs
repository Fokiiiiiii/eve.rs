namespace Eve.Services.Space.Effects;

// The superweapon family of static data group 588 ("Super Weapon", category 7), V24.01 build 3396210, read with psql on sde.typeDogma, sde.dogmaEffects and
// sde.dogmaAttributes. The Pulse Activated Nexus Invulnerability Core (42522, effect 6719) is not here: it has its own handler (IndustrialInvulnerabilityEffect).
//
//   Effect (row)                 Types in group 588                                         Effect category
//   superWeapon{Race} (4489-4492) one titan doomsday per race: 24550, 24552, 24554, 23674     2 (target)
//   doomsdayBeamDOT (6472)        lances 40631, 41439, 41440, 41441                          1 (activation)
//   doomsdaySlash (6201)          reapers 40632, 41442, 41443, 41444 and Catmageddon 37474   1
//   debuffLance (11691)           disruptive lances 77398, 77399, 77400, 77401               1
//   doomsdayConeDOT (6473)        Bosonic Field Generator 40633                              1
//   doomsdayHOG (6474)            Gravitational Transportation Field Oscillator 40634         1 (and a structure variant on category 66, same effect)
//
// What each family is, from the SDE (values after dogma are the module's own; the numbers are the build's): the cone 40633 has capacitorNeed 30000, maxRange 30000,
// duration 300000, consumptionType 16275 (Strontium Clathrates) x consumptionQuantity 2000 per cycle, doomsdayAOEShape 3 (cone), doomsdayWarningDuration 10000,
// doomsdayDamageDuration 20000 at doomsdayDamageCycleTime 1000, doomsdayEnergyNeut amount 30000 within radius 10000, doomsdayImmobilityDuration 30000 and
// doomsdayNoJumpOrCloakDuration 300000, isPointTargeted 1. The HOG 40634 has maxRange 100000, duration 300000, consumption 2000 of 16275, doomsdayAOEShape 4
// (projected sphere), doomsdayAOERange 10000, doomsdayWarningDuration 15000, isPointTargeted 1. Its SDE description (not an attribute) says it moves capsuleer
// ships through a localised wormhole between two points, cannot move capital ships, and leaves the moved ships unable to warp for 60 seconds.
//
// The Bosonic Field Generator (40633) has a body: DoomsdayConeEffect.cs (the strike of protocol 25, with the fuel of its cycle through the shared mechanism). The
// Judgment and the other titan doomsdays (protocol 26) are DoomsdayTitanEffect.cs; the lances are DoomsdayLanceEffect.cs (a strike of a cylinder). The other
// families have none in the space host yet. Each handler is registered and REFUSES the activation by name, before any package is built: no simulation
// action, no fuel taken (ShipEffect.Build is never reached, so BurnsFuel is never set), and no success is reported. The reasons are the ones below.
//
// What is missing for a body, per family (the owner is the superweapons spec in docs/project/TODO.md section 12, not an EveJS port):
//   - the point the client sends (superWeaponMgr ActivateSinglePointTargetedModule / ActivateSlashModule) reaches the host only through the callback wired in
//     Program.cs (SuperWeaponService): the point is not carried into a package yet;
//   - the strike: damage after a delay, a chain with a loss per jump (EveJS tickDoomsdayEffect), and the immobility and no-jump windows. It needs a new sim
//     action and a wire bump (decisions D7 and D8 of the superweapons brief); the area is the Area shape of ADR 0011, not built yet;
//   - the lance beacon (a Deployed object, ADR 0012): the lance has a body (LanceEffect, DoomsdayLanceEffect.cs) without it, the beacon is the client's graphic only;
//   - the disruptive lance's siege requirement (5426): no siege state in the simulation (its debuff is built, protocol 28; DEVIATIONS 139);
//   - the HOG's transport: the destination point, the capital-ship exclusion and the 60 s warp block come from the SDE text only. EveJS has no function for
//     doomsdayHOG (U11, UNVERIFIED). The structure variant (category 66) is module and wiring only (O5), so it is refused the same way.

/// <summary>
/// A superweapon handler that only refuses: it names the family in its reason, so the refusal says which body is missing rather than a generic
/// <see cref="NotImplemented"/>. It is not coverage (<see cref="PendingModuleEffect"/>): the effect stays in the gap manifest of <c>ModuleEffectsTests</c>.
/// </summary>
internal abstract class SuperWeaponPendingEffect(string reason, params string[] effectNames) : PendingModuleEffect(effectNames)
{
    private readonly string _reason = reason;

    protected override string? Refusal(ModuleEffectContext module) => _reason;
}



/// <summary>The Gravitational Transportation Field Oscillator (40634, <c>doomsdayHOG</c>): refused until its transport has a body (the SDE text only; U11).</summary>
internal sealed class HogEffect()
    : SuperWeaponPendingEffect("SUPERWEAPON_HOG_NOT_IMPLEMENTED", "doomsdayHOG");

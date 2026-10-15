using Eve.Dogma;
using Eve.Ships;
using Eve.Space;

namespace Eve.Services.Space.Effects;

/// <summary>
/// The module effects the space host runs. To run another effect: write a <see cref="ShipEffect"/> that names it and builds its <see cref="SimEffect"/>, and add it here; to run
/// another projected effect add a line to <see cref="ProjectedEffect"/>. <see cref="EffectRegistry{THost, TResult}.Uncovered"/> lists what the static data has that is not here.
/// </summary>
internal static class ModuleEffects
{
    public static SensorBoosterEffect SensorBooster { get; } = new();

    /// <summary>
    /// The ship's values of the attributes with the item in the given state (and the extra modifiers), as dogma works them out: what an effect tells the client
    /// (<c>round</c>: to six places, as the reference tells them).
    /// </summary>
    internal static List<SimChange> ToldValues(DogmaWorld world, long itemId, ItemState state, IReadOnlyList<Modifier> modifiers, long shipId, int[] attributeIds, bool round)
    {
        using var assumed = world.Assume(itemId, state, modifiers);
        return [.. attributeIds.Select(id => new SimChange(shipId, id, round ? ModuleAttributes.Six(world.Get(shipId, id)) : world.Get(shipId, id)))];
    }

    public static EffectRegistry<ShipSnapshot, SimEffect> Default { get; } = new EffectRegistry<ShipSnapshot, SimEffect>()
        .Register(SensorBooster)
        .Register(new MiningLaserEffect())
        .Register(new GasHarvesterEffect())
        .Register(new SalvagerEffect())
        .Register(new TractorBeamEffect())
        .Register(new PassiveTargetingEffect())
        .Register(new AutoTargetingEffect())
        .Register(new PropulsionEffect())
        .Register(new TurretEffect())
        .Register(new BarragePendingEffect())
        .Register(new MissileEffect())
        .Register(new BombEffect())
        .Register(new InterdictionSphereEffect())
        .Register(new DefenderEffect())
        .Register(new RefusedLauncherEffect())
        .Register(new WarpDisruptSphereEffect())
        .Register(new RepairEffect())
        .Register(new ProjectedEffect())
        .Register(new SmartbombEffect())
        .Register(new EcmBurstEffect())
        .Register(new CloakingEffect())
        .Register(new MicroJumpDriveEffect())
        .Register(new DogmaModifierEffect())
        .Register(new CynoGeneratorEffect())
        .Register(new WarpModifierEffect())
        .Register(new TypeAttributeResistanceEffect())
        .Register(new ReactiveArmorHardenerEffect())
        .Register(new AncillaryRemoteArmorRepairEffect())
        .Register(new AncillaryRemoteShieldBoosterEffect())
        .Register(new PowerBoosterEffect())
        .Register(new FueledArmorRepairEffect())
        .Register(new FueledShieldBoostingEffect())
        // the Nexus invulnerability core (P.A.N.I.C., track C6): its start waits for the host's grant of the fleet's buffs (SpaceContainers.Burst.cs)
        .Register(new IndustrialInvulnerabilityEffect())
        // the superweapons of group 588 (not P.A.N.I.C.): wired, each refused by its own name until its body exists (Effects/SuperWeaponEffects.cs, docs/project/TODO.md section 12)
        .Register(new DoomsdayTitanEffect())
        .Register(new LanceEffect())
        .Register(new ReaperEffect())
        .Register(new DisruptiveLanceEffect())
        .Register(new ConeEffect())
        .Register(new HogEffect())
        .Register(new StructureHogEffect())
        .Register(new PointDefenseEffect())
        // the Standup burst projectors (ST-8b): each pulse reaches the ships the Host authorizes for it (StructureBurstEffects.cs)
        .Register(new StructureBurstProjectorEffect())
        // the entosis link works on a sovereignty hub (ST-9 b), see StructureModuleEffects.cs
        .Register(new EntosisLinkEffect())
        // the ship compressors: a facility for the fleet while they run (CompressionModuleEffect.cs)
        .Register(new CompressionModuleEffect())
        // exploration, hacking and scanner effects: wired, refused as EFFECT_NOT_IMPLEMENTED until their bodies are written (see ExplorationModuleEffects.cs)
        .Register(new DataAnalyzerEffect())
        .Register(new DataSubverterEffect())
        .Register(new CargoScanEffect())
        .Register(new ShipScanEffect())
        .Register(new ScanStrengthEffect())
        // the command bursts (track C6, protocol 43): the five families and the expedition bursts, settled at each cycle's end by the host (SpaceContainers.Burst.cs)
        .Register(new CommandBurstEffect())
        .Register(new ScanStrengthTargetEffect());
}

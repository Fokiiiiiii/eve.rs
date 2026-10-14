using Eve.Dogma;
using Eve.Inventory;
using Eve.Npc;
using Eve.Space;
using static Eve.Services.Space.Effects.ModuleAttributes;

namespace Eve.Services.Space.Effects;

/// <summary>
/// A launcher's package: a port of the missile half of <c>buildWeaponModuleSnapshot</c> (<c>buildMissileModuleSnapshot</c> of <c>space/combat/weaponDogma.js</c>, ADR 0009). A launcher
/// fires a missile in the place of a shot. What a missile does is in its charge after dogma (the skills of the pilot and the bonuses of the ship are in them): its damage times the pilot's
/// <c>missileDamageMultiplier</c>, how fast it flies (<c>maxVelocity</c>) and for how long before it explodes (<c>explosionDelay</c>, which with the speed is the range of the launcher), and the
/// explosion radius, velocity and damage reduction that decide how much of the damage a target takes. The cycle and the cost are the launcher's own values; a cycle burns the launcher's
/// <c>chargeRate</c> rounds. The pilot asks for <c>missileLaunching</c> (or <c>torpedoLaunching</c>), the effect of the charge; what runs is the launcher's own <c>useMissiles</c>, which is what the
/// client is told. Structure anticapital and multirole launchers use this handler; guided bombs and the festival launcher use their family handlers.
/// Probe launchers are refused with their own reason (<see cref="RefusedLauncherEffect"/>), listed by <see cref="MissileEffect.RefusedLauncherGroups"/>.
/// </summary>
internal sealed class MissileEffect : ShipEffect
{
    /// <summary>The effect a launcher runs (<c>useMissiles</c>).</summary>
    public const string LauncherEffectName = "useMissiles";

    /// <summary>The effects of a charge that name the firing of a launcher of the missile family: the client asks for one of them, the launcher runs <see cref="LauncherEffectName"/>.</summary>
    private static readonly string[] ClientNames = ["missileLaunching", "torpedoLaunching", FofMissileLaunching];

    /// <summary>The target of a launch that has none to lock: the launcher picks the nearest valid hostile in the missile's flight range (F.O.F. missiles, see <see cref="ResolveAutoTarget"/>).</summary>
    public const long AutoTargetId = -1;

    private const string FofMissileLaunching = "fofMissileLaunching";
    private const string SnowBallLaunching = "snowBallLaunching";
    private const string DefenderMissileLaunching = "defenderMissileLaunching";
    private const int FestivalLauncherGroup = 501;
    private const int StructureAntiCapitalLauncherGroup = 1327;
    private const int StructureGuidedBombLauncherGroup = 1328;
    private const int StructureMultiroleLauncherGroup = 1562;
    private const int StructureFestivalLauncherGroup = 1974;
    private const int CountermeasureLauncherGroup = 308;
    private const int DefenderChargeGroup = 88;
    /// <summary>The breacher pod launchers (SCARAB pods): the charge's firing effect is <c>dotMissileLaunching</c>, and a hit starts its damage over time.</summary>
    private const int BreacherLauncherGroup = 4807;
    private const string DotMissileLaunching = "dotMissileLaunching";
    /// <summary>The breacher pod charge's damage over time (<c>dotDuration</c>, <c>dotMaxDamagePerTick</c>, <c>dotMaxHPPercentagePerTick</c>).</summary>
    private const int DotDuration = 5735;
    private const int DotMaxDamagePerTick = 5736;
    private const int DotMaxHpPercentPerTick = 5737;

    /// <summary>
    /// The firing effects a launcher of a group that is not in the missile family takes (the festival launcher's snowballs, the countermeasure launcher's torpedoes and defenders).
    /// Which handler runs is the charge's: <see cref="MissileEffect"/> for a missile or torpedo, <see cref="DefenderEffect"/> for a defender.
    /// </summary>
    private static readonly IReadOnlyDictionary<int, string[]> OtherLauncherNames = new Dictionary<int, string[]>
    {
        [FestivalLauncherGroup] = [SnowBallLaunching],
        [StructureFestivalLauncherGroup] = [SnowBallLaunching],
        [CountermeasureLauncherGroup] = ["missileLaunching", "torpedoLaunching", DefenderMissileLaunching],
        [BreacherLauncherGroup] = [DotMissileLaunching],
    };

    /// <summary>The firing effects of the launchers that put a deployable in space or fire at no target (<see cref="BombEffect"/>, <see cref="InterdictionSphereEffect"/>,
    /// <see cref="DefenderEffect"/>), by group.</summary>
    private static readonly IReadOnlyDictionary<int, string[]> DeployableLaunchers = new Dictionary<int, string[]>
    {
        [512] = ["defenderMissileLaunching"],
        [589] = ["torpedoLaunching"],
        [862] = ["bombLaunching"],
        [StructureGuidedBombLauncherGroup] = ["bombLaunching"],
    };

    /// <summary>Every firing effect a charge can name (the default effect of the client's request), so a launcher that is refused is refused by name, not as unknown.</summary>
    private static readonly string[] KnownFiringNames =
        ["missileLaunching", "torpedoLaunching", "fofMissileLaunching", "bombLaunching", "defenderMissileLaunching", "probeLaunching", "snowBallLaunching", "dotMissileLaunching"];

    /// <summary>The attribute of a pilot that scales the damage of every missile (<c>missileDamageMultiplier</c>).</summary>
    private const int MissileDamageMultiplier = 212;

    private const int StructureHitPoints = 9;
    private const int Agility = 70;
    private const int Radius = 162;
    private const int ExplosionDelay = 281;
    private const int AoeVelocity = 653;
    private const int AoeCloudSize = 654;
    private const int AoeDamageReductionFactor = 1353;
    private const int AoeDamageReductionSensitivity = 1354;
    private const double DefaultReductionSensitivity = 5.5;

    private const int TorpedoGroup = 89;
    private const int XlTorpedoGroup = 476;
    private const int TorpedoLauncherGroup = 508;
    private const int XlTorpedoLauncherGroup = 524;
    private const int RapidTorpedoLauncherGroup = 1673;

    private const string MissileDeployment = "effects.MissileDeployment";
    private const string TorpedoDeployment = "effects.TorpedoDeployment";

    /// <summary>
    /// The groups whose launchers carry <c>useMissiles</c> in the static data but are not run yet, each with the reason it is refused (the client shows the module cannot be
    /// started). Every such group is named here, so no launcher group of the static data is left to an unexplained <c>UNSUPPORTED_EFFECT</c>.
    /// Structure launcher groups are handled by this class and <see cref="BombEffect"/>; probe launchers still need the probe lifecycle. Breacher pods use their dedicated damage-over-time path.
    /// </summary>
    public static readonly IReadOnlyDictionary<int, string> RefusedLauncherGroups = new Dictionary<int, string>
    {
        [481] = "PROBE_LAUNCHER_NOT_IMPLEMENTED",
        [1226] = "PROBE_LAUNCHER_NOT_IMPLEMENTED",
    };

    /// <summary>The launchers that put a deployable in space or fire at no target (<see cref="BombEffect"/>, <see cref="InterdictionSphereEffect"/>, <see cref="DefenderEffect"/>).</summary>
    public static IReadOnlySet<int> DeployableLauncherGroups { get; } = DeployableLaunchers.Keys.ToHashSet();

    /// <summary>The launcher groups this effect runs: the missile family derived from the static data (<see cref="LauncherFamilies"/>).</summary>
    private static readonly IReadOnlySet<int> StructureMissileLauncherGroups = new HashSet<int> { StructureAntiCapitalLauncherGroup, StructureMultiroleLauncherGroup };

    public static IReadOnlySet<int> ServedLauncherGroups(DogmaStatic statics) => statics.MissileLauncherGroups.Concat(StructureMissileLauncherGroups).ToHashSet();

    /// <summary>The launchers this effect runs that are not of the missile family: the festival launcher and the countermeasure launcher (<see cref="OtherLauncherNames"/>).</summary>
    public static IReadOnlySet<int> OtherServedLauncherGroups { get; } = OtherLauncherNames.Keys.ToHashSet();

    public override IReadOnlyCollection<string> EffectNames { get; } = [LauncherEffectName];

    /// <summary>
    /// The effect a launcher runs when the pilot asks for the firing effect of its charge: the launcher's own, or null when the module is no launcher or the name is not one of the firing effects.
    /// A launcher that is refused (<see cref="RefusedLauncherGroups"/>) is a launcher too: it is asked for its own effect and refused there.
    /// </summary>
    public static EffectDef? LauncherEffectFor(TypeDef type, DogmaStatic statics, string requested)
    {
        var group = type.GroupId;
        var names = statics.MissileLauncherGroups.Contains(group) || StructureMissileLauncherGroups.Contains(group) ? ClientNames
            : OtherLauncherNames.GetValueOrDefault(group) ?? (RefusedLauncherGroups.ContainsKey(group) ? KnownFiringNames : DeployableLaunchers.GetValueOrDefault(group, []));
        return names.Any(n => n.Equals(requested, StringComparison.OrdinalIgnoreCase))
            ? type.EffectIds.Select(statics.Effect).FirstOrDefault(e => e is not null && e.Name.Equals(LauncherEffectName, StringComparison.OrdinalIgnoreCase))
            : null;
    }

    /// <summary>The missile family, the festival launcher, and the countermeasure launcher when its charge is a missile (a defender is <see cref="DefenderEffect"/>'s).</summary>
    protected override bool Handles(ModuleEffectContext module) =>
        module.Statics.MissileLauncherGroups.Contains(module.Type.GroupId) || StructureMissileLauncherGroups.Contains(module.Type.GroupId)
        || module.Type.GroupId is FestivalLauncherGroup or StructureFestivalLauncherGroup
        || module.Type.GroupId == BreacherLauncherGroup || module.Type.GroupId == CountermeasureLauncherGroup && !IsDefenderCharge(module);

    /// <summary>Whether the loaded charge of a launcher is a defender missile (its group, 88).</summary>
    internal static bool IsDefenderCharge(ModuleEffectContext module) =>
        module.Snapshot.ChargeOf(module.Module) is { } charge && module.Statics.Type(charge.TypeId)?.GroupId == DefenderChargeGroup;

    /// <summary>Whether the loaded charge is an F.O.F. (auto-targeting) missile: its default firing effect is <c>fofMissileLaunching</c>, from the static data.</summary>
    internal static bool IsFofCharge(ModuleEffectContext module) =>
        module.Snapshot.ChargeOf(module.Module) is { } charge && module.Statics.Type(charge.TypeId) is { } type
        && type.EffectIds.Select(module.Statics.Effect).Any(effect => effect is { } e && e.Name.Equals(FofMissileLaunching, StringComparison.OrdinalIgnoreCase));

    /// <summary>A launcher with no missile in it is refused before it is asked for a target (the reference's order: <c>NO_AMMO</c> first, then the target).</summary>
    protected override string? Refusal(ModuleEffectContext module)
    {
        var charge = module.Snapshot.ChargeOf(module.Module);
        if (charge is null || charge.Quantity < ChargesPerCycle(module.Attributes))
        {
            return "NO_AMMO";
        }
        // an F.O.F. missile locks nothing: the launcher picks its target when it fires (see ResolveAutoTarget)
        return module.TargetId <= 0 && !IsFofCharge(module) ? "TARGET_REQUIRED" : null;
    }

    protected override SimEffect Build(ModuleEffectContext module)
    {
        var charge = module.Snapshot.ChargeOf(module.Module)!;
        var attributes = module.Attributes;
        var shot = module.World.Attributes(charge.Id);
        var pilot = module.World.Attributes(module.Snapshot.Character.Id);
        var multiplier = Math.Max(0, pilot.TryGetValue(MissileDamageMultiplier, out var m) && double.IsFinite(m) ? m : 1);
        double Damage(int attribute) => NpcArmament.Round6(Math.Max(0, shot.TryGetValue(attribute, out var damage) && double.IsFinite(damage) ? damage : 0) * multiplier);

        var missile = MissileOf(module, charge);
        var range = NpcArmament.Round6(missile.MaxVelocity * (missile.FlightTimeMs / 1000));

        var (duration, capacitor, _) = module.Cycle();
        var fx = FxOf(module, charge);
        var target = IsFofCharge(module) ? AutoTargetId : Math.Max(0, module.TargetId);
        var launcher = new SimTurret(
            module.Module.TypeId, fx, [Damage(EmDamage), Damage(ThermalDamage), Damage(KineticDamage), Damage(ExplosiveDamage)], range, 0, 0, 40_000,
            new SimTurretCharge(charge.TypeId, charge.Flag, charge.Quantity, ChargesPerCycle(attributes), Reloads: module.Value(ModuleAttributes.ReloadTime, 0) > 0), missile);
        // the client is told the cost and the cycle of the launcher as it starts and stops (and no range: a missile's is its own)
        SimChange[] told = [new(module.ModuleId, CapacitorNeed, capacitor), new(module.ModuleId, Speed, Three(duration))];
        return new SimEffect(module.ModuleId, module.Module.TypeId, module.Effect.Id, fx, duration, capacitor, Math.Max(1, module.Repeat), target, null, null, told, told, launcher);
    }

    /// <summary>
    /// The missile a charge is, from its dogma: the ball of the charge (its size, weight and agility, its hit points, its speed and its flight time) and its explosion
    /// (radius, velocity, damage reduction factor and sensitivity). The launchers that fire a missile (<see cref="MissileEffect"/>, and the defender launcher) use it.
    /// </summary>
    internal static SimMissile MissileOf(ModuleEffectContext module, Item charge)
    {
        var shot = module.World.Attributes(charge.Id);
        double Charge(int attribute, double fallback) => shot.TryGetValue(attribute, out var value) && double.IsFinite(value) ? value : fallback;
        var maxVelocity = Math.Max(0, NpcArmament.Round6(Charge(MaxVelocity, 0)));
        var flightTime = Math.Max(1, NpcArmament.Round6(Charge(ExplosionDelay, 1000)));
        // the ball of the missile: what the charge's type says of its size and weight, and its agility (a missile turns at once, so it is small); the reference's defaults where it says nothing
        double Positive(int attribute, double fallback) => Charge(attribute, 0) is var value and > 0 ? value : fallback;
        // a breacher pod's hit starts the damage over time its charge carries (a charge with no duration has none)
        var duration = Charge(DotDuration, 0);
        var dot = duration > 0
            ? new SimDot(NpcArmament.Round6(duration), Math.Max(0, NpcArmament.Round6(Charge(DotMaxDamagePerTick, 0))), Math.Max(0, NpcArmament.Round6(Charge(DotMaxHpPercentPerTick, 0))))
            : null;
        var missile = new SimMissile(
            charge.TypeId, module.Statics.Type(charge.TypeId)?.GroupId ?? 0, module.Statics.Type(charge.TypeId)?.Name ?? "Missile",
            Positive(Radius, 1), Positive(Mass, 1), Positive(Agility, 0.1), Math.Max(0, Charge(StructureHitPoints, 0)), maxVelocity, flightTime,
            Math.Max(1, NpcArmament.Round6(Charge(AoeCloudSize, 1))),
            Math.Max(0.001, NpcArmament.Round6(Charge(AoeVelocity, 0.001))),
            Math.Clamp(Charge(AoeDamageReductionFactor, 1), 0.000001, 1),
            Math.Max(0.000001, NpcArmament.Round6(Charge(AoeDamageReductionSensitivity, DefaultReductionSensitivity))), dot);
        return missile;
    }

    /// <summary>What the client plays: the effect's own, else the deployment of the missiles, or of the torpedoes for a torpedo launcher or charge (<c>resolveWeaponSpecialFxGUID</c>).</summary>
    private static string FxOf(ModuleEffectContext module, Item charge)
    {
        var guid = module.Effect.Guid.Trim();
        if (guid.Length > 0 && !guid.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return guid;
        }
        var torpedo = module.Type.GroupId is TorpedoLauncherGroup or XlTorpedoLauncherGroup or RapidTorpedoLauncherGroup
            || module.Statics.Type(charge.TypeId)?.GroupId is TorpedoGroup or XlTorpedoGroup;
        return torpedo ? TorpedoDeployment : MissileDeployment;
    }
}

/// <summary>
/// The handler of a launcher whose group carries <c>useMissiles</c> but is not run (<see cref="MissileEffect.RefusedLauncherGroups"/>): an activation is refused with the
/// reason of its group, before a target is asked for. It is registered under the same name as <see cref="MissileEffect"/> and serves only the groups that one does not,
/// so it adds no coverage (a pending handler, see <see cref="PendingModuleEffect"/>).
/// </summary>
internal sealed class RefusedLauncherEffect : PendingModuleEffect
{
    public RefusedLauncherEffect() : base(MissileEffect.LauncherEffectName)
    {
    }

    protected override bool Handles(ModuleEffectContext module) => MissileEffect.RefusedLauncherGroups.ContainsKey(module.Type.GroupId);

    protected override string? Refusal(ModuleEffectContext module) =>
        MissileEffect.RefusedLauncherGroups.TryGetValue(module.Type.GroupId, out var reason) ? reason : NotImplemented;
}

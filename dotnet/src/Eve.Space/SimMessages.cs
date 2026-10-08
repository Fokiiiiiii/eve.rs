namespace Eve.Space;

/// <summary>A message on the link between the service host and the simulation (docs/design/adr/0007-space-simulation-process.md).</summary>
public abstract record SimMessage;

public sealed record SimHello(ushort Version) : SimMessage;

public sealed record SimHelloAck(ushort Version, uint PollHz) : SimMessage;

/// <summary>How a ship comes into space.</summary>
public enum EnterHow : byte
{
    /// <summary>It undocks from a station.</summary>
    Undock = 0,

    /// <summary>It is already in space (a login in space, a jump arrival).</summary>
    InSpace = 1,
}

/// <summary>A fitted module, as the ship's slim item lists it.</summary>
public sealed record SimModule(long ItemId, int TypeId, int Flag);

/// <summary>How the three layers of a ship resist each kind of damage (em, thermal, kinetic, explosive): the share of a hit that gets through.</summary>
public sealed record SimResonances(IReadOnlyList<double> Shield, IReadOnlyList<double> Armor, IReadOnlyList<double> Structure)
{
    /// <summary>Nothing resisted.</summary>
    public static readonly SimResonances None = new([1, 1, 1, 1], [1, 1, 1, 1], [1, 1, 1, 1]);
}

/// <summary>
/// What a ship can take, in points, and how it resists: dogma's numbers for the ship as it is fitted and flown. <paramref name="ArmorPinned"/> is set while a fitted
/// Polarized weapon holds the armor's resonances at one: the simulation then keeps them there whatever a reactive armor hardener does.
/// </summary>
public sealed record SimVitals(double ShieldCapacity, double ArmorHp, double StructureHp, SimResonances Resonances, bool ArmorPinned = false)
{
    /// <summary>Nothing to hurt.</summary>
    public static readonly SimVitals Unhurtable = new(0, 0, 0, SimResonances.None);
}

/// <summary>What a ship has taken, as fractions: the hull's damage, the armor's, the shield's charge and how full the capacitor is.</summary>
public sealed record SimCondition(double Damage, double ArmorDamage, double ShieldCharge, double Charge)
{
    /// <summary>A ship that has taken nothing.</summary>
    public static readonly SimCondition Full = new(0, 0, 1, 1);
}

/// <summary>What motion and the slim item need to know of a ship: the host works these out (dogma, skills, fitting), the simulation only uses them.</summary>
public sealed record SimShip(
    long ShipId, long CharacterId, int TypeId, int GroupId, int CategoryId, string Name, long OwnerId, int CorporationId, int AllianceId, int WarFactionId,
    double SecurityStatus, double Radius, double Mass, double MaxVelocity, double Agility, double WarpSpeedAu, double ShieldRechargeMs,
    double MaxTargetRange, double ScanResolution, double SignatureRadius, int MaxLockedTargets, double CapacitorCapacity, double CapacitorRechargeSeconds, IReadOnlyList<SimModule> Modules,
    SimVitals? Vitals = null, SimCondition? Condition = null, SimSensors? Sensors = null);

/// <summary>What a ship holds against a jammer: the strength of its sensors (gravimetric, ladar, magnetometric, radar; the strongest counts) and how much of a jam it takes (<c>ECMResistance</c>: 1 for an ordinary ship, 0 for one that cannot be jammed).</summary>
public sealed record SimSensors(double Gravimetric, double Ladar, double Magnetometric, double Radar, double EcmResistance)
{
    /// <summary>A ship with no sensors that count and no resistance.</summary>
    public static readonly SimSensors Plain = new(0, 0, 0, 0, 1);
}

/// <summary>A ship enters a solar system.</summary>
public sealed record SimEnter(ulong SessionId, int SolarSystemId, EnterHow How, long StationId, SimShip Ship, IReadOnlyList<double>? Position = null, IReadOnlyList<double>? Direction = null) : SimMessage;

public sealed record SimHeatModule(
    long ModuleId, int Flag, double StructureHp, double DamageHp, double HeatDamageHp, double HeatAbsorptionRate, bool Overloadable, bool HeatImmune, bool Online);

public sealed record SimHeatProfile(
    double GenerationMultiplier, double DamageMultiplier, int ThermodynamicsLevel,
    IReadOnlyList<double> Capacity, IReadOnlyList<double> InitialRackHeat, IReadOnlyList<double> DissipationRate, IReadOnlyList<double> Attenuation,
    IReadOnlyList<SimHeatModule> Modules);

public sealed record SimSetHeatProfile(ulong SessionId, SimHeatProfile Profile) : SimMessage;

public sealed record SimSetModuleOverheat(ulong SessionId, long ModuleId, bool Active) : SimMessage;

public sealed record SimSetModuleOffline(ulong SessionId, long ModuleId) : SimMessage;

public sealed record SimSetRackOverheat(ulong SessionId, int Rack, bool Active) : SimMessage;

/// <summary>Whether an <see cref="SimEnter"/> was done: <see cref="Error"/> is null when it was, the reason when it was not.</summary>
public sealed record SimEnterReply(ulong SessionId, string? Error) : SimMessage;

/// <summary>Why a ship leaves a scene.</summary>
public enum LeaveWhy : byte
{
    Docked = 0,
    LoggedOff = 1,
    Jumped = 2,
    Ejected = 3,
    Boarded = 4,
}

public sealed record SimLeave(ulong SessionId, LeaveWhy Why) : SimMessage;

/// <summary>The client bound the solar system's ballpark: send it the scene.</summary>
public sealed record SimBind(ulong SessionId) : SimMessage;

/// <summary>The client asked for the state again (<c>UpdateStateRequest</c>).</summary>
public sealed record SimUpdateState(ulong SessionId) : SimMessage;

/// <summary>The codes of the commands a pilot gives.</summary>
public static class SimCommands
{
    public const byte SetSpeedFraction = 1;
    public const byte GotoDirection = 2;
    public const byte Stop = 3;
    public const byte Dock = 4;
    public const byte FollowBall = 5;
    public const byte Orbit = 6;
    public const byte AlignTo = 7;
    public const byte WarpTo = 8;
    public const byte Jump = 9;

    /// <summary>Start locking a target (the target's id).</summary>
    public const byte AddTarget = 10;

    /// <summary>Give up a lock that is going (the target's id).</summary>
    public const byte CancelAddTarget = 11;

    /// <summary>Let go of locked targets (their ids).</summary>
    public const byte RemoveTargets = 12;

    /// <summary>Let go of every target.</summary>
    public const byte ClearTargets = 13;

    /// <summary>What the ship has locked.</summary>
    public const byte GetTargets = 14;

    /// <summary>Who has the ship locked.</summary>
    public const byte GetTargeters = 15;

    /// <summary>Stop a module's effect at the end of its cycle (the module's id).</summary>
    public const byte Deactivate = 16;

    /// <summary>The code an <see cref="SimActivate"/> is answered under.</summary>
    public const byte Activate = 17;

    /// <summary>Mend the ship: full shield, armor, hull and capacitor. Answered with <see cref="SimAccepted"/> after what the pilot is told.</summary>
    public const byte Heal = 18;

    /// <summary>What the ship has taken, answered with <see cref="SimConditionReply"/>.</summary>
    public const byte GetCondition = 19;

    /// <summary>How far a wreck or a can is (its id), answered with <see cref="SimMeasured"/>.</summary>
    public const byte Measure = 20;

    /// <summary>Warp to an explicit coordinate, with x, y, z, and arrival range.</summary>
    public const byte WarpToPoint = 21;

    /// <summary>Resolve a target's warp-in point without beginning a warp.</summary>
    public const byte ResolveWarpTarget = 22;

    /// <summary>Read a body's or ship's current coordinates.</summary>
    public const byte PositionOf = 23;

    /// <summary>Send the visual half of an in-space login before the later full ballpark bind.</summary>
    public const byte PrimeLoginBallparkVisuals = 24;

    /// <summary>The code a <see cref="SimPlaceContainer"/> is answered under (<see cref="SimContainerPlaced"/>, or <see cref="SimRefused"/>).</summary>
    public const byte PlaceContainer = 28;

    /// <summary>The code a <see cref="SimChangeWreck"/> is answered under.</summary>
    public const byte ChangeWreck = 30;
}

public sealed record SimCommand(ulong SessionId, byte Code, IReadOnlyList<double> Args) : SimMessage;

/// <summary>New numbers for a ship in space.</summary>
public sealed record SimShipChanged(ulong SessionId, SimShip Ship) : SimMessage;

/// <summary>A notification the simulation has for a player's client: the host numbers it and sends it. The arguments are a marshalled tuple.</summary>
public sealed record SimNotify(ulong SessionId, string Name, string IdType, ReadOnlyMemory<byte> Args) : SimMessage;

/// <summary>The ship has arrived and the docking is done in space: the host moves the character and keeps what the ship has taken.</summary>
public sealed record SimDocked(ulong SessionId, long StationId, SimCondition? Condition = null) : SimMessage;

/// <summary>What the ship has taken and how full its capacitor is (the answer to <see cref="SimCommands.GetCondition"/>).</summary>
public sealed record SimConditionReply(ulong SessionId, byte Command, SimCondition Condition) : SimMessage;

/// <summary>A command the scene cannot do.</summary>
public sealed record SimRefused(ulong SessionId, byte Command, string Reason) : SimMessage;

/// <summary>A command was accepted, after the update it made. A <c>Dock</c> is answered with the time (a FILETIME).</summary>
public sealed record SimAccepted(ulong SessionId, byte Command, long At) : SimMessage;


/// A requested target coordinate was resolved by the simulation.
public sealed record SimPosition(ulong SessionId, byte Command, IReadOnlyList<double> Position) : SimMessage;

/// <summary>A <c>Dock</c> could not be done from where the ship is: it now approaches the station, <paramref name="Distance"/> metres off its surface.</summary>
public sealed record SimApproaching(ulong SessionId, byte Command, double Distance) : SimMessage;

/// <summary>A <c>Jump</c> was accepted: the jump-out effect has begun, and the ship leaves for <paramref name="DestinationSystem"/> a few seconds on.</summary>
public sealed record SimJumping(ulong SessionId, byte Command, int DestinationSystem, long DestinationGate) : SimMessage;

/// <summary>The ship has left its system through a gate and is in the scene of the other one, in front of <paramref name="ToGate"/>: the host moves the character.</summary>
public sealed record SimJumped(ulong SessionId, int FromSystem, long FromGate, int ToSystem, long ToGate) : SimMessage;

/// <summary>
/// What a targeting command answers: <paramref name="Pending"/> is set when a lock is going, <paramref name="Targets"/> are the ships and bodies locked
/// (for <c>GetTargeters</c>, the ships that have this one locked).
/// </summary>
public sealed record SimTargets(ulong SessionId, byte Command, bool Pending, IReadOnlyList<long> Targets) : SimMessage;

/// <summary>
/// A targeting command the scene refused, with what the client is told of the target: its type, and (only when it was out of range) the range the ship
/// locks at and how far off the target is.
/// </summary>
public sealed record SimTargetRefused(ulong SessionId, byte Command, string Reason, int TargetType, double DesiredRange, double ActualDistance) : SimMessage;

/// <summary>The ship's numbers an effect changes.</summary>
public sealed record SimNumbers(double Mass, double MaxVelocity, double SignatureRadius);

/// <summary>A value the client is told an item now has.</summary>
public sealed record SimChange(long ItemId, int AttributeId, double Value);

/// <summary>
/// What a turret shoots: the type of the weapon (what the combat log calls it), the effect the client plays, the damage of a shot before the quality of the hit
/// (em, thermal, kinetic, explosive: the damage multiplier is in it), the range, the falloff, the tracking (radians a second) and the signature resolution. A launcher is a turret that
/// flies a <see cref="SimMissile"/> in place of shooting: its range is what the missile covers, and what reaches the target is what the missile's numbers say.
/// </summary>
public sealed record SimTurret(
    int WeaponTypeId, string Fx, IReadOnlyList<double> RawDamage, double OptimalRange, double Falloff, double TrackingSpeed, double SignatureResolution,
    SimTurretCharge? Charge = null, SimMissile? Missile = null, SimSpool? Spool = null, SimAoe? Aoe = null, SimArc? Arc = null);

/// <summary>
/// The damage ramp of a weapon that builds up its damage on the target it keeps shooting at (a precursor weapon: <c>damageMultiplierBonusPerCycle</c> and
/// <c>damageMultiplierBonusMax</c>). Each cycle the bonus grows by <paramref name="PerCycle"/>, up to <paramref name="Max"/>; a shot is multiplied by one plus the
/// bonus built up before it (the first shot has none). The simulation keeps the bonus of the running weapon and starts it again from none when the target changes
/// or the weapon stops.
/// </summary>
public sealed record SimSpool(double PerCycle, double Max);

/// <summary>
/// The explosion of a shot that does not roll to hit (a vorton projector): the radius of its cloud, its velocity, and the damage reduction factor and its
/// sensitivity, as a missile's explosion has them. What reaches a target is the damage times the share the explosion gives it at the target's signature
/// and speed (the missile's curve in <c>eve-space</c>'s <c>missiles</c>).
/// </summary>
public sealed record SimAoe(double ExplosionRadius, double ExplosionVelocity, double DamageReductionFactor, double DamageReductionSensitivity);

/// <summary>
/// The chain a shot links from its target: the ships within <paramref name="Range"/> of the target, up to <paramref name="Targets"/> ships in all (the target
/// counts, so the chain makes one link fewer than the ships).
/// </summary>
public sealed record SimArc(double Range, int Targets);

/// <summary>
/// What a launcher flies: the type of the missile it fires and its group, what the client calls it, the radius, mass and agility of its ball, the hit points it has while it flies (what a smartbomb
/// or a point defense has to do to shoot it down), its speed (metres a second), how long it flies before it explodes (milliseconds) and the numbers that decide how much of its damage a target
/// takes (the explosion radius and velocity, the damage reduction factor and its sensitivity). The simulation makes an entity of its own of each missile that is fired (<c>eve-space</c>'s
/// <c>missile_entity</c>), apart from the ships.
/// </summary>
public sealed record SimMissile(
    int ChargeTypeId, int GroupId, string Name, double Radius, double Mass, double Agility, double HitPoints, double MaxVelocity, double FlightTimeMs,
    double ExplosionRadius, double ExplosionVelocity, double DamageReductionFactor, double DamageReductionSensitivity);

/// <summary>
/// The charge a turret fires: its type, the flag it sits at in the ship (the slot of the module), the rounds left and the rounds a cycle takes. The simulation
/// spends them cycle by cycle (<see cref="SimChargeSpent"/>) and stops the weapon when they are gone.
/// </summary>
public sealed record SimTurretCharge(int TypeId, int Flag, long Rounds, long PerCycle);

/// <summary>What the host changes in the rounds of a gun that runs: rounds go in (up to the capacity, and no more than the hold has) or come out (all of them when the quantity is 0).</summary>
public abstract record SimChargeOp
{
    private SimChargeOp()
    {
    }

    /// <summary>Rounds go into the gun: up to <paramref name="Capacity"/> of them in all, and no more than <paramref name="Available"/> more.</summary>
    public sealed record Add(long Capacity, long Available) : SimChargeOp;

    /// <summary>Rounds come out of the gun: <paramref name="Quantity"/> of them, all of them when it is 0.</summary>
    public sealed record Remove(long Quantity) : SimChargeOp;
}

/// <summary>
/// The host changes the rounds of a gun of this session's ship (a pilot loaded it or unloaded it in space). The simulation keeps the rounds of a gun that runs, so it
/// says what they come to; it answers with <see cref="SimChargeAdjusted"/>, after every <see cref="SimChargeSpent"/> it told before.
/// </summary>
public sealed record SimAdjustCharge(ulong RequestId, ulong SessionId, long ModuleId, int ChargeType, SimChargeOp Op) : SimMessage;

/// <summary>The answer to <see cref="SimAdjustCharge"/>: whether the gun runs (when it does not, the host has its rounds and nothing was changed), the rounds it had and the rounds it has.</summary>
public sealed record SimChargeAdjusted(ulong RequestId, bool Running, long Before, long After) : SimMessage;

/// <summary>A turret of this session's ship spent rounds: how many it has left (the host keeps the quantity of the charge item).</summary>
public sealed record SimChargeSpent(ulong SessionId, long ModuleId, long RoundsLeft) : SimMessage;

/// <summary>
/// An effect the host asks the simulation to run, with what the host has worked out for it (dogma): the numbers of the ship with and without it, the cycle and
/// its cost; for a weapon, what it shoots (<paramref name="Turret"/>): the target is hit at the start of each cycle.
/// </summary>
public sealed record SimEffect(
    long ModuleId, int ModuleType, int EffectId, string Fx, double DurationMs, double CapacitorNeed, int Repeat, long TargetId, SimNumbers? On, SimNumbers? Off,
    IReadOnlyList<SimChange> OnChanges, IReadOnlyList<SimChange> OffChanges, SimTurret? Turret = null, SimAction? Action = null,
    /// <summary>The effect burns the fuel its consumption attributes name (713 and 714) each cycle: the simulation asks the host for it before each cycle that starts
    /// (<see cref="SimModuleWorkCommands.ConsumeFuel"/>), and does not start one the host refuses.</summary>
    bool BurnsFuel = false);

/// <summary>The codes of the work a module asks the host for (<see cref="SimModuleWorkCycle.Command"/>). Its cycles share the drone codes of mining and salvage (7, 8).</summary>
public static class SimModuleWorkCommands
{
    /// <summary>The fuel of the next cycle: the host takes the module's consumption quantity of its consumption type from the ship, or refuses (<c>NO_FUEL</c>).</summary>
    public const byte ConsumeFuel = 9;
}

/// <summary>A layer of a ship's health.</summary>
public enum SimLayer : byte
{
    Shield = 0,
    Armor = 1,
    Structure = 2,
}

/// <summary>What a cycle of an effect does to the scene besides what it costs and the numbers it changes (the host has worked out the points from dogma).</summary>
public abstract record SimAction
{
    private SimAction()
    {
    }

    /// <summary>
    /// A layer of the ship that runs the effect is mended, <paramref name="Amount"/> points a cycle: as the cycle ends (an armor repairer, a hull repairer) or as it begins (a
    /// shield booster).
    /// </summary>
    public sealed record Repair(SimLayer Layer, double Amount, bool AtCycleEnd) : SimAction;

    /// <summary>
    /// The effect is projected at the locked target (<see cref="SimEffect.TargetId"/>), which must be in reach (<paramref name="Optimal"/> metres from surface to surface, and
    /// <paramref name="Falloff"/> more over which it weakens); what it does lands as the cycle ends or as it begins. <paramref name="Jam"/> is what the target's HUD calls it (<c>remoteArmorRepair</c>, <c>ewEnergyNeut</c>).
    /// </summary>
    public sealed record Remote(SimRemote What, double Optimal, double Falloff, bool AtCycleEnd, string Jam) : SimAction;

    /// <summary>
    /// A smartbomb: as each cycle begins every ship within <paramref name="Range"/> of the surface of the ship that runs it takes <paramref name="Damage"/> (em, thermal, kinetic,
    /// explosive), with no target and no roll. <paramref name="HurtsPilots"/> is false where the safety of the pilot would stop it: it does not start with another pilot in its range
    /// (<c>SafetyActivated</c>) and stops when one comes in.
    /// </summary>
    public sealed record Smartbomb(double Range, IReadOnlyList<double> Damage, bool HurtsPilots) : SimAction;


    /// <summary>An ECM burst rolls against every nearby ship at cycle start and breaks successful targets' current locks without leaving a lasting jam.</summary>
    public sealed record EcmBurst(double Range, IReadOnlyList<double> Strength) : SimAction;

    /// <summary>Uses loaded charges at each cycle; after they run out the effect may switch to an uncharged action or stop.</summary>
    public sealed record Charged(SimAction Action, SimAction? Fallback, int ChargeTypeId, int ChargeFlag, int ChargePerCycle, long Charges) : SimAction;


    /// <summary>Engages or disengages the ship's active cloaking device.</summary>
    public sealed record Cloak : SimAction;

    /// <summary>Spools once, then shifts the ship along its heading; cooldown begins when the spool succeeds or is interrupted.</summary>
    public sealed record MicroJumpDrive(double DistanceMeters, long CooldownMs) : SimAction;

    /// <summary>
    /// A reactive armor hardener: the armor's resonances start at <paramref name="Seed"/> (the module's own values, em, thermal, kinetic, explosive), and at the end
    /// of each cycle move by <paramref name="Shift"/> of a resonance at most, as the damage the armor took in the cycle says. The pilot is told the armor's resonances
    /// as the attributes <paramref name="AttributeIds"/> (in the same order) when they change.
    /// </summary>
    public sealed record ResistanceShift(IReadOnlyList<double> Seed, double Shift, IReadOnlyList<int> AttributeIds) : SimAction;

    /// <summary>
    /// A mining laser's cycle (tag 7). At the end of each cycle with its target there the simulation sends the host a <see cref="SimModuleWorkCycle"/> and parks the
    /// module until the answer: the host decides the yield. <paramref name="Family"/> is 0 ore, 1 ice or 2 gas (<see cref="SimMiningFamilies"/>).
    /// </summary>
    public sealed record Mining(byte Family, double MiningAmountM3, double WasteVolumeMultiplier, double WasteProbability, double CritProbability, double CritQuantityMultiplier) : SimAction;

    /// <summary>A salvager's cycle (tag 8): as a mining cycle, with the wreck's access chance moved by <paramref name="AccessBonusPercent"/>.</summary>
    public sealed record Salvage(double AccessBonusPercent) : SimAction;

    /// <summary>A tractor beam (tag 9). Its package has not landed: the activation is refused until it has (the simulation never runs it).</summary>
    public sealed record Tractor(double VelocityMetersPerSecond, double HoldDistanceMeters) : SimAction;

    /// <summary>A passive targeter (tag 10). Its package has not landed: the activation is refused until it has.</summary>
    public sealed record PassiveLock : SimAction;

    /// <summary>An auto-targeting system (tag 11). Its package has not landed: the activation is refused until it has.</summary>
    public sealed record AutoTarget(double RangeMeters) : SimAction;

    /// <summary>
    /// A doomsday's strike (tag 13, ADR 0013 epoch 25): an area in front of the ship, hit after a warning, pulse by pulse. The shape is the static data's
    /// <c>doomsdayAOEShape</c> (1 a cylinder, 3 a cone); <paramref name="Direction"/> is the unit aim from the ship's centre; the damage is per pulse (em,
    /// thermal, kinetic, explosive). The ship is held for <paramref name="ImmobileMs"/> and cannot jump or cloak for <paramref name="NoJumpMs"/>; every ship
    /// within <paramref name="NeutRadius"/> loses <paramref name="NeutAmount"/> capacitor once at the start (see the simulation's <c>strike.rs</c>).
    /// </summary>
    public sealed record Strike(
        byte Shape,
        IReadOnlyList<double> Direction,
        double Length,
        double Radius,
        double WarningMs,
        double PulseMs,
        int Pulses,
        IReadOnlyList<double> Damage,
        double SignatureRadius,
        double ImmobileMs,
        double NoJumpMs,
        double NeutAmount,
        double NeutRadius,
        double NeutSignatureRadius) : SimAction
    {
        /// <summary>Whether the numbers are ones the simulation runs (the same checks the simulation makes on decode).</summary>
        public bool IsValid =>
            (Shape is 1 or 3)
            && Direction.Count == 3 && Damage.Count == 4
            && Direction.All(double.IsFinite) && Damage.All(d => double.IsFinite(d) && d >= 0)
            && Math.Abs(Math.Sqrt(Direction.Sum(c => c * c)) - 1.0) < 1e-6
            && double.IsFinite(Length) && Length > 0
            && double.IsFinite(Radius) && Radius >= 0
            && double.IsFinite(WarningMs) && WarningMs >= 0
            && double.IsFinite(PulseMs) && PulseMs > 0
            && Pulses >= 1
            && double.IsFinite(SignatureRadius) && SignatureRadius > 0
            && double.IsFinite(ImmobileMs) && ImmobileMs >= 0
            && double.IsFinite(NoJumpMs) && NoJumpMs >= 0
            && double.IsFinite(NeutAmount) && NeutAmount >= 0
            && double.IsFinite(NeutRadius) && NeutRadius >= 0
            && double.IsFinite(NeutSignatureRadius) && NeutSignatureRadius >= 0;
    }
}

/// <summary>What a mining laser mines (<see cref="SimAction.Mining"/>'s family byte).</summary>
public static class SimMiningFamilies
{
    public const byte Ore = 0;
    public const byte Ice = 1;
    public const byte Gas = 2;
}

/// <summary>What a module that is projected at another ship does to it each cycle.</summary>
public abstract record SimRemote
{
    private SimRemote()
    {
    }

    /// <summary>A layer of the target is mended by <paramref name="Amount"/> points (a remote shield booster, armor repairer or hull repairer).</summary>
    public sealed record Repair(SimLayer Layer, double Amount) : SimRemote;

    /// <summary>A mutadaptive repairer asks the host for its current per-cycle repair amount so target-specific spool state stays authoritative there.</summary>
    public sealed record MutadaptiveRepair(SimLayer Layer, double Amount, double RepairMultiplierBonusPerCycle, double RepairMultiplierBonusMax) : SimRemote;

    /// <summary>The target's capacitor is given <paramref name="Amount"/> GJ (a remote capacitor transmitter).</summary>
    public sealed record Transfer(double Amount) : SimRemote;

    /// <summary>
    /// <paramref name="Amount"/> GJ are taken from the target's capacitor (an energy neutralizer); one made for bigger ships is weaker against a target whose signature is smaller than
    /// <paramref name="SignatureResolution"/> (0 for none).
    /// </summary>
    public sealed record Neutralize(double Amount, double SignatureResolution) : SimRemote;

    /// <summary>What can be taken from the target's capacitor, up to <paramref name="Amount"/> GJ, goes into that of the ship that runs the effect (an energy nosferatu).</summary>
    public sealed record Drain(double Amount, double SignatureResolution) : SimRemote;

    /// <summary>The target's top speed is changed by <paramref name="Percent"/> of itself for as long as the effect lands (a stasis webifier: negative).</summary>
    public sealed record Web(double Percent) : SimRemote;

    /// <summary>The target's signature radius is changed by <paramref name="Percent"/> (a target painter: positive).</summary>
    public sealed record Paint(double Percent) : SimRemote;

    /// <summary>The range the target locks at and its scan resolution are changed by those percentages (a sensor dampener: negative).</summary>
    public sealed record Damp(double RangePercent, double ResolutionPercent, double GravimetricStrengthPercent = 0, double LadarStrengthPercent = 0, double MagnetometricStrengthPercent = 0, double RadarStrengthPercent = 0) : SimRemote;

    /// <summary>The target is held out of warp: <paramref name="Strength"/> points of scramble (a warp scrambler 2, a warp disruptor 1); <paramref name="BlocksMwd"/>: and out of its microwarpdrive.</summary>
    public sealed record Scramble(double Strength, bool BlocksMwd) : SimRemote;

    /// <summary>The turrets the target fires are changed by these percentages of their optimal range, falloff and tracking speed for as long as the effect lands (a tracking disruptor: negative).</summary>
    public sealed record Disrupt(double RangePercent, double FalloffPercent, double TrackingPercent) : SimRemote;

    /// <summary>The missiles the target launches are changed by these percentages of their speed, flight time, explosion velocity and explosion radius for as long as the effect lands (a guidance disruptor: negative, but for the radius).</summary>
    public sealed record GuidanceDisrupt(double VelocityPercent, double DelayPercent, double AoeVelocityPercent, double AoeCloudPercent) : SimRemote;

    /// <summary>
    /// An ECM jammer: for as long as a cycle the target loses its locks and can lock only the ships that jam it when the jam lands; it lands by the chance of this strength for the
    /// kind of sensor the target has over that sensor's strength (gravimetric, ladar, magnetometric, radar), weakened by the reach and the target's resistance.
    /// </summary>
    public sealed record Jam(double Gravimetric, double Ladar, double Magnetometric, double Radar) : SimRemote;
}

/// <summary>Run an effect. Answered under <see cref="SimCommands.Activate"/>: <see cref="SimAccepted"/> or <see cref="SimRefused"/>.</summary>
public sealed record SimActivate(ulong SessionId, SimEffect Effect) : SimMessage;

/// <summary>
/// The ship of a session was destroyed (its hull was gone): where it was and how it faced, the wreck the simulation made of it (0 for none: a player's
/// wreck is the host's to make) and the ship that took the last of the hull (0 when nobody did). The session is no longer in space.
/// </summary>
public sealed record SimDestroyed(
    ulong SessionId, long ShipId, long WreckId, IReadOnlyList<double> Position, IReadOnlyList<double> Direction, double Radius, long KillerShipId,
    SimWreckKind? WreckKind = null) : SimMessage;

/// <summary>A weapon of an NPC: an effect of the NPC's own ship, so the module is the ship. One that flies a <see cref="SimMissile"/> is a launcher (the NPC's own: no rounds to spend).</summary>
public sealed record SimNpcWeapon(
    long ModuleId, int EffectId, double CycleMs, double CapacitorNeed, int WeaponTypeId, string Fx, IReadOnlyList<double> RawDamage, double OptimalRange, double Falloff,
    double TrackingSpeed, double SignatureResolution, SimTurretCharge? Charge = null, SimMissile? Missile = null);

/// <summary>What a ship leaves when it is destroyed.</summary>
public sealed record SimWreckKind(int TypeId, string Name, int GraphicId);

/// <summary>What sets an NPC apart: the standings at which it treats others as enemies or friends, what it attacks at and with, and what it leaves.</summary>
public sealed record SimNpcIdentity(
    long HostileResponseThreshold, long FriendlyResponseThreshold, double AggressionRange, IReadOnlyList<SimNpcWeapon> Weapons, SimWreckKind? Wreck, bool Hull = false, bool HostWreck = false)
{
    /// <summary>The limits the reference gives every NPC, and nothing else.</summary>
    public static readonly SimNpcIdentity Default = new(-11, 11, 0, [], null);
}

/// <summary>
/// Spawn an NPC in the scene of a system (there is one while somebody is in the system): its ship, where it comes into space and how it moves, the
/// scripted tree it runs (empty for none) and what it is. Answered with <see cref="SimSpawnReply"/>.
/// </summary>
public sealed record SimSpawn(
    ulong RequestId, int SolarSystemId, SimShip Ship, long AnchorShipId, IReadOnlyList<double> Position, IReadOnlyList<double> Velocity, IReadOnlyList<double> Direction,
    string Behavior, SimNpcIdentity Identity) : SimMessage;

/// <summary>
/// Launches a drone beside the ship of a pilot (<paramref name="SessionId"/>): the drone's ship, the weapon it fights with (its module is the ship), the graphic of its type, how far it
/// circles (500 to 5,000 m) and the fraction of its top speed it flies at while it only follows its controller. Answered with <see cref="SimSpawnReply"/>.
/// </summary>
/// <summary>
/// The non-damage effect a drone runs at its target each cycle (a stasis webifier, a remote repairer, an ECM jammer): the dogma effect, its name in the destiny stream, how long a cycle is,
/// how long what it lands lasts on its target, and the action a cycle does (an <see cref="SimAction.Remote"/>).
/// </summary>
public sealed record SimDroneEffect(int EffectId, string Fx, double CycleMs, double LastingMs, SimAction Action);

public sealed record SimLaunchDrone(ulong RequestId, ulong SessionId, SimShip Ship, SimNpcWeapon? Weapon, int GraphicId, double OrbitDistance, double AttackRange, double CruiseFraction, SimDroneWorkProfile Work = default, SimDroneEffect? Effect = null) : SimMessage;

/// <summary>The codes of the orders to drones (<see cref="SimDroneCommand"/>).</summary>
public static class SimDroneCommands
{
    public const byte Engage = 1;
    public const byte ReturnHome = 2;
    public const byte ReturnBay = 3;
    public const byte Abandon = 4;
    public const byte Assist = 5;
    public const byte Guard = 6;
    public const byte MineRepeatedly = 7;
    public const byte Salvage = 8;

    /// <summary>The key an order is answered under in <see cref="CommandReply"/>s: an order to drones shares the codes of the commands, so it is told apart by this offset.</summary>
public const byte Reconnect = 9;

    public static byte Key(byte command) => (byte)(0xE0 + command);
}

/// <summary>A pilot gives its drones (their item ids) an order; the answer is <see cref="SimDroneResults"/>.</summary>
public sealed record SimDroneCommand(ulong SessionId, byte Command, long TargetId, IReadOnlyList<long> DroneIds, double ControlRange = 20_000.0) : SimMessage;

/// <summary>What one drone did with an order: <paramref name="Reason"/> is empty when it took it.</summary>
public sealed record SimDroneResult(long DroneId, string Reason);

/// <summary>What each drone did with an order.</summary>
public sealed record SimDroneResults(ulong SessionId, byte Command, IReadOnlyList<SimDroneResult> Results) : SimMessage;

public readonly record struct SimDroneWorkProfile(
    double MiningAmountM3,
    double CycleTimeMs,
    double MiningWasteVolumeMultiplier,
    double MiningWasteProbability,
    double SalvageAccessDifficultyBonus);

/// <summary>Host-authored SDE/dogma work capabilities attached to a launched drone.</summary>
public sealed record SimDroneWorkCycle(
    ulong SessionId, long CharacterId, long ShipId, int SolarSystemId, long DroneId, long TargetId, ulong CycleId, byte Command,
    double MiningAmountM3, double WasteVolumeMultiplier, double WasteProbability, double AccessDifficultyBonus, double Roll) : SimMessage;

/// <summary>
/// A module's work cycle (a mining laser's or a salvager's) ended with its target there: the numbers of the cycle and the three rolls the simulation drew (waste,
/// rounding, crit). The module is parked until a <see cref="SimModuleWorkResult"/>, and the same <see cref="CycleId"/> is sent again while it waits (ADR 0013).
/// </summary>
public sealed record SimModuleWorkCycle(
    ulong SessionId, long CharacterId, long ShipId, int SolarSystemId, long ModuleId, long TargetId, ulong CycleId, byte Command,
    double MiningAmountM3, double WasteVolumeMultiplier, double WasteProbability, double CritProbability, double CritQuantityMultiplier,
    double AccessDifficultyBonus, double WasteRoll, double RoundingRoll, double CritRoll) : SimMessage;

/// <summary>The host's decision for one module work cycle; the fields are those of <see cref="SimDroneWorkResult"/>.</summary>
public sealed record SimModuleWorkResult(
    ulong SessionId, long ModuleId, long TargetId, ulong CycleId, bool Accepted, string Reason, long UnitsRemaining, long DeliveredUnits, bool Complete) : SimMessage;

/// <summary>The reasons an effect of a module ended on the simulation's account (<see cref="SimModuleEnded"/>).</summary>
public static class SimModuleEndReasons
{
    /// <summary>The cycles were done, the capacitor was short, or the pilot asked.</summary>
    public const byte Done = 1;
    /// <summary>The target was lost.</summary>
    public const byte TargetLost = 2;
    /// <summary>Something forbade the effect (a warp scrambler, the module went offline).</summary>
    public const byte Forced = 3;
    /// <summary>The charge was spent.</summary>
    public const byte NoRounds = 4;
    /// <summary>The host refused the work cycle that ended the cycle.</summary>
    public const byte WorkRefused = 5;
    /// <summary>The fuel of the next cycle was not there (the host refused its consumption, <see cref="SimModuleWorkCommands.ConsumeFuel"/>).</summary>
    public const byte NoFuel = 6;
}

/// <summary>
/// An effect of a module ended on the simulation's account, once per end (<see cref="SimModuleEndReasons"/>). The host clears the module from the ship it sends, so
/// the simulation does not keep its resonances or numbers (FP-C1).
/// </summary>
public sealed record SimModuleEnded(ulong SessionId, long ModuleId, byte Reason) : SimMessage;

/// <summary>Inventory authority's decision for one completed drone cycle.</summary>
public sealed record SimDroneWorkResult(
    ulong SessionId, long DroneId, long TargetId, ulong CycleId, bool Accepted, string Reason, long UnitsRemaining, long DeliveredUnits, bool Complete) : SimMessage;

/// <summary>
/// One eligible mutadaptive remote-repair cycle. CycleNumber is 1-based within the current running effect; CycleAtMs is the simulation-clock cycle-end time, and CycleDurationMs is that effect's SDE duration.
/// Continue only for the same target, the next cycle number, and a time delta in [0, CycleDurationMs]. Exactly one duration remains consecutive; a larger gap expires the spool, and the current request then starts at one spool step. Ignore duplicate or older CycleId values. EndsEffectAfterCycle means Rust will not start another cycle after this result; target loss sends the normal effect-ended notification without a repair request.
/// </summary>
public sealed record SimRemoteRepairCycle(
    ulong SessionId, long ModuleId, long TargetId, ulong CycleId, int CycleNumber, long CycleAtMs, long CycleDurationMs, bool EndsEffectAfterCycle, double BaseAmount,
    double RepairMultiplierBonusPerCycle, double RepairMultiplierBonusMax) : SimMessage;

/// <summary>Completes one pending mutadaptive remote-repair cycle. Amount is the host's spooled HP amount before Rust applies range falloff.</summary>
public sealed record SimRemoteRepairCycleResult(
    ulong SessionId, long ModuleId, long TargetId, ulong CycleId, bool Accepted, string Reason, double Amount) : SimMessage;

/// <summary>An SDE-typed mineable asteroid rock with host-owned remaining units.</summary>
public sealed record SimPlaceAsteroid(
    int SolarSystemId, long AsteroidId, int TypeId, int GroupId, int CategoryId, string Name, int? GraphicId,
    IReadOnlyList<double> Position, IReadOnlyList<double> Direction, double Radius, long UnitsRemaining) : SimMessage;

/// <summary>A drone came within reach of its ship after an order to return to the bay and was taken in: the host moves its item back into the drone bay.</summary>
public sealed record SimDroneScooped(ulong SessionId, long DroneId) : SimMessage;

/// <summary>A drone is let go (abandoned, or its controller left the scene): it is out of the scene and the host decides what becomes of its item.</summary>
public sealed record SimDroneReleased(ulong SessionId, long DroneId, IReadOnlyList<double> Position) : SimMessage;

/// <summary>Whether a <see cref="SimSpawn"/> was done: the session the NPC is known by, or why not.</summary>
public sealed record SimSpawnReply(ulong RequestId, ulong? Session, string? Error) : SimMessage;

/// <summary>
/// Gives a session whose ship was destroyed another (the capsule the host made for its pilot): it comes into the scene at rest at the place. Answered as
/// an <see cref="SimEnter"/> is, with <see cref="SimEnterReply"/>.
/// </summary>
public sealed record SimRespawn(ulong SessionId, SimShip Ship, IReadOnlyList<double> Position, IReadOnlyList<double> Direction) : SimMessage;

/// <summary>
/// A wreck the host made (an item with contents) of a ship that was destroyed, to be put in space where the ship was, for <see cref="Credit"/> to loot; or the
/// corpse of a pilot whose capsule was destroyed (<see cref="GroupId"/> 14: a plain space item that has the pilot's name).
/// </summary>
public sealed record SimPlaceWreck(
    int SolarSystemId, long WreckId, int GroupId, SimWreckKind Kind, IReadOnlyList<double> Position, IReadOnlyList<double> Direction, double Radius, long LauncherId, SimCredit Credit,
    bool IsEmpty) : SimMessage;

/// <summary>
/// A can the host made (an item of its own) for cargo a pilot jettisons: the scene puts it <see cref="Distance"/> metres ahead of the pilot's ship (never nearer than fifty), tells
/// those who see it the ball and the effect that plays the jettison, and answers with <see cref="SimContainerPlaced"/> (or <see cref="SimRefused"/> under
/// <see cref="SimCommands.PlaceContainer"/>).
/// </summary>
public sealed record SimPlaceContainer(ulong SessionId, long ContainerId, SimWreckKind Kind, double Radius, long LauncherId, SimCredit Credit, double Distance) : SimMessage;

/// <summary>
/// A player's ship came to rest at an asteroid belt after a warp: the belt it warped to, or else the nearest within 250 km of where it stopped.
/// <c>Position</c> is where the belt is (what the host places what lives there around). The host decides whether anything does (belt rats); the
/// simulation does not send it for NPC ships.
/// </summary>
public sealed record SimArrived(ulong SessionId, long BeltId, IReadOnlyList<double> Position) : SimMessage;

/// <summary>A can the host asked for is in space: where it lies and how it faces.</summary>
public sealed record SimContainerPlaced(ulong SessionId, long ContainerId, IReadOnlyList<double> Position, IReadOnlyList<double> Direction) : SimMessage;

/// <summary>
/// What is in a wreck or a can changed (it has something to take, or nothing): whoever has it in view is told its slim item again, and the pilot who changed it
/// (<see cref="SessionId"/>) is answered with <see cref="SimAccepted"/> under <see cref="SimCommands.ChangeWreck"/> after that (or <see cref="SimRefused"/> when there is no such wreck).
/// </summary>
public sealed record SimChangeWreck(ulong SessionId, long WreckId, bool IsEmpty) : SimMessage;

/// <summary>Takes a wreck or a can out of space; <see cref="Effect"/> is the destruction effect the client plays first (0: none).</summary>
public sealed record SimRemoveWreck(long WreckId, int Effect) : SimMessage;

/// <summary>The answer to <see cref="SimCommands.Measure"/>: metres from the centre of the ship to the surface of the wreck or can, or null when there is no such wreck.</summary>
public sealed record SimMeasured(ulong SessionId, byte Command, double? Distance) : SimMessage;

/// <summary>Who may take what a destroyed ship leaves: the pilot and the corporation, the alliance and the faction they are in (0 for none).</summary>
public sealed record SimCredit(long CharacterId, long CorporationId, long AllianceId, long WarFactionId)
{
    public static SimCredit Nobody { get; } = new(0, 0, 0, 0);
}

/// <summary>Destroy this ship: the explosion, the wreck (for <see cref="Credit"/> to loot), and (for a player's ship) a <see cref="SimDestroyed"/> for the host.</summary>
public sealed record SimDestroy(long ShipId, SimCredit Credit) : SimMessage;

namespace Eve.Space;

/// <summary>A message on the link between the service host and the simulation (docs/adr/0007-space-simulation-process.md).</summary>
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

/// <summary>What a ship can take, in points, and how it resists: dogma's numbers for the ship as it is fitted and flown.</summary>
public sealed record SimVitals(double ShieldCapacity, double ArmorHp, double StructureHp, SimResonances Resonances)
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
    SimVitals? Vitals = null, SimCondition? Condition = null);

/// <summary>A ship enters a solar system.</summary>
public sealed record SimEnter(ulong SessionId, int SolarSystemId, EnterHow How, long StationId, SimShip Ship) : SimMessage;

/// <summary>Whether an <see cref="SimEnter"/> was done: <see cref="Error"/> is null when it was, the reason when it was not.</summary>
public sealed record SimEnterReply(ulong SessionId, string? Error) : SimMessage;

/// <summary>Why a ship leaves a scene.</summary>
public enum LeaveWhy : byte
{
    Docked = 0,
    LoggedOff = 1,
    Jumped = 2,
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
/// (em, thermal, kinetic, explosive: the damage multiplier is in it), the range, the falloff, the tracking (radians a second) and the signature resolution.
/// </summary>
public sealed record SimTurret(
    int WeaponTypeId, string Fx, IReadOnlyList<double> RawDamage, double OptimalRange, double Falloff, double TrackingSpeed, double SignatureResolution,
    SimTurretCharge? Charge = null);

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
    IReadOnlyList<SimChange> OnChanges, IReadOnlyList<SimChange> OffChanges, SimTurret? Turret = null, SimAction? Action = null);

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
}

/// <summary>What a module that is projected at another ship does to it each cycle.</summary>
public abstract record SimRemote
{
    private SimRemote()
    {
    }

    /// <summary>A layer of the target is mended by <paramref name="Amount"/> points (a remote shield booster, armor repairer or hull repairer).</summary>
    public sealed record Repair(SimLayer Layer, double Amount) : SimRemote;

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
    public sealed record Damp(double RangePercent, double ResolutionPercent) : SimRemote;

    /// <summary>The target is held out of warp: <paramref name="Strength"/> points of scramble (a warp scrambler 2, a warp disruptor 1); <paramref name="BlocksMwd"/>: and out of its microwarpdrive.</summary>
    public sealed record Scramble(double Strength, bool BlocksMwd) : SimRemote;
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

/// <summary>A weapon of an NPC: an effect of the NPC's own ship, so the module is the ship.</summary>
public sealed record SimNpcWeapon(
    long ModuleId, int EffectId, double CycleMs, double CapacitorNeed, int WeaponTypeId, string Fx, IReadOnlyList<double> RawDamage, double OptimalRange, double Falloff,
    double TrackingSpeed, double SignatureResolution, SimTurretCharge? Charge = null);

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

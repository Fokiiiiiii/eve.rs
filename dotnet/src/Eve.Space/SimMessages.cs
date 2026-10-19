namespace Eve.Space;

/// <summary>A message on the link between the service host and the simulation (docs/design/adr/0007-space-simulation-process.md).</summary>
public abstract record SimMessage;

/// <summary>Where an entity lives in the simulation: a public location, a content instance and its room.</summary>
public readonly record struct SimSceneKey(int LocationId, long InstanceId, int RoomIndex)
{
    public bool IsValid => LocationId > 0 && InstanceId >= 0 &&
        (InstanceId == 0 ? RoomIndex == 0 : RoomIndex is >= 1 and <= 3);

    public static SimSceneKey SolarSystem(int systemId) => new(systemId, 0, 0);
}

public enum SimAbyssalModifierOperation : byte
{
    Add = 0,
    Multiply = 1,
}

public sealed record SimAbyssalAttributeModifier(int AttributeId, SimAbyssalModifierOperation Operation, double Value);

public sealed record SimAbyssalEnvironmentField(
    IReadOnlyList<double> Center, double RadiusMeters, IReadOnlyList<SimAbyssalAttributeModifier> Modifiers);

public sealed record SimAbyssalEnvironmentProfile(
    IReadOnlyList<SimAbyssalAttributeModifier> GlobalModifiers,
    IReadOnlyList<SimAbyssalEnvironmentField> Fields);

public sealed record SimAbyssalBoundaryDamagePoint(
    double DistanceOutsideMeters, double AppliedDamagePerSecond);

public sealed record SimAbyssalBoundaryProfile(
    IReadOnlyList<double> Center, double RadiusMeters, long TickIntervalMs,
    IReadOnlyList<SimAbyssalBoundaryDamagePoint> DamageCurve, long TerminalExposureMs = 15_000);

public sealed record SimAbyssalSceneSpec(
    SimSceneKey Scene, SimAbyssalEnvironmentProfile Environment,
    SimAbyssalBoundaryProfile Boundary, long ExpiryFileTime);

/// <summary>A static, non-NPC Abyssal conduit ball materialized in one isolated room.</summary>
public sealed record SimAbyssalSceneObject(
    SimSceneKey Scene, long ItemId, int TypeId, int GroupId, int CategoryId, long OwnerId,
    double Radius, int GraphicId, string Name, IReadOnlyList<double> Position,
    IReadOnlyList<double> Direction, bool IsOpen);

public sealed record SimEnsureAbyssalSceneObject(ulong RequestId, SimAbyssalSceneObject Object) : SimMessage;
public sealed record SimEnsureAbyssalSceneObjectReply(ulong RequestId, string? Error) : SimMessage;

/// <summary>A non-NPC, SDE-backed damageable reward cache in one private room.</summary>
public sealed record SimEnsureAbyssalRewardCache(
    ulong RequestId, string ContentId, SimSceneKey Scene, SimShip Entity,
    IReadOnlyList<double> Position, IReadOnlyList<double> Direction) : SimMessage;
public sealed record SimEnsureAbyssalRewardCacheReply(ulong RequestId, string? Error) : SimMessage;
public sealed record SimEnsureAbyssalLootContainer(
    ulong RequestId, string ContentId, SimPlaceWreck Container) : SimMessage;
public sealed record SimEnsureAbyssalLootContainerReply(ulong RequestId, string? Error) : SimMessage;

public sealed record SimAbyssalPointDefense(
    long TickIntervalMs, IReadOnlyList<double> Damage, int TypeListId,
    IReadOnlyList<int> IncludedCategories, IReadOnlyList<int> IncludedGroups, IReadOnlyList<int> IncludedTypes,
    IReadOnlyList<int> ExcludedCategories, IReadOnlyList<int> ExcludedGroups, IReadOnlyList<int> ExcludedTypes,
    string EffectName);
public sealed record SimAbyssalEnvironmentObject(
    SimAbyssalSceneObject Body, double EffectRadiusMeters, SimAbyssalPointDefense? PointDefense);
public sealed record SimEnsureAbyssalEnvironmentObject(
    ulong RequestId, string ContentId, SimAbyssalEnvironmentObject Object) : SimMessage;
public sealed record SimEnsureAbyssalEnvironmentObjectReply(ulong RequestId, string? Error) : SimMessage;
public sealed record SimAbyssalFieldMembership(
    SimSceneKey Scene, long SourceItemId, long ShipId, ulong SessionId, bool Inside) : SimMessage;
public sealed record SimRefreshAbyssalNpc(
    ulong RequestId, SimSceneKey Scene, ulong SessionId, SimShip Ship, SimNpcIdentity Identity) : SimMessage;
public sealed record SimRefreshAbyssalNpcReply(ulong RequestId, string? Error) : SimMessage;

public sealed record SimEnsureAbyssalScene(ulong RequestId, SimAbyssalSceneSpec Spec) : SimMessage;
public sealed record SimEnsureAbyssalSceneReply(ulong RequestId, string? Error) : SimMessage;

public sealed record SimMoveAbyssalParticipant(
    ulong RequestId, long ShipId, SimSceneKey Source, SimSceneKey Destination,
    IReadOnlyList<double> Position, IReadOnlyList<double> Direction) : SimMessage
{
    /// <summary>The simulation carries the resident's current authoritative pose across rooms.</summary>
    public bool PreserveSourcePose { get; init; }

    /// <summary>The host will perform a client SessionChange before the simulation's movement notifications.</summary>
    public bool SessionHandoff { get; init; }
}

public sealed record SimMoveAbyssalParticipantReply(ulong RequestId, string? Error) : SimMessage;

public sealed record SimRebindAbyssalSession(
    ulong RequestId, ulong SessionId, long ShipId, SimSceneKey Scene) : SimMessage;

public sealed record SimRebindAbyssalSessionReply(ulong RequestId, string? Error) : SimMessage;

public sealed record SimRemoveAbyssalScene(ulong RequestId, SimSceneKey Scene) : SimMessage
{
    public IReadOnlyList<long> SettledNpcIds { get; init; } = [];
    public IReadOnlyList<long> SettledLootIds { get; init; } = [];
}
public sealed record SimRemoveAbyssalSceneReply(ulong RequestId, string? Error) : SimMessage;

/// <summary>The persisted item and SDE metadata needed to materialize a public Abyssal trace as a dynamic space entity.</summary>
public sealed record SimAbyssalTrace(
    int SolarSystemId, long ItemId, int TypeId, int GroupId, int CategoryId, long OwnerId, double Radius,
    IReadOnlyList<double> Position, IReadOnlyList<double> Velocity, IReadOnlyList<double> Direction,
    IReadOnlyList<double>? TargetPoint, double SpeedFraction, int GameModeId, int FilamentTypeId,
    int DifficultyTier, long ExpiresAtMs);

public enum SimAbyssalTraceStatus : byte
{
    Created = 0,
    AlreadyPresent = 1,
    Removed = 2,
    AlreadyAbsent = 3,
    Failed = 255,
}

public sealed record SimEnsureAbyssalTracePublished(
    ulong RequestId, ulong? ActorSessionId, SimAbyssalTrace Trace) : SimMessage;

public sealed record SimEnsureAbyssalTracePublishedReply(
    ulong RequestId, SimAbyssalTraceStatus Status, int RecipientCount, bool ActorNotified, string? Error) : SimMessage;

public sealed record SimRemoveAbyssalTrace(ulong RequestId, int SolarSystemId, long ItemId) : SimMessage;

public sealed record SimRemoveAbyssalTraceReply(
    ulong RequestId, SimAbyssalTraceStatus Status, int RecipientCount, string? Error) : SimMessage;

public sealed record SimRemoveAbyssalParticipant(ulong RequestId, SimSceneKey Scene, long ShipId) : SimMessage;

public sealed record SimRemoveAbyssalParticipantReply(
    ulong RequestId, bool AlreadyAbsent, string? Error) : SimMessage;

public sealed record SimGetAbyssalParticipantPose(
    ulong RequestId, SimSceneKey Scene, long ShipId) : SimMessage;

public sealed record SimAbyssalDeploymentObject(long ItemId, int TypeId, double CenterDistanceMeters);
public sealed record SimAbyssalDeploymentSnapshot(
    bool Warping, bool Cloaked, bool CynoActive, bool UndockInvulnerable, bool WarpScrambled,
    IReadOnlyList<SimAbyssalDeploymentObject> NearbyObjects);

public sealed record SimGetAbyssalParticipantPoseReply(
    ulong RequestId, SimSceneKey Scene, long ShipId,
    IReadOnlyList<double> Position, IReadOnlyList<double> Direction, string? Error) : SimMessage
{
    public SimAbyssalDeploymentSnapshot? Deployment { get; init; }
}

public sealed record SimHello(ushort Version) : SimMessage;

public sealed record SimHelloAck(ushort Version, uint PollHz) : SimMessage;

/// <summary>How a ship comes into space.</summary>
public enum EnterHow : byte
{
    /// <summary>It undocks from a station.</summary>
    Undock = 0,

    /// <summary>It is already in space (a login in space, a jump arrival).</summary>
    InSpace = 1,

    /// <summary>It is put in space at the anchor of its solar system, where a game master's relocation brings it (no position is sent).</summary>
    Anchor = 2,
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
/// <summary>What the ship takes and resists, and the numbers a running module can change without it being hurt (the maximum velocity of a siege, its mass).
/// The host compares them to decide whether the simulation must be told the ship again (they are not on the wire).</summary>
public sealed record SimVitals(double ShieldCapacity, double ArmorHp, double StructureHp, SimResonances Resonances, bool ArmorPinned = false, double MaxVelocity = 0, double Mass = 0)
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

/// <summary>A ship's stance (a Tactical Destroyer's mode): the stance it had before, when it switched (a FILETIME), and the stance it has. The slim item carries it (protocol 55).</summary>
public sealed record SimShipStance(int OldStance, long SwitchedAt, int Stance);

/// <summary>What motion and the slim item need to know of a ship: the host works these out (dogma, skills, fitting), the simulation only uses them.</summary>
public sealed record SimShip(
    long ShipId, long CharacterId, int TypeId, int GroupId, int CategoryId, string Name, long OwnerId, int CorporationId, int AllianceId, int WarFactionId,
    double SecurityStatus, double Radius, double Mass, double MaxVelocity, double Agility, double WarpSpeedAu, double ShieldRechargeMs,
    double MaxTargetRange, double ScanResolution, double SignatureRadius, int MaxLockedTargets, double CapacitorCapacity, double CapacitorRechargeSeconds, IReadOnlyList<SimModule> Modules,
    SimVitals? Vitals = null, SimCondition? Condition = null, SimSensors? Sensors = null, double DroneDamageMultiplier = 1d, SimShipStance? Stance = null);

/// <summary>What a ship holds against a jammer and a neutraliser: the strength of its sensors (gravimetric, ladar, magnetometric, radar; the strongest counts), how much of a jam it takes (<c>ECMResistance</c>: 1 for an ordinary ship, 0 for one that cannot be jammed) and how much of an energy neutralisation it takes (<c>energyWarfareResistance</c>, 2045: 1 for an ordinary ship, 0 for one that cannot be neutralised; protocol 27).</summary>
public sealed record SimSensors(double Gravimetric, double Ladar, double Magnetometric, double Radar, double EcmResistance, double EnergyWarfareResistance)
{
    /// <summary>A ship with no sensors that count and no resistance.</summary>
    public static readonly SimSensors Plain = new(0, 0, 0, 0, 1, 1);
}

/// <summary>A ship enters a simulation scene.</summary>
public sealed record SimEnter(ulong SessionId, SimSceneKey Scene, EnterHow How, long StationId, SimShip Ship, IReadOnlyList<double>? Position = null, IReadOnlyList<double>? Direction = null) : SimMessage;

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

    /// <summary>Jump to a cynosural field lit in another system (the field's id, the destination system and the spool in milliseconds): code 26.</summary>
    public const byte CynoJump = 26;
    /// <summary>Opens a jump bridge where the ship is (protocol 55): the field, its system, the bridge's life, the hold, and the capacitor.</summary>
    public const byte OpenBridge = 30;
    /// <summary>A passenger's jump through the bridge of another ship (protocol 55): the bridging ship, the field, its system.</summary>
    public const byte BridgeJump = 31;
    /// <summary>A Black Ops conduit jump (protocol 57): the anchor and the ships of the passenger list jump to the field at once, within the conduit's range.</summary>
    public const byte ConduitJump = 32;

    /// <summary>Walks the ship through an acceleration gate of a dungeon room to a point of the other room (protocol 75): args are the gate, then x, y, z.</summary>
    public const byte AccelerationGateTransit = 34;
    public const byte ControlStructure = 33;

    /// <summary>The scan targets of the scene for a probe scan (protocol 52).</summary>
    public const byte ScanTargets = 27;

    /// <summary>What a directional scan can see in the scene (protocol 54).</summary>
    public const byte DirectionalScan = 28;

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
    /// <summary>The point a distance ahead of the ship along its heading (protocol 49): where a mobile warp disruptor is dropped.</summary>
    public const byte DropPoint = 25;

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

/// <summary>Something a probe scan can find (protocol 52): the item id, its kind (1 ship, 2 drone, 3 fighter, 4 deployable), its type and where it is.</summary>
public sealed record SimScanTarget(long Id, byte Kind, int TypeId, IReadOnlyList<double> Position);

/// <summary>The scan targets of the scene, the answer to command 27 (protocol 52).</summary>
public sealed record SimScanTargets(ulong SessionId, byte Command, IReadOnlyList<SimScanTarget> Targets) : SimMessage;

/// <summary>Something a directional scan can see (protocol 54): the item id, its kind (1 ship, 2 NPC, 3 drone, 4 fighter, 5 probe, 6 wreck, 7 celestial), its type,
/// where it is, its radius, and whether it is cloaked and whether it warps.</summary>
public sealed record SimDirectionalTarget(long Id, byte Kind, int TypeId, IReadOnlyList<double> Position, double Radius, bool Cloaked, bool Warping);

/// <summary>What the scene has in a cone of space for a directional scan, the answer to command 28 (protocol 54).</summary>
public sealed record SimDirectionalTargets(ulong SessionId, byte Command, IReadOnlyList<SimDirectionalTarget> Targets) : SimMessage;

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
    SimTurretCharge? Charge = null, SimMissile? Missile = null, SimSpool? Spool = null, SimAoe? Aoe = null, SimArc? Arc = null,
    /// <summary>The weapon requires Gunnery: a tracking disruptor or computer on the shooter reaches it (a vorton projector does not require it).</summary>
    bool RequiresGunnery = false);

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
/// counts, so the chain makes one link fewer than the ships). If present, <see cref="DamageLossTarget"/> is the SDE per-hop damage factor.
/// </summary>
public sealed record SimArc(double Range, int Targets, double? DamageLossTarget = null);

public sealed record SimDeployable(int ChargeTypeId, int GroupId, string Name, double Radius);

/// <summary>What a bomb does where it goes off (wire byte: 0 damage, 1 ECM, 2 neutralisation).</summary>
public enum SimBombKind : byte
{
    Damage = 0,
    Ecm = 1,
    Neutralize = 2,
}

/// <summary>
/// What a launcher flies: the type of the missile it fires and its group, what the client calls it, the radius, mass and agility of its ball, the hit points it has while it flies (what a smartbomb
/// or a point defense has to do to shoot it down), its speed (metres a second), how long it flies before it explodes (milliseconds) and the numbers that decide how much of its damage a target
/// takes (the explosion radius and velocity, the damage reduction factor and its sensitivity). The simulation makes an entity of its own of each missile that is fired (<c>eve-space</c>'s
/// <c>missile_entity</c>), apart from the ships.
/// </summary>
public sealed record SimMissile(
    int ChargeTypeId, int GroupId, string Name, double Radius, double Mass, double Agility, double HitPoints, double MaxVelocity, double FlightTimeMs,
    double ExplosionRadius, double ExplosionVelocity, double DamageReductionFactor, double DamageReductionSensitivity, SimDot? Dot = null);

/// <summary>
/// The damage over time a breacher pod's hit starts on its ship (the charge's <c>dotDuration</c>, <c>dotMaxDamagePerTick</c> and <c>dotMaxHPPercentagePerTick</c>):
/// the duration in milliseconds, the most a tick does in points, and the most a tick does as a percentage of the target's hit points (0.6 is 0.6 %).
/// </summary>
public sealed record SimDot(double DurationMs, double MaxDamagePerTick, double MaxHpPercentPerTick);

/// <summary>
/// The charge a turret fires: its type, the flag it sits at in the ship (the slot of the module), the rounds left and the rounds a cycle takes. The simulation
/// spends them cycle by cycle (<see cref="SimChargeSpent"/>) and stops the weapon when they are gone.
/// </summary>
public sealed record SimTurretCharge(int TypeId, int Flag, long Rounds, long PerCycle, bool Reloads = false);

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

/// <summary>One buff of a ship (<see cref="SimBuffState"/>): its buff collection, the value it gives the ship, and the time it has left, in milliseconds.</summary>
public sealed record SimBuffEntry(int CollectionId, double Value, double RemainingMs);

/// <summary>
/// The timed buffs of a ship of this session (protocol 43, track C6): the host sends them after each change, and the simulation tells the client <c>OnDbuffUpdated</c>
/// with them. An empty list clears the client's buffs. Nothing is answered.
/// </summary>
public sealed record SimBuffState(ulong SessionId, long ShipId, IReadOnlyList<SimBuffEntry> Entries) : SimMessage;

/// <summary>
/// A refresh of a running effect (protocol 38, FP-C4b and FP-C5): an overheat, or a tracking computer that starts, stops or overheats, changes the cycle, what a
/// cycle costs the capacitor, the turret's numbers and the attributes the client is told. The simulation applies it at the start of the next cycle; nothing is answered.
/// </summary>
public sealed record SimUpdateEffect(ulong SessionId, long ModuleId, double DurationMs, double CapacitorNeed, SimTurret? Turret, IReadOnlyList<SimChange> Changes) : SimMessage;

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
    bool BurnsFuel = false,
    /// <summary>The module rules of the module's type (the group and its limit, the early deactivation, the industrial core and the reactivation delay): what the
    /// simulation enforces on the activation. Null when the host sends none (a module the rules do not name).</summary>
    SimModuleRules? Rules = null,
    /// <summary>The capacitor cost of each cycle after a charged effect's charges are exhausted.</summary>
    double FallbackCapacitorNeed = 0);

/// <summary>
/// The module rules of an effect the simulation enforces: the group the module belongs to and how many of it run at once (<paramref name="MaxGroupActive"/>, 0 for no
/// limit), whether the module holds the ship's early deactivation (906), needs an active industrial core (3265), is one, and how long it waits after it stops (669, in ms).
/// </summary>
public sealed record SimModuleRules(
    int GroupId, int MaxGroupActive, bool DisallowEarlyDeactivation, bool RequiresIndustrialCore, bool ProvidesIndustrialCore, double ReactivationDelayMs,
    /// <summary>The highest and the lowest speed (m/s) a module starts at (maxVelocityActivationLimit 1334, minVelocityActivationLimit 2608; 0 for none). Protocol 38 (WP19).</summary>
    double MaxVelocityLimitMs = 0, double MinVelocityLimitMs = 0);

/// <summary>The kinds of scan a scanner's cycle is (<see cref="SimAction.Scan.Kind"/>).</summary>
public static class SimScanKinds
{
    /// <summary>A Cargo Scanner: the target's hold, one container deep.</summary>
    public const byte Cargo = 0;

    /// <summary>A Ship Scanner: the target's fitted hardware, each item rolled against the reach's falloff.</summary>
    public const byte Ship = 1;
}

/// <summary>The codes of the work a module asks the host for (<see cref="SimModuleWorkCycle.Command"/>). Its cycles share the drone codes of mining and salvage (7, 8).</summary>
public static class SimModuleWorkCommands
{
    /// <summary>The fuel of the next cycle: the host takes the module's consumption quantity of its consumption type from the ship, or refuses (<c>NO_FUEL</c>).</summary>
    public const byte ConsumeFuel = 9;

    /// <summary>A Cargo Scanner's cycle ended with its target in reach: the host reveals the target's hold (protocol 40). It carries no mining numbers.</summary>
    public const byte ScanCargo = 10;

    /// <summary>A Ship Scanner's cycle ended with its target in reach: the host reveals the target's fitted hardware (protocol 40). It carries no mining numbers.</summary>
    public const byte ScanShip = 11;

    /// <summary>A command burst's cycle ended: the host applies the burst's buffs to the ships it reaches (<see cref="SimModuleWorkCycle.Recipients"/>), protocol 43.</summary>
    public const byte Burst = 12;

    /// <summary>A P.A.N.I.C.'s start: the host grants the ships it reaches (<see cref="SimModuleWorkCycle.Recipients"/>) their buffs before the effect starts (protocol 43).</summary>
    public const byte Panic = 13;

    /// <summary>An Entosis Link's cycle ended with its structure in reach (protocol 70): the host settles the capture. It carries no mining numbers.</summary>
    public const byte Entosis = 14;
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


    /// <summary>
    /// Engages the ship's active cloaking device (tag 5). A cloak that is not warp-safe (<paramref name="WarpSafe"/> false, a standard cloaking device) holds the
    /// ship out of warp while it runs; a warp-safe (covert ops) cloak carries it into warp.
    /// </summary>
    public sealed record Cloak(bool WarpSafe, double TargetingDelayMs) : SimAction;

    /// <summary>
    /// A warp disruption field generator's bubble (tag 16): while the effect runs the ship is a warp-disruption source of <paramref name="RangeMeters"/> around itself,
    /// and a ship within it cannot start a warp (<c>WARP_DISRUPTED_BY_BUBBLE</c>).
    /// </summary>
    public sealed record WarpBubble(double RangeMeters) : SimAction;

    /// <summary>The ship is immune to warp bubbles while the effect runs (tag 17): an interdiction nullifier (attribute 1538).</summary>
    public sealed record WarpImmunity : SimAction;

    /// <summary>The ship's warp scramble status changes by <paramref name="Delta"/> while the effect runs (tag 18): a warp core stabilizer's -2 (attribute 104).</summary>
    public sealed record ScrambleStatus(double Delta) : SimAction;

    /// <summary>
    /// An interdiction probe (tag 19): as each cycle starts a probe (<paramref name="Ball"/>) is put in space at the ship. It is a warp-disruption source of
    /// <paramref name="RangeMeters"/> for <paramref name="LifetimeMs"/>, then it is gone. The simulation keeps the lifetime (a restart drops it).
    /// </summary>
    public sealed record WarpProbe(SimDeployable Ball, double RangeMeters, double LifetimeMs) : SimAction;

    /// <summary>
    /// A stasis webification burst (tag 21, the Stasis Webification Probe): as each cycle starts, every ship within <paramref name="RangeMeters"/> of the ship that runs it
    /// is webbed by <paramref name="SpeedFactorPercent"/> percent (a negative number slows it) for <paramref name="DurationMs"/>. The probe is spent by its charge.
    /// </summary>
    public sealed record StasisBurst(double RangeMeters, double DurationMs, double SpeedFactorPercent) : SimAction;

    /// <summary>
    /// A defender launcher's cycle (tag 22, the Defender Launcher I): as the cycle starts its defender missile (<paramref name="Missile"/>, the charge's flight) goes for
    /// the nearest hostile missile in its reach and shoots it down, or hurts it by <paramref name="Damage"/> (em, thermal, kinetic, explosive) when it does not destroy it.
    /// Target-less.
    /// </summary>
    public sealed record Defender(SimMissile Missile, IReadOnlyList<double> Damage) : SimAction;

    /// <summary>
    /// A bomb (tag 20): as each cycle starts it is released at the ship; <paramref name="LifetimeMs"/> later it detonates where it is. The ships within
    /// <paramref name="BlastRadius"/> take its effect by its <paramref name="Kind"/>: the four damages, or the four ECM strengths, or the capacitor it neutralises.
    /// </summary>
    public sealed record Bomb(SimDeployable Ball, SimBombKind Kind, double LifetimeMs, double BlastRadius, IReadOnlyList<double> Damage, IReadOnlyList<double> Ecm, double Neutralize) : SimAction;

    /// <summary>
    /// A cynosural field (tag 27): as the cycle ends the field is lit where the ship is, and it lives <paramref name="LifetimeMs"/> (the field type's explosionDelay).
    /// The simulation releases it as a deployable; it is not taken down when the generator stops.
    /// </summary>
    public sealed record CynoField(SimDeployable Ball, double LifetimeMs) : SimAction;

    /// <summary>
    /// A command burst's cycle (tag 28, protocol 43): non-targeted. At the cycle's end the simulation sends the host the ships within <paramref name="RangeMeters"/> (plus one
    /// metre) as a work cycle (<see cref="SimModuleWorkCommands.Burst"/>) and parks until the host answers; the host reads the buffs from the charge and the module.
    /// </summary>
    public sealed record CommandBurst(double RangeMeters) : SimAction;

    /// <summary>
    /// The Pulse Activated Nexus Invulnerability Core (tag 29, protocol 43): non-targeted, one cycle of its duration. It needs an asteroid the pilot holds the lock on, and
    /// parks its start until the host grants the ships of its fleet in <paramref name="RangeMeters"/> (plus one metre) their buffs (<see cref="SimModuleWorkCommands.Panic"/>).
    /// </summary>
    public sealed record Panic(double RangeMeters) : SimAction;

    /// <summary>
    /// An Entosis Link's cycle (tag 31, protocol 70): non-offensive. Its target is a structure the pilot holds the lock on, within <paramref name="RangeMeters"/> (plus one
    /// metre); at each cycle's end the host settles the capture (<see cref="SimModuleWorkCommands.Entosis"/>).
    /// </summary>
    public sealed record Entosis(double RangeMeters) : SimAction;

    /// <summary>A structure point-defense cycle; the host authorizes damageable candidates per cycle, and damage keeps the four SDE channels.</summary>
    public sealed record PointDefense(double RangeMeters, IReadOnlyList<double> Damage) : SimAction
    {
        public bool IsValid => double.IsFinite(RangeMeters) && RangeMeters > 0
            && Damage is { Count: 4 } && Damage.All(amount => double.IsFinite(amount) && amount >= 0);
    }

    /// <summary>
    /// A controlled structure's burst projector (ST-8b, tag 32): each pulse applies <paramref name="What"/> to the ships in <paramref name="RangeMeters"/> that
    /// the Host authorizes for that pulse. The remote effect is the family's own (neutralizer, web, painter, damp, disruptor, scrambler or jammer).
    /// </summary>
    public sealed record StructureBurst(double RangeMeters, double DurationMs, SimRemote What) : SimAction
    {
        public bool IsValid => double.IsFinite(RangeMeters) && RangeMeters > 0 && double.IsFinite(DurationMs) && DurationMs > 0
            && What is SimRemote.Neutralize or SimRemote.Web or SimRemote.Paint or SimRemote.Damp or SimRemote.Disrupt or SimRemote.Scramble or SimRemote.Jam;
    }

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

    /// <summary>
    /// A scanner's cycle (tag 26, protocol 40): a cargo scan (<see cref="SimScanKinds.Cargo"/>) or a ship scan (<see cref="SimScanKinds.Ship"/>) of the locked ship.
    /// The reach is <paramref name="OptimalMeters"/> whole, then <paramref name="FalloffMeters"/> of falloff (0 for a hard cutoff, as a cargo scan has). The
    /// simulation parks the module at the cycle's end and the host settles the reveal (<see cref="SimModuleWorkCommands.ScanCargo"/> or <c>ScanShip</c>).
    /// </summary>
    public sealed record Scan(byte Kind, double OptimalMeters, double FalloffMeters) : SimAction
    {
        /// <summary>Whether the numbers are ones the simulation runs: a known kind, a positive optimal range and a falloff that is not negative.</summary>
        public bool IsValid => Kind is SimScanKinds.Cargo or SimScanKinds.Ship
            && double.IsFinite(OptimalMeters) && OptimalMeters > 0
            && double.IsFinite(FalloffMeters) && FalloffMeters >= 0;
    }

    /// <summary>A tractor beam (tag 9, protocol 30): it pulls the locked wreck or can at <paramref name="VelocityMetersPerSecond"/> until it is
    /// <paramref name="HoldDistanceMeters"/> off the ship, and a target beyond <paramref name="RangeMeters"/> (the module's reach) is out of range.</summary>
    public sealed record Tractor(double VelocityMetersPerSecond, double HoldDistanceMeters, double RangeMeters) : SimAction;

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
    /// <summary>
    /// A titan doomsday's hit (tag 14, ADR 0013 epoch 26): the locked target takes <paramref name="Damage"/> once, <paramref name="DelayMs"/> after the cycle
    /// starts. The ship is held for its windows from the start (<paramref name="ImmobileMs"/>, <paramref name="NoJumpMs"/>).
    /// </summary>
    public sealed record Doomsday(double DelayMs, IReadOnlyList<double> Damage, double ImmobileMs, double NoJumpMs) : SimAction
    {
        /// <summary>Whether the numbers are ones the simulation runs (finite and not negative, four kinds of damage).</summary>
        public bool IsValid =>
            Damage.Count == 4
            && new[] { DelayMs, ImmobileMs, NoJumpMs }.All(n => double.IsFinite(n) && n >= 0)
            && Damage.All(d => double.IsFinite(d) && d >= 0);
    }

    /// <summary>
    /// A kamikaze squadron's detonation (tag 23, protocol 36): the squadron closes in on its locked target to <paramref name="RangeMeters"/> and, once there, the
    /// target takes <paramref name="Damage"/> (the four kinds) and the squadron is destroyed.
    /// </summary>
    public sealed record Kamikaze(IReadOnlyList<double> Damage, double RangeMeters) : SimAction
    {
        /// <summary>Whether the numbers are ones the simulation runs (finite and not negative, four kinds of damage).</summary>
        public bool IsValid =>
            Damage.Count == 4
            && double.IsFinite(RangeMeters) && RangeMeters >= 0
            && Damage.All(d => double.IsFinite(d) && d >= 0);
    }

    /// <summary>
    /// A fighter's missiles (tag 24, protocol 36): the locked target takes <paramref name="Damage"/> once, scaled by the application factor of the explosion
    /// (<paramref name="ExplosionRadius"/>, <paramref name="ExplosionVelocity"/>, the reduction factor and its sensitivity) for the target's signature and
    /// speed, when it is within <paramref name="ReachMeters"/>.
    /// </summary>
    public sealed record FighterMissile(
        IReadOnlyList<double> Damage,
        double ReachMeters,
        double ExplosionRadius,
        double ExplosionVelocity,
        double ReductionFactor,
        double ReductionSensitivity) : SimAction
    {
        /// <summary>Whether the numbers are ones the simulation runs (finite and not negative, four kinds of damage).</summary>
        public bool IsValid =>
            Damage.Count == 4
            && new[] { ReachMeters, ExplosionRadius, ExplosionVelocity, ReductionFactor, ReductionSensitivity }.All(n => double.IsFinite(n) && n >= 0)
            && Damage.All(d => double.IsFinite(d) && d >= 0);
    }

    /// <summary>
    /// A fighter's LaunchBomb blast (tag 25, protocol 37): when its window ends the blast goes off at <paramref name="Centre"/> (metres), and each ship within
    /// <paramref name="RadiusMeters"/> of it (its surface) takes <paramref name="Damage"/> (the four kinds) once. The area of ADR 0011.
    /// </summary>
    public sealed record Blast(IReadOnlyList<double> Centre, double RadiusMeters, IReadOnlyList<double> Damage) : SimAction
    {
        /// <summary>Whether the numbers are ones the simulation runs (a finite centre, a positive radius, four finite and not negative damages).</summary>
        public bool IsValid =>
            Centre.Count == 3 && Centre.All(double.IsFinite)
            && double.IsFinite(RadiusMeters) && RadiusMeters > 0
            && Damage.Count == 4 && Damage.All(d => double.IsFinite(d) && d >= 0);
    }

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
        double NeutSignatureRadius,
        double DebuffMs = 0,
        IReadOnlyList<double>? SweepEnd = null,
        IReadOnlyList<double>? Beacon = null) : SimAction
    {
        /// <summary>Whether the numbers are ones the simulation runs (the same checks the simulation makes on decode).</summary>
        public bool IsValid =>
            (Shape is 1 or 3 or 4)
            && Direction.Count == 3 && Damage.Count == 4
            && Direction.All(double.IsFinite) && Damage.All(d => double.IsFinite(d) && d >= 0)
            && Math.Abs(Math.Sqrt(Direction.Sum(c => c * c)) - 1.0) < 1e-6
            && double.IsFinite(Length) && Length > 0
            && double.IsFinite(Radius) && Radius >= 0
            && (Shape != 4 || Radius > 0)
            && double.IsFinite(WarningMs) && WarningMs >= 0
            && double.IsFinite(PulseMs) && PulseMs > 0
            && Pulses >= 1
            // a strike with no damage (the transport field's) has no signature to scale: its static data has none
            && (SignatureRadius > 0 || Damage.All(d => d == 0)) && double.IsFinite(SignatureRadius)
            && double.IsFinite(ImmobileMs) && ImmobileMs >= 0
            && double.IsFinite(DebuffMs) && DebuffMs >= 0
            && (SweepEnd is null || (SweepEnd.Count == 3 && SweepEnd.All(double.IsFinite) && SweepEnd.Any(c => c != 0)))
            && (Beacon is null || (Beacon.Count == 3 && Beacon.All(double.IsFinite)))
            && double.IsFinite(NoJumpMs) && NoJumpMs >= 0
            && double.IsFinite(NeutAmount) && NeutAmount >= 0
            && double.IsFinite(NeutRadius) && NeutRadius >= 0
            && double.IsFinite(NeutSignatureRadius) && NeutSignatureRadius >= 0;
    }

    /// <summary>
    /// The Gravitational Transportation Field Oscillator's field (tag 15, protocol 29): a sphere strike (shape 4) whose sphere is centred on the aim point, then
    /// at its pulse the ships of <paramref name="Moved"/> inside it are carried to <paramref name="Destination"/> (an offset from the firing ship's centre), and
    /// the capital ships of <paramref name="Held"/> inside it cannot warp for <paramref name="WarpBlockMs"/>. The ship ids are the host's list.
    /// </summary>
    public sealed record Field(
        Strike Sphere,
        IReadOnlyList<double> Destination,
        double WarpBlockMs,
        IReadOnlyList<long> Moved,
        IReadOnlyList<long> Held) : SimAction
    {
        /// <summary>Whether the field is one the simulation runs: a sphere strike, a finite destination and warp block, and at most 4096 ids a list.</summary>
        public bool IsValid =>
            Sphere.IsValid && Sphere.Shape == 4
            && Destination.Count == 3 && Destination.All(double.IsFinite)
            && double.IsFinite(WarpBlockMs) && WarpBlockMs >= 0
            && Moved.Count <= 4096 && Held.Count <= 4096;
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

    /// <summary>A fighter's tackle: a warp scramble of <paramref name="Strength"/> points and a web of <paramref name="WebPercent"/> of the target's top speed (negative), both as long as it lands (one remote for both: the wire's tag 12).</summary>
    public sealed record Tackle(double Strength, double WebPercent) : SimRemote;

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
    SimWreckKind? WreckKind = null, int ShipTypeId = 0, int CorporationId = 0) : SimMessage;

/// <summary>A weapon of an NPC: an effect of the NPC's own ship, so the module is the ship. One that flies a <see cref="SimMissile"/> is a launcher (the NPC's own: no rounds to spend).</summary>
public sealed record SimNpcWeapon(
    long ModuleId, int EffectId, double CycleMs, double CapacitorNeed, int WeaponTypeId, string Fx, IReadOnlyList<double> RawDamage, double OptimalRange, double Falloff,
    double TrackingSpeed, double SignatureResolution, SimTurretCharge? Charge = null, SimMissile? Missile = null, bool RequiresGunnery = false);

/// <summary>A local self-repair effect of an NPC hull, derived from its SDE type dogma.</summary>
public sealed record SimNpcSelfRepair(int EffectId, double Amount, double CycleMs, double CapacitorCost, double DelayChance)
{
    public static bool IsValid(SimNpcSelfRepair repair) =>
        repair.EffectId is 6884 or 6990
        && double.IsFinite(repair.Amount) && repair.Amount > 0
        && double.IsFinite(repair.CycleMs) && repair.CycleMs >= 1
        && double.IsFinite(repair.CapacitorCost) && repair.CapacitorCost >= 0
        && double.IsFinite(repair.DelayChance) && repair.DelayChance is >= 0 and <= 1;
}

/// <summary>What a ship leaves when it is destroyed.</summary>
public sealed record SimWreckKind(int TypeId, string Name, int GraphicId);

/// <summary>What sets an NPC apart: its combat and local SDE-derived self-repair effects.</summary>
public sealed record SimNpcIdentity(
    long HostileResponseThreshold, long FriendlyResponseThreshold, double AggressionRange, IReadOnlyList<SimNpcWeapon> Weapons, SimWreckKind? Wreck, bool Hull = false, bool HostWreck = false, IReadOnlyList<SimNpcSelfRepair>? SelfRepairs = null)
{
    public IReadOnlyList<SimNpcSelfRepair> SelfRepairs { get; init; } = SelfRepairs ?? [];
    /// <summary>The limits the reference gives every NPC, and nothing else.</summary>
    public static readonly SimNpcIdentity Default = new(-11, 11, 0, [], null);
}

/// <summary>
/// Spawn an NPC in the scene of a system (there is one while somebody is in the system): its ship, where it comes into space and how it moves, the
/// scripted tree it runs (empty for none) and what it is. Answered with <see cref="SimSpawnReply"/>.
/// </summary>
public sealed record SimSpawn(
    ulong RequestId, SimSceneKey Scene, SimShip Ship, long AnchorShipId, IReadOnlyList<double> Position, IReadOnlyList<double> Velocity, IReadOnlyList<double> Direction,
    string Behavior, SimNpcIdentity Identity, string? AbyssalContentId = null) : SimMessage;

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

/// <summary>
/// A pilot's fighter squadron is launched beside its ship: the squadron's ship, the graphic of its type and its cruise fraction (no weapon, no work and no
/// effect: a squadron is not a drone). Answered with <see cref="SimSpawnReply"/>.
/// </summary>
public sealed record SimLaunchFighter(ulong RequestId, ulong SessionId, SimShip Ship, int GraphicId, double CruiseFraction, int SquadronSize) : SimMessage;

/// <summary>A squadron out in space lost members (or got back some): its size now, for the pilot who controls it (the host keeps the item's quantity to it).</summary>
public sealed record SimFighterSize(ulong SessionId, long FighterId, int Size) : SimMessage;

/// <summary>The codes of the orders to fighter squadrons (<see cref="SimFighterOrder"/>), as the simulation numbers them.</summary>
public static class SimFighterOrders
{
    public const byte Orbit = 1;
    public const byte Follow = 2;
    public const byte Stop = 3;
    public const byte Recall = 4;
    public const byte Abandon = 5;
    /// <summary>GOTO_POINT: the squadron flies to a point (sent as <see cref="SimFighterGotoPoint"/>, answered under this command).</summary>
    public const byte GotoPoint = 6;
    /// <summary>Takes back a squadron let go in space, from within reach of its ship (answered under this command: the squadrons taken, and why not).</summary>
    public const byte Scoop = 7;

    /// <summary>The key an order is answered under in <see cref="CommandReply"/>s: apart from the drones' (<see cref="SimDroneCommands.Key"/>).</summary>
    public static byte Key(byte command) => (byte)(0xF0 + command);
}

/// <summary>
/// A pilot gives its fighter squadrons (their item ids) an order: for an orbit the target and the distance, for a follow the target and the range (the
/// <paramref name="Range"/> of either). The answer is <see cref="SimFighterResults"/>.
/// </summary>
public sealed record SimFighterOrder(ulong SessionId, byte Command, long TargetId, IReadOnlyList<long> FighterIds, double Range) : SimMessage;

/// <summary>A pilot sends its squadrons to a point of the scene (GOTO_POINT): the squadrons (their item ids) and the point, in metres. Answered with
/// <see cref="SimFighterResults"/> under command 6.</summary>
public sealed record SimFighterGotoPoint(ulong SessionId, IReadOnlyList<long> FighterIds, IReadOnlyList<double> Point) : SimMessage;

/// <summary>What one squadron did with an order: <paramref name="Reason"/> is empty when it took it.</summary>
public sealed record SimFighterResult(long FighterId, string Reason);

/// <summary>What each squadron did with an order.</summary>
public sealed record SimFighterResults(ulong SessionId, byte Command, IReadOnlyList<SimFighterResult> Results) : SimMessage;

/// <summary>A squadron came back after a recall and landed: the host moves its item into the tube it came from.</summary>
public sealed record SimFighterLanded(ulong SessionId, long FighterId) : SimMessage;

/// <summary>A squadron was let go (abandoned, or its controller left the scene) at <paramref name="Position"/>: the host decides what becomes of its item.</summary>
public sealed record SimFighterReleased(ulong SessionId, long FighterId, IReadOnlyList<double> Position) : SimMessage;

/// <summary>One ship an ECM burst reached: its item id, and whether the roll of the burst jammed it.</summary>
public readonly record struct SimBurstReach(long ShipId, bool Jammed);

/// <summary>
/// An ECM burst reached the ships about it on the simulation's account: the ship that runs it, and each ship reached (a cloaked ship is not reached), with whether its
/// roll jammed it. The host records the attack on each pilot reached.
/// </summary>
public sealed record SimEcmBurst(ulong SessionId, long ShipId, IReadOnlyList<SimBurstReach> Reached) : SimMessage;

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
    double AccessDifficultyBonus, double WasteRoll, double RoundingRoll, double CritRoll,
    /// <summary>The ships a command burst reaches (<see cref="SimModuleWorkCommands.Burst"/> only; null or empty for every other command): the ships of the scene within reach.</summary>
    IReadOnlyList<long>? Recipients = null) : SimMessage;

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

/// <summary>The simulation's fresh damageable candidates for one controlled-structure area pulse or chain hop.</summary>
public sealed record SimStructureDamageCandidatesRequest(
    ulong SessionId, long StructureId, long ModuleId, int EffectId, ulong ActivationNonce, long ControlLeaseGeneration,
    ulong CycleId, uint PulseHopOrdinal, IReadOnlyList<long> CandidateIds) : SimMessage;

/// <summary>The Host-authorized subset for the same structure-damage application; Field pulses also carry the complete moved/held partition. All correlation fields must match the request.</summary>
public sealed record SimStructureDamageCandidatesResult(
    ulong SessionId, long StructureId, long ModuleId, int EffectId, ulong ActivationNonce, long ControlLeaseGeneration,
    ulong CycleId, uint PulseHopOrdinal, IReadOnlyList<long> AllowedTargetIds) : SimMessage
{
    public IReadOnlyList<long> FieldMovedTargetIds { get; init; } = Array.Empty<long>();
    public IReadOnlyList<long> FieldHeldTargetIds { get; init; } = Array.Empty<long>();
}

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
    SimSceneKey Scene, long WreckId, int GroupId, SimWreckKind Kind, IReadOnlyList<double> Position, IReadOnlyList<double> Direction, double Radius, long LauncherId, SimCredit Credit,
    bool IsEmpty) : SimMessage;

/// <summary>
/// A can the host made (an item of its own) for cargo a pilot jettisons: the scene puts it <see cref="Distance"/> metres ahead of the pilot's ship (never nearer than fifty), tells
/// those who see it the ball and the effect that plays the jettison, and answers with <see cref="SimContainerPlaced"/> (or <see cref="SimRefused"/> under
/// <see cref="SimCommands.PlaceContainer"/>).
/// </summary>
public sealed record SimPlaceContainer(ulong SessionId, long ContainerId, SimWreckKind Kind, double Radius, long LauncherId, SimCredit Credit, double Distance) : SimMessage
{
    /// <summary>Placement must still belong to this scene and LauncherId ship when supplied.</summary>
    public SimSceneKey? ExpectedScene { get; init; }
}

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

/// <summary>
/// A cynosural field the host keeps a record of (protocol 44). The simulation says it was lit, with its place, owner, type and whole life; the host sends the
/// same message back to put it in space after a restart, with what is left of its life then (<see cref="LifetimeMs"/>).
/// </summary>
public sealed record SimDeployed(
    int SolarSystemId, long DeployableId, string Name, int TypeId, int GroupId, IReadOnlyList<double> Position, double Radius, long OwnerId, int CorporationId,
    int AllianceId, long ShipId, double LifetimeMs) : SimMessage;

/// <summary>The host takes a cynosural field out of space when its record's time is up (protocol 44); a deployable that is not one, or not in space, is left alone.</summary>
public sealed record SimRemoveDeployed(long DeployableId) : SimMessage;

/// <summary>A scanner probe the host launched, put in space where it was launched (protocol 45). It stays until <see cref="SimRemoveProbe"/>.</summary>
public sealed record SimPlaceProbe(
    long ProbeId, int SolarSystemId, string Name, int TypeId, int GroupId, IReadOnlyList<double> Position, double Radius, long OwnerId, int CorporationId,
    int AllianceId, long ShipId) : SimMessage;

/// <summary>The host takes a scanner probe out of space (recalled, or its time is up); a deployable that is not a probe is left alone (protocol 45).</summary>
public sealed record SimRemoveProbe(long ProbeId) : SimMessage;

/// <summary>A structure's health as the host has it (protocol 60): the points of its shield, armor and hull, the resonances of each layer (shield, armor
/// and hull, four kinds each), the shield's recharge in milliseconds, the limit each layer takes in a second and the repair pause of each layer.</summary>
public sealed record SimStructureHealth(
    double ShieldCapacity, double ArmorHp, double StructureHp, IReadOnlyList<double> Resonances, double ShieldRechargeMs,
    IReadOnlyList<double> PerSecondLimit, IReadOnlyList<double> RepairPause);

/// <summary>A structure's slim fields as the host has them (protocol 59): its state and upkeep, its timer (start, end and the time it was paused, when it is),
/// its unanchoring time and whether it is repaired (none when the client should show no repair). The simulation shows them and decides nothing.</summary>
public sealed record SimStructureStatus(int State, int UpkeepState, (long Start, long End, long? Paused)? Timer, long? Unanchoring, bool? Repairing);

/// <summary>An Upwell structure the host puts in space, as a body of the system with its slim state (protocol 59). It stays until <see cref="SimRemoveStructure"/>.</summary>
public sealed record SimPlaceStructure(
    long StructureId, int SolarSystemId, string Name, int TypeId, int GroupId, IReadOnlyList<double> Position, double Radius, long OwnerId, int CorporationId,
    int AllianceId, SimStructureHealth Health, SimStructureStatus Status, long PlanetId = 0) : SimMessage;

/// <summary>The simulation says a hit emptied a layer of a structure (<c>Depleted</c>: 0 none, 1 shield, 2 armor, 3 hull) or paused its repair
/// (<c>OverThreshold</c>), to the host (simulation to host, protocol 60).</summary>
public sealed record SimStructureDamaged(long StructureId, int Depleted, bool OverThreshold) : SimMessage;

/// <summary>The host takes a structure out of space (protocol 59); a body that is not a structure is left alone.</summary>
public sealed record SimRemoveStructure(long StructureId) : SimMessage;

/// <summary>A wormhole end the host puts in space (protocol 63): its body in the system, and the far end it leads to (the other system, the far end's
/// body ID, that system's class, the far end's position and radius, and the largest ship the wormhole takes). A ship that comes out of this end
/// appears by the far end. It stays until <see cref="SimRemoveWormhole"/>.</summary>
public sealed record SimPlaceWormhole(
    long WormholeId, int SolarSystemId, string Name, int TypeId, int GroupId, IReadOnlyList<double> Position, double Radius,
    int DestinationSystemId, long DestinationEnd, int DestinationClass, IReadOnlyList<double> DestinationPosition, double DestinationRadius,
    double MaxJumpMass) : SimMessage;

/// <summary>The host takes a wormhole end out of space (protocol 63); a body that is not a wormhole end is left alone.</summary>
public sealed record SimRemoveWormhole(long WormholeId) : SimMessage;

/// <summary>The host changes a structure's slim state (protocol 59); a structure not in space is left alone.</summary>
public sealed record SimSetStructureState(int SolarSystemId, long StructureId, SimStructureStatus Status) : SimMessage;

public sealed record SimTetherCandidate(long StructureId, double Range);

public sealed record SimTetherPolicy(
    ulong SessionId, ulong BlockedForMs, ulong LeaseMs, IReadOnlyList<SimTetherCandidate> Candidates) : SimMessage;

public sealed record SimTetherChanged(
    ulong SessionId, long PreviousStructureId, long StructureId, bool Repairing) : SimMessage;

public sealed record SimControlStructure(
    ulong SessionId, int SolarSystemId, long StructureId, long CharacterId, bool Active, long ControlLeaseGeneration) : SimMessage;

/// <summary>A mobile warp disruptor the host keeps a record of, put in space where its pilot dropped it (protocol 48). Its disruption is on while <see cref="Active"/>
/// (it is anchored); <see cref="Range"/> is the warp disruption range in metres.</summary>
public sealed record SimPlaceDisruptor(
    long DeployableId, int SolarSystemId, string Name, int TypeId, int GroupId, IReadOnlyList<double> Position, double Radius, double Range, bool Active,
    long OwnerId, int CorporationId, int AllianceId, long ShipId) : SimMessage;

/// <summary>The host says a mobile warp disruptor is anchored (<see cref="Active"/>) or not (protocol 48); one not in space is left alone.</summary>
public sealed record SimSetDisruptorActive(long DeployableId, bool Active) : SimMessage;

/// <summary>The state of a CRAB the host sets (protocol 61), as its slim item keeps it for the client: whether it is active (and when it becomes active otherwise), the
/// activation wait in seconds, when it was put in space, the link state (1 idle, 2 running, 3 completed, 4 disabled), when the link completes and the linked ship.
/// The times are FILETIME ticks and 0 for none.</summary>
public sealed record SimAnalysisBeaconState(bool Active, long ActivateAtFiletime, long DeployedAtFiletime, int ActivationSeconds, int LinkState, long LinkCompleteAtFiletime, long LinkedShipId);

/// <summary>A CONCORD Rogue Analysis Beacon the host keeps a record of, put in space where its pilot dropped it (protocol 61).</summary>
public sealed record SimPlaceAnalysisBeacon(
    long DeployableId, int SolarSystemId, string Name, int TypeId, int GroupId, IReadOnlyList<double> Position, double Radius, long OwnerId, int CorporationId,
    int AllianceId, long ShipId, SimAnalysisBeaconState State) : SimMessage;

/// <summary>The host sets a CRAB's state (protocol 61); one not in space is left alone.</summary>
public sealed record SimSetAnalysisBeaconState(long DeployableId, SimAnalysisBeaconState State) : SimMessage;

/// <summary>A value of a dynamic slim-item field (protocol 76): none, an int, a real, a bool, a text, or a tuple of those.</summary>
public abstract record SimSlimValue;

public sealed record SimSlimNone : SimSlimValue;

public sealed record SimSlimInt(long Value) : SimSlimValue;

public sealed record SimSlimReal(double Value) : SimSlimValue;

public sealed record SimSlimBool(bool Value) : SimSlimValue;

public sealed record SimSlimText(string Value) : SimSlimValue;

public sealed record SimSlimTuple(IReadOnlyList<SimSlimValue> Items) : SimSlimValue;

/// <summary>
/// The host sets a dynamic field of a deployable's slim item (protocol 76). The client reads it on the slim item, so whoever has the deployable in view is
/// told the whole slim item again; a value already set is not told again, and an item not in space is left alone.
/// </summary>
public sealed record SimSetSlimField(long ItemId, string FieldName, SimSlimValue Value) : SimMessage;

/// <summary>A Mobile Cynosural Inhibitor the host keeps a record of, put in space where its pilot dropped it (protocol 50): the same placement as a disruptor's,
/// with the inhibitor's range in metres (no cynosural field lights within it while it is anchored).</summary>
public sealed record SimPlaceInhibitor(
    long DeployableId, int SolarSystemId, string Name, int TypeId, int GroupId, IReadOnlyList<double> Position, double Radius, double Range, bool Active,
    long OwnerId, int CorporationId, int AllianceId, long ShipId) : SimMessage;

/// <summary>A Mobile Cynosural Beacon the host keeps a record of, put in space where its pilot dropped it (protocol 58). It is a cynosural field a pilot may jump
/// to while <see cref="Active"/> (anchored); the wire carries it as a disruptor's placement with a range of 0.</summary>
public sealed record SimPlaceCynoBeacon(
    long DeployableId, int SolarSystemId, string Name, int TypeId, int GroupId, IReadOnlyList<double> Position, double Radius, bool Active,
    long OwnerId, int CorporationId, int AllianceId, long ShipId) : SimMessage;

/// <summary>Takes the named structure-owned cyno beacon out of its solar system's scene.</summary>
public sealed record SimRemoveCynoBeacon(int SolarSystemId, long StructureBeaconId) : SimMessage;

/// <summary>A Mobile Observatory the host keeps a record of, put in space where its pilot dropped it (protocol 72). The wire carries it as a disruptor's placement
/// with a range of 0; it pings while <see cref="Active"/> (anchored).</summary>
public sealed record SimPlaceObservatory(
    long DeployableId, int SolarSystemId, string Name, int TypeId, int GroupId, IReadOnlyList<double> Position, double Radius, bool Active,
    long OwnerId, int CorporationId, int AllianceId, long ShipId) : SimMessage;

/// <summary>A player control tower the host keeps a record of, put in space where its pilot launched it (protocol 73). The wire carries it as a disruptor's placement
/// with a range of 0; it is anchored while <see cref="Active"/>.</summary>
public sealed record SimPlaceControlTower(
    long DeployableId, int SolarSystemId, string Name, int TypeId, int GroupId, IReadOnlyList<double> Position, double Radius, bool Active,
    long OwnerId, int CorporationId, int AllianceId, long ShipId) : SimMessage;

/// <summary>A deadspace pocket of a dungeon (a mission pocket or an incursion site) the host keeps a record of, put in the scene at its origin with its radius (protocol 74).
/// It is in no client ball; the wire carries it as a disruptor's placement with no type, group, range or owner.</summary>
public sealed record SimPlaceDeadspacePocket(long DeployableId, int SolarSystemId, string Name, IReadOnlyList<double> Position, double Radius) : SimMessage;

/// <summary>
/// An acceleration gate of a dungeon room, put in its system's space at the point the host gives (protocol 75): a stargate-kind ball, interactive, that the
/// pilots of the system see. The wire carries it as a disruptor's placement with no range, owner or ship.
/// </summary>
public sealed record SimPlaceAccelerationGate(long DeployableId, int SolarSystemId, string Name, int TypeId, int GroupId, IReadOnlyList<double> Position, double Radius) : SimMessage;

/// <summary>A battlefield's scoreboard put in its system's space (protocol 78): a ball the pilots of the system see; its score is the dynamic slim field the host sets.</summary>
public sealed record SimPlaceBattlefieldScoreboard(long DeployableId, int SolarSystemId, string Name, int TypeId, int GroupId, IReadOnlyList<double> Position, double Radius) : SimMessage;

/// <summary>A battlefield's capture point put in its system's space (protocol 77): a ball the pilots of the system see. Its attacker and occupier factions
/// (0 for none) and its next point time (a FILETIME, 0 for none) are the dynamic slim fields the client reads (<c>component_fwCapturePoint_factionInfo</c>,
/// <c>component_fwCapturePoint_nextPointTime</c>).</summary>
public sealed record SimPlaceCapturePoint(long DeployableId, int SolarSystemId, string Name, int TypeId, int GroupId, IReadOnlyList<double> Position, double Radius,
    int AttackerFaction, int OccupierFaction, long NextPointTime) : SimMessage;

/// <summary>A Mobile Observatory's decloaking ping in its solar system (protocol 72): the cloaked ships there whose cloak is not stabilized may be decloaked.</summary>
public sealed record SimRemoteDecloakPing(int SolarSystemId, long ObservatoryId) : SimMessage;

/// <summary>Registers or updates a Sim-created damageable entity's creation identity for Host-side owner resolution (protocol 69). Host validates these facts against authoritative type/owner sources.</summary>
public sealed record SimDamageableEntityUpserted(
    int SolarSystemId, long EntityId, int TypeId, long OwnerId, long CorporationId, long AllianceId, long WarFactionId) : SimMessage;

/// <summary>Removes a non-destroyed Sim-created damageable entity from the Host's owner-resolution registry (protocol 69).</summary>
public sealed record SimDamageableEntityRemoved(int SolarSystemId, long EntityId) : SimMessage;

/// <summary>The answer to <see cref="SimCommands.Measure"/>: metres from the centre of the ship to the surface of the wreck or can, or null when there is no such wreck.</summary>
public sealed record SimMeasured(ulong SessionId, byte Command, double? Distance) : SimMessage;

/// <summary>Who may take what a destroyed ship leaves: the pilot and the corporation, the alliance and the faction they are in (0 for none).</summary>
public sealed record SimCredit(long CharacterId, long CorporationId, long AllianceId, long WarFactionId)
{
    public static SimCredit Nobody { get; } = new(0, 0, 0, 0);
}

/// <summary>Destroy this ship: the explosion, the wreck (for <see cref="Credit"/> to loot), and (for a player's ship) a <see cref="SimDestroyed"/> for the host.</summary>
public sealed record SimDestroy(long ShipId, SimCredit Credit) : SimMessage;

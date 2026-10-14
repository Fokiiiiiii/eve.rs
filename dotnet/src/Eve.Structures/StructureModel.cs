namespace Eve.Structures;

/// <summary>The structure states the client knows (<c>structures/const.py</c> STATE_*; EVE.js <c>STRUCTURE_STATE</c>). The ids are on the wire and in the table.</summary>
public static class StructureState
{
    public const int Unanchored = 1;
    public const int Anchoring = 2;
    public const int FittingInvulnerable = 101;
    public const int OnliningVulnerable = 102;
    public const int ShieldVulnerable = 110;
    public const int ArmorReinforce = 111;
    public const int ArmorVulnerable = 112;
    public const int HullReinforce = 113;
    public const int HullVulnerable = 114;
    public const int AnchorVulnerable = 115;
    public const int DeployVulnerable = 116;
    public const int FobInvulnerable = 118;

    /// <summary>The states in which a structure can be shot at (client <c>STATE_VULNERABILITY</c>).</summary>
    public static bool IsVulnerable(int state) =>
        state is OnliningVulnerable or ShieldVulnerable or ArmorVulnerable or HullVulnerable or AnchorVulnerable or DeployVulnerable;

    /// <summary>The states in which an unanchoring in progress is called off (client <c>STATE_CANCELS_UNANCHOR</c>).</summary>
    public static bool CancelsUnanchor(int state) =>
        state is Unanchored or Anchoring or FittingInvulnerable or ArmorReinforce or ArmorVulnerable or HullReinforce or HullVulnerable or AnchorVulnerable or DeployVulnerable;
}

/// <summary>The upkeep states (client <c>UPKEEP_STATE_*</c>).</summary>
public static class UpkeepState
{
    public const int FullPower = 1;
    public const int LowPower = 2;
    public const int Abandoned = 3;
}

/// <summary>The states of a service module (EVE.js <c>STRUCTURE_SERVICE_STATE</c>).</summary>
public static class ServiceState
{
    public const int Online = 1;
    public const int Offline = 2;
    public const int Cleanup = 3;
}

/// <summary>A pirate Forward Operating Base (<c>Fob</c>): no rig size; one armor reinforcement, no hull reinforcement, no repair countdown after the shield.</summary>
public enum StructureSize { Flex, Medium, Large, ExtraLarge, Fob }

/// <summary>The kind of space a structure lies in, as far as its timers care (client <c>REINFORCE_DURATION_*_BY_SECURITY</c>).</summary>
public enum SecurityBand { HighSec, LowSec, NullSec, Wormhole, Pochven }

public enum StructureLayer { Shield, Armor, Hull }

/// <summary>The durations of the structure timeline, in milliseconds (EVE University wiki "Vulnerability", client <c>structures/const.py</c>).</summary>
public static class StructureTimes
{
    public const long Second = 1_000;
    public const long Hour = 3_600_000;
    public const long Day = 86_400_000;

    /// <summary>The 15 minute repair timer of the anchor, onlining, shield and armor stages.</summary>
    public const long RepairMs = 900 * Second;
    /// <summary>The 30 minute repair timer of the hull stage.</summary>
    public const long HullRepairMs = 1_800 * Second;
    public const long AnchoringMs = Day;
    public const long UnanchoringMs = 7 * Day;
    /// <summary>A Flex structure (client <c>FLEX_UNANCHORING_TIME</c>).</summary>
    public const long FlexUnanchoringMs = 2_700 * Second;
    /// <summary>A FOB's armor reinforcement: 3 hours, with 1 hour of variance either way (wiki "Forward Operating Bases"; patch 24.01).</summary>
    public const long FobArmorReinforceMs = 3 * Hour;
    public const long FobReinforceJitterMs = Hour;
    /// <summary>A structure that stays in Low Power for this long is abandoned.</summary>
    public const long AbandonAfterMs = 7 * Day;
    /// <summary>A new reinforcement time takes effect this long after it was asked for.</summary>
    public const long ReinforceChangeDelayMs = 30 * Day;
    /// <summary>The repair countdown holds this long after a hit that pauses it (EVE.js <c>STRUCTURE_REPAIR_RESUME_DELAY_MS</c>).</summary>
    public const long RepairResumeMs = 10 * Second;

    public const int NoReinforceWeekday = 255;
    public const int DefaultReinforceWeekday = 5;
    public const int DefaultReinforceHour = 18;
}

/// <summary>
/// What the timeline needs to know about a structure's type and place. The Host works it out from the static data (<c>rigSize</c> 1547 gives the size, the system gives the
/// band); the state machine itself reads no static data.
/// </summary>
public sealed record StructureRules(StructureSize Size, SecurityBand Band, bool CanBeAbandoned, long AnchoringMs, long UnanchoringMs)
{
    public static StructureRules Of(StructureSize size, SecurityBand band, bool canBeAbandoned = true, long anchoringMs = StructureTimes.AnchoringMs, long? unanchoringMs = null) =>
        new(size, band, canBeAbandoned, anchoringMs, unanchoringMs ?? (size == StructureSize.Flex ? StructureTimes.FlexUnanchoringMs : StructureTimes.UnanchoringMs));

    /// <summary>The size class from the type's <c>rigSize</c> attribute: 2 Medium, 3 Large, 4 X-Large; a type without one (the Flex structures) is Flex.</summary>
    public static StructureSize SizeOfRigSize(double rigSize) => (int)rigSize switch
    {
        2 => StructureSize.Medium,
        3 => StructureSize.Large,
        4 => StructureSize.ExtraLarge,
        _ => StructureSize.Flex,
    };

    /// <summary>Duration of the armor reinforcement. Medium structures depend on the space; Large and X-Large are 24 hours everywhere.</summary>
    public long ArmorReinforceMs => Size switch
    {
        StructureSize.Fob => StructureTimes.FobArmorReinforceMs,
        StructureSize.Medium => Band switch
        {
            SecurityBand.Wormhole or SecurityBand.Pochven => 216_000 * StructureTimes.Second,
            SecurityBand.HighSec => 388_800 * StructureTimes.Second,
            _ => 302_400 * StructureTimes.Second,
        },
        _ => StructureTimes.Day,
    };

    /// <summary>Duration of the hull reinforcement. A Flex structure waits 30 minutes (wiki); the others depend on the space.</summary>
    public long HullReinforceMs => Size == StructureSize.Flex
        ? 1_800 * StructureTimes.Second
        : Band switch
        {
            SecurityBand.Wormhole or SecurityBand.Pochven => 129_600 * StructureTimes.Second,
            SecurityBand.HighSec => 388_800 * StructureTimes.Second,
            _ => 216_000 * StructureTimes.Second,
        };

    /// <summary>How far the exit time may stray from the owner's hour: 1.5 hours for Medium, 30 minutes for Flex, 3 hours for the rest.</summary>
    public long ExitJitterMs => Size switch
    {
        StructureSize.Fob => StructureTimes.FobReinforceJitterMs,
        StructureSize.Medium => 5_400 * StructureTimes.Second,
        StructureSize.Flex => 1_800 * StructureTimes.Second,
        _ => 10_800 * StructureTimes.Second,
    };

    /// <summary>Whether a structure that loses its shield goes into armor reinforcement. Not a Flex or an abandoned one; not a Large or X-Large one in Low Power.</summary>
    public bool CanArmorReinforce(int upkeep) =>
        upkeep != UpkeepState.Abandoned && Size != StructureSize.Flex && !(Size != StructureSize.Medium && upkeep == UpkeepState.LowPower);

    /// <summary>Whether a structure that loses its armor goes into hull reinforcement. Large, X-Large and Flex in full power only; never a Medium or an abandoned one.</summary>
    public bool CanHullReinforce(int upkeep) => upkeep == UpkeepState.FullPower && Size is not (StructureSize.Medium or StructureSize.Fob);
}

/// <summary>The structure as the state machine sees it: the columns of the <c>structures</c> table that belong to the timeline, plus the number of online services.</summary>
public sealed record StructureSnapshot
{
    public long StructureId { get; init; }
    public int State { get; init; } = StructureState.Unanchored;
    public long StateStartedMs { get; init; }
    public long StateEndsMs { get; init; }
    /// <summary>The deployment-time anchoring duration, persisted so later TimerDue and restart retain sovereignty bonuses.</summary>
    public long AnchoringMs { get; init; } = StructureTimes.AnchoringMs;
    public long TimerPausedMs { get; init; }
    public int Upkeep { get; init; } = UpkeepState.LowPower;
    public bool CoreInstalled { get; init; }
    public int OnlineServices { get; init; }
    public int ReinforceWeekday { get; init; } = StructureTimes.DefaultReinforceWeekday;
    public int ReinforceHour { get; init; } = StructureTimes.DefaultReinforceHour;
    public int FightWeekday { get; init; } = -1;
    public int FightHour { get; init; } = -1;
    public int PendingWeekday { get; init; } = -1;
    public int PendingHour { get; init; } = -1;
    public long PendingEffectiveMs { get; init; }
    public long UnanchorEndsMs { get; init; }
    public long LowPowerSinceMs { get; init; }
    public long DestroyedMs { get; init; }

    /// <summary>Whether the repair countdown is held by a recent hit.</summary>
    public bool IsPaused(long now) => TimerPausedMs > 0 && now < TimerPausedMs + StructureTimes.RepairResumeMs;
}

/// <summary>What happened to a structure. The state machine turns one into the next snapshot.</summary>
public abstract record StructureEvent;

/// <summary>The anchor was placed: the 15 minute anchoring vulnerability begins.</summary>
public sealed record AnchorPlaced : StructureEvent;

/// <summary>A layer took damage. <paramref name="Depleted"/>: the layer is gone. <paramref name="PausesRepair"/>: the layer took at least the pause threshold (10 percent of its per-second damage cap) in one second.</summary>
public sealed record Damaged(StructureLayer Layer, bool Depleted, bool PausesRepair) : StructureEvent;

/// <summary>The clock passed a deadline of the structure: apply everything that is due.</summary>
public sealed record TimerDue : StructureEvent;

public sealed record CoreInserted : StructureEvent;
public sealed record CoreRemoved : StructureEvent;

/// <summary>A service module went online (the snapshot already counts it).</summary>
public sealed record ServiceOnline : StructureEvent;

/// <summary>A service module went offline (the snapshot already counts the services that remain).</summary>
public sealed record ServiceOffline : StructureEvent;

/// <summary>The fuel ran out: every service is offline (the snapshot already says so).</summary>
public sealed record FuelOut : StructureEvent;

public sealed record UnanchorStarted : StructureEvent;
public sealed record UnanchorCancelled : StructureEvent;

/// <summary>A pirate FOB is put in space (its row is new): invulnerable until its insurgency exposes it.</summary>
public sealed record FobDeployed : StructureEvent;

/// <summary>The insurgency's empire side reached its goal: the FOB can be attacked, from its shield stage.</summary>
public sealed record FobExposed : StructureEvent;

/// <summary>The FOB leaves space without a fight (the insurgency ended in the pirates' favour, or with no winner).</summary>
public sealed record FobDespawned : StructureEvent;

/// <summary>The owner asked for another reinforcement day and hour. It becomes the structure's own after <see cref="StructureTimes.ReinforceChangeDelayMs"/>.</summary>
public sealed record ReinforceTimeRequested(int Weekday, int Hour) : StructureEvent;

public enum StructureRemoval { None, Unanchored, Destroyed }

/// <summary>
/// The result of an event: the next snapshot, whether anything changed, a refusal name when the event is not allowed in the structure's state (the snapshot is then unchanged),
/// whether a repair countdown completed (the layers are full again: the simulation must heal the structure) and whether the structure is gone.
/// </summary>
public sealed record StructureTransition(StructureSnapshot Next, bool Changed, string? Refusal = null, bool Repaired = false, StructureRemoval Removal = StructureRemoval.None);

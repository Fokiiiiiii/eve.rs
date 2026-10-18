using Eve.Npc;

namespace Eve.Services.Space;

/// <summary>An NPC the host spawned was destroyed (for the objectives of a mission, a kill record): who dealt the last blow, what the NPC was and where.</summary>
/// <param name="FinalBlowCharacterId">The character of the pilot whose ship took the last of the NPC's hull; 0 when nobody did (an NPC did, or something else).</param>
/// <param name="NpcTypeId">The type of the NPC's ship.</param>
/// <param name="Profile">What the NPC was authored as (<see cref="NpcProfile.Id"/> names it).</param>
/// <param name="SolarSystemId">The solar system it was destroyed in.</param>
/// <param name="ShipId">The id its ship had in the scene.</param>
public sealed record NpcKill(long FinalBlowCharacterId, int NpcTypeId, NpcProfile Profile, int SolarSystemId, long ShipId);

/// <summary>What destroyed a pilot's ship, as far as the host can tell.</summary>
public enum ShipLossCause
{
    /// <summary>The pilot destroyed it itself (<c>/suicide</c>, the self-destruct of the client).</summary>
    SelfDestruct,

    /// <summary>The last blow was an NPC's.</summary>
    Npc,

    /// <summary>The last blow was another pilot's.</summary>
    Player,

    /// <summary>Nobody is known to have dealt it.</summary>
    Unknown,

    /// <summary>CONCORD enforced a criminal offense by destroying the ship.</summary>
    Concord,
}

/// <summary>A pilot's ship (not its capsule) was destroyed in space: whose it was, what it was and where (for an insurance payout, a kill record).</summary>
/// <param name="CharacterId">The pilot.</param>
/// <param name="ShipId">The ship's item id (the item is gone: what it held went into a wreck or was destroyed).</param>
/// <param name="ShipTypeId">The type of the ship.</param>
/// <param name="Cause">What destroyed it.</param>
/// <param name="SolarSystemId">The solar system it was destroyed in.</param>
/// <param name="KillerCharacterId">The character of the pilot who dealt the last blow (0 unless <see cref="Cause"/> is <see cref="ShipLossCause.Player"/>).</param>
/// <summary>An item a lost ship held (a module, a cargo stack), as it was: its type and quantity.</summary>
public sealed record ShipLossItem(int TypeId, long Quantity);

/// <param name="Contents">What the ship held when it was destroyed (its modules and cargo, destroyed or in the wreck); the loss value of the ship is its base price and these.</param>
public sealed record ShipLoss(long CharacterId, long ShipId, int ShipTypeId, ShipLossCause Cause, int SolarSystemId, long KillerCharacterId,
    IReadOnlyList<ShipLossItem>? Contents = null);

/// <summary>A pilot's capsule was destroyed in space: whose it was, where, and who dealt the last blow (the pod is a loss of its own, separate from the ship's).</summary>
/// <param name="CharacterId">The pilot.</param>
/// <param name="CapsuleId">The capsule's item id (the item is gone).</param>
/// <param name="SolarSystemId">The solar system it was destroyed in.</param>
/// <param name="Cause">What destroyed it (<see cref="ShipLossCause.Player"/> when another pilot dealt the last blow).</param>
/// <param name="KillerCharacterId">The character of that pilot (0 unless <see cref="Cause"/> is <see cref="ShipLossCause.Player"/>).</param>
public sealed record PodLoss(long CharacterId, long CapsuleId, int SolarSystemId, ShipLossCause Cause, long KillerCharacterId);

/// <summary>
/// The topics a module of a ship announces its start and stop on, and the siege mode (a siege module's start and stop). Nothing subscribes yet; the names are
/// the contract for the subscribers that will (missions, crimewatch), so they are kept as they are.
/// </summary>
public static class ShipModeTopics
{
    public const string ModuleStarted = "ship.module.started";
    public const string ModuleStopped = "ship.module.stopped";
    public const string SiegeEntered = "ship.siege.entered";
    public const string SiegeLeft = "ship.siege.left";
}

/// <summary>A module of a pilot's ship started or stopped (the siege topics carry the same payload).</summary>
public sealed record ShipModeEvent(long CharacterId, long ShipId, long ModuleId, int ModuleTypeId, int SolarSystemId);

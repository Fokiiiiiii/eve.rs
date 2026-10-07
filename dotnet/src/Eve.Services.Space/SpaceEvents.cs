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
public sealed record ShipLoss(long CharacterId, long ShipId, int ShipTypeId, ShipLossCause Cause, int SolarSystemId, long KillerCharacterId);

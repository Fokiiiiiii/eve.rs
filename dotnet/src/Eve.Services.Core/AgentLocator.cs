using System.Text.Json.Nodes;
using Eve.Agents;

namespace Eve.Services.Core;

/// <summary>Where a character is now: the solar system, and the station when docked (0 in space).</summary>
public sealed record LocateTarget(long SolarSystemId, long StationId);

/// <summary>The constellation and region of a solar system.</summary>
public sealed record LocatePlace(long ConstellationId, long RegionId);

/// <summary>
/// What the locator needs from the server: the character by name, the target's place from the session that is online, the
/// geography of a system (the SDE), and the delivery of a located result: the fee and the notification, in one transaction.
/// </summary>
public interface IAgentLocatorPort
{
    LocateStore Locates { get; }

    ValueTask<long?> FindCharacterAsync(string name, CancellationToken cancellationToken);

    /// <summary>The target's place, or null when the character is not online.</summary>
    ValueTask<LocateTarget?> TargetAsync(long characterId, CancellationToken cancellationToken);

    ValueTask<LocatePlace?> PlaceAsync(long solarSystemId, CancellationToken cancellationToken);

    /// <summary>
    /// Charges the fee and starts the cooldown in one transaction, then sends the result (type 69, sent by the agent). False, with nothing
    /// written, when the pilot cannot pay.
    /// </summary>
    ValueTask<bool> DeliverAsync(long characterId, long agentId, long fee, JsonObject result, CancellationToken cancellationToken);
}

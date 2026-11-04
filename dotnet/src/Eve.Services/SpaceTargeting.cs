namespace Eve.Services;

/// <summary>What a pilot asks about its targets (the calls of the client's dogma location; the numbers are the simulation's command codes).</summary>
public enum TargetingOrder : byte
{
    /// <summary>Start locking a target.</summary>
    Lock = 10,

    /// <summary>Give up a lock that is going.</summary>
    Cancel = 11,

    /// <summary>Let go of locked targets.</summary>
    Remove = 12,

    /// <summary>Let go of every target.</summary>
    Clear = 13,

    /// <summary>What the ship has locked.</summary>
    Targets = 14,

    /// <summary>Who has the ship locked.</summary>
    Targeters = 15,
}

/// <summary>
/// The answer to a targeting order: what the ship has locked (for <see cref="TargetingOrder.Targeters"/>, who has it locked) and whether a lock is going;
/// or, when the order was refused, why, with what the client is told of the target (its type and, for a target out of range, the range and how far off it was).
/// </summary>
public sealed record TargetingResult(bool Ok, bool Pending, IReadOnlyList<long> Targets, string? Reason = null, int TargetType = 0, double DesiredRange = 0, double ActualDistance = 0)
{
    public static TargetingResult Refusal(string reason, int targetType = 0, double desiredRange = 0, double actualDistance = 0) =>
        new(false, false, [], reason, targetType, desiredRange, actualDistance);
}

/// <summary>What the dogma service asks of space: the locks of the ship a session flies.</summary>
public interface ISpaceTargeting
{
    /// <summary>Carries out an order for the session's ship; null when the session has no ship in space.</summary>
    Task<TargetingResult?> OrderAsync(ClientSession session, TargetingOrder order, IReadOnlyList<long> targets, CancellationToken cancellationToken);
}

/// <summary>Answers whether two characters belong to opposite sides of an active war, including accepted war allies.</summary>
public interface IWars
{

    Task<bool> AreAtWarAsync(long attackerCharacterId, long targetCharacterId, long nowUnixMs, CancellationToken cancellationToken = default);
}

namespace Eve.Services;

/// <summary>What a ship in space has taken, as fractions: its hull, its capacitor's charge, its shield's charge and its armor.</summary>
public sealed record SpaceCondition(double Damage, double Charge, double ShieldCharge, double ArmorDamage);

/// <summary>What the services ask of space for the health of the ship a session flies.</summary>
public interface ISpaceHealth
{
    /// <summary>What the session's ship in space has taken now; null when the session has no ship in space.</summary>
    Task<SpaceCondition?> ConditionAsync(ClientSession session, CancellationToken cancellationToken);

    /// <summary>Mends the session's ship in space (full shield, armor, hull and capacitor; the pilot and those who see it are told); false when it has no ship in space.</summary>
    Task<bool> HealAsync(ClientSession session, CancellationToken cancellationToken);
}

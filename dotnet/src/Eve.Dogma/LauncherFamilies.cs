namespace Eve.Dogma;

/// <summary>A type that carries <c>useMissiles</c>, as the static data states it.</summary>
/// <param name="TypeId">The type.</param>
/// <param name="GroupId">Its group.</param>
/// <param name="Published">Whether the type is published.</param>
/// <param name="CategoryId">The category of its group (7 is a ship module; 66 a structure module).</param>
/// <param name="ChargeGroups">The groups of the charges it takes (<c>chargeGroup1</c> to <c>chargeGroup5</c>; zero or missing when unused).</param>
public sealed record LauncherCandidate(int TypeId, int GroupId, bool Published, int CategoryId, IReadOnlyList<int> ChargeGroups);

/// <summary>A charge: its group, whether it is published, and the name of its default effect (null when it has none).</summary>
public sealed record ChargeCandidate(int TypeId, int GroupId, bool Published, string? DefaultEffect);

/// <summary>
/// The launcher family of the missile effects, derived from the static data (not a list of group ids). A launcher belongs to the family when it is a published
/// ship module (category 7) that carries <c>useMissiles</c>, and every published charge in its charge groups that has a default firing effect fires one of the
/// missile effects (<see cref="MissileEffects"/>), with at least one <c>missileLaunching</c>. Groups whose charges have no firing effect are neutral.
/// A launcher with any other kind of charge (defender, bomb, probe, festival, breacher) is not in the family: it is refused by its own reason.
/// </summary>
public static class LauncherFamilies
{
    /// <summary>The effect that a launcher runs (<c>useMissiles</c>).</summary>
    public const string LauncherEffectName = "useMissiles";

    /// <summary>The effect a missile charge fires when it is launched at a target.</summary>
    public const string MissileLaunchingEffect = "missileLaunching";

    /// <summary>The default firing effects of the missile family: plain missiles and the F.O.F. / auto-targeting missiles.</summary>
    public static readonly IReadOnlySet<string> MissileEffects = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { MissileLaunchingEffect, "fofMissileLaunching" };

    /// <summary>The ship-module category of the SDE (a launcher on a ship; the structure modules are category 66).</summary>
    public const int ShipModuleCategory = 7;

    /// <summary>The attributes that name the charge groups a module takes (chargeGroup1 to chargeGroup5).</summary>
    public static readonly int[] ChargeGroupAttributes = [604, 605, 606, 609, 610];

    /// <summary>The groups of the launchers in the missile family.</summary>
    public static IReadOnlySet<int> MissileGroups(IEnumerable<LauncherCandidate> launchers, IEnumerable<ChargeCandidate> charges)
    {
        var firing = charges
            .Where(charge => charge.Published && !string.IsNullOrEmpty(charge.DefaultEffect))
            .GroupBy(charge => charge.GroupId)
            .ToDictionary(group => group.Key, group => group.Select(charge => charge.DefaultEffect!).ToArray());
        var groups = new HashSet<int>();
        foreach (var launcher in launchers)
        {
            if (!launcher.Published || launcher.CategoryId != ShipModuleCategory)
            {
                continue;
            }
            var effects = launcher.ChargeGroups
                .Where(group => group > 0 && firing.ContainsKey(group))
                .SelectMany(group => firing[group])
                .ToArray();
            if (effects.Length > 0 && effects.All(MissileEffects.Contains) && effects.Any(effect => effect.Equals(MissileLaunchingEffect, StringComparison.OrdinalIgnoreCase)))
            {
                groups.Add(launcher.GroupId);
            }
        }
        return groups;
    }
}

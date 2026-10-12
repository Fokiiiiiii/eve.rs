using Eve.Universe;

namespace Eve.Wormholes;

/// <summary>A planned static connection: the static of <see cref="SourceSystemId"/> leads to <see cref="DestinationSystemId"/>.</summary>
public sealed record PlannedConnection(int SourceSystemId, int DestinationSystemId, string Code, int TypeId, double MassKg, ulong Seed);

/// <summary>
/// The static connections of J-space (the documented mechanic: the wiki's "Wormhole Types" and "Wormhole" pages). Each static's
/// exit leads to a random system of the target class of its type (SDE 1381: high-, low- or null-sec for types 7, 8 and 9; a
/// J-space class for the others), chosen among the systems of that class that do not already hold a connection from the source.
/// The choice is seeded: the same world seed gives the same connections. The mass is the type's stable mass times a roll in
/// 90% to 110% (wiki "Wormhole Types": the total is set at birth within that range). Probabilities are not published; the
/// statics are fixed per system (client data and EveJS), so no roll decides whether a static exists.
/// </summary>
public static class WormholePlanner
{
    public const double MassRollLow = 0.9;
    public const double MassRollHigh = 1.1;

    /// <summary>The systems of each class, in ascending ID order.</summary>
    public static IReadOnlyDictionary<int, IReadOnlyList<int>> ByClass(IReadOnlyDictionary<int, int> systemClasses) =>
        systemClasses.GroupBy(pair => pair.Value)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<int>)group.Select(pair => pair.Key).OrderBy(id => id).ToArray());

    /// <summary>
    /// The statics of every J-space system, in system ID then slot order. A static whose type has no target class (-1) or whose
    /// target class has no system is not planned; the caller can compare the counts.
    /// </summary>
    public static IReadOnlyList<PlannedConnection> PlanStatics(IEnumerable<JSpaceSystem> jSystems, IReadOnlyDictionary<int, int> systemClasses,
        IReadOnlyDictionary<int, WormholeType> types, ulong worldSeed)
    {
        var byClass = ByClass(systemClasses);
        var random = new SeededRandom(worldSeed);
        var pairs = new HashSet<(int Source, int Destination)>();
        var planned = new List<PlannedConnection>();
        foreach (var system in jSystems.OrderBy(s => s.Id))
        {
            foreach (var entry in system.Statics)
            {
                if (!types.TryGetValue(entry.TypeId, out var type))
                {
                    continue;
                }
                var destination = PickDestination(system.Id, type.TargetSystemClass, pairs, byClass, random);
                if (destination is null)
                {
                    continue;
                }
                pairs.Add((system.Id, destination.Value));
                planned.Add(new PlannedConnection(system.Id, destination.Value, entry.Code, type.TypeId, RollMass(type, random), random.NextUInt64()));
            }
        }
        return planned;
    }

    /// <summary>
    /// A random system of <paramref name="targetClass"/> that is not the source and does not already hold a connection from it
    /// (<paramref name="pairs"/> holds the (source, destination) pairs). Null when no system is eligible.
    /// </summary>
    public static int? PickDestination(int sourceSystemId, int targetClass, ISet<(int Source, int Destination)> pairs,
        IReadOnlyDictionary<int, IReadOnlyList<int>> byClass, SeededRandom random)
    {
        if (!byClass.TryGetValue(targetClass, out var pool))
        {
            return null;
        }
        var eligible = pool.Where(id => id != sourceSystemId && !pairs.Contains((sourceSystemId, id))).ToArray();
        return eligible.Length == 0 ? null : eligible[random.Next(eligible.Length)];
    }

    /// <summary>The initial mass of a new wormhole of this type, kilograms.</summary>
    public static double RollMass(WormholeType type, SeededRandom random) =>
        type.MaxStableMassKg * (MassRollLow + (MassRollHigh - MassRollLow) * random.NextDouble());
}

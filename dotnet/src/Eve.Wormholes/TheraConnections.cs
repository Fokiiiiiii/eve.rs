using Eve.Universe;

namespace Eve.Wormholes;

/// <summary>
/// time" (tracked by EVE Scout); the "Wormhole Types" table gives the eight types with an end in Thera and the space at their other end.
/// Rule: <see cref="Count"/> connections (12, "a dozen or so"), each of a type drawn with the seed; the free end of the connection is a
/// system of one of the type's classes (the high-, low- or null-sec systems, or the J-space classes the type names), not Thera and not
/// are not published (DEVIATIONS).
/// </summary>
public sealed record TheraType(string Code, int TypeId, bool FromThera, int[] FreeClasses);

public static class TheraConnections
{
    public const int SystemId = 31_000_005;

    public const int Count = 12;

    public static readonly TheraType[] Types =
    [
        new("F135", 34372, FromThera: false, FreeClasses: [2, 3]),
        new("F353", 34371, FromThera: false, FreeClasses: [1]),
        new("L031", 34367, FromThera: false, FreeClasses: [9]),
        new("M164", 34366, FromThera: false, FreeClasses: [8]),
        new("T458", 34338, FromThera: false, FreeClasses: [7]),
        new("Q063", 34368, FromThera: true, FreeClasses: [7]),
        new("V898", 34369, FromThera: true, FreeClasses: [8]),
        new("E587", 34370, FromThera: true, FreeClasses: [9]),
    ];

    /// <summary>The Thera type with this code, or null.</summary>
    public static TheraType? TypeOf(string code) => Array.Find(Types, type => type.Code == code);

    /// <summary>
    /// The ends of one Thera connection of this type, the free end drawn among the systems of its classes that are not Thera and not already
    /// connected to Thera (<paramref name="connected"/> holds the free systems that are). Null when no system is eligible.
    /// </summary>
    public static (int Source, int Destination)? PickEnds(TheraType type, IReadOnlyDictionary<int, IReadOnlyList<int>> byClass,
        ISet<int> connected, SeededRandom random)
    {
        var classes = type.FreeClasses;
        var pool = classes.Where(byClass.ContainsKey).SelectMany(c => byClass[c])
            .Where(id => id != SystemId && !connected.Contains(id)).ToArray();
        if (pool.Length == 0)
        {
            return null;
        }
        var free = pool[random.Next(pool.Length)];
        return type.FromThera ? (SystemId, free) : (free, SystemId);
    }

    /// <summary>The seeded world's Thera connections: <see cref="Count"/> of them (fewer only when no free system is left).</summary>
    public static IReadOnlyList<PlannedConnection> Plan(IReadOnlyDictionary<int, int> systemClasses, IReadOnlyDictionary<int, WormholeType> types, ulong seed)
    {
        var byClass = WormholePlanner.ByClass(systemClasses);
        var random = new SeededRandom(seed);
        var planned = new List<PlannedConnection>();
        var connected = new HashSet<int>();
        for (var i = 0; i < Count; i++)
        {
            var type = Types[random.Next(Types.Length)];
            if (PickEnds(type, byClass, connected, random) is not { } ends || !types.TryGetValue(type.TypeId, out var wormhole))
            {
                continue;
            }
            connected.Add(ends.Source == SystemId ? ends.Destination : ends.Source);
            planned.Add(new PlannedConnection(ends.Source, ends.Destination, type.Code, type.TypeId, WormholePlanner.RollMass(wormhole, random), random.NextUInt64()));
        }
        return planned;
    }
}

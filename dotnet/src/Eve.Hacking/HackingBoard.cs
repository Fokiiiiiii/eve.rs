using System.Collections.ObjectModel;

namespace Eve.Hacking;

/// <summary>A cell of the board. <c>Y</c> is the row; odd rows sit half a cell to the right (the client's layout).</summary>
public readonly record struct Coord(int X, int Y);

public sealed class Tile
{
    public required int Id { get; init; }
    public required Coord Coord { get; init; }
    public int Type { get; set; } = HackingWire.TypeSegment;
    public int Subtype { get; set; } = HackingWire.SubtypeNone;
    public int Coherence { get; set; }
    public int Strength { get; set; }
    public bool Hidden { get; set; } = true;
    /// <summary>An unbroken defensive subsystem: its neighbours cannot be explored from it.</summary>
    public bool Blocked { get; set; }
    /// <summary>What a data cache holds, revealed when it is opened.</summary>
    public (int Type, int Subtype)? Contents { get; set; }
    public int? UtilityAmount { get; set; }
    public int? UtilityDuration { get; set; }
    public int? CacheUtilityAmount { get; set; }
    public int? CacheUtilityDuration { get; set; }
}

/// <summary>The small deterministic random stream used by board generation and its turn effects.</summary>
internal sealed class HackingRandom(int seed)
{
    private uint _state = unchecked((uint)seed) is 0 ? 1u : unchecked((uint)seed);

    public double NextDouble()
    {
        unchecked
        {
            _state += 0x6d2b79f5u;
            var value = _state;
            value = (value ^ (value >> 15)) * (value | 1u);
            value ^= value + ((value ^ (value >> 7)) * (value | 61u));
            value ^= value >> 14;
            return value / 4_294_967_296d;
        }
    }

    public int Next(int maxValue) => (int)(NextDouble() * maxValue);

    public int Next(int minValue, int maxValue) => minValue + Next(maxValue - minValue);
}

/// <summary>The difficulty-tuned board and natural spawn pools used by the functional EveJS reference.</summary>
public sealed record HackingDifficultyProfile(
    int Tier,
    int Columns,
    int Rows,
    int CoreCoherence,
    int CoreStrength,
    int CoreSubtype,
    int FirewallCoherence,
    int FirewallStrength,
    int AntiVirusCoherence,
    int AntiVirusStrength,
    int? RestorationCoherence,
    int? RestorationStrength,
    int? SuppressorCoherence,
    int? SuppressorStrength,
    IReadOnlyList<int> DefenseTypes,
    IReadOnlyList<int> UtilityTypes,
    IReadOnlyList<int> CacheDefenseTypes,
    IReadOnlyList<int> CacheUtilityTypes)
{
    public (int Coherence, int Strength) DefenseStats(int subtype) => subtype switch
    {
        HackingWire.SubtypeDsAntiVirus => (AntiVirusCoherence, AntiVirusStrength),
        HackingWire.SubtypeDsFirewall => (FirewallCoherence, FirewallStrength),
        HackingWire.SubtypeDsRestorationNode when RestorationCoherence is { } coherence && RestorationStrength is { } strength => (coherence, strength),
        HackingWire.SubtypeDsVirusSuppressor when SuppressorCoherence is { } coherence && SuppressorStrength is { } strength => (coherence, strength),
        _ => (80, 20),
    };
}

public sealed class HackingBoard
{
    public const int CoreMinDistance = 8;
    public static readonly Coord Start = new(0, 4);

    private static readonly (int Dx, int Dy)[] OddRowOffsets = [(0, -1), (1, -1), (-1, 0), (1, 0), (0, 1), (1, 1)];
    private static readonly (int Dx, int Dy)[] EvenRowOffsets = [(-1, -1), (0, -1), (-1, 0), (1, 0), (-1, 1), (0, 1)];

    private sealed record Topology(double ExemptChance, double HoleFraction, IReadOnlyList<int> ClusterSizeWeights);

    private static readonly Topology EightByEight = new(0.05, 0.22, Values(1, 1, 1, 1, 10));
    private static readonly Topology NineByNine = new(0.07, 0.24, Values(1, 1, 1, 1, 4));
    private static readonly Topology TenByNine = new(0.07, 0.25, Values(1, 1, 1, 1, 6));

    private static IReadOnlyList<int> Values(params int[] values) => new ReadOnlyCollection<int>(values);

    private static readonly HackingDifficultyProfile[] Profiles =
    [
        new(0, 8, 8, 50, 10, HackingWire.SubtypeCoreLow, 40, 20, 30, 30, null, null, null, null,
            Values(HackingWire.SubtypeDsFirewall), Values(HackingWire.SubtypeUeSelfRepair),
            Values(HackingWire.SubtypeDsFirewall, HackingWire.SubtypeDsAntiVirus),
            Values(HackingWire.SubtypeUeSelfRepair, HackingWire.SubtypeUeKernelRot)),
        new(1, 8, 8, 50, 10, HackingWire.SubtypeCoreLow, 40, 20, 30, 30, null, null, null, null,
            Values(HackingWire.SubtypeDsFirewall), Values(HackingWire.SubtypeUeSelfRepair),
            Values(HackingWire.SubtypeDsFirewall, HackingWire.SubtypeDsAntiVirus),
            Values(HackingWire.SubtypeUeSelfRepair, HackingWire.SubtypeUeKernelRot)),
        new(2, 8, 8, 70, 10, HackingWire.SubtypeCoreLow, 50, 20, 30, 30, null, null, null, null,
            Values(HackingWire.SubtypeDsFirewall), Values(HackingWire.SubtypeUeSelfRepair),
            Values(HackingWire.SubtypeDsFirewall, HackingWire.SubtypeDsAntiVirus),
            Values(HackingWire.SubtypeUeSelfRepair, HackingWire.SubtypeUeKernelRot)),
        new(3, 9, 9, 78, 10, HackingWire.SubtypeCoreMedium, 80, 20, 50, 40, 80, 10, null, null,
            Values(HackingWire.SubtypeDsFirewall, HackingWire.SubtypeDsAntiVirus, HackingWire.SubtypeDsRestorationNode),
            Values(HackingWire.SubtypeUeSelfRepair, HackingWire.SubtypeUeKernelRot, HackingWire.SubtypeUePolymorphicShield),
            Values(HackingWire.SubtypeDsFirewall, HackingWire.SubtypeDsAntiVirus, HackingWire.SubtypeDsRestorationNode),
            Values(HackingWire.SubtypeUeSelfRepair, HackingWire.SubtypeUeKernelRot, HackingWire.SubtypeUePolymorphicShield)),
        new(4, 10, 9, 90, 10, HackingWire.SubtypeCoreHigh, 90, 20, 60, 40, 80, 10, 60, 15,
            Values(HackingWire.SubtypeDsFirewall, HackingWire.SubtypeDsAntiVirus, HackingWire.SubtypeDsRestorationNode, HackingWire.SubtypeDsVirusSuppressor),
            Values(HackingWire.SubtypeUeSelfRepair, HackingWire.SubtypeUeKernelRot, HackingWire.SubtypeUePolymorphicShield, HackingWire.SubtypeUeSecondaryVector),
            Values(HackingWire.SubtypeDsFirewall, HackingWire.SubtypeDsAntiVirus, HackingWire.SubtypeDsRestorationNode, HackingWire.SubtypeDsVirusSuppressor),
            Values(HackingWire.SubtypeUeSelfRepair, HackingWire.SubtypeUeKernelRot, HackingWire.SubtypeUePolymorphicShield, HackingWire.SubtypeUeSecondaryVector)),
        new(5, 10, 9, 120, 10, HackingWire.SubtypeCoreHigh, 100, 20, 70, 40, 90, 10, 70, 15,
            Values(HackingWire.SubtypeDsFirewall, HackingWire.SubtypeDsAntiVirus, HackingWire.SubtypeDsRestorationNode, HackingWire.SubtypeDsVirusSuppressor),
            Values(HackingWire.SubtypeUeSelfRepair, HackingWire.SubtypeUeKernelRot, HackingWire.SubtypeUePolymorphicShield, HackingWire.SubtypeUeSecondaryVector),
            Values(HackingWire.SubtypeDsFirewall, HackingWire.SubtypeDsAntiVirus, HackingWire.SubtypeDsRestorationNode, HackingWire.SubtypeDsVirusSuppressor),
            Values(HackingWire.SubtypeUeSelfRepair, HackingWire.SubtypeUeKernelRot, HackingWire.SubtypeUePolymorphicShield, HackingWire.SubtypeUeSecondaryVector)),
    ];

    public HackingBoard(IEnumerable<Tile> tiles, int columns = HackingWire.GridColumns, int rows = HackingWire.GridRows)
    {
        Tiles = tiles.ToDictionary(t => t.Coord);
        Columns = columns;
        Rows = rows;
    }

    public IReadOnlyDictionary<Coord, Tile> Tiles { get; }
    public int Columns { get; }
    public int Rows { get; }
    internal HackingRandom? Random { get; set; }

    public static HackingDifficultyProfile ProfileFor(int difficulty) => Profiles[Math.Clamp(difficulty, 0, Profiles.Length - 1)];

    /// <summary>Every cell hidden except the start, which is an explored empty segment.</summary>
    public static HackingBoard Blank() => Blank(HackingWire.GridColumns, HackingWire.GridRows);

    public static HackingBoard Blank(int columns, int rows)
    {
        if (columns < 1 || rows <= Start.Y || columns <= Start.X)
            throw new ArgumentOutOfRangeException(nameof(columns), "a hacking board must contain the client's start cell");

        var tiles = new List<Tile>(columns * rows);
        for (var y = 0; y < rows; y++)
            for (var x = 0; x < columns; x++)
                tiles.Add(new Tile { Id = tiles.Count + 1, Coord = new Coord(x, y) });
        var board = new HackingBoard(tiles, columns, rows);
        board.Tiles[Start].Hidden = false;
        return board;
    }

    public static bool InBounds(Coord c, int columns = HackingWire.GridColumns, int rows = HackingWire.GridRows) =>
        c.X >= 0 && c.X < columns && c.Y >= 0 && c.Y < rows;

    public static IEnumerable<Coord> Neighbours(Coord c, int columns = HackingWire.GridColumns, int rows = HackingWire.GridRows)
    {
        foreach (var (dx, dy) in c.Y % 2 != 0 ? OddRowOffsets : EvenRowOffsets)
        {
            var n = new Coord(c.X + dx, c.Y + dy);
            if (InBounds(n, columns, rows))
                yield return n;
        }
    }

    /// <summary>Steps between two cells over the grid, ignoring contents.</summary>
    public static Dictionary<Coord, int> DistancesFrom(Coord from, int columns = HackingWire.GridColumns, int rows = HackingWire.GridRows)
    {
        var distance = new Dictionary<Coord, int> { [from] = 0 };
        var queue = new Queue<Coord>();
        queue.Enqueue(from);
        while (queue.Count > 0)
        {
            var c = queue.Dequeue();
            foreach (var n in Neighbours(c, columns, rows))
            {
                if (distance.ContainsKey(n))
                    continue;
                distance[n] = distance[c] + 1;
                queue.Enqueue(n);
            }
        }
        return distance;
    }

    /// <summary>Steps between cells using only the tiles that exist on this generated board.</summary>
    public static Dictionary<Coord, int> DistancesThrough(Coord from, IEnumerable<Coord> existingTiles, int columns, int rows)
    {
        var existing = existingTiles.ToHashSet();
        var distance = new Dictionary<Coord, int> { [from] = 0 };
        var queue = new Queue<Coord>();
        queue.Enqueue(from);
        while (queue.Count > 0)
        {
            var coord = queue.Dequeue();
            foreach (var neighbour in Neighbours(coord, columns, rows))
            {
                if (!existing.Contains(neighbour) || distance.ContainsKey(neighbour))
                    continue;
                distance[neighbour] = distance[coord] + 1;
                queue.Enqueue(neighbour);
            }
        }
        return distance;
    }

    public static HackingBoard Generate(int difficulty, int seed)
    {
        var profile = ProfileFor(difficulty);
        var rng = new HackingRandom(seed);
        var fullGrid = new List<Tile>(profile.Columns * profile.Rows);
        for (var y = 0; y < profile.Rows; y++)
            for (var x = 0; x < profile.Columns; x++)
                fullGrid.Add(new Tile { Id = y * profile.Columns + x + 1, Coord = new Coord(x, y) });

        var topology = TopologyFor(profile);
        var generated = DigPresentTiles(fullGrid, profile.Columns, profile.Rows, topology, rng);
        var minimumCells = (int)Math.Ceiling(profile.Columns * profile.Rows * 0.6);
        var attempts = 1;
        while (generated.Count < minimumCells && attempts < 32)
        {
            generated = DigPresentTiles(fullGrid, profile.Columns, profile.Rows, topology, rng);
            attempts++;
        }

        var tiles = generated.Count < minimumCells ? fullGrid : generated;
        var board = new HackingBoard(tiles, profile.Columns, profile.Rows) { Random = rng };
        var nodeByCoord = board.Tiles;
        var neighboursByCoord = nodeByCoord.Keys.ToDictionary(
            coord => coord,
            coord => NeighbourCoords(coord, tiles, profile.Columns, profile.Rows));
        var neighbourCount = neighboursByCoord.ToDictionary(pair => pair.Key, pair => pair.Value.Count);
        var rim = tiles.Where(tile => tile.Coord.X == 0 || tile.Coord.X == profile.Columns - 1
            || tile.Coord.Y == 0 || tile.Coord.Y == profile.Rows - 1).ToList();
        var viableRim = rim.Where(tile => neighbourCount[tile.Coord] >= 2).ToList();
        var startPool = viableRim.Count > 0 ? viableRim : rim.Count > 0 ? rim : tiles;
        var fewestStartNeighbours = startPool.Min(tile => neighbourCount[tile.Coord]);
        var start = Pick(startPool.Where(tile => neighbourCount[tile.Coord] == fewestStartNeighbours).ToList(), rng);
        start.Hidden = false;

        var fullDistances = DistancesFrom(start.Coord, profile.Columns, profile.Rows);
        var far = tiles.Where(tile => tile != start && fullDistances[tile.Coord] >= CoreMinDistance).ToList();
        var core = Pick(far.Count >= 3 ? far : tiles.Where(tile => tile != start).ToList(), rng);
        core.Type = HackingWire.TypeCore;
        core.Subtype = profile.CoreSubtype;
        core.Coherence = profile.CoreCoherence;
        core.Strength = profile.CoreStrength;

        var occupied = new HashSet<Coord> { start.Coord, core.Coord };
        var startNeighbours = neighboursByCoord[start.Coord].ToHashSet();
        var eligible = tiles.Where(tile => !occupied.Contains(tile.Coord) && neighbourCount[tile.Coord] < 6).ToList();
        var defencePool = eligible.Where(tile => !startNeighbours.Contains(tile.Coord)).ToList();
        var cachePool = defencePool.Where(tile => neighbourCount[tile.Coord] <= 3).ToList();

        List<Tile> Take(IReadOnlyList<Tile> pool, int count)
        {
            var available = pool.Where(tile => !occupied.Contains(tile.Coord)).ToList();
            var selected = new List<Tile>();
            while (selected.Count < count && available.Count > 0)
            {
                var index = rng.Next(available.Count);
                var node = available[index];
                available.RemoveAt(index);
                occupied.Add(node.Coord);
                selected.Add(node);
            }
            return selected;
        }

        var defenseCount = defencePool.Count / 15 + 1;
        foreach (var tile in Take(defencePool, defenseCount))
        {
            tile.Type = HackingWire.TypeDefenseSoftware;
            tile.Subtype = Pick(profile.DefenseTypes, rng);
            ApplyDefenceStats(tile, profile.Tier);
        }

        var guardPool = neighboursByCoord[core.Coord].Select(coord => nodeByCoord[coord])
            .Where(tile => !occupied.Contains(tile.Coord) && neighbourCount[tile.Coord] < 6
                && !startNeighbours.Contains(tile.Coord)).ToList();
        if (guardPool.Count > 0)
        {
            var guard = Pick(guardPool, rng);
            occupied.Add(guard.Coord);
            guard.Type = HackingWire.TypeDefenseSoftware;
            guard.Subtype = Pick(profile.DefenseTypes, rng);
            ApplyDefenceStats(guard, profile.Tier);
        }

        var populationCount = eligible.Count / 15 + 1;
        var utilities = Take(eligible, populationCount);
        foreach (var tile in utilities)
        {
            tile.Type = HackingWire.TypeUtilityElementTile;
            tile.Subtype = Pick(profile.UtilityTypes, rng);
        }
        var caches = Take(cachePool, populationCount);
        foreach (var tile in caches)
            tile.Type = HackingWire.TypeDataCache;

        foreach (var tile in utilities)
            (tile.UtilityAmount, tile.UtilityDuration) = UtilityValues(tile.Subtype, rng);
        foreach (var tile in caches)
        {
            if (rng.NextDouble() < 0.5)
            {
                var subtype = Pick(profile.CacheUtilityTypes, rng);
                tile.Contents = (HackingWire.TypeUtilityElementTile, subtype);
                (tile.CacheUtilityAmount, tile.CacheUtilityDuration) = UtilityValues(subtype, rng);
            }
            else
            {
                var subtype = Pick(profile.CacheDefenseTypes, rng);
                tile.Contents = (HackingWire.TypeDefenseSoftware, subtype);
            }
        }
        return board;
    }

    /// <summary>Defensive subsystems the selected profile places directly.</summary>
    public static int[] NaturalDefence(int tier) => ProfileFor(tier).DefenseTypes.ToArray();

    public static int[] NaturalUtility(int tier) => ProfileFor(tier).UtilityTypes.ToArray();

    public static void ApplyDefenceStats(Tile tile, int difficulty, bool fromDataCache = false)
    {
        var (coherence, strength) = ProfileFor(difficulty).DefenseStats(tile.Subtype);
        if (fromDataCache && tile.Subtype is HackingWire.SubtypeDsAntiVirus or HackingWire.SubtypeDsFirewall)
            coherence += 10;
        (tile.Coherence, tile.Strength) = (coherence, strength);
    }

    private static Topology TopologyFor(HackingDifficultyProfile profile) => profile.Columns switch
    {
        8 => EightByEight,
        9 => NineByNine,
        _ => TenByNine,
    };

    private static List<Coord> NeighbourCoords(Coord coord, IReadOnlyList<Tile> orderedTiles, int columns, int rows)
    {
        var geometricNeighbours = Neighbours(coord, columns, rows).ToHashSet();
        return orderedTiles.Where(tile => geometricNeighbours.Contains(tile.Coord)).Select(tile => tile.Coord).ToList();
    }

    private static List<Tile> DigPresentTiles(
        IReadOnlyList<Tile> fullGrid, int columns, int rows, Topology topology, HackingRandom rng)
    {
        var fullByCoord = fullGrid.ToDictionary(tile => tile.Coord);
        var adjacent = fullGrid.ToDictionary(
            tile => tile.Coord,
            tile => NeighbourCoords(tile.Coord, fullGrid, columns, rows));
        var removed = new HashSet<Coord>();

        bool CanRemove(Coord coord, HashSet<Coord> cluster)
        {
            if (rng.NextDouble() < topology.ExemptChance)
                return true;
            foreach (var neighbour in adjacent[coord])
            {
                if (removed.Contains(neighbour) || cluster.Contains(neighbour))
                    continue;
                var alive = adjacent[neighbour].Count(other => !removed.Contains(other) && !cluster.Contains(other));
                if (alive - 1 < 3)
                    return false;
            }
            return true;
        }

        int RollClusterSize()
        {
            var totalWeight = topology.ClusterSizeWeights.Sum();
            var roll = rng.NextDouble() * totalWeight;
            for (var index = 0; index < topology.ClusterSizeWeights.Count; index++)
            {
                roll -= topology.ClusterSizeWeights[index];
                if (roll < 0)
                    return index + 1;
            }
            return 5;
        }

        var target = (int)Math.Floor(fullGrid.Count * topology.HoleFraction);
        for (var guard = 0; removed.Count < target && guard < 20_000; guard++)
        {
            var seeds = fullGrid.Where(tile => !removed.Contains(tile.Coord) && CanRemove(tile.Coord, [])).ToList();
            if (seeds.Count == 0)
                break;
            var seed = Pick(seeds, rng);
            var cluster = new HashSet<Coord> { seed.Coord };
            var frontier = adjacent[seed.Coord].Where(coord => !removed.Contains(coord)).ToList();
            var wanted = RollClusterSize();
            while (cluster.Count < wanted && frontier.Count > 0)
            {
                var candidates = frontier.Where(coord => !removed.Contains(coord) && !cluster.Contains(coord)
                    && CanRemove(coord, cluster)).ToList();
                if (candidates.Count == 0)
                    break;
                var bestTouching = candidates.Max(coord => adjacent[coord].Count(cluster.Contains));
                candidates = candidates.Where(coord => adjacent[coord].Count(cluster.Contains) == bestTouching).ToList();
                var chosen = Pick(candidates, rng);
                cluster.Add(chosen);
                frontier = frontier.Where(coord => !cluster.Contains(coord)).ToList();
                foreach (var neighbour in adjacent[chosen])
                    if (!removed.Contains(neighbour) && !cluster.Contains(neighbour))
                        frontier.Add(neighbour);
            }
            removed.UnionWith(cluster);
        }

        var present = fullGrid.Where(tile => !removed.Contains(tile.Coord)).ToList();
        var presentCoords = present.Select(tile => tile.Coord).ToHashSet();
        var visited = new HashSet<Coord>();
        var largest = new List<Tile>();
        foreach (var tile in present)
        {
            if (!visited.Add(tile.Coord))
                continue;
            var component = new List<Tile> { tile };
            for (var index = 0; index < component.Count; index++)
            {
                foreach (var neighbour in adjacent[component[index].Coord])
                {
                    if (!presentCoords.Contains(neighbour) || !visited.Add(neighbour))
                        continue;
                    component.Add(fullByCoord[neighbour]);
                }
            }
            if (component.Count > largest.Count)
                largest = component;
        }
        return largest;
    }

    private static T Pick<T>(IReadOnlyList<T> items, HackingRandom rng) => items[rng.Next(items.Count)];

    private static (int Amount, int Duration) UtilityValues(int subtype, HackingRandom rng) =>
        (subtype == HackingWire.SubtypeUeSelfRepair ? 5 + rng.Next(6)
            : subtype == HackingWire.SubtypeUeKernelRot ? 50
            : subtype == HackingWire.SubtypeUeSecondaryVector ? 20 : 2,
         subtype is HackingWire.SubtypeUeSelfRepair or HackingWire.SubtypeUeSecondaryVector ? 3
            : subtype == HackingWire.SubtypeUePolymorphicShield ? 2 : 1);
}

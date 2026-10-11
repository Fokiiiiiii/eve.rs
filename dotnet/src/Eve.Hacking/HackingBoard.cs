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
}

/// <summary>
/// The grid of one attempt. <see cref="Generate"/> places the content by the EVE University wiki rules (Rule of 8,
/// Rule of Six, the difficulty tiers); the sizes and the defensive subsystem stats the wiki does not give are the
/// constants below (decisions, recorded in DEVIATIONS).
/// </summary>
public sealed class HackingBoard
{
    public const int CoreMinDistance = 8;
    public static readonly Coord Start = new(0, 4);

    private static readonly (int Dx, int Dy)[] OddRowOffsets = [(0, -1), (1, -1), (-1, 0), (1, 0), (0, 1), (1, 1)];
    private static readonly (int Dx, int Dy)[] EvenRowOffsets = [(-1, -1), (0, -1), (-1, 0), (1, 0), (-1, 1), (0, 1)];

    public HackingBoard(IEnumerable<Tile> tiles)
    {
        Tiles = tiles.ToDictionary(t => t.Coord);
    }

    public IReadOnlyDictionary<Coord, Tile> Tiles { get; }

    /// <summary>Every cell hidden except the start, which is an explored empty segment.</summary>
    public static HackingBoard Blank()
    {
        var tiles = new List<Tile>();
        for (var y = 0; y < HackingWire.GridRows; y++)
            for (var x = 0; x < HackingWire.GridColumns; x++)
                tiles.Add(new Tile { Id = tiles.Count + 1, Coord = new Coord(x, y) });
        var board = new HackingBoard(tiles);
        board.Tiles[Start].Hidden = false;
        return board;
    }

    public static bool InBounds(Coord c) =>
        c.X >= 0 && c.X < HackingWire.GridColumns && c.Y >= 0 && c.Y < HackingWire.GridRows;

    public static IEnumerable<Coord> Neighbours(Coord c)
    {
        foreach (var (dx, dy) in c.Y % 2 != 0 ? OddRowOffsets : EvenRowOffsets)
        {
            var n = new Coord(c.X + dx, c.Y + dy);
            if (InBounds(n))
                yield return n;
        }
    }

    /// <summary>Steps between two cells over the grid, ignoring contents.</summary>
    public static Dictionary<Coord, int> DistancesFrom(Coord from)
    {
        var distance = new Dictionary<Coord, int> { [from] = 0 };
        var queue = new Queue<Coord>();
        queue.Enqueue(from);
        while (queue.Count > 0)
        {
            var c = queue.Dequeue();
            foreach (var n in Neighbours(c))
            {
                if (distance.ContainsKey(n))
                    continue;
                distance[n] = distance[c] + 1;
                queue.Enqueue(n);
            }
        }
        return distance;
    }

    public static HackingBoard Generate(int difficulty, int seed)
    {
        var rng = new Random(seed);
        var tier = Math.Clamp(difficulty, 1, 4);
        var board = Blank();
        var cells = board.Tiles.Values.OrderBy(t => t.Id).Select(t => t.Coord).ToList();

        var fromStart = DistancesFrom(Start);
        var far = cells.Where(c => c != Start && fromStart[c] >= CoreMinDistance).ToList();
        var core = Pick(far.Count > 0 ? far : cells.Where(c => c != Start).ToList(), rng);
        var coreTile = board.Tiles[core];
        coreTile.Type = HackingWire.TypeCore;
        coreTile.Subtype = HackingWire.SubtypeCoreLow;
        coreTile.Coherence = coreTile.Strength = Math.Max(1, difficulty) * 10;

        // Rule of Six: a complete node (six neighbours) holds no defensive subsystem unless the core is adjacent to it.
        var used = new HashSet<Coord> { Start, core };
        var defenceCells = cells.Where(c => !used.Contains(c) && (Neighbours(c).Count() != 6 || Neighbours(c).Contains(core))).ToList();
        var naturalDefence = NaturalDefence(tier);
        foreach (var c in Shuffle(defenceCells, rng).Take(2 + tier))
        {
            used.Add(c);
            var tile = board.Tiles[c];
            tile.Type = HackingWire.TypeDefenseSoftware;
            tile.Subtype = naturalDefence[rng.Next(naturalDefence.Length)];
            ApplyDefenceStats(tile);
        }

        var free = Shuffle(cells.Where(c => !used.Contains(c)).ToList(), rng);
        var naturalUtility = NaturalUtility(tier);
        foreach (var c in free.Take(3))
        {
            var tile = board.Tiles[c];
            tile.Type = HackingWire.TypeUtilityElementTile;
            tile.Subtype = naturalUtility[rng.Next(naturalUtility.Length)];
        }

        var cacheDefence = tier <= 2 ? new[] { HackingWire.SubtypeDsFirewall, HackingWire.SubtypeDsAntiVirus } : naturalDefence;
        var cacheUtility = new[]
        {
            HackingWire.SubtypeUeSelfRepair, HackingWire.SubtypeUeKernelRot,
            HackingWire.SubtypeUePolymorphicShield, HackingWire.SubtypeUeSecondaryVector,
        };
        foreach (var c in free.Skip(3).Take(2))
        {
            var tile = board.Tiles[c];
            tile.Type = HackingWire.TypeDataCache;
            tile.Contents = rng.Next(2) == 0
                ? (HackingWire.TypeDefenseSoftware, cacheDefence[rng.Next(cacheDefence.Length)])
                : (HackingWire.TypeUtilityElementTile, cacheUtility[rng.Next(cacheUtility.Length)]);
        }
        return board;
    }

    /// <summary>Defensive subsystems the difficulty tier places directly (Anti-Virus only from data caches before Medium).</summary>
    public static int[] NaturalDefence(int tier) => tier switch
    {
        <= 2 => [HackingWire.SubtypeDsFirewall],
        3 => [HackingWire.SubtypeDsFirewall, HackingWire.SubtypeDsAntiVirus, HackingWire.SubtypeDsRestorationNode],
        _ => [HackingWire.SubtypeDsFirewall, HackingWire.SubtypeDsAntiVirus, HackingWire.SubtypeDsRestorationNode, HackingWire.SubtypeDsVirusSuppressor],
    };

    public static int[] NaturalUtility(int tier) => tier switch
    {
        <= 2 => [HackingWire.SubtypeUeSelfRepair],
        3 => [HackingWire.SubtypeUeSelfRepair, HackingWire.SubtypeUeKernelRot, HackingWire.SubtypeUePolymorphicShield],
        _ => [HackingWire.SubtypeUeSelfRepair, HackingWire.SubtypeUeKernelRot, HackingWire.SubtypeUePolymorphicShield, HackingWire.SubtypeUeSecondaryVector],
    };

    /// <summary>Coherence and strength by subtype. Decision: the wiki gives only the relative shape (firewall high coherence, anti-virus high strength).</summary>
    public static void ApplyDefenceStats(Tile tile)
    {
        (tile.Coherence, tile.Strength) = tile.Subtype switch
        {
            HackingWire.SubtypeDsFirewall => (60, 10),
            HackingWire.SubtypeDsAntiVirus => (20, 40),
            _ => (30, 10),
        };
    }

    private static T Pick<T>(IReadOnlyList<T> items, Random rng) => items[rng.Next(items.Count)];

    private static List<T> Shuffle<T>(List<T> items, Random rng)
    {
        var list = new List<T>(items);
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
        return list;
    }
}

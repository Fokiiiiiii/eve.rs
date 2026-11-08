using Eve.Hacking;

namespace Eve.Hacking.Tests;

public sealed class HackingBoardTests
{
    private static string Signature(HackingBoard board) => string.Join(";", board.Tiles.Values
        .OrderBy(t => t.Id)
        .Select(t => $"{t.Coord.X},{t.Coord.Y}:{t.Type}:{t.Subtype}:{t.Coherence}:{t.Strength}:{t.Contents}"));

    private static Coord CoreOf(HackingBoard board) =>
        board.Tiles.Values.Single(t => t.Type == HackingWire.TypeCore).Coord;

    [Fact]
    public void A_seed_gives_the_same_board_and_another_seed_a_different_one()
    {
        Assert.Equal(Signature(HackingBoard.Generate(2, 11)), Signature(HackingBoard.Generate(2, 11)));
        Assert.NotEqual(Signature(HackingBoard.Generate(2, 11)), Signature(HackingBoard.Generate(2, 12)));
    }

    [Theory]
    [InlineData(0, 8, 8, 50, 10, 11)]
    [InlineData(1, 8, 8, 50, 10, 11)]
    [InlineData(2, 8, 8, 70, 10, 11)]
    [InlineData(3, 9, 9, 78, 10, 12)]
    [InlineData(4, 10, 9, 90, 10, 10)]
    [InlineData(5, 10, 9, 120, 10, 10)]
    public void Tier_profiles_set_the_wire_board_size_and_core_properties(
        int tier, int columns, int rows, int coherence, int strength, int subtype)
    {
        var board = HackingBoard.Generate(tier, seed: 2_026);
        var core = board.Tiles.Values.Single(tile => tile.Type == HackingWire.TypeCore);

        Assert.Equal((columns, rows), (board.Columns, board.Rows));
        Assert.All(board.Tiles.Keys, coord => Assert.True(HackingBoard.InBounds(coord, columns, rows)));
        Assert.InRange(board.Tiles.Count, (int)Math.Ceiling(columns * rows * 0.6), columns * rows);
        Assert.Equal((coherence, strength, subtype), (core.Coherence, core.Strength, core.Subtype));
    }

    [Theory]
    [InlineData(0, 50, 7, 1, 1, 4, 34, 39, 4, 3, 3)]
    [InlineData(1, 50, 7, 1, 1, 4, 34, 39, 4, 3, 3)]
    [InlineData(2, 50, 7, 1, 1, 4, 34, 39, 4, 3, 3)]
    [InlineData(3, 62, 0, 2, 8, 2, 27, 48, 5, 4, 4)]
    [InlineData(4, 65, 0, 5, 8, 6, 69, 54, 4, 3, 3)]
    [InlineData(5, 65, 0, 5, 8, 6, 69, 54, 4, 3, 3)]
    public void Seed_2026_matches_the_EveJS_reference_board_for_each_tier(
        int tier, int tileCount, int startX, int startY, int coreX, int coreY, int coreId,
        int segments, int defenses, int utilities, int caches)
    {
        var board = HackingBoard.Generate(tier, seed: 2_026);
        var core = board.Tiles.Values.Single(tile => tile.Type == HackingWire.TypeCore);

        Assert.Equal(tileCount, board.Tiles.Count);
        Assert.Equal(new Coord(startX, startY), Assert.Single(board.Tiles.Values, tile => !tile.Hidden).Coord);
        Assert.Equal(new Coord(coreX, coreY), core.Coord);
        Assert.Equal(coreId, core.Id);
        Assert.Equal(segments, board.Tiles.Values.Count(tile => tile.Type == HackingWire.TypeSegment));
        Assert.Equal(defenses, board.Tiles.Values.Count(tile => tile.Type == HackingWire.TypeDefenseSoftware));
        Assert.Equal(utilities, board.Tiles.Values.Count(tile => tile.Type == HackingWire.TypeUtilityElementTile));
        Assert.Equal(caches, board.Tiles.Values.Count(tile => tile.Type == HackingWire.TypeDataCache));
    }

    [Fact]
    public void Seeded_easy_board_matches_the_reference_envelope_and_population()
    {
        var board = HackingBoard.Generate(2, seed: 2_026);
        var core = board.Tiles.Values.Single(tile => tile.Type == HackingWire.TypeCore);

        Assert.Equal(50, board.Tiles.Count);
        Assert.Equal(new Coord(7, 1), Assert.Single(board.Tiles.Values, tile => !tile.Hidden).Coord);
        Assert.Equal(new Coord(1, 4), core.Coord);
        Assert.Equal(34, core.Id);
        Assert.Equal((70, 10, HackingWire.SubtypeCoreLow), (core.Coherence, core.Strength, core.Subtype));
        Assert.Equal(39, board.Tiles.Values.Count(tile => tile.Type == HackingWire.TypeSegment));
        Assert.Equal(4, board.Tiles.Values.Count(tile => tile.Type == HackingWire.TypeDefenseSoftware));
        Assert.Equal(3, board.Tiles.Values.Count(tile => tile.Type == HackingWire.TypeUtilityElementTile));
        Assert.Equal(3, board.Tiles.Values.Count(tile => tile.Type == HackingWire.TypeDataCache));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Tier_profiles_keep_the_reference_defence_stats_and_spawn_pools(int tier)
    {
        var profile = HackingBoard.ProfileFor(tier);
        var expectedDefenses = tier <= 2
            ? new[] { HackingWire.SubtypeDsFirewall }
            : tier == 3
                ? new[] { HackingWire.SubtypeDsFirewall, HackingWire.SubtypeDsAntiVirus, HackingWire.SubtypeDsRestorationNode }
                : new[] { HackingWire.SubtypeDsFirewall, HackingWire.SubtypeDsAntiVirus, HackingWire.SubtypeDsRestorationNode, HackingWire.SubtypeDsVirusSuppressor };
        var expectedUtilities = tier <= 2
            ? new[] { HackingWire.SubtypeUeSelfRepair }
            : tier == 3
                ? new[] { HackingWire.SubtypeUeSelfRepair, HackingWire.SubtypeUeKernelRot, HackingWire.SubtypeUePolymorphicShield }
                : new[] { HackingWire.SubtypeUeSelfRepair, HackingWire.SubtypeUeKernelRot, HackingWire.SubtypeUePolymorphicShield, HackingWire.SubtypeUeSecondaryVector };
        Assert.Equal(expectedDefenses, profile.DefenseTypes);
        Assert.Equal(expectedUtilities, profile.UtilityTypes);
        Assert.Equal(tier <= 2 ? [HackingWire.SubtypeDsFirewall, HackingWire.SubtypeDsAntiVirus] : expectedDefenses, profile.CacheDefenseTypes);
        Assert.Equal(tier <= 2
            ? [HackingWire.SubtypeUeSelfRepair, HackingWire.SubtypeUeKernelRot]
            : expectedUtilities, profile.CacheUtilityTypes);

        var expectedFirewall = tier <= 1 ? (40, 20) : tier == 2 ? (50, 20) : tier == 3 ? (80, 20) : tier == 4 ? (90, 20) : (100, 20);
        var expectedAntiVirus = tier <= 2 ? (30, 30) : tier == 3 ? (50, 40) : tier == 4 ? (60, 40) : (70, 40);
        Assert.Equal(expectedFirewall, profile.DefenseStats(HackingWire.SubtypeDsFirewall));
        Assert.Equal(expectedAntiVirus, profile.DefenseStats(HackingWire.SubtypeDsAntiVirus));

        foreach (var seed in Enumerable.Range(1, 8))
        {
            var board = HackingBoard.Generate(tier, seed);
            Assert.All(board.Tiles.Values.Where(tile => tile.Type == HackingWire.TypeDefenseSoftware), tile =>
            {
                Assert.Contains(tile.Subtype, expectedDefenses);
                Assert.Equal(profile.DefenseStats(tile.Subtype), (tile.Coherence, tile.Strength));
            });
            Assert.All(board.Tiles.Values.Where(tile => tile.Type == HackingWire.TypeUtilityElementTile), tile =>
                Assert.Contains(tile.Subtype, expectedUtilities));
            Assert.All(board.Tiles.Values.Where(tile => tile.Contents is not null), tile =>
            {
                var contents = tile.Contents!.Value;
                if (contents.Type == HackingWire.TypeDefenseSoftware)
                    Assert.Contains(contents.Subtype, profile.CacheDefenseTypes);
                else
                    Assert.Contains(contents.Subtype, profile.CacheUtilityTypes);
            });
        }
    }

    [Fact]
    public void Rule_of_eight_uses_far_cells_and_the_reference_fallback_for_every_seed()
    {
        for (var seed = 1; seed <= 200; seed++)
        {
            var board = HackingBoard.Generate(seed % 4 + 1, seed);
            var start = Assert.Single(board.Tiles.Values, tile => !tile.Hidden);
            var distances = HackingBoard.DistancesFrom(start.Coord, board.Columns, board.Rows);
            var farTiles = board.Tiles.Values.Where(tile => tile != start
                && distances[tile.Coord] >= HackingBoard.CoreMinDistance).ToList();
            var core = CoreOf(board);

            if (farTiles.Count >= 3)
                Assert.Contains(farTiles, tile => tile.Coord == core);
            else
                Assert.NotEqual(start.Coord, core);
        }
    }

    [Fact]
    public void Rule_of_six_keeps_defence_off_complete_nodes_unless_the_core_is_next_to_them()
    {
        for (var seed = 1; seed <= 200; seed++)
        {
            var board = HackingBoard.Generate(seed % 4 + 1, seed);
            var core = CoreOf(board);
            foreach (var tile in board.Tiles.Values.Where(t => t.Type == HackingWire.TypeDefenseSoftware))
            {
                var neighbours = HackingBoard.Neighbours(tile.Coord, board.Columns, board.Rows)
                    .Where(board.Tiles.ContainsKey).ToList();
                if (neighbours.Count == 6)
                    Assert.Contains(core, neighbours);
            }
        }
    }

    [Fact]
    public void Generated_populations_follow_the_reference_placement_rules()
    {
        for (var tier = 0; tier <= 5; tier++)
        for (var seed = 1; seed <= 50; seed++)
        {
            var board = HackingBoard.Generate(tier, seed);
            var tiles = board.Tiles.Values.ToList();
            var start = Assert.Single(tiles, tile => !tile.Hidden);
            var core = Assert.Single(tiles, tile => tile.Type == HackingWire.TypeCore);
            var degree = tiles.ToDictionary(tile => tile.Coord,
                tile => HackingBoard.Neighbours(tile.Coord, board.Columns, board.Rows).Count(board.Tiles.ContainsKey));
            var startNeighbours = HackingBoard.Neighbours(start.Coord, board.Columns, board.Rows).ToHashSet();
            var eligible = tiles.Where(tile => tile != start && tile != core && degree[tile.Coord] < 6).ToList();
            var defensePool = eligible.Where(tile => !startNeighbours.Contains(tile.Coord)).ToList();
            var cachePool = defensePool.Where(tile => degree[tile.Coord] <= 3).ToHashSet();
            var plannedDensity = eligible.Count / 15 + 1;
            var defenses = tiles.Where(tile => tile.Type == HackingWire.TypeDefenseSoftware).ToList();
            var utilities = tiles.Where(tile => tile.Type == HackingWire.TypeUtilityElementTile).ToList();
            var caches = tiles.Where(tile => tile.Type == HackingWire.TypeDataCache).ToList();

            Assert.All(defenses, tile => Assert.Contains(tile, defensePool));
            Assert.InRange(defenses.Count, Math.Min(plannedDensity, defensePool.Count), Math.Min(plannedDensity, defensePool.Count) + 1);
            Assert.Equal(Math.Min(plannedDensity, eligible.Count - defenses.Count), utilities.Count);
            Assert.Equal(Math.Min(plannedDensity, cachePool.Count(poolTile =>
                !defenses.Contains(poolTile) && !utilities.Contains(poolTile))), caches.Count);
            Assert.All(defenses, tile => Assert.Contains(tile.Subtype, HackingBoard.ProfileFor(tier).DefenseTypes));
            Assert.All(utilities, tile => Assert.Contains(tile.Subtype, HackingBoard.ProfileFor(tier).UtilityTypes));
        }
    }

    [Fact]
    public void The_hardest_tier_places_suppressors_and_secondary_vectors_somewhere_in_fifty_seeds()
    {
        var boards = Enumerable.Range(1, 50).Select(seed => HackingBoard.Generate(4, seed)).ToList();
        Assert.Contains(boards, b => b.Tiles.Values.Any(t =>
            t.Type == HackingWire.TypeDefenseSoftware && t.Subtype == HackingWire.SubtypeDsVirusSuppressor));
        Assert.Contains(boards, b => b.Tiles.Values.Any(t =>
            t.Contents is (_, HackingWire.SubtypeUeSecondaryVector) || (t.Type == HackingWire.TypeUtilityElementTile && t.Subtype == HackingWire.SubtypeUeSecondaryVector)));
    }
}

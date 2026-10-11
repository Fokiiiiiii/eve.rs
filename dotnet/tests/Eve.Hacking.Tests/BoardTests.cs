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

    [Fact]
    public void Rule_of_eight_puts_the_core_at_least_eight_steps_from_the_start_for_every_seed()
    {
        for (var seed = 1; seed <= 200; seed++)
        {
            var board = HackingBoard.Generate(seed % 4 + 1, seed);
            var steps = HackingBoard.DistancesFrom(HackingBoard.Start)[CoreOf(board)];
            Assert.True(steps >= HackingBoard.CoreMinDistance, $"seed {seed}: core {steps} steps from the start");
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
                var neighbours = HackingBoard.Neighbours(tile.Coord).ToList();
                if (neighbours.Count == 6)
                    Assert.Contains(core, neighbours);
            }
        }
    }

    [Fact]
    public void Tier_one_places_only_firewalls_and_the_counts_follow_the_tier()
    {
        for (var seed = 1; seed <= 50; seed++)
        {
            var easy = HackingBoard.Generate(1, seed);
            Assert.All(easy.Tiles.Values.Where(t => t.Type == HackingWire.TypeDefenseSoftware),
                t => Assert.Equal(HackingWire.SubtypeDsFirewall, t.Subtype));

            var medium = HackingBoard.Generate(2, seed).Tiles.Values;
            Assert.Equal(4, medium.Count(t => t.Type == HackingWire.TypeDefenseSoftware));
            Assert.Equal(3, medium.Count(t => t.Type == HackingWire.TypeUtilityElementTile));
            Assert.Equal(2, medium.Count(t => t.Type == HackingWire.TypeDataCache));
            Assert.Equal(1, medium.Count(t => t.Type == HackingWire.TypeCore));
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

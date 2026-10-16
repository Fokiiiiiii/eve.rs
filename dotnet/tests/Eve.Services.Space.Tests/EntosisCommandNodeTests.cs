using Eve.Services.Space.Entosis;

namespace Eve.Services.Space.Tests;

/// <summary>ST-17 a, the command node rules (pure): the tally's outcomes, a node's progress with its links, and the completion time.</summary>
public sealed class EntosisCommandNodeTests
{
    private const long Start = 1_700_000_000_000;

    private static EntosisLink Link(EntosisSide side, long at) => new(1, 9001, 7001, side == EntosisSide.Attacker ? 5 : 7, side, at, at);

    [Fact]
    public void Nine_attacker_captures_or_six_defender_captures_decide_the_stage()
    {
        Assert.Equal(CommandNodeOutcome.Open, CommandNodeRules.Outcome(8, 0));
        Assert.Equal(CommandNodeOutcome.AttackersWon, CommandNodeRules.Outcome(9, 0));
        Assert.Equal(CommandNodeOutcome.Open, CommandNodeRules.Outcome(0, 5));
        Assert.Equal(CommandNodeOutcome.DefendersWon, CommandNodeRules.Outcome(0, 6));
    }

    [Fact]
    public void An_attacker_alone_moves_a_node_toward_capture_at_the_adm_scaled_time()
    {
        var links = new[] { Link(EntosisSide.Attacker, Start) };
        Assert.Equal(0.5, CommandNodeRules.Advance(0, Start, Start + 120_000, links, 1), 9);
        // at ADM 2 the attackers' time doubles: the same two minutes give a quarter
        Assert.Equal(0.25, CommandNodeRules.Advance(0, Start, Start + 120_000, links, 2), 9);
    }

    [Fact]
    public void A_defender_alone_moves_it_back_and_both_sides_pause_it()
    {
        var defender = new[] { Link(EntosisSide.Defender, Start) };
        Assert.Equal(-0.5, CommandNodeRules.Advance(0, Start, Start + 120_000, defender, 1), 9);
        var both = new[] { Link(EntosisSide.Attacker, Start), Link(EntosisSide.Defender, Start) };
        Assert.Equal(0.3, CommandNodeRules.Advance(0.3, Start, Start + 120_000, both, 1), 9);
    }

    [Fact]
    public void Without_a_link_a_node_regenerates_toward_uncaptured()
    {
        var regen = 60_000 / CommandNodeRules.RegenMs;
        Assert.Equal(0.5 - regen, CommandNodeRules.Advance(0.5, Start, Start + 60_000, [], 1), 9);
        Assert.Equal(0, CommandNodeRules.Advance(0.01, Start, Start + 600_000_000, [], 1), 9);
    }

    [Fact]
    public void The_completion_time_is_the_capture_time_for_the_side_alone()
    {
        Assert.Equal(Start + 240_000, CommandNodeRules.CompletionAt(0, Start, [Link(EntosisSide.Attacker, Start)], 1));
        Assert.Equal(Start + 120_000, CommandNodeRules.CompletionAt(-0.5, Start, [Link(EntosisSide.Defender, Start)], 1));
        Assert.Null(CommandNodeRules.CompletionAt(0, Start, [Link(EntosisSide.Attacker, Start), Link(EntosisSide.Defender, Start)], 1));
    }
}

using Eve.Npc;

namespace Eve.Npc.Tests;

/// <summary>The rules of an NPC bounty, as the reference's <c>bountyRuntime.js</c> has them.</summary>
public sealed class NpcBountyTests
{
    private static BountyBucket Bucket(params (int Type, int Count)[] types) =>
        NpcBounty.Empty(140_000_001, 30000142, 1_700_000_000_000) with { NpcTypes = types.ToDictionary(t => t.Type, t => t.Count) };

    [Theory]
    [InlineData(15000.0, 15000.0)]
    [InlineData(175000.0, 175000.0)]
    [InlineData(12.345, 12.35)]
    [InlineData(0.004, null)]
    [InlineData(0.0, null)]
    [InlineData(-50.0, null)]
    public void A_kill_pays_the_bounty_to_the_cent_and_nothing_when_that_is_nothing(double bounty, double? expected) =>
        Assert.Equal(expected is { } e ? (decimal)e : null, NpcBounty.AmountOf((decimal)bounty));

    [Fact]
    public void A_moment_is_a_filetime()
    {
        Assert.Equal(116_444_736_000_000_000, NpcBounty.Filetime(0));
        Assert.Equal(133_444_736_000_000_000, NpcBounty.Filetime(1_700_000_000_000));
    }

    [Fact]
    public void A_bucket_is_named_for_the_character_the_system_and_the_payout_time_and_a_credit_for_the_bucket()
    {
        var bucket = NpcBounty.Empty(140_000_001, 30000142, 1_700_000_000_000);

        Assert.Equal("140000001:30000142:133444736000000000", bucket.Key);
        Assert.Equal(1_700_000_000_000, bucket.PayoutAtMs);
        Assert.Equal(133_444_736_000_000_000, bucket.PayoutTime);
        Assert.Equal("npc-bounty:140000001:30000142:133444736000000000", NpcBounty.OperationKey(bucket.Key));
    }

    [Theory]
    [InlineData(1, 17)]
    [InlineData(2, 85)]
    [InlineData(40, 85)]
    public void One_kill_is_a_bounty_prize_and_more_are_bounty_prizes(int kills, int expected) => Assert.Equal(expected, NpcBounty.RefType(kills));

    [Fact]
    public void The_description_lists_the_types_killed_by_id_then_the_system_and_the_kills()
    {
        var bucket = Bucket((23245, 2), (17, 1)) with { Kills = 3 };

        Assert.Equal("NBL:\n  17: 1\n  23245: 2\nsolarSystemID: 30000142\nkills: 3", NpcBounty.Description(bucket));
    }

    [Fact]
    public void A_bucket_with_no_types_says_so()
    {
        Assert.Equal("NBL: {}\nsolarSystemID: 30000142\nkills: 0", NpcBounty.Description(Bucket()));
        // types with nothing killed or no id are left out
        Assert.Equal("NBL: {}\nsolarSystemID: 30000142\nkills: 0", NpcBounty.Description(Bucket((0, 3), (5, 0))));
    }

    [Fact]
    public void A_kill_adds_its_amount_and_counts_its_type_and_its_victim()
    {
        var kill = new BountyKill(140_000_001, 30000142, 23245, 4_100_000_000_000_001, 15000m);

        var bucket = NpcBounty.Add(NpcBounty.Add(Bucket(), kill), kill with { VictimShipId = 4_100_000_000_000_002, Amount = 0.005m });

        Assert.Equal(15000.01m, bucket.Amount);
        Assert.Equal(2, bucket.Kills);
        Assert.Equal(2, bucket.NpcTypes[23245]);
        Assert.Equal([4_100_000_000_000_001, 4_100_000_000_000_002], bucket.SourceReferences);
    }

    [Fact]
    public void A_bucket_remembers_no_more_than_32_victims_and_still_counts_the_rest()
    {
        var bucket = Bucket();
        for (var ship = 1; ship <= 40; ship++)
        {
            bucket = NpcBounty.Add(bucket, new BountyKill(1, 1, 5, ship, 1m));
        }

        Assert.Equal(40, bucket.Kills);
        Assert.Equal(40m, bucket.Amount);
        Assert.Equal(32, bucket.SourceReferences.Count);
        Assert.Equal(32, bucket.SourceReferences[^1]);
    }

    [Fact]
    public void A_type_without_an_id_is_not_counted_but_its_bounty_is_paid()
    {
        var bucket = NpcBounty.Add(Bucket(), new BountyKill(1, 1, 0, 0, 100m));

        Assert.Equal(100m, bucket.Amount);
        Assert.Equal(1, bucket.Kills);
        Assert.Empty(bucket.NpcTypes);
        Assert.Empty(bucket.SourceReferences);
    }

    [Fact]
    public void The_first_kill_starts_a_window_of_twenty_minutes_and_later_ones_join_the_one_that_ends_first()
    {
        const long now = 1_700_000_000_000;

        Assert.Equal(now + 1_200_000, NpcBounty.PayoutAt([], now));
        Assert.Equal(now + 500, NpcBounty.PayoutAt([now + 900_000, now + 500, now + 1_200_000], now));
        // windows that have ended (or end this instant) are not joined
        Assert.Equal(now + 1_200_000, NpcBounty.PayoutAt([now - 1, now], now));
        Assert.Equal(now + 7, NpcBounty.PayoutAt([now - 1, now + 7], now));
    }
}

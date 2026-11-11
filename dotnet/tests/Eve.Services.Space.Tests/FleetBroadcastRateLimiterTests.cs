using Eve.Services.Space;

namespace Eve.Services.Space.Tests;

public class FleetBroadcastRateLimiterTests
{
    [Fact]
    public void Same_name_uses_two_seconds_and_a_different_name_uses_at_least_667_milliseconds()
    {
        var limiter = new FleetBroadcastRateLimiter();

        Assert.True(limiter.TryAccept(10, 20, "Target", 1_000));
        Assert.False(limiter.TryAccept(10, 20, "Target", 2_999));
        Assert.True(limiter.TryAccept(10, 20, "Target", 3_000));
        Assert.False(limiter.TryAccept(10, 20, "NeedBackup", 3_665));
        Assert.False(limiter.TryAccept(10, 20, "NeedBackup", 3_666));
        Assert.True(limiter.TryAccept(10, 20, "NeedBackup", 3_667));
        Assert.False(limiter.TryAccept(10, 20, "NeedBackup", 5_665));
        Assert.False(limiter.TryAccept(10, 20, "NeedBackup", 5_666));
        Assert.True(limiter.TryAccept(10, 20, "NeedBackup", 5_667));

        Assert.True(limiter.TryAccept(11, 20, "NeedBackup", 5_667));
        Assert.True(limiter.TryAccept(10, 21, "NeedBackup", 5_667));
    }

    [Fact]
    public void Concurrent_calls_for_the_same_character_admit_only_one_broadcast()
    {
        var limiter = new FleetBroadcastRateLimiter();
        var accepted = 0;

        Parallel.For(0, 128, _ =>
        {
            if (limiter.TryAccept(10, 20, "EnemySpotted", 5_000))
                Interlocked.Increment(ref accepted);
        });

        Assert.Equal(1, accepted);
    }
}

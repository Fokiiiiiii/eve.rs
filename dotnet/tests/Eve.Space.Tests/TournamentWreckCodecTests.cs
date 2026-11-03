namespace Eve.Space.Tests;

public sealed class TournamentWreckCodecTests
{
    [Fact]
    public void Correlated_wreck_operations_roundtrip_and_distinguish_unloaded_scenes()
    {
        var scene = SimSceneKey.SolarSystem(30_000_142);
        SimMessage[] messages = [new SimQueryTournamentWreck(1, scene, 9001),
            new SimQueryTournamentWreckReply(1, scene, 9001, true, null),
            new SimQueryTournamentWreckReply(2, scene, 9001, false, null),
            new SimQueryTournamentWreckReply(3, scene, 9001, false, "SCENE_NOT_LOADED"),
            new SimRemoveTournamentWreck(4, scene, 9001, 101, 202, 26506, [1, 2, 3], 500),
            new SimRemoveTournamentWreckReply(4, scene, 9001, true, null),
            new SimRemoveTournamentWreckReply(5, scene, 9001, false, null)];
        foreach (var message in messages)
        {
            var decoded = SimCodec.Decode(SimCodec.Encode(message).AsMemory(4));
            if (message is SimRemoveTournamentWreck removal)
            {
                var copy = Assert.IsType<SimRemoveTournamentWreck>(decoded);
                Assert.Equal(removal.RequestId, copy.RequestId); Assert.Equal(removal.Scene, copy.Scene);
                Assert.Equal(removal.ItemId, copy.ItemId); Assert.Equal(removal.OwnerId, copy.OwnerId);
                Assert.Equal(removal.LauncherId, copy.LauncherId); Assert.Equal(removal.TypeId, copy.TypeId);
                Assert.Equal(removal.Center, copy.Center); Assert.Equal(removal.Radius, copy.Radius);
            }
            else Assert.Equal(message, decoded);
        }
        Assert.Throws<ArgumentException>(() => SimCodec.Encode(new SimQueryTournamentWreck(0, scene, 9001)));
        Assert.Throws<ArgumentException>(() => SimCodec.Encode(new SimRemoveTournamentWreck(1, new SimSceneKey(30_000_142, 5, 1), 9001, 101, 202, 26506, [1, 2, 3], 500)));
    }
}

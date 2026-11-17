namespace Eve.Space.Tests;

public sealed class DeploymentBallsCodecTests
{
    [Fact]
    public void Deployment_ball_query_and_warp_in_points_roundtrip_over_the_sim_link()
    {
        var scene = SimSceneKey.SolarSystem(30_000_001);
        SimMessage[] messages =
        [
            new SimGetDeploymentBalls(101, scene),
            new SimGetDeploymentBallsReply(101, scene,
            [
                new SimDeploymentBall(9001, 601, [100, 200, 300], 17, false),
                new SimDeploymentBall(9002, 2016, [400, 500, 600], 0, true),
            ], null),
            new SimGetDeploymentBallsReply(102, scene, [], "SCENE_NOT_LOADED"),
        ];

        foreach (var message in messages)
        {
            var decoded = SimCodec.Decode(SimCodec.Encode(message).AsMemory(4));
            if (message is SimGetDeploymentBalls request)
                Assert.Equal(request, decoded);
            else if (message is SimGetDeploymentBallsReply expected)
            {
                var actual = Assert.IsType<SimGetDeploymentBallsReply>(decoded);
                Assert.Equal(expected.RequestId, actual.RequestId);
                Assert.Equal(expected.Scene, actual.Scene);
                Assert.Equal(expected.Error, actual.Error);
                Assert.Equal(expected.Balls.Count, actual.Balls.Count);
                for (var index = 0; index < expected.Balls.Count; index++)
                {
                    Assert.Equal(expected.Balls[index].ItemId, actual.Balls[index].ItemId);
                    Assert.Equal(expected.Balls[index].TypeId, actual.Balls[index].TypeId);
                    Assert.Equal(expected.Balls[index].Position, actual.Balls[index].Position);
                    Assert.Equal(expected.Balls[index].Radius, actual.Balls[index].Radius);
                    Assert.Equal(expected.Balls[index].IsWarpInPoint, actual.Balls[index].IsWarpInPoint);
                }
            }
        }

        Assert.Throws<ArgumentException>(() => SimCodec.Encode(new SimGetDeploymentBalls(0, scene)));
        Assert.Throws<ArgumentException>(() => SimCodec.Encode(new SimGetDeploymentBalls(103, new SimSceneKey(30_000_001, 5, 1))));
        Assert.Throws<ArgumentException>(() => SimCodec.Encode(new SimGetDeploymentBallsReply(104, scene,
            [new SimDeploymentBall(9003, 601, [1, 2], 17, false)], null)));
        Assert.Throws<ArgumentException>(() => SimCodec.Encode(new SimGetDeploymentBallsReply(105, scene,
            [new SimDeploymentBall(9004, 601, [1, 2, 3], 17, true)], null)));
    }
}

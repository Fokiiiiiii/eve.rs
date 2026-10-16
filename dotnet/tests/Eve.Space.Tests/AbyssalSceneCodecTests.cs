namespace Eve.Space.Tests;

public sealed class AbyssalSceneCodecTests
{
    [Fact]
    public void Ensure_scene_roundtrips_run_room_environment_boundary_and_expiry()
    {
        var spec = new SimAbyssalSceneSpec(
            new SimSceneKey(31_000_201, 42, 2),
            new SimAbyssalEnvironmentProfile(
                [new SimAbyssalAttributeModifier(37, SimAbyssalModifierOperation.Multiply, 1.5)],
                [new SimAbyssalEnvironmentField(
                    [100.0, 200.0, 300.0], 5_000.0,
                    [new SimAbyssalAttributeModifier(265, SimAbyssalModifierOperation.Add, 25.0)])]),
            new SimAbyssalBoundaryProfile(
                [0.0, 0.0, 0.0], 75_000.0, 1_000,
                [new SimAbyssalBoundaryDamagePoint(0, 12.5)]),
            134_351_527_136_350_000);
        var message = new SimEnsureAbyssalScene(17, spec);

        var decoded = Assert.IsType<SimEnsureAbyssalScene>(SimCodec.Decode(SimCodec.Encode(message).AsMemory(4)));

        Assert.Equal(message.RequestId, decoded.RequestId);
        Assert.Equal(spec.Scene, decoded.Spec.Scene);
        Assert.Equal(spec.ExpiryFileTime, decoded.Spec.ExpiryFileTime);
        Assert.Equal(spec.Environment.GlobalModifiers, decoded.Spec.Environment.GlobalModifiers);
        Assert.Equal(spec.Environment.Fields[0].Center, decoded.Spec.Environment.Fields[0].Center);
        Assert.Equal(spec.Environment.Fields[0].Modifiers, decoded.Spec.Environment.Fields[0].Modifiers);
        Assert.Equal(spec.Boundary.Center, decoded.Spec.Boundary.Center);
        Assert.Equal(spec.Boundary.DamageCurve[0].AppliedDamagePerSecond, decoded.Spec.Boundary.DamageCurve[0].AppliedDamagePerSecond);
        Assert.Equal(15_000, decoded.Spec.Boundary.TerminalExposureMs);
    }


    [Fact]
    public void Abyssal_static_conduit_roundtrips_as_a_scene_object()
    {
        var scene = new SimSceneKey(31_000_201, 42, 2);
        var sceneObject = new SimAbyssalSceneObject(
            scene, 8_400_000_001, 47_685, 366, 2, 140_000_342, 5_000.0, 22_130,
            "Transfer Conduit (Triglavian)", [100.0, 200.0, 35_300.0], [0.0, 0.0, 1.0], false);
        var message = new SimEnsureAbyssalSceneObject(19, sceneObject);

        var decoded = Assert.IsType<SimEnsureAbyssalSceneObject>(SimCodec.Decode(SimCodec.Encode(message).AsMemory(4)));

        Assert.Equal(message.RequestId, decoded.RequestId);
        Assert.Equal(sceneObject.Scene, decoded.Object.Scene);
        Assert.Equal(sceneObject.ItemId, decoded.Object.ItemId);
        Assert.Equal(sceneObject.TypeId, decoded.Object.TypeId);
        Assert.Equal(sceneObject.GroupId, decoded.Object.GroupId);
        Assert.Equal(sceneObject.CategoryId, decoded.Object.CategoryId);
        Assert.Equal(sceneObject.OwnerId, decoded.Object.OwnerId);
        Assert.Equal(sceneObject.Radius, decoded.Object.Radius);
        Assert.Equal(sceneObject.GraphicId, decoded.Object.GraphicId);
        Assert.Equal(sceneObject.Name, decoded.Object.Name);
        Assert.Equal(sceneObject.Position, decoded.Object.Position);
        Assert.Equal(sceneObject.Direction, decoded.Object.Direction);
        Assert.Equal(sceneObject.IsOpen, decoded.Object.IsOpen);
    }


    [Fact]
    public void Ensure_scene_rejects_negative_resolved_applied_damage()
    {
        var spec = new SimAbyssalSceneSpec(
            new SimSceneKey(31_000_201, 42, 2),
            new SimAbyssalEnvironmentProfile([], []),
            new SimAbyssalBoundaryProfile(
                [0.0, 0.0, 0.0], 75_000.0, 1_000,
                [new SimAbyssalBoundaryDamagePoint(0.0, -0.1)]),
            134_351_527_136_350_000);

        Assert.Throws<ArgumentException>(() => SimCodec.Encode(new SimEnsureAbyssalScene(18, spec)));
    }

    [Fact]
    public void Ensure_scene_rejects_nonpositive_terminal_exposure()
    {
        var spec = new SimAbyssalSceneSpec(
            new SimSceneKey(31_000_201, 42, 2),
            new SimAbyssalEnvironmentProfile([], []),
            new SimAbyssalBoundaryProfile(
                [0.0, 0.0, 0.0], 75_000.0, 1_000,
                [new SimAbyssalBoundaryDamagePoint(0.0, 12.5)], 0),
            134_351_527_136_350_000);

        Assert.Throws<ArgumentException>(() => SimCodec.Encode(new SimEnsureAbyssalScene(20, spec)));
    }

    [Fact]
    public void A_sim_scene_key_rejects_room_or_instance_mismatches_before_encoding()
    {
        var message = new SimEnsureAbyssalScene(1, new SimAbyssalSceneSpec(
            new SimSceneKey(31_000_201, 0, 2),
            new SimAbyssalEnvironmentProfile([], []),
            new SimAbyssalBoundaryProfile([0.0, 0.0, 0.0], 1.0, 1, []),
            134_351_527_136_350_000));

        Assert.Throws<ArgumentException>(() => SimCodec.Encode(message));
    }

    [Fact]
    public void Abyssal_trace_materialize_and_remove_messages_roundtrip_full_persisted_state()
    {
        var trace = new SimAbyssalTrace(
            31_000_201,
            8_400_000_489,
            48_964,
            1_991,
            2,
            90_001,
            500.0,
            [1.0, 2.0, 3.0],
            [4.0, 5.0, 6.0],
            [0.0, 0.0, 1.0],
            [7.0, 8.0, 9.0],
            0.5,
            2,
            56132,
            0,
            1_800_000_000_000);
        var publish = new SimEnsureAbyssalTracePublished(18, 27, trace);
        var decodedPublish = Assert.IsType<SimEnsureAbyssalTracePublished>(
            SimCodec.Decode(SimCodec.Encode(publish).AsMemory(4)));
        Assert.Equal(publish.RequestId, decodedPublish.RequestId);
        Assert.Equal(publish.ActorSessionId, decodedPublish.ActorSessionId);
        Assert.Equal(trace.SolarSystemId, decodedPublish.Trace.SolarSystemId);
        Assert.Equal(trace.ItemId, decodedPublish.Trace.ItemId);
        Assert.Equal(trace.TypeId, decodedPublish.Trace.TypeId);
        Assert.Equal(trace.GroupId, decodedPublish.Trace.GroupId);
        Assert.Equal(trace.CategoryId, decodedPublish.Trace.CategoryId);
        Assert.Equal(trace.OwnerId, decodedPublish.Trace.OwnerId);
        Assert.Equal(trace.Radius, decodedPublish.Trace.Radius);
        Assert.Equal(trace.Position, decodedPublish.Trace.Position);
        Assert.Equal(trace.Velocity, decodedPublish.Trace.Velocity);
        Assert.Equal(trace.Direction, decodedPublish.Trace.Direction);
        Assert.Equal(trace.TargetPoint, decodedPublish.Trace.TargetPoint);

        var remove = new SimRemoveAbyssalTrace(19, trace.SolarSystemId, trace.ItemId);
        var decodedRemove = Assert.IsType<SimRemoveAbyssalTrace>(
            SimCodec.Decode(SimCodec.Encode(remove).AsMemory(4)));
        Assert.Equal(remove, decodedRemove);
    }

    [Fact]
    public void Abyssal_room_move_can_preserve_the_sim_authoritative_source_pose()
    {
        var move = new SimMoveAbyssalParticipant(
            20,
            8_400_000_489,
            new SimSceneKey(31_000_201, 42, 1),
            new SimSceneKey(31_000_201, 42, 2),
            [],
            [])
        {
            PreserveSourcePose = true
        };

        var decoded = Assert.IsType<SimMoveAbyssalParticipant>(
            SimCodec.Decode(SimCodec.Encode(move).AsMemory(4)));
        Assert.Equal(move.RequestId, decoded.RequestId);
        Assert.Equal(move.ShipId, decoded.ShipId);
        Assert.Equal(move.Source, decoded.Source);
        Assert.Equal(move.Destination, decoded.Destination);
        Assert.True(decoded.PreserveSourcePose);
    }


    [Fact]
    public void Resident_pose_roundtrips_for_a_public_source_system_scene()
    {
        var scene = SimSceneKey.SolarSystem(30_000_142);
        var request = new SimGetAbyssalParticipantPose(21, scene, 8_400_000_193);

        var decoded = Assert.IsType<SimGetAbyssalParticipantPose>(
            SimCodec.Decode(SimCodec.Encode(request).AsMemory(4)));
        Assert.Equal(request, decoded);

        var reply = new SimGetAbyssalParticipantPoseReply(
            request.RequestId, scene, request.ShipId, [100.0, 200.0, 300.0], [0.0, 0.0, 1.0], null);
        var decodedReply = Assert.IsType<SimGetAbyssalParticipantPoseReply>(
            SimCodec.Decode(SimCodec.Encode(reply).AsMemory(4)));
        Assert.Equal(reply.RequestId, decodedReply.RequestId);
        Assert.Equal(reply.Scene, decodedReply.Scene);
        Assert.Equal(reply.ShipId, decodedReply.ShipId);
        Assert.Equal(reply.Position, decodedReply.Position);
        Assert.Equal(reply.Direction, decodedReply.Direction);
        Assert.Null(decodedReply.Error);
    }
}

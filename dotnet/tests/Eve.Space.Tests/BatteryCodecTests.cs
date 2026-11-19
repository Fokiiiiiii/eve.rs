namespace Eve.Space.Tests;

public sealed class BatteryCodecTests
{
    [Fact]
    public void Battery_placement_and_orders_roundtrip_over_the_sim_link()
    {
        var placed = new SimPlaceBattery(1_050_000_001, 30_000_001, 16631, 1000, "Battery", 1_790_646_377_000_015,
            [1.0, 2.0, 3.0], 60.0, 400.0, 50_000.0, 2, true);
        var decodedPlaced = Assert.IsType<SimPlaceBattery>(SimCodec.Decode(SimCodec.Encode(placed).AsMemory(4)));
        Assert.Equal(placed.BatteryId, decodedPlaced.BatteryId);
        Assert.Equal(placed.SolarSystemId, decodedPlaced.SolarSystemId);
        Assert.Equal(placed.TypeId, decodedPlaced.TypeId);
        Assert.Equal(placed.GroupId, decodedPlaced.GroupId);
        Assert.Equal(placed.DisplayName, decodedPlaced.DisplayName);
        Assert.Equal(placed.HolderSession, decodedPlaced.HolderSession);
        Assert.Equal(placed.Position, decodedPlaced.Position);
        Assert.Equal(placed.Radius, decodedPlaced.Radius);
        Assert.Equal(placed.ScanResolution, decodedPlaced.ScanResolution);
        Assert.Equal(placed.MaxTargetRange, decodedPlaced.MaxTargetRange);
        Assert.Equal(placed.MaxLockedTargets, decodedPlaced.MaxLockedTargets);
        Assert.Equal(placed.Online, decodedPlaced.Online);

        var offline = new SimPlaceBattery(1_050_000_001, 30_000_001, 16631, 1000, string.Empty, 0, [0.0, 0.0, 0.0], 60.0, 0.0, 0.0, 0, false);
        Assert.False(Assert.IsType<SimPlaceBattery>(SimCodec.Decode(SimCodec.Encode(offline).AsMemory(4))).Online);

        var answered = new SimBatteryPlaced(1_050_000_001, "SYSTEM_UNKNOWN");
        Assert.Equal(answered, SimCodec.Decode(SimCodec.Encode(answered).AsMemory(4)));

        var order = new SimBatteryTarget(70, 1_050_000_001, 30_000_001, 1, 1_790_646_377_000_016);
        Assert.Equal(order, SimCodec.Decode(SimCodec.Encode(order).AsMemory(4)));

        var targets = new SimBatteryTargets(70, 1_050_000_001, string.Empty, true, [1_790_646_377_000_015, 1_790_646_377_000_016]);
        var decodedTargets = Assert.IsType<SimBatteryTargets>(SimCodec.Decode(SimCodec.Encode(targets).AsMemory(4)));
        Assert.Equal(targets.RequestId, decodedTargets.RequestId);
        Assert.Equal(targets.BatteryId, decodedTargets.BatteryId);
        Assert.Equal(targets.Reason, decodedTargets.Reason);
        Assert.Equal(targets.Pending, decodedTargets.Pending);
        Assert.Equal(targets.Targets, decodedTargets.Targets);
    }

    [Fact]
    public void Battery_fire_and_its_answer_roundtrip_over_the_sim_link()
    {
        var fire = new SimBatteryFire(73, 1_050_000_001, 30_000_001, true, 1_790_646_377_000_016);
        Assert.Equal(fire, SimCodec.Decode(SimCodec.Encode(fire).AsMemory(4)));
        var stop = new SimBatteryFire(74, 1_050_000_001, 30_000_001, false, 0);
        Assert.Equal(stop, SimCodec.Decode(SimCodec.Encode(stop).AsMemory(4)));

        var accepted = new SimBatteryFired(73, 1_050_000_001, string.Empty, true, 1_790_646_377_000_016);
        Assert.Equal(accepted, SimCodec.Decode(SimCodec.Encode(accepted).AsMemory(4)));
        var refused = new SimBatteryFired(74, 1_050_000_001, "TARGET_NOT_LOCKED", false, 0);
        Assert.Equal(refused, SimCodec.Decode(SimCodec.Encode(refused).AsMemory(4)));
    }

    [Fact]
    public void Battery_fire_refuses_a_flag_that_is_not_a_boolean()
    {
        var frame = SimCodec.Encode(new SimBatteryFire(73, 1_050_000_001, 30_000_001, true, 1_790_646_377_000_016));
        // the on flag follows the kind (1), request (8), battery (8) and system (4) after the 4-byte length: index 4 + 1 + 8 + 8 + 4
        frame[4 + 21] = 2;
        Assert.Throws<SimLinkException>(() => SimCodec.Decode(frame.AsMemory(4)));
    }

    [Fact]
    public void Battery_encoder_refuses_orders_and_numbers_the_simulation_cannot_read()
    {
        Assert.Throws<ArgumentException>(() => SimCodec.Encode(new SimBatteryTarget(71, 1_050_000_001, 30_000_001, 0, 1)));
        Assert.Throws<ArgumentException>(() => SimCodec.Encode(new SimBatteryTarget(72, 1_050_000_001, 30_000_001, 5, 1)));
        Assert.Throws<ArgumentException>(() => SimCodec.Encode(new SimPlaceBattery(1_050_000_001, 30_000_001, 16631, 1000, "Battery",
            1, [double.NaN, 2.0, 3.0], 60.0, 400.0, 50_000.0, 2, true)));
        Assert.Throws<ArgumentException>(() => SimCodec.Encode(new SimPlaceBattery(1_050_000_001, 30_000_001, 16631, 1000, "Battery",
            1, [1.0, 2.0], 60.0, 400.0, 50_000.0, 2, true)));
    }
}

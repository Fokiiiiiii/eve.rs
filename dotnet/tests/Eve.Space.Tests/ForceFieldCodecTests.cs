namespace Eve.Space.Tests;

public sealed class ForceFieldCodecTests
{
    [Fact]
    public void Force_field_placement_and_removal_roundtrip_over_the_sim_link()
    {
        var placed = new SimPlaceForceField(1_050_000_100, 1_050_000_099, 30_000_001, 16_000, 411, "Force Field",
            [1.0, 2.0, 3.0], 1_000.0);
        var decodedPlaced = Assert.IsType<SimPlaceForceField>(SimCodec.Decode(SimCodec.Encode(placed).AsMemory(4)));
        Assert.Equal(placed.FieldId, decodedPlaced.FieldId);
        Assert.Equal(placed.TowerId, decodedPlaced.TowerId);
        Assert.Equal(placed.SolarSystemId, decodedPlaced.SolarSystemId);
        Assert.Equal(placed.TypeId, decodedPlaced.TypeId);
        Assert.Equal(placed.GroupId, decodedPlaced.GroupId);
        Assert.Equal(placed.DisplayName, decodedPlaced.DisplayName);
        Assert.Equal(placed.Position, decodedPlaced.Position);
        Assert.Equal(placed.Radius, decodedPlaced.Radius);

        var answered = new SimForceFieldPlaced(1_050_000_100, "FORCE_FIELD_RADIUS_INVALID");
        Assert.Equal(answered, SimCodec.Decode(SimCodec.Encode(answered).AsMemory(4)));

        var removed = new SimRemoveForceField(1_050_000_099, 30_000_001);
        Assert.Equal(removed, SimCodec.Decode(SimCodec.Encode(removed).AsMemory(4)));

        var removedAnswer = new SimForceFieldRemoved(1_050_000_099, string.Empty);
        Assert.Equal(removedAnswer, SimCodec.Decode(SimCodec.Encode(removedAnswer).AsMemory(4)));
    }

    [Fact]
    public void Force_field_kinds_are_the_protocol_104_codes_in_the_180_range()
    {
        Assert.Equal((byte)180, SimCodec.Encode(new SimPlaceForceField(1, 2, 3, 4, 5, "F", [0.0, 0.0, 0.0], 1.0))[4]);
        Assert.Equal((byte)181, SimCodec.Encode(new SimForceFieldPlaced(1, string.Empty))[4]);
        Assert.Equal((byte)182, SimCodec.Encode(new SimRemoveForceField(2, 3))[4]);
        Assert.Equal((byte)183, SimCodec.Encode(new SimForceFieldRemoved(2, string.Empty))[4]);
        Assert.Equal(104, SimCodec.ProtocolVersion);
    }

    [Fact]
    public void Force_field_access_and_ship_password_roundtrip_over_the_sim_link()
    {
        var access = new SimSetForceFieldAccess(1_050_000_099, 30_000_001, 98_000_001, 97_000_001, true, false);
        Assert.Equal(access, SimCodec.Decode(SimCodec.Encode(access).AsMemory(4)));
        Assert.Equal((byte)184, SimCodec.Encode(access)[4]);

        var password = new SimShipForceFieldPassword(1_790_646_377_000_015, 1_050_000_099, 30_000_001, true);
        Assert.Equal(password, SimCodec.Decode(SimCodec.Encode(password).AsMemory(4)));
        Assert.Equal((byte)185, SimCodec.Encode(password)[4]);
    }

    [Fact]
    public void Force_field_encoder_refuses_numbers_the_simulation_cannot_read()
    {
        Assert.Throws<ArgumentException>(() => SimCodec.Encode(new SimPlaceForceField(1, 2, 3, 4, 5, "F", [double.NaN, 0.0, 0.0], 1.0)));
        Assert.Throws<ArgumentException>(() => SimCodec.Encode(new SimPlaceForceField(1, 2, 3, 4, 5, "F", [0.0, 0.0], 1.0)));
        Assert.Throws<ArgumentException>(() => SimCodec.Encode(new SimPlaceForceField(1, 2, 3, 4, 5, "F", [0.0, 0.0, 0.0], double.PositiveInfinity)));
    }
}

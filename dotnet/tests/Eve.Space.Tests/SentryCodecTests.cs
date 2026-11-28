using System.Buffers.Binary;

namespace Eve.Space.Tests;

public sealed class SentryCodecTests
{
    [Fact]
    public void Sentry_kinds_are_the_protocol_106_codes_in_the_192_range()
    {
        Assert.Equal((byte)192, SimCodec.Encode(new SimSentryPolicy(1, 2, true, null, null, false, false, 3, 0))[4]);
        Assert.Equal((byte)193, SimCodec.Encode(new SimSentryBattery(1, 2, 3))[4]);
        Assert.Equal((byte)194, SimCodec.Encode(new SimSentryPilot(1, 2, 3, 4, 0, null, 0.0, false))[4]);
        Assert.Equal(107, SimCodec.ProtocolVersion);
    }

    [Fact]
    public void A_sentry_policy_carries_its_thresholds_flags_and_owner_in_the_stated_layout()
    {
        var policy = new SimSentryPolicy(1_050_000_099, 30_000_001, true, -2.5, null, true, false, 98_000_001, 97_000_001);
        var frame = SimCodec.Encode(policy);
        // The body after the kind: tower (8), system (4), online (1), standing flag (1) and number (8), security flag (1) and number (8),
        // war (1), high security (1), owner corporation (8) and owner alliance (8): 49 bytes.
        Assert.Equal(49, frame.Length - 4 - 1);
        Assert.Equal(1_050_000_099, BinaryPrimitives.ReadInt64LittleEndian(frame.AsSpan(5)));
        Assert.Equal(30_000_001, BinaryPrimitives.ReadInt32LittleEndian(frame.AsSpan(13)));
        Assert.Equal((byte)1, frame[17]);
        Assert.Equal((byte)1, frame[18]);
        Assert.Equal(-2.5, BinaryPrimitives.ReadDoubleLittleEndian(frame.AsSpan(19)));
        Assert.Equal((byte)0, frame[27]);
        Assert.Equal(0.0, BinaryPrimitives.ReadDoubleLittleEndian(frame.AsSpan(28)));
        Assert.Equal((byte)1, frame[36]);
        Assert.Equal((byte)0, frame[37]);
        Assert.Equal(98_000_001, BinaryPrimitives.ReadInt64LittleEndian(frame.AsSpan(38)));
        Assert.Equal(97_000_001, BinaryPrimitives.ReadInt64LittleEndian(frame.AsSpan(46)));
    }

    [Fact]
    public void A_sentry_pilot_carries_its_standing_and_war_after_its_optional_security_status()
    {
        var pilot = new SimSentryPilot(1_050_000_099, 30_000_001, 1_790_646_377_000_016, 98_000_002, 0, -4.9, 0.0, true);
        var frame = SimCodec.Encode(pilot);
        // Body after the kind: tower 5..13, system 13..17, ship 17..25, corporation 25..33, alliance 33..41, security flag 41 and number 42..50,
        // standing 50..58, war 58. The body is 54 bytes.
        Assert.Equal(54, frame.Length - 4 - 1);
        Assert.Equal((byte)1, frame[41]);
        Assert.Equal(-4.9, BinaryPrimitives.ReadDoubleLittleEndian(frame.AsSpan(42)));
        Assert.Equal(0.0, BinaryPrimitives.ReadDoubleLittleEndian(frame.AsSpan(50)));
        Assert.Equal((byte)1, frame[58]);
    }

    [Fact]
    public void The_sentry_encoder_refuses_numbers_the_simulation_cannot_read()
    {
        Assert.Throws<ArgumentException>(() => SimCodec.Encode(new SimSentryPolicy(1, 2, true, double.NaN, null, false, false, 3, 0)));
        Assert.Throws<ArgumentException>(() => SimCodec.Encode(new SimSentryPolicy(1, 2, true, null, double.PositiveInfinity, false, false, 3, 0)));
        Assert.Throws<ArgumentException>(() => SimCodec.Encode(new SimSentryPilot(1, 2, 3, 4, 0, null, double.NaN, false)));
    }
}

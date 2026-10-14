namespace Eve.Space.Tests;

public sealed class SimCodecFieldPartitionTests
{
    private static SimStructureDamageCandidatesResult Result() =>
        new(7, 1_040_000_000_000_001, 9_988_400_001_940, 6_474, 31, 20, 4_300, 0, [101, 102]);

    [Fact]
    public void Empty_partition_keeps_the_legacy_kind_86_layout()
    {
        var frame = SimCodec.Encode(Result());

        Assert.Equal(81, frame.Length);
        Assert.Equal((byte)86, frame[4]);
        var decoded = Assert.IsType<SimStructureDamageCandidatesResult>(SimCodec.Decode(frame.AsMemory(4)));
        Assert.Equal(new long[] { 101, 102 }, decoded.AllowedTargetIds);
        Assert.Empty(decoded.FieldMovedTargetIds);
        Assert.Empty(decoded.FieldHeldTargetIds);
    }

    [Fact]
    public void A_complete_partition_round_trips_on_the_optional_tail()
    {
        var frame = SimCodec.Encode(Result() with
        {
            FieldMovedTargetIds = [101],
            FieldHeldTargetIds = [102],
        });

        var decoded = Assert.IsType<SimStructureDamageCandidatesResult>(SimCodec.Decode(frame.AsMemory(4)));
        Assert.Equal(new long[] { 101, 102 }, decoded.AllowedTargetIds);
        Assert.Equal(new long[] { 101 }, decoded.FieldMovedTargetIds);
        Assert.Equal(new long[] { 102 }, decoded.FieldHeldTargetIds);
    }

    [Fact]
    public void The_decoder_rejects_an_incomplete_partition()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)86);
        writer.Write((ulong)7);
        writer.Write((long)1_040_000_000_000_001);
        writer.Write((long)9_988_400_001_940);
        writer.Write((int)6_474);
        writer.Write((ulong)31);
        writer.Write((long)20);
        writer.Write((ulong)4_300);
        writer.Write((uint)0);
        writer.Write((uint)2);
        writer.Write((long)101);
        writer.Write((long)102);
        writer.Write((byte)1);
        writer.Write((uint)1);
        writer.Write((long)101);
        writer.Write((uint)0);

        var error = Assert.Throws<SimLinkException>(() => SimCodec.Decode(stream.ToArray()));
        Assert.Equal(SimLinkError.BadValue, error.Error);
    }

    [Fact]
    public void The_encoder_rejects_an_incomplete_partition()
    {
        var invalid = Result() with
        {
            FieldMovedTargetIds = [101],
        };

        Assert.Throws<ArgumentException>(() => SimCodec.Encode(invalid));
    }

    [Fact]
    public void The_encoder_rejects_overlapping_dispositions()
    {
        var invalid = Result() with
        {
            FieldMovedTargetIds = [101],
            FieldHeldTargetIds = [101],
        };

        Assert.Throws<ArgumentException>(() => SimCodec.Encode(invalid));
    }

    [Fact]
    public void The_decoder_rejects_a_partition_on_a_request()
    {
        var resultFrame = SimCodec.Encode(Result() with
        {
            FieldMovedTargetIds = [101],
            FieldHeldTargetIds = [102],
        });
        var requestPayload = resultFrame.AsSpan(4).ToArray();
        requestPayload[0] = 85;

        Assert.Throws<SimLinkException>(() => SimCodec.Decode(requestPayload));
    }
}

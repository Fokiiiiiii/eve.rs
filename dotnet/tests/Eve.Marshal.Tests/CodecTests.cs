using System.Text.Json.Nodes;

namespace Eve.Marshal.Tests;

/// <summary>Behaviour the golden fixtures do not reach: limits, boundaries and robustness against hostile input.</summary>
public class CodecTests
{
    private static readonly byte[] Header = [0x7e, 0, 0, 0, 0];

    private static byte[] Stream(params byte[] body) => [.. Header, .. body];

    private static PyValue Decode(params byte[] body) => MarshalCodec.Decode(Stream(body));

    [Fact]
    public void A_char_string_of_any_byte_decodes_to_that_byte()
    {
        for (var b = 0; b < 256; b++)
        {
            var value = Assert.IsType<PyBytes>(Decode(0x0f, (byte)b));
            Assert.Equal([(byte)b], value.Value.ToArray());
        }
    }

    [Fact]
    public void A_variable_width_integer_is_a_number_up_to_the_largest_safe_integer_and_big_beyond()
    {
        // 2^53 - 1
        Assert.Equal(new PyInt(9_007_199_254_740_991), Decode(0x2f, 7, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0x1f));
        // 2^53
        var big = Assert.IsType<PyBigUnsigned>(Decode(0x2f, 7, 0, 0, 0, 0, 0, 0, 0x20));
        Assert.Equal([0, 0, 0, 0, 0, 0, 0x20], big.Bytes);
        // trailing zero bytes are not significant
        Assert.Equal(new PyInt(5), Decode(0x2f, 3, 5, 0, 0));
        Assert.Equal(new PyInt(0), Decode(0x2f, 0));
        Assert.Equal(MarshalErrorKind.TruncatedInteger, Assert.Throws<MarshalException>(() => Decode(0x2f, 4, 1, 2)).Kind);
    }

    [Fact]
    public void A_ucs2_string_cut_short_yields_the_whole_code_units_there_are()
    {
        // Declares 2 code units (4 bytes) but only 3 bytes follow.
        var value = Assert.IsType<PyWString>(Decode(0x12, 2, 0x41, 0x00, 0x42));
        Assert.Equal("A", value.Value);
    }

    [Fact]
    public void A_string_longer_than_the_input_yields_the_bytes_there_are()
    {
        var value = Assert.IsType<PyBytes>(Decode(0x13, 10, 1, 2, 3));
        Assert.Equal([1, 2, 3], value.Value.ToArray());
        Assert.IsType<PyBuffer>(Decode(0x0d, 200));
    }

    [Fact]
    public void Nesting_is_limited_in_both_directions()
    {
        PyValue Nest(int levels)
        {
            PyValue value = PyNone.Instance;
            for (var i = 0; i < levels; i++)
            {
                value = new PyTuple([value]);
            }
            return value;
        }

        // The root is depth 1, so 255 tuples around a leaf reach depth 256.
        var accepted = MarshalCodec.Encode(Nest(MarshalCodec.MaxDepth - 1));
        Assert.NotNull(MarshalCodec.Decode(accepted));
        Assert.Equal(MarshalErrorKind.TooDeep, Assert.Throws<MarshalException>(() => MarshalCodec.Encode(Nest(MarshalCodec.MaxDepth))).Kind);

        var tooDeep = Stream([.. Enumerable.Repeat((byte)0x25, MarshalCodec.MaxDepth), 0x01]);
        Assert.Equal(MarshalErrorKind.TooDeep, Assert.Throws<MarshalException>(() => MarshalCodec.Decode(tooDeep)).Kind);
    }

    [Fact]
    public void A_hundred_thousand_levels_are_refused_without_exhausting_the_stack()
    {
        var hostile = Stream([.. Enumerable.Repeat((byte)0x25, 100_000)]);
        Assert.Equal(MarshalErrorKind.TooDeep, Assert.Throws<MarshalException>(() => MarshalCodec.Decode(hostile)).Kind);

        PyValue value = PyNone.Instance;
        for (var i = 0; i < 100_000; i++)
        {
            value = new PyList([value]);
        }
        Assert.Equal(MarshalErrorKind.TooDeep, Assert.Throws<MarshalException>(() => MarshalCodec.Encode(value)).Kind);
    }

    [Fact]
    public void A_chain_of_nested_streams_cannot_get_around_the_depth_limit()
    {
        // Each level is a PySubStream whose payload is another stream of the same shape.
        byte[] inner = Stream(0x01);
        for (var i = 0; i < 400; i++)
        {
            // 0x2b, size, then the nested stream (0xff-prefixed 4-byte size when it does not fit a byte)
            var size = inner.Length < 0xff ? new[] { (byte)inner.Length } : [0xff, .. BitConverter.GetBytes((uint)inner.Length)];
            inner = Stream([0x2b, .. size, .. inner]);
        }
        var value = MarshalCodec.Decode(inner);

        var depth = 0;
        var current = value;
        while (current is PySubStream { Decoded: { } next })
        {
            depth++;
            current = next;
        }
        Assert.True(depth < MarshalCodec.MaxDepth, $"decoded {depth} nested streams");
        Assert.True(current is PySubStream { Decoded: null }, "the chain ends in raw bytes once the limit is reached");
    }

    [Theory]
    [InlineData(247, 254)]
    [InlineData(248, 255)]
    [InlineData(300, 5 + 1 + 5 + 300)]
    public void A_nested_stream_length_uses_one_byte_below_255_and_five_above(int payloadBytes, int innerLength)
    {
        var inner = new PySubStream(new PyBytes(new byte[payloadBytes]));
        var encoded = MarshalCodec.Encode(inner);
        // 7e 00000000 | 2b <size> <stream>
        Assert.Equal(0x2b, encoded[5]);
        if (innerLength < 255)
        {
            Assert.Equal((byte)innerLength, encoded[6]);
        }
        else
        {
            Assert.Equal(0xff, encoded[6]);
            Assert.Equal((uint)(innerLength), BitConverter.ToUInt32(encoded, 7));
        }
        var decoded = Assert.IsType<PySubStream>(MarshalCodec.DecodeExact(encoded));
        Assert.Equal(new PyBytes(new byte[payloadBytes]), decoded.Decoded);
    }

    [Fact]
    public void A_count_that_promises_more_than_the_input_can_hold_fails_fast()
    {
        // A tuple of 4 billion items, a dict of 4 billion entries, a list of 4 billion items.
        foreach (var opcode in new byte[] { 0x14, 0x15, 0x16 })
        {
            var hostile = Stream(opcode, 0xff, 0xff, 0xff, 0xff, 0xff);
            Assert.Equal(MarshalErrorKind.UnexpectedEnd, Assert.Throws<MarshalException>(() => MarshalCodec.Decode(hostile)).Kind);
        }
    }

    [Fact]
    public void A_save_table_larger_than_the_stream_is_refused()
    {
        var hostile = new byte[] { 0x7e, 0xff, 0xff, 0xff, 0x7f, 0x01 };
        Assert.Equal(MarshalErrorKind.SaveTable, Assert.Throws<MarshalException>(() => MarshalCodec.Decode(hostile)).Kind);
    }

    [Fact]
    public void Every_prefix_of_every_recorded_stream_decodes_or_fails_with_a_marshal_error()
    {
        var cases = (JsonArray)JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "marshal", "cases.json")))!;
        var checkedPrefixes = 0;
        foreach (var record in cases)
        {
            if ((string?)record!["encoded"] is not { } hex)
            {
                continue;
            }
            var bytes = Convert.FromHexString(hex);
            var step = Math.Max(1, bytes.Length / 40); // long streams: sample the prefixes
            for (var length = 0; length < bytes.Length; length += step)
            {
                try
                {
                    MarshalCodec.Decode(bytes.AsMemory(0, length));
                }
                catch (MarshalException)
                {
                }
                checkedPrefixes++;
            }
        }
        Assert.True(checkedPrefixes > 5_000, $"{checkedPrefixes}");
    }

    [Fact]
    public void Corrupted_streams_never_fail_with_anything_but_a_marshal_error()
    {
        var cases = (JsonArray)JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "marshal", "cases.json")))!;
        var streams = cases
            .Select(record => (string?)record!["encoded"])
            .Where(hex => hex is not null)
            .Select(hex => Convert.FromHexString(hex!))
            .Where(bytes => bytes.Length is > 8 and < 20_000)
            .ToList();
        var random = new Random(0x45564a53);
        var survived = 0;
        for (var round = 0; round < 6_000; round++)
        {
            var bytes = (byte[])streams[random.Next(streams.Count)].Clone();
            for (var flips = random.Next(1, 4); flips > 0; flips--)
            {
                bytes[random.Next(bytes.Length)] = (byte)random.Next(256);
            }
            try
            {
                var value = MarshalCodec.Decode(bytes);
                survived++;
                _ = MarshalCodec.Encode(value); // whatever decoded must be encodable again
            }
            catch (MarshalException)
            {
            }
        }
        Assert.True(survived > 0);
    }

    [Fact]
    public void A_stream_frame_splits_off_the_front_of_a_buffer()
    {
        var frame = MarshalCodec.EncodePacket(new PyTuple([new PyInt(7), new PyStr("machoNet")]));
        var input = frame.Concat(new byte[] { 1, 2, 3 }).ToArray();
        Assert.True(MarshalCodec.TrySplitPacket(input, out var payload, out var rest));
        Assert.Equal(frame.Length - 4, payload.Length);
        Assert.Equal([1, 2, 3], rest.ToArray());
        Assert.Equal(new PyTuple([new PyInt(7), new PyBytes("machoNet"u8.ToArray())]).Items.Count, ((PyTuple)MarshalCodec.DecodeExact(payload)).Items.Count);

        Assert.False(MarshalCodec.TrySplitPacket(frame.AsMemory(0, frame.Length - 1), out _, out _));
        Assert.False(MarshalCodec.TrySplitPacket(frame.AsMemory(0, 3), out _, out _));
        Assert.False(MarshalCodec.TrySplitPacket(new byte[] { 0xff, 0xff, 0xff, 0xff, 0 }, out _, out _));
    }

    [Fact]
    public void Run_length_coding_round_trips_arbitrary_blocks()
    {
        var random = new Random(1234);
        for (var round = 0; round < 3_000; round++)
        {
            var length = random.Next(0, 200);
            var zeroBias = random.NextDouble();
            var block = new byte[length];
            for (var i = 0; i < length; i++)
            {
                block[i] = random.NextDouble() < zeroBias ? (byte)0 : (byte)random.Next(1, 256);
            }
            var compressed = MarshalCodec.CompressRle(block);
            Assert.Equal(block, MarshalCodec.DecompressRle(compressed, length));
        }
    }

    [Fact]
    public void Values_compare_structurally()
    {
        Assert.Equal(new PyTuple([new PyInt(1), new PyStr("a")]), new PyTuple([new PyInt(1), new PyStr("a")]));
        Assert.NotEqual(new PyTuple([new PyInt(1)]), new PyTuple([new PyLong(1)]));
        Assert.Equal(new PyBytes(new byte[] { 1, 2 }), new PyBytes(new byte[] { 1, 2 }));
        Assert.Equal(
            new PyDict([new(new PyStr("k"), new PyInt(1))]),
            new PyDict([new(new PyStr("k"), new PyInt(1))]));
        Assert.Equal(new PyBigUnsigned([1, 2, 3]), new PyBigUnsigned([1, 2, 3]));
        Assert.Equal(
            new PyTuple([new PyInt(1)]).GetHashCode(),
            new PyTuple([new PyInt(1)]).GetHashCode());
        Assert.Same(PyValue.None, PyNone.Instance);
    }

    [Fact]
    public void A_server_value_survives_an_encode_decode_round_trip_as_the_client_sees_it()
    {
        var value = new PyTuple([
            new PyInt(-129), new PyInt(70_000), new PyLong(1), new PyReal(0.5), new PyReal(0),
            new PyStr("beyonce"), new PyStr("x"), new PyStr(""), new PyWString("日本語"),
            new PyList([PyBool.True]),
            new PyDict([new(new PyStr("k"), PyNone.Instance)]),
        ]);
        var decoded = Assert.IsType<PyTuple>(MarshalCodec.DecodeExact(MarshalCodec.Encode(value)));
        Assert.Equal(new PyInt(-129), decoded.Items[0]);
        Assert.Equal(new PyInt(70_000), decoded.Items[1]);
        Assert.Equal(new PyLong(1), decoded.Items[2]);
        Assert.Equal(new PyReal(0.5), decoded.Items[3]);
        Assert.Equal(new PyReal(0), decoded.Items[4]);
        Assert.Equal(new PyStr("beyonce"), decoded.Items[5]); // a string-table item stays a server string
        Assert.Equal(new PyBytes("x"u8.ToArray()), decoded.Items[6]); // anything else comes back as a Python str
        Assert.Equal(new PyWString("日本語"), decoded.Items[8]);
    }
}

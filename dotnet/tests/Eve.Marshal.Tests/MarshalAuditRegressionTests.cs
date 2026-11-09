using System.Numerics;

namespace Eve.Marshal.Tests;

public sealed class MarshalAuditRegressionTests
{
    [Fact]
    public void Pickled_data_ends_at_pickle_stop_and_preserves_the_next_marshaled_value()
    {
        // EveJS 261028.1 encodes this protocol-2 pickle without a marshal length.
        // The BINUNICODE argument deliberately contains 0x2e, which is not STOP.
        var pickle = new byte[] { 0x80, 0x02, 0x58, 0x03, 0, 0, 0, 0x2e, 0x78, 0x2e, 0x2e };
        var expectedWire = Convert.FromHexString("7e000000002c21800258030000002e782e2e09");
        var value = new PyTuple([new PyCPickled(pickle), new PyInt(1)]);

        Assert.Equal(expectedWire, MarshalCodec.Encode(value));

        var decoded = Assert.IsType<PyTuple>(MarshalCodec.DecodeExact(expectedWire));
        Assert.Equal(new PyCPickled(pickle), decoded.Items[0]);
        Assert.Equal(new PyInt(1), decoded.Items[1]);
    }

    [Fact]
    public void Variable_width_integers_decode_as_little_endian_twos_complement()
    {
        Assert.Equal(new PyInt(-1), MarshalCodec.DecodeExact(Convert.FromHexString("7e000000002f01ff")));
        Assert.Equal(new PyInt(-128), MarshalCodec.DecodeExact(Convert.FromHexString("7e000000002f0180")));
        Assert.Equal(new PyInt(-129), MarshalCodec.DecodeExact(Convert.FromHexString("7e000000002f027fff")));
        Assert.Equal(new PyBigInteger(-9_007_199_254_740_992),
            MarshalCodec.DecodeExact(Convert.FromHexString("7e000000002f07000000000000e0")));
        Assert.Equal(new PyBigInteger(long.MinValue),
            MarshalCodec.DecodeExact(Convert.FromHexString("7e000000002f080000000000000080")));
        Assert.Equal(new PyBigUnsigned([0, 0, 0, 0, 0, 0, 0, 0x80]),
            MarshalCodec.DecodeExact(Convert.FromHexString("7e000000002f09000000000000008000")));
    }

    [Fact]
    public void Variable_width_integer_encoding_keeps_sign_and_positive_padding()
    {
        var negativeMinimum = new PyBigInteger(new BigInteger(long.MinValue));
        var negativeWire = Convert.FromHexString("7e000000002f080000000000000080");
        Assert.Equal(negativeWire, MarshalCodec.Encode(negativeMinimum));
        Assert.Equal(negativeMinimum, MarshalCodec.DecodeExact(negativeWire));

        var positiveMaximum = new PyBigUnsigned(Enumerable.Repeat((byte)0xff, 8).ToArray());
        var positiveWire = Convert.FromHexString("7e000000002f09ffffffffffffffff00");
        Assert.Equal(positiveWire, MarshalCodec.Encode(positiveMaximum));
        Assert.Equal(positiveMaximum, MarshalCodec.DecodeExact(positiveWire));
    }

    [Fact]
    public void Inbound_value_reads_cannot_consume_the_save_table()
    {
        var malformed = Convert.FromHexString("7e0100000001000000");

        Assert.Throws<MarshalException>(() => MarshalCodec.DecodeInbound(malformed));
    }

    [Fact]
    public void Inbound_sized_values_cannot_be_truncated_at_the_input_end()
    {
        var malformed = Convert.FromHexString("7e00000000130a010203");

        Assert.Throws<MarshalException>(() => MarshalCodec.DecodeInbound(malformed));
    }

    [Theory]
    [InlineData("7e000000002201")]
    [InlineData("7e0000000022012d")]
    public void Object_ex_requires_both_section_terminators(string wireHex)
    {
        Assert.Throws<MarshalException>(() => MarshalCodec.DecodeInbound(Convert.FromHexString(wireHex)));
    }

    [Fact]
    public void A_reference_to_an_unpublished_save_slot_is_rejected()
    {
        var malformed = Convert.FromHexString("7e010000001502411b0101000000");

        Assert.Throws<MarshalException>(() => MarshalCodec.DecodeInbound(malformed));
    }

    [Fact]
    public void A_saved_list_is_published_before_its_children_are_decoded()
    {
        var wire = Convert.FromHexString("7e0100000055011b0101000000");

        var decoded = Assert.IsType<PyList>(MarshalCodec.DecodeExact(wire));

        Assert.Single(decoded.Items);
        Assert.Same(decoded, decoded.Items[0]);
    }

    [Fact]
    public void A_saved_dictionary_is_published_before_its_entries_are_decoded()
    {
        var wire = Convert.FromHexString("7e0100000056011b010f6b01000000");

        var decoded = Assert.IsType<PyDict>(MarshalCodec.DecodeExact(wire));

        var entry = Assert.Single(decoded.Entries);
        Assert.Equal(new PyBytes("k"u8.ToArray()), entry.Key);
        Assert.Same(decoded, entry.Value);
    }

    [Fact]
    public void A_saved_object_is_published_after_its_name_and_before_its_arguments()
    {
        var wire = Convert.FromHexString("7e010000005702016e1b0101000000");

        var decoded = Assert.IsType<PyObject>(MarshalCodec.DecodeExact(wire));

        Assert.Equal(new PyToken("n"), decoded.Name);
        Assert.Same(decoded, decoded.Args);
    }

    [Fact]
    public void A_saved_object_ex_is_published_after_its_header_and_before_its_sections()
    {
        var wire = Convert.FromHexString("7e0100000062011b012d2d01000000");

        var decoded = Assert.IsType<PyObjectEx>(MarshalCodec.DecodeExact(wire));

        Assert.Same(decoded, Assert.Single(decoded.List));
        Assert.Empty(decoded.Dict);
    }

    [Fact]
    public void A_tuple_cannot_reference_itself()
    {
        var wire = Convert.FromHexString("7e0100000065011b0101000000");

        Assert.Throws<MarshalException>(() => MarshalCodec.DecodeExact(wire));
    }

    [Fact]
    public void An_invalid_save_table_index_is_rejected_before_decoding()
    {
        var malformed = Convert.FromHexString("7e0100000001000000");
        malformed[^4] = 0;

        Assert.Throws<MarshalException>(() => MarshalCodec.DecodeInbound(malformed));
    }
}

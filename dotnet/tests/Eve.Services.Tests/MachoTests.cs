using Eve.Macho;
using Eve.Marshal;

namespace Eve.Services.Tests;

public class MachoTests
{
    private static PyValue Str(string text) => new PyBytes(System.Text.Encoding.UTF8.GetBytes(text));

    [Fact]
    public void Each_address_kind_decodes_from_its_field_order()
    {
        var node = MachoAddress.Decode(new PyTuple([new PyInt(1), new PyInt(1234), Str("svc"), new PyInt(9)]));
        Assert.Equal((AddressKind.Node, "svc"), (node.Kind, node.Service));
        Assert.Equal(new PyInt(1234), node.NodeId);
        Assert.Equal(new PyInt(9), node.CallId);

        var client = MachoAddress.Decode(new PyTuple([new PyInt(2), new PyInt(5), new PyInt(3), Str("svc")]));
        Assert.Equal((AddressKind.Client, "svc"), (client.Kind, client.Service));
        Assert.Equal(new PyInt(5), client.ClientId);
        Assert.Equal(new PyInt(3), client.CallId);

        var any = MachoAddress.Decode(new PyTuple([new PyInt(8), Str("svc"), new PyInt(4)]));
        Assert.Equal((AddressKind.Any, "svc"), (any.Kind, any.Service));

        var broadcast = MachoAddress.Decode(new PyTuple([new PyInt(4), Str("all"), new PyList([]), Str("t")]));
        Assert.Equal((AddressKind.Broadcast, "all", "t"), (broadcast.Kind, broadcast.BroadcastId, broadcast.IdType));
    }

    [Fact]
    public void An_address_may_arrive_wrapped_in_its_object_and_short_or_odd_ones_are_unknown()
    {
        var wrapped = new PyObject(new PyStr(MachoAddress.ClassName), new PyTuple([new PyInt(8), Str("svc"), PyNone.Instance]));
        Assert.Equal("svc", MachoAddress.Decode(wrapped).Service);
        Assert.Equal(AddressKind.Unknown, MachoAddress.Decode(PyNone.Instance).Kind);
        Assert.Equal(AddressKind.Unknown, MachoAddress.Decode(new PyTuple([])).Kind);
        Assert.Equal(AddressKind.Unknown, MachoAddress.Decode(new PyTuple([new PyInt(99)])).Kind);
        Assert.Equal(AddressKind.Unknown, MachoAddress.Decode(new PyTuple([new PyLong(8), Str("svc")])).Kind); // a BigInt is not a number
        var shortNode = MachoAddress.Decode(new PyTuple([new PyInt(1)]));
        Assert.Equal((AddressKind.Node, null), (shortNode.Kind, shortNode.Service));
        Assert.Equal(new PyInt(0), shortNode.NodeId);
    }

    [Fact]
    public void Addresses_encode_with_falsy_ids_as_none_like_the_reference()
    {
        var encoded = (PyObject)MachoAddress.Any("svc", new PyInt(0)).Encode();
        Assert.Equal(new PyStr(MachoAddress.ClassName), encoded.Name);
        Assert.Equal(new PyTuple([new PyInt(8), new PyStr("svc"), PyNone.Instance]), encoded.Args);

        var node = (PyObject)new MachoAddress(AddressKind.Node, null, null).Encode();
        Assert.Equal(new PyTuple([new PyInt(1), new PyInt(1), PyNone.Instance, PyNone.Instance]), node.Args);

        var client = (PyObject)new MachoAddress(AddressKind.Client, "s", new PyInt(7), ClientId: new PyLong(5)).Encode();
        Assert.Equal(new PyTuple([new PyInt(2), new PyLong(5), new PyInt(7), new PyStr("s")]), client.Args);

        var broadcast = (PyObject)new MachoAddress(AddressKind.Broadcast, BroadcastId: "b").Encode();
        Assert.Equal(new PyTuple([new PyInt(4), new PyStr("b"), new PyList([]), PyNone.Instance]), broadcast.Args);

        var unknown = (PyObject)new MachoAddress(AddressKind.Unknown).Encode();
        Assert.Equal(new PyTuple([new PyInt(8), PyNone.Instance, PyNone.Instance]), unknown.Args);
    }

    [Fact]
    public void A_packet_needs_at_least_six_fields_and_reads_its_type_as_a_number()
    {
        Assert.Null(MachoPacket.Decode(PyNone.Instance));
        Assert.Null(MachoPacket.Decode(new PyTuple([new PyInt(6), PyNone.Instance])));
        Assert.Null(MachoPacket.Decode(new PyList([])));

        var address = new PyTuple([new PyInt(8), Str("svc"), PyNone.Instance]);
        var packet = MachoPacket.Decode(new PyObject(Str("x"), new PyTuple([
            new PyInt(6), address, address, new PyInt(5), PyNone.Instance, PyNone.Instance])))!;
        Assert.Equal((6, "CALL_REQ", "svc"), (packet.Type, packet.TypeName, packet.Service));
        Assert.Equal(PyNone.Instance, packet.Oob);
        Assert.Equal(PyNone.Instance, packet.Extra13);

        var notANumber = MachoPacket.Decode(new PyTuple([new PyLong(6), address, address, PyNone.Instance, PyNone.Instance, PyNone.Instance]))!;
        Assert.Equal(0, notANumber.Type);
        Assert.Equal("UNKNOWN(99)", MachoMessage.TypeName(99));
    }

    private static PyValue Body(PyValue remoteObject, PyValue method, PyValue args, PyValue kwargs) =>
        new PyTuple([new PyTuple([new PyInt(0), new PySubStream(new PyTuple([remoteObject, method, args, kwargs]))])]);

    [Fact]
    public void A_call_request_is_read_from_the_body_the_client_sends()
    {
        var request = CallRequest.Decode(Body(new PyInt(1), Str("GetTime"), new PyTuple([new PyInt(7)]), PyNone.Instance))!;
        Assert.Equal("GetTime", request.Method);
        Assert.Equal([new PyInt(7)], request.Args);
        Assert.Equal(PyNone.Instance, request.Kwargs);
        Assert.Equal(new PyInt(1), request.RemoteObject);
    }

    [Theory]
    [MemberData(nameof(UnreadableBodies))]
    public void A_body_that_is_not_a_call_reads_as_unknown_or_nothing(PyValue? payload, bool none)
    {
        var request = CallRequest.Decode(payload);
        if (none)
        {
            Assert.Null(request);
            return;
        }
        Assert.Equal("unknown", request!.Method);
        Assert.Empty(request.Args);
    }

    public static IEnumerable<object?[]> UnreadableBodies()
    {
        yield return [null, true];
        yield return [PyNone.Instance, true];
        yield return [new PyTuple([]), true];
        yield return [new PyInt(5), true];
        yield return [new PyTuple([new PyInt(5)]), false];
        yield return [new PyTuple([new PyTuple([new PyInt(0), new PySubStream(new PyTuple([new PyInt(1)]))])]), false];
        yield return [new PyTuple([new PySubStream(new PyInt(3))]), false];
    }

    [Fact]
    public void A_body_without_a_substream_is_taken_as_the_call_itself()
    {
        var payload = new PyTuple([new PyTuple([Str("obj"), Str("Method"), new PyTuple([]), PyNone.Instance])]);
        var request = CallRequest.Decode(payload)!;
        Assert.Equal("Method", request.Method);
        Assert.Equal(Str("obj"), request.RemoteObject);
    }

    [Fact]
    public void A_bound_object_name_is_found_where_the_client_puts_it()
    {
        Assert.Equal("N=1:2", BoundObjects.ExtractName(Str("N=1:2")));
        Assert.Equal("N=1:2", BoundObjects.ExtractName(new PyStr("N=1:2")));
        Assert.Equal("N=1:2", BoundObjects.ExtractName(new PyTuple([PyNone.Instance, Str("N=1:2")])));
        Assert.Equal("N=3:4", BoundObjects.ExtractName(new PyWString("N=3:4")));
        Assert.Null(BoundObjects.ExtractName(Str("other")));
        Assert.Null(BoundObjects.ExtractName(new PyInt(1)));
        Assert.Null(BoundObjects.ExtractName(PyNone.Instance));
        // Deeper than six levels is not searched.
        PyValue deep = Str("N=9:9");
        for (var i = 0; i < 8; i++)
        {
            deep = new PyTuple([deep]);
        }
        Assert.Null(BoundObjects.ExtractName(deep));
    }

    [Fact]
    public void Truthiness_follows_javascript_including_for_empty_buffers()
    {
        Assert.False(JsSemantics.Truthy(PyNone.Instance));
        Assert.False(JsSemantics.Truthy(new PyInt(0)));
        Assert.False(JsSemantics.Truthy(new PyLong(0)));
        Assert.False(JsSemantics.Truthy(new PyReal(double.NaN)));
        Assert.False(JsSemantics.Truthy(new PyStr("")));
        Assert.False(JsSemantics.Truthy(PyBool.False));
        Assert.True(JsSemantics.Truthy(new PyBytes(Array.Empty<byte>()))); // an empty Buffer is an object
        Assert.True(JsSemantics.Truthy(new PyTuple([])));
        Assert.True(JsSemantics.Truthy(new PyWString("")));
        Assert.Equal(PyNone.Instance, JsSemantics.OrNull(new PyInt(0)));
        Assert.Equal(new PyInt(3), JsSemantics.OrNull(new PyInt(3)));
    }

    [Fact]
    public void Wide_strings_convert_to_their_contents() =>
        Assert.Equal("日本語", JsSemantics.ToJsString(new PyWString("日本語")));

    [Fact]
    public void What_the_reference_sends_back_of_a_received_value_is_normalised()
    {
        Assert.Equal(new PyBuffer(new byte[] { 1 }), JsSemantics.AsReceived(new PyBytes(new byte[] { 1 })));
        Assert.Equal(new PyInt(2), JsSemantics.AsReceived(new PyReal(2.0)));
        Assert.Equal(new PyReal(2.5), JsSemantics.AsReceived(new PyReal(2.5)));
        Assert.Equal(new PyLong(1L << 60), JsSemantics.AsReceived(new PyBigUnsigned([0, 0, 0, 0, 0, 0, 0, 0x10])));
        Assert.Equal(
            new PyTuple([new PyBuffer(new byte[] { 1 }), new PyList([new PyInt(3)])]),
            JsSemantics.AsReceived(new PyTuple([new PyBytes(new byte[] { 1 }), new PyList([new PyReal(3.0)])])));
        Assert.Throws<MarshalException>(() => JsSemantics.AsReceived(new PyReal(1e30)));
    }

    [Fact]
    public void User_errors_carry_their_message_and_class()
    {
        var error = MachoErrors.UserError("CustomNotify", ("notify", new PyStr("hi")));
        Assert.Equal(MachoErrorType.WrappedException, error.ErrorCode);
        Assert.Equal("eveexceptions.UserError", error.ClassName);
        Assert.True(error.IsUserError);
        Assert.Equal("CustomNotify", error.UserErrorMessage);
        Assert.False(MachoErrors.WrongMachoNode(2).IsUserError);
        Assert.Equal("", MachoErrors.WrongMachoNode(2).UserErrorMessage);
        Assert.Equal("carbon.common.script.net.objectCaching.CacheOK", MachoErrors.CacheOk().ClassName);
        Assert.Equal("", new MachoError(2, PyNone.Instance).ClassName);
    }

    [Fact]
    public void Cached_objects_report_the_size_of_their_compressed_part()
    {
        PyValue Cached(long compressed, byte[] pickle) => new PyObject(
            new PyStr("objectCaching.CachedObject"),
            new PyTuple([PyNone.Instance, PyNone.Instance, PyNone.Instance, PyNone.Instance, new PyBytes(pickle), new PyInt(compressed)]));

        Assert.Equal(3, CachedObjects.CompressedPart(Cached(1, [1, 2, 3])));
        Assert.Equal(0, CachedObjects.CompressedPart(Cached(0, [1, 2, 3])));
        Assert.Equal(5, CachedObjects.CompressedPart(new PyTuple([Cached(1, [1, 2, 3]), Cached(1, [4, 5])])));
        Assert.Equal(0, CachedObjects.CompressedPart(new PyInt(1)));
        Assert.Equal(0, CachedObjects.CompressedPart(PyNone.Instance));
    }
}

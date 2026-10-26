using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Xunit;

namespace Eve.Host.Tests;

public sealed class ServiceNotificationHostWireTests
{
    private sealed class Sink : IPacketSink
    {
        public List<PyValue> Packets { get; } = [];
        public void Send(PyValue packet) => Packets.Add(packet);
    }

    [Fact]
    public void A_failed_target_lock_reaches_the_client_target_service_with_one_target_id()
    {
        const long targetId = 9988400002151;
        var sink = new Sink();
        var session = new ClientSession(sink);
        session.SendServiceNotification("target", "FailLockTarget", [new PyLong(targetId)]);

        var packet = Assert.IsType<PyObject>(Assert.Single(sink.Packets));
        var packetItems = Assert.IsType<PyTuple>(packet.Args).Items;
        var address = Assert.IsType<PyTuple>(Assert.IsType<PyObject>(packetItems[2]).Args).Items;
        Assert.Equal(new PyInt(MachoAddress.TypeAny), address[0]);
        Assert.Equal(new PyStr("target"), address[1]);
        Assert.Equal(PyNone.Instance, address[2]);

        var envelope = Assert.IsType<PyTuple>(Assert.Single(Assert.IsType<PyTuple>(packetItems[4]).Items));
        var call = Assert.IsType<PyTuple>(MarshalCodec.Decode(Assert.IsType<PyBuffer>(envelope.Items[1]).Value));
        Assert.Equal(new PyInt(1), call.Items[0]);
        Assert.Equal("FailLockTarget", System.Text.Encoding.UTF8.GetString(Assert.IsType<PyBytes>(call.Items[1]).Value.Span));
        Assert.Equal(new PyLong(targetId), Assert.Single(Assert.IsType<PyTuple>(call.Items[2]).Items));

        var sequence = Assert.IsType<PyDict>(packetItems[5]).Entries.Single(entry =>
            JsSemantics.PlainText(entry.Key) == "sn").Value;
        Assert.Equal(new PyInt(0), sequence);
    }
}

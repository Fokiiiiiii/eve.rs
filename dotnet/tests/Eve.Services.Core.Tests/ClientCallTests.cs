using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Xunit;

namespace Eve.Services.Core.Tests;

/// <summary>A call the server makes to a client service (the agents' YesNo, SingleChoiceBox, GetQuantity): sent as a CallReq, answered once by the client's CallRsp.</summary>
public class ClientCallTests
{
    private sealed class Sink : IPacketSink
    {
        public List<PyValue> Packets { get; } = [];
        public void Send(PyValue packet) => Packets.Add(packet);
    }

    [Fact]
    public void A_client_call_is_sent_as_a_call_request_and_answered_once_by_its_call_id()
    {
        var sink = new Sink();
        var session = new ClientSession(sink) { ClientId = 7, UserId = 9 };
        var answers = new List<PyValue?>();

        Assert.True(session.SendClientCall("agents", "YesNo", [new PyInt(1)], answers.Add));

        var request = MachoPacket.Decode(Assert.Single(sink.Packets))!;
        Assert.Equal(MachoMessage.CallReq, request.Type);
        Assert.Equal("agents", request.Service);
        var callId = ((PyInt)request.CallId).Value;
        session.CompleteClientCall(callId, PyBool.True);
        session.CompleteClientCall(callId, PyBool.False);

        var answer = Assert.Single(answers);
        Assert.True(answer is PyBool { Value: true });
    }

    [Fact]
    public void The_call_arguments_are_a_tuple_as_the_engine_sends_them()
    {
        var sink = new Sink();
        var session = new ClientSession(sink) { ClientId = 7, UserId = 9 };

        Assert.True(session.SendClientCall("agents", "SingleChoiceBox", [new PyStr("title"), new PyInt(3009318)], _ => { }));

        // the payload is (0, substream of (1, method, args, kwargs)): the body decodes to the call, its arguments in a tuple
        var request = MachoPacket.Decode(Assert.Single(sink.Packets))!;
        var entry = Assert.IsType<PyTuple>(Assert.Single(Assert.IsType<PyTuple>(request.Payload).Items));
        var body = Assert.IsType<PyTuple>(Assert.IsType<PySubStream>(entry.Items[1]).Decoded);
        Assert.Equal("SingleChoiceBox", Assert.IsType<PyStr>(body.Items[1]).Value);
        var args = Assert.IsType<PyTuple>(body.Items[2]);
        Assert.Equal(2, args.Items.Count);
    }

    [Fact]
    public void A_session_without_a_sink_makes_no_client_call()
    {
        var session = new ClientSession(null);
        Assert.False(session.SendClientCall("agents", "YesNo", [], _ => throw new InvalidOperationException("must not run")));
    }
}

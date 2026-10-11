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
    public void A_session_without_a_sink_makes_no_client_call()
    {
        var session = new ClientSession(null);
        Assert.False(session.SendClientCall("agents", "YesNo", [], _ => throw new InvalidOperationException("must not run")));
    }
}

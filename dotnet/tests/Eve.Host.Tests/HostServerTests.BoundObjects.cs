using System.Collections.Concurrent;
using System.Text;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    private sealed class SharedBoundService : IService, IBoundObjectReleaseHandler
    {
        public const string Oid = "N=ProxyNode:session-owned-test";

        public string Name => "sessionOwnedSvc";

        public int BoundCalls { get; private set; }

        public ConcurrentQueue<long?> ReleasedBySession { get; } = new();

        public bool HasMethod(string method) => method is "MachoBindObject" or "ReadBound";

        public ValueTask<PyValue> CallAsync(ServiceCall call)
        {
            if (call.Method == "MachoBindObject")
            {
                var registration = new PySubStruct(new PySubStream(
                    new PyTuple([new PyStr(Oid), new PyLong(100)])));
                return ValueTask.FromResult<PyValue>(new PyTuple([registration, PyNone.Instance]));
            }

            BoundCalls++;
            return ValueTask.FromResult<PyValue>(new PyInt(41));
        }

        public void OnBoundObjectReleased(string oid, ClientSession? session) =>
            ReleasedBySession.Enqueue(session?.SessionId);
    }

    [Fact]
    public async Task Bound_oids_are_session_owned_and_released_after_the_last_holding_session_closes()
    {
        var service = new SharedBoundService();
        var opened = new ConcurrentDictionary<long, TaskCompletionSource<bool>>();
        var closed = new ConcurrentDictionary<long, TaskCompletionSource<bool>>();
        Start(
            configure: registry => registry.Register(service),
            maxConnections: 2,
            sessionOpened: session => opened.GetOrAdd(
                session.SessionId, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult(true),
            sessionClosed: session =>
            {
                closed.GetOrAdd(session.SessionId, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult(true);
                return Task.CompletedTask;
            });

        var first = await Handshaken(await ConnectAsync());
        var second = await Handshaken(await ConnectAsync());
        var firstSession = Open(101, 42);
        var secondSession = Open(102, 43);
        await first.SendAsync(firstSession);
        Assert.IsType<Send>(await first.ReceiveAsync());
        await opened.GetOrAdd(101, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).Task
            .WaitAsync(TimeSpan.FromSeconds(5));
        await second.SendAsync(secondSession);
        Assert.IsType<Send>(await second.ReceiveAsync());
        await opened.GetOrAdd(102, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).Task
            .WaitAsync(TimeSpan.FromSeconds(5));

        await first.SendAsync(new Packet(firstSession.SessionId, MarshalCodec.Encode(
            Packets.Call(firstSession, 1, service.Name, "MachoBindObject"))));
        Assert.Equal(MachoMessage.CallRsp, Command(Assert.IsType<Send>(await first.ReceiveAsync())));

        await second.SendAsync(new Packet(secondSession.SessionId, MarshalCodec.Encode(
            Packets.BoundCall(secondSession, 1, SharedBoundService.Oid, "ReadBound", new PyTuple([]), PyNone.Instance))));
        var foreignCall = Assert.IsType<Send>(await second.ReceiveAsync());
        Assert.Equal(MachoMessage.ErrorResponse, Command(foreignCall));
        Assert.Contains("UnMachoDestination", Encoding.Latin1.GetString(foreignCall.Payload.Span));
        Assert.Equal(0, service.BoundCalls);

        await second.SendAsync(new Packet(secondSession.SessionId, MarshalCodec.Encode(
            Packets.Call(secondSession, 2, service.Name, "MachoBindObject"))));
        Assert.Equal(MachoMessage.CallRsp, Command(Assert.IsType<Send>(await second.ReceiveAsync())));

        await first.SendAsync(new Packet(firstSession.SessionId, MarshalCodec.Encode(
            Packets.BoundCall(firstSession, 2, SharedBoundService.Oid, "ReadBound", new PyTuple([]), PyNone.Instance))));
        var firstRead = Assert.IsType<Send>(await first.ReceiveAsync());
        Assert.Equal(MachoMessage.CallRsp, Command(firstRead));
        Assert.Equal(new PyInt(41), Result(MarshalCodec.Decode(firstRead.Payload)));

        await second.SendAsync(new Packet(secondSession.SessionId, MarshalCodec.Encode(
            Packets.BoundCall(secondSession, 3, SharedBoundService.Oid, "ReadBound", new PyTuple([]), PyNone.Instance))));
        var secondRead = Assert.IsType<Send>(await second.ReceiveAsync());
        Assert.Equal(MachoMessage.CallRsp, Command(secondRead));
        Assert.Equal(new PyInt(41), Result(MarshalCodec.Decode(secondRead.Payload)));
        Assert.Equal(2, service.BoundCalls);

        first.Dispose();
        await closed.GetOrAdd(101, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).Task
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains(SharedBoundService.Oid, _testRegistry.BoundObjects.Keys);
        Assert.Empty(service.ReleasedBySession);

        second.Dispose();
        await closed.GetOrAdd(102, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).Task
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.DoesNotContain(SharedBoundService.Oid, _testRegistry.BoundObjects.Keys);
        Assert.Equal([102L], service.ReleasedBySession);
    }

    private static int Command(Send send) =>
        Assert.IsType<MachoPacket>(MachoPacket.Decode(MarshalCodec.Decode(send.Payload))).Type;
}

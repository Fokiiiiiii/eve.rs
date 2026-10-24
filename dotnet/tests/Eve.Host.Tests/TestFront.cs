using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Eve.Marshal;

namespace Eve.Host.Tests;

/// <summary>A scripted network front: speaks the host-link protocol over a socket.</summary>
internal sealed class TestFront : IDisposable
{
    private readonly Socket _socket;
    private readonly NetworkStream _stream;

    private TestFront(Socket socket)
    {
        _socket = socket;
        _stream = new NetworkStream(socket);
    }

    public static async Task<TestFront> ConnectAsync(IPEndPoint endpoint)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        await socket.ConnectAsync(endpoint);
        return new TestFront(socket);
    }

    public Task SendAsync(HostMessage message) => SendRawAsync(HostCodec.Encode(message));

    public async Task SendRawAsync(byte[] bytes)
    {
        await _stream.WriteAsync(bytes);
        await _stream.FlushAsync();
    }

    /// <summary>The next message, or null when the host closed the link.</summary>
    public async Task<HostMessage?> ReceiveOrEndAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var header = new byte[4];
        try
        {
            var read = 0;
            while (read < 4)
            {
                var count = await _stream.ReadAsync(header.AsMemory(read), timeout.Token);
                if (count == 0)
                {
                    return null;
                }
                read += count;
            }
            var payload = new byte[BinaryPrimitives.ReadUInt32LittleEndian(header)];
            await _stream.ReadExactlyAsync(payload, timeout.Token);
            return HostCodec.Decode(payload);
        }
        catch (IOException)
        {
            return null;
        }
    }

    public async Task<HostMessage> ReceiveAsync(CancellationToken cancellationToken = default) =>
        await ReceiveOrEndAsync(cancellationToken) ?? throw new InvalidOperationException("the host closed the link");

    public async Task<AuthReply> AuthenticateAsync(string user, byte[] hash, uint corr)
    {
        await SendAsync(new AuthRequest(corr, user, hash, "EN", "127.0.0.1"));
        return Assert.IsType<AuthReply>(await ReceiveAsync());
    }

    public void Dispose()
    {
        _stream.Dispose();
        _socket.Dispose();
    }
}

/// <summary>The packets a client sends.</summary>
internal static class Packets
{
    private static PyValue Packet(string className, PyValue[] head) => new PyObject(
        new PyStr("carbon.common.script.net.machoNetPacket." + className),
        new PyTuple([.. head, .. Enumerable.Repeat<PyValue>(PyNone.Instance, 14 - head.Length)]));

    private static PyValue Source(SessionOpen open, long callId) =>
        new PyTuple([new PyInt(2), new PyLong((long)open.ClientId), new PyInt(callId), PyNone.Instance]);

    public static PyValue Call(SessionOpen open, long callId, string service, string method, params PyValue[] args) =>
        CallWithKwargs(open, callId, service, method, new PyTuple(args), PyNone.Instance);

    public static PyValue CallWithKwargs(SessionOpen open, long callId, string service, string method, PyValue args, PyValue kwargs)
    {
        var body = new PyTuple([new PyInt(1), new PyStr(method), args, kwargs]);
        var payload = new PyTuple([new PyTuple([new PyInt(0), new PySubStream(body)])]);
        return Packet("CallReq", [new PyInt(6), Source(open, callId), new PyTuple([new PyInt(8), new PyStr(service), PyNone.Instance]), new PyInt(open.UserId), payload, PyNone.Instance]);
    }

    public static PyValue CallWithKwargs(SessionOpen open, long callId, string service, string method, PyValue args, PyValue kwargs)
    {
        var body = new PyTuple([new PyInt(1), new PyStr(method), args, kwargs]);
        var payload = new PyTuple([new PyTuple([new PyInt(0), new PySubStream(body)])]);
        return Packet("CallReq", [new PyInt(6), Source(open, callId), new PyTuple([new PyInt(8), new PyStr(service), PyNone.Instance]), new PyInt(open.UserId), payload, PyNone.Instance]);
    }

    public static PyValue BoundCall(SessionOpen open, long callId, string oid, string method, PyValue args, PyValue kwargs)
    {
        var body = new PyTuple([new PyStr(oid), new PyStr(method), args, kwargs]);
        var payload = new PyTuple([new PyTuple([new PyInt(0), new PySubStream(body)])]);
        return Packet("CallReq", [new PyInt(6), Source(open, callId), new PyTuple([new PyInt(8), PyNone.Instance, PyNone.Instance]), new PyInt(open.UserId), payload, PyNone.Instance]);
    }

    public static PyValue Ping(SessionOpen open, long callId) =>
        Packet("PingReq", [new PyInt(20), Source(open, callId), new PyTuple([new PyInt(8), PyNone.Instance, PyNone.Instance]), new PyInt(open.UserId), new PyTuple([]), PyNone.Instance]);
}

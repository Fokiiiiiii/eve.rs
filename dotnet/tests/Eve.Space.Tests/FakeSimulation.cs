using System.Net.Sockets;

namespace Eve.Space.Tests;

/// <summary>The simulation's end of the link, played by a test: it listens on a Unix socket, answers hello, and answers what the test tells it to.</summary>
internal sealed class FakeSimulation : IAsyncDisposable
{
    private readonly Socket _listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Stream> _streams = [];
    private readonly object _gate = new();
    private Task? _accepting;

    public FakeSimulation()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"eve-sim-test-{Guid.NewGuid():N}.sock");
    }

    public string Path { get; }

    /// <summary>What the host has said, in order (hello included).</summary>
    public List<SimMessage> Heard { get; } = [];

    /// <summary>What to say to each message (a <c>Hello</c> is always answered).</summary>
    public Func<SimMessage, IEnumerable<SimMessage>> Reply { get; set; } = _ => [];

    public ushort AckVersion { get; set; } = SimCodec.ProtocolVersion;

    public int Connections { get; private set; }

    public void Listen()
    {
        _listener.Bind(new UnixDomainSocketEndPoint(Path));
        _listener.Listen(4);
        _accepting = Task.Run(AcceptAsync);
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var socket = await _listener.AcceptAsync(_stop.Token);
                var stream = new NetworkStream(socket, ownsSocket: true);
                lock (_gate)
                {
                    _streams.Add(stream);
                    Connections++;
                }
                _ = Task.Run(() => ServeAsync(stream));
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ServeAsync(Stream stream)
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var length = new byte[4];
                await stream.ReadExactlyAsync(length, _stop.Token);
                var payload = new byte[BitConverter.ToUInt32(length)];
                await stream.ReadExactlyAsync(payload, _stop.Token);
                var message = SimCodec.Decode(payload);
                lock (_gate)
                {
                    Heard.Add(message);
                }
                var answers = message is SimHello ? [new SimHelloAck(AckVersion, 4)] : Reply(message);
                foreach (var answer in answers)
                {
                    await stream.WriteAsync(SimCodec.Encode(answer), _stop.Token);
                }
            }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    /// <summary>Says something to every connected host.</summary>
    public async Task SayAsync(SimMessage message)
    {
        Stream[] streams;
        lock (_gate)
        {
            streams = [.. _streams];
        }
        foreach (var stream in streams)
        {
            await stream.WriteAsync(SimCodec.Encode(message));
        }
    }

    /// <summary>Drops every connection (the simulation "crashed"); it keeps listening.</summary>
    public void Drop()
    {
        lock (_gate)
        {
            foreach (var stream in _streams)
            {
                stream.Dispose();
            }
            _streams.Clear();
        }
    }

    public List<SimMessage> HeardNow()
    {
        lock (_gate)
        {
            return [.. Heard];
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        Drop();
        _listener.Dispose();
        if (_accepting is not null)
        {
            try
            {
                await _accepting;
            }
            catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException)
            {
            }
        }
        File.Delete(Path);
    }
}

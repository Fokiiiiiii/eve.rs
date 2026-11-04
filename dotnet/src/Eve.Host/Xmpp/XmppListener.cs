using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Channels;

namespace Eve.Host.Xmpp;

/// <summary>
/// The chat port: accepts client streams, optionally over TLS. Each connection is isolated: a malformed stream, a stanza over
/// the limit, a full outbound queue or a missed authentication deadline closes that connection only.
/// </summary>
public sealed class XmppListener : IAsyncDisposable
{
    public const int MaxOutboundStanzas = 2048;
    public static readonly TimeSpan AuthenticationDeadline = TimeSpan.FromSeconds(30);

    private readonly TcpListener _listener;
    private readonly XmppChatHub _hub;
    private readonly X509Certificate2? _certificate;
    private readonly CancellationTokenSource _stopping = new();
    private readonly HashSet<Task> _connections = [];
    private readonly Task _accepting;

    private XmppListener(TcpListener listener, XmppChatHub hub, X509Certificate2? certificate)
    {
        _listener = listener;
        _hub = hub;
        _certificate = certificate;
        _accepting = AcceptAsync();
    }

    /// <summary>The port the listener is bound to (the one asked for, or the one the system chose for 0).</summary>
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>True when the connections are wrapped in TLS.</summary>
    public bool UsesTls => _certificate is not null;

    public static XmppListener Start(IPAddress address, int port, XmppChatHub hub, X509Certificate2? certificate = null)
    {
        var listener = new TcpListener(address, port);
        listener.Start();
        return new XmppListener(listener, hub, certificate);
    }

    public async ValueTask DisposeAsync()
    {
        _stopping.Cancel();
        _listener.Stop();
        await _accepting.ContinueWith(_ => { }, TaskScheduler.Default);
        Task[] open;
        lock (_connections)
            open = [.. _connections];
        await Task.WhenAll(open).ContinueWith(_ => { }, TaskScheduler.Default);
        _stopping.Dispose();
    }

    private async Task AcceptAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stopping.Token);
            }
            catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                break;
            }
            var serving = ServeAsync(client);
            lock (_connections)
                _connections.Add(serving);
            _ = serving.ContinueWith(task =>
            {
                lock (_connections)
                    _connections.Remove(task);
            }, TaskScheduler.Default);
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using var socket = client;
        var connection = new XmppConnection(_stopping.Token);
        var session = new XmppClientSession(_hub, connection.Send, connection.CloseWith);
        Task? writing = null;
        try
        {
            Stream stream = socket.GetStream();
            if (_certificate is not null)
            {
                var secure = new SslStream(stream);
                await secure.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = _certificate,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    ClientCertificateRequired = false,
                }, connection.Cancellation).WaitAsync(AuthenticationDeadline, connection.Cancellation);
                stream = secure;
            }
            writing = connection.WriteAsync(stream);
            await ReadAsync(stream, session, connection);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or TimeoutException or AuthenticationException or ObjectDisposedException)
        {
            connection.Abort();
        }
        finally
        {
            session.OnClosed();
            // a graceful close drains the queue (the closing tag and any stream error go out first); a stuck socket is cut after a short wait
            connection.Complete();
            if (writing is not null)
                await writing.WaitAsync(TimeSpan.FromSeconds(5)).ContinueWith(_ => { }, TaskScheduler.Default);
            connection.Abort();
        }
    }

    private async Task ReadAsync(Stream stream, XmppClientSession session, XmppConnection connection)
    {
        var parser = new XmppStreamParser();
        var buffer = new byte[4096];
        while (true)
        {
            var wait = session.Authenticated ? Timeout.InfiniteTimeSpan : AuthenticationDeadline;
            var read = await stream.ReadAsync(buffer, connection.Cancellation).AsTask().WaitAsync(wait, connection.Cancellation);
            if (read == 0)
                return;
            try
            {
                foreach (var stanzaEvent in parser.Feed(buffer.AsSpan(0, read)))
                {
                    switch (stanzaEvent)
                    {
                        case XmppStreamOpened:
                            session.OnStreamOpened();
                            break;
                        case XmppStanza stanza:
                            await session.OnStanzaAsync(stanza.Element, connection.Cancellation);
                            if (session.ConsumeStreamRestartRequest())
                                parser.Restart();
                            break;
                        case XmppStreamClosed:
                            connection.CloseWith(null);
                            return;
                    }
                }
            }
            catch (XmppStreamException error)
            {
                connection.CloseWith(error.Condition);
                return;
            }
        }
    }

    /// <summary>One client socket: a bounded outbound queue written by one task, so the chat hub never blocks on a socket.</summary>
    private sealed class XmppConnection
    {
        private readonly Channel<string> _outbound = Channel.CreateBounded<string>(MaxOutboundStanzas);
        private readonly CancellationTokenSource _cancellation;
        private volatile bool _closing;

        public XmppConnection(CancellationToken stopping)
        {
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        }

        public CancellationToken Cancellation => _cancellation.Token;

        /// <summary>Queues a stanza. A queue that is full (the client is not reading) closes the connection.</summary>
        public void Send(string stanza)
        {
            if (!_closing && !_outbound.Writer.TryWrite(stanza))
                Abort();
        }

        /// <summary>Queues a last stanza (a stream error or the closing tag), then closes once it is written.</summary>
        public void CloseWith(string? streamErrorCondition)
        {
            if (_closing)
                return;
            _closing = true;
            if (streamErrorCondition is not null)
                _outbound.Writer.TryWrite($"<stream:error><{streamErrorCondition} xmlns='{XmppXml.StreamsNamespace}'/></stream:error>");
            _outbound.Writer.TryWrite("</stream:stream>");
            _outbound.Writer.TryComplete();
        }

        /// <summary>Completes the outbound queue after the read side ends so its writer can drain.</summary>
        public void Complete()
        {
            _closing = true;
            _outbound.Writer.TryComplete();
        }

        public void Abort()
        {
            _closing = true;
            _outbound.Writer.TryComplete();
            _cancellation.Cancel();
        }

        public async Task WriteAsync(Stream socket)
        {
            try
            {
                await foreach (var stanza in _outbound.Reader.ReadAllAsync(_cancellation.Token))
                {
                    await socket.WriteAsync(Encoding.UTF8.GetBytes(stanza), _cancellation.Token);
                    await socket.FlushAsync(_cancellation.Token);
                }
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException)
            {
            }
            finally
            {
                _closing = true;
                _outbound.Writer.TryComplete();
                _cancellation.Cancel();
            }
        }
    }
}

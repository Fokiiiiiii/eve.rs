using System.Threading.Channels;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Host;

/// <summary>Serializes presence notices after their character-session map changes.</summary>
internal sealed class OnlineStatusNotifications : IAsyncDisposable
{
    private abstract record Work;
    private sealed record Changed(long CharacterId, bool Online, IReadOnlyList<(long CharacterId, ClientSession Session)> Recipients) : Work;
    private sealed record Barrier(TaskCompletionSource Completion) : Work;

    private readonly Channel<Work> _queue = Channel.CreateUnbounded<Work>(new UnboundedChannelOptions
    {
        SingleReader = true,
        AllowSynchronousContinuations = false,
    });
    private readonly Func<long, CancellationToken, ValueTask<IReadOnlyCollection<long>>> _readWatchlist;
    private readonly Func<long, ClientSession?> _currentSession;
    private readonly Action<string> _log;
    private readonly Task _worker;

    public OnlineStatusNotifications(
        Func<long, CancellationToken, ValueTask<IReadOnlyCollection<long>>> readWatchlist,
        Func<long, ClientSession?> currentSession,
        Action<string>? log = null)
    {
        _readWatchlist = readWatchlist;
        _currentSession = currentSession;
        _log = log ?? (message => Console.Error.WriteLine($"eve-host: {message}"));
        _worker = RunAsync();
    }

    public void Enqueue(long characterId, bool online, IReadOnlyList<(long CharacterId, ClientSession Session)> recipients) =>
        _queue.Writer.TryWrite(new Changed(characterId, online, recipients));

    internal async Task FlushAsync()
    {
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _queue.Writer.WriteAsync(new Barrier(barrier));
        await barrier.Task;
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        await _worker;
    }

    private async Task RunAsync()
    {
        await foreach (var work in _queue.Reader.ReadAllAsync())
        {
            try
            {
                if (work is Changed changed)
                    await NotifyAsync(changed);
                else if (work is Barrier barrier)
                    barrier.Completion.TrySetResult();
            }
            catch (Exception error)
            {
                _log($"online-status notification failed: {error.Message}");
                if (work is Barrier barrier)
                    barrier.Completion.TrySetException(error);
            }
        }
    }

    private async Task NotifyAsync(Changed change)
    {
        if (change.CharacterId <= 0)
            return;

        var recipients = change.Recipients.Where(recipient =>
            recipient.CharacterId > 0 && recipient.CharacterId != change.CharacterId &&
            IsCurrent(recipient.CharacterId, recipient.Session)).ToArray();
        if (recipients.Length == 0)
            return;

        var targetWatchlist = await _readWatchlist(change.CharacterId, CancellationToken.None);
        foreach (var (observerId, session) in recipients)
        {
            try
            {
                if (!targetWatchlist.Contains(observerId) ||
                    !(await _readWatchlist(observerId, CancellationToken.None)).Contains(change.CharacterId) ||
                    !IsCurrent(observerId, session))
                    continue;

                session.SendNotification(
                    change.Online ? "OnContactLoggedOn" : "OnContactLoggedOff",
                    "clientID",
                    new PyLong(change.CharacterId));
            }
            catch (Exception error)
            {
                _log($"online-status notification to {observerId} failed: {error.Message}");
            }
        }
    }

    private bool IsCurrent(long characterId, ClientSession session) =>
        !session.IsClosed && session.CharacterId == characterId && ReferenceEquals(_currentSession(characterId), session);
}

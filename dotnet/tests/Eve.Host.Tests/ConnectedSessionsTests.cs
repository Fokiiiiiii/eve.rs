using Eve.Services;

namespace Eve.Host.Tests;

public sealed class ConnectedSessionsTests
{
    [Fact]
    public void A_new_session_replaces_the_account_session_and_old_close_cannot_remove_it()
    {
        var connected = new ConnectedSessions();
        string? disconnectReason = null;
        var old = Session(101, 7, reason => disconnectReason = reason);
        var next = Session(202, 7, reason => Assert.Equal("unexpected disconnect", reason));

        Assert.Null(connected.AddExclusive(old));
        Assert.Same(old, connected.AddExclusive(next));
        old.RequestDisconnect("Another client logged in to this account.");
        Assert.Equal("Another client logged in to this account.", disconnectReason);
        Assert.Same(next, Assert.Single(connected.Snapshot()));

        old.Close();
        connected.Remove(old);

        Assert.Same(next, Assert.Single(connected.Snapshot()));
    }

    [Fact]
    public async Task Concurrent_logins_leave_one_account_session()
    {
        var connected = new ConnectedSessions();
        var disconnected = new System.Collections.Concurrent.ConcurrentBag<string>();
        var sessions = Enumerable.Range(1, 16).Select(id => Session(id, 9, disconnected.Add)).ToArray();

        await Task.WhenAll(sessions.Select(session => Task.Run(() =>
            connected.AddExclusive(session)?.RequestDisconnect("Another client logged in to this account."))));

        Assert.Single(connected.Snapshot());
        Assert.Equal(sessions.Length - 1, disconnected.Count);
    }

    private static ClientSession Session(long sessionId, long accountId, Action<string> onDisconnect)
    {
        var session = new ClientSession { SessionId = sessionId };
        session.UserId = accountId;
        session.AttachDisconnectClient(onDisconnect);
        return session;
    }
}

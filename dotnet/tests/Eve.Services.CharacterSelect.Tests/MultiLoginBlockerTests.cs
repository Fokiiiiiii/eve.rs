using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.CharacterSelect.Tests;

/// <summary>The SSO multi-login check resolves current clone grades and atomically admits accounts per computer.</summary>
public class MultiLoginBlockerTests
{
    private const long Alpha = MultiLoginBlockerService.CloneGradeAlpha;
    private const long Omega = 1;

    private static ClientSession SessionOf(long accountId, long sessionId) => new(new Capture())
    {
        SessionId = sessionId,
        ClientId = sessionId + 1000,
        UserId = accountId,
    };

    private static async ValueTask<PyValue> LoginAsync(MultiLoginBlockerService service, ClientSession session, string hash) =>
        await ((IService)service).CallAsync(new ServiceCall { Method = "Login", Args = [new PyStr(hash)], Session = session });

    private static async Task<PyValue[]> ConcurrentLoginsAsync(
        MultiLoginBlockerService service,
        params (ClientSession Session, string Hash)[] attempts)
    {
        using var ready = new Barrier(attempts.Length);
        var calls = attempts.Select(attempt => Task.Factory.StartNew(
            async () =>
            {
                ready.SignalAndWait();
                return await LoginAsync(service, attempt.Session, attempt.Hash);
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap());
        return await Task.WhenAll(calls);
    }

    private static MultiLoginBlockerService Grades(Dictionary<long, long> gradeByAccount) =>
        new(session => gradeByAccount.GetValueOrDefault(session.UserId ?? 0, Omega));

    [Fact]
    public async Task A_login_without_a_computer_hash_is_allowed_and_recorded_nowhere()
    {
        var service = Grades(new() { [1] = Alpha });

        Assert.Equal(PyBool.True, await LoginAsync(service, SessionOf(1, 10), ""));
        Assert.Equal(0, service.Count);
    }

    [Fact]
    public async Task Concurrent_omega_accounts_on_one_computer_are_both_allowed()
    {
        var service = Grades(new() { [1] = Omega, [2] = Omega });

        var replies = await ConcurrentLoginsAsync(
            service,
            (SessionOf(1, 10), "ABC"),
            (SessionOf(2, 20), "abc "));

        Assert.All(replies, reply => Assert.Equal(PyBool.True, reply));
        Assert.Equal(2, service.Count);
    }

    [Fact]
    public async Task Concurrent_alpha_accounts_on_one_computer_allow_exactly_one()
    {
        var service = Grades(new() { [1] = Alpha, [2] = Alpha });

        var replies = await ConcurrentLoginsAsync(
            service,
            (SessionOf(1, 10), "hash"),
            (SessionOf(2, 20), "hash"));

        Assert.Equal(1, replies.Count(reply => reply == PyBool.True));
        Assert.Equal(1, replies.Count(reply => reply == PyBool.False));
        Assert.Equal(1, service.Count);
    }

    [Fact]
    public async Task Concurrent_alpha_and_omega_accounts_on_one_computer_allow_exactly_one()
    {
        var service = Grades(new() { [1] = Alpha, [2] = Omega });

        var replies = await ConcurrentLoginsAsync(
            service,
            (SessionOf(1, 10), "hash"),
            (SessionOf(2, 20), "hash"));

        Assert.Equal(1, replies.Count(reply => reply == PyBool.True));
        Assert.Equal(1, replies.Count(reply => reply == PyBool.False));
        Assert.Equal(1, service.Count);
    }

    [Fact]
    public async Task An_alpha_account_on_a_computer_already_used_by_another_account_is_refused()
    {
        var service = Grades(new() { [1] = Omega, [2] = Alpha });
        Assert.Equal(PyBool.True, await LoginAsync(service, SessionOf(1, 10), "hash"));

        Assert.Equal(PyBool.False, await LoginAsync(service, SessionOf(2, 20), "hash"));
    }

    [Fact]
    public async Task An_omega_account_is_refused_when_the_other_account_on_the_computer_is_alpha()
    {
        var service = Grades(new() { [1] = Alpha, [2] = Omega });
        Assert.Equal(PyBool.True, await LoginAsync(service, SessionOf(1, 10), "hash"));

        Assert.Equal(PyBool.False, await LoginAsync(service, SessionOf(2, 20), "hash"));
    }

    [Fact]
    public async Task The_same_account_on_two_sessions_and_other_computers_are_allowed()
    {
        var service = Grades(new() { [1] = Alpha, [2] = Alpha });
        Assert.Equal(PyBool.True, await LoginAsync(service, SessionOf(1, 10), "hash"));

        Assert.Equal(PyBool.True, await LoginAsync(service, SessionOf(1, 11), "hash"));
        Assert.Equal(PyBool.True, await LoginAsync(service, SessionOf(2, 20), "another"));
    }

    [Fact]
    public async Task An_existing_alpha_session_stops_blocking_after_its_account_becomes_omega()
    {
        var gradeByAccount = new Dictionary<long, long> { [1] = Alpha, [2] = Omega };
        var service = Grades(gradeByAccount);
        var existing = SessionOf(1, 10);
        Assert.Equal(PyBool.True, await LoginAsync(service, existing, "hash"));

        gradeByAccount[1] = Omega;

        Assert.Equal(PyBool.True, await LoginAsync(service, SessionOf(2, 20), "hash"));
        Assert.Equal(2, service.Count);
    }

    [Fact]
    public async Task A_closed_session_no_longer_blocks_another_account()
    {
        var service = Grades(new() { [1] = Alpha, [2] = Omega });
        var first = SessionOf(1, 10);
        Assert.Equal(PyBool.True, await LoginAsync(service, first, "hash"));
        Assert.Equal(PyBool.False, await LoginAsync(service, SessionOf(2, 20), "hash"));

        first.Close();

        Assert.Equal(0, service.Count);
        Assert.Equal(PyBool.True, await LoginAsync(service, SessionOf(2, 21), "hash"));
    }
}

using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.CharacterSelect.Tests;

/// <summary>The SSO multi-login check: an Alpha account and another account on the same computer are refused; the rest are allowed.</summary>
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
    public async Task Two_omega_accounts_on_one_computer_are_both_allowed()
    {
        var service = Grades(new() { [1] = Omega, [2] = Omega });

        Assert.Equal(PyBool.True, await LoginAsync(service, SessionOf(1, 10), "ABC"));
        Assert.Equal(PyBool.True, await LoginAsync(service, SessionOf(2, 20), "abc "));
        Assert.Equal(2, service.Count);
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

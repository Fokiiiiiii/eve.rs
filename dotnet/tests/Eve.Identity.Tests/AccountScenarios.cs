namespace Eve.Identity.Tests;

/// <summary>
/// What every account directory must do. The in-memory and the PostgreSQL directories run the same
/// scenarios, so they cannot drift apart.
/// </summary>
internal static class AccountScenarios
{
    public delegate IAccountDirectory Make(AccountOptions options);

    private static LoginAttempt Attempt(string user, params byte[] hash) => new(user, hash, "EN", "127.0.0.1");

    private static ValueTask<LoginResult> Login(IAccountDirectory directory, string user, params byte[] hash) =>
        directory.LoginAsync(Attempt(user, hash), CancellationToken.None);

    public static async Task First_login_creates_the_account_and_then_needs_the_same_hash(Make make)
    {
        var directory = make(new AccountOptions { AutoCreate = true });
        var created = Assert.IsType<LoggedIn>(await Login(directory, "pilot", 1, 2, 3));
        Assert.Equal(1u, created.AccountId);
        Assert.Equal("GB", created.CountryCode);
        Assert.Equal(0UL, created.Role);

        Assert.Equal(created, await Login(directory, "pilot", 1, 2, 3));
        Assert.Equal(new LoginRefused("LoginAuthFailed"), await Login(directory, "pilot", 9, 9, 9));
        Assert.Equal(created, await Login(directory, "pilot", 1, 2, 3)); // a refusal changed nothing
        Assert.Equal(2u, Assert.IsType<LoggedIn>(await Login(directory, "other", 4)).AccountId);
    }

    public static async Task An_account_can_be_looked_up_by_its_id(Make make)
    {
        var directory = make(new AccountOptions { AutoCreate = true });
        var lookup = Assert.IsAssignableFrom<IAccountLookup>(directory);
        var before = DateTimeOffset.UtcNow.AddMinutes(-1);
        var pilot = Assert.IsType<LoggedIn>(await Login(directory, "pilot", 1, 2, 3));
        var other = Assert.IsType<LoggedIn>(await Login(directory, "other", 4));

        var found = await lookup.FindAsync(pilot.AccountId, CancellationToken.None);
        Assert.NotNull(found);
        Assert.Equal(pilot.AccountId, found.Id);
        Assert.Equal("pilot", found.UserName);
        Assert.InRange(found.CreatedAt, before, DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.Equal("other", (await lookup.FindAsync(other.AccountId, CancellationToken.None))!.UserName);
        Assert.Null(await lookup.FindAsync(999, CancellationToken.None));
    }

    public static async Task A_hash_of_another_length_is_not_the_hash(Make make)
    {
        var directory = make(new AccountOptions { AutoCreate = true });
        await Login(directory, "pilot", 1, 2, 3);
        Assert.IsType<LoginRefused>(await Login(directory, "pilot", 1, 2));
        Assert.IsType<LoginRefused>(await Login(directory, "pilot", 1, 2, 3, 0));
        Assert.IsType<LoginRefused>(await Login(directory, "pilot"));
    }

    public static async Task Skipping_the_password_check_accepts_any_hash(Make make)
    {
        var directory = make(new AccountOptions { SkipPasswordCheck = true, AutoCreate = true });
        var first = Assert.IsType<LoggedIn>(await Login(directory, "pilot", 1));
        Assert.Equal(first, await Login(directory, "pilot", 2));
    }

    public static async Task Unknown_users_are_refused_when_auto_creation_is_off(Make make)
    {
        var directory = make(new AccountOptions());
        Assert.Equal(new LoginRefused("LoginAuthFailed"), await Login(directory, "nobody", 1));
    }

    public static async Task New_accounts_get_the_configured_role_and_country(Make make)
    {
        var directory = make(new AccountOptions { AutoCreate = true, NewAccountRole = 0x0100_0000_0000_0040, CountryCode = "JP" });
        var login = Assert.IsType<LoggedIn>(await Login(directory, "gm", 5));
        Assert.Equal(0x0100_0000_0000_0040UL, login.Role);
        Assert.Equal("JP", login.CountryCode);
    }

    public static async Task User_names_are_exact_not_folded(Make make)
    {
        var directory = make(new AccountOptions { AutoCreate = true });
        var lower = Assert.IsType<LoggedIn>(await Login(directory, "pilot", 1));
        var upper = Assert.IsType<LoggedIn>(await Login(directory, "Pilot", 1));
        Assert.NotEqual(lower.AccountId, upper.AccountId);
    }

    public static async Task Names_that_cannot_be_accounts_are_refused(Make make)
    {
        var directory = make(new AccountOptions { AutoCreate = true });
        foreach (var name in new[] { "", "   ", "\t", "a\0b", new string('x', AccountNames.MaxLength + 1) })
        {
            Assert.Equal(new LoginRefused("LoginAuthFailed"), await Login(directory, name, 1));
        }
        Assert.IsType<LoggedIn>(await Login(directory, new string('x', AccountNames.MaxLength), 1));
        Assert.IsType<LoggedIn>(await Login(directory, "パイロット", 1));
    }

    public static async Task Concurrent_first_logins_of_one_name_create_one_account(Make make)
    {
        var directory = make(new AccountOptions { AutoCreate = true });
        var results = await Task.WhenAll(Enumerable.Range(0, 40).Select(_ => Login(directory, "racer", 7, 7).AsTask()));
        var ids = results.Select(r => Assert.IsType<LoggedIn>(r).AccountId).Distinct().ToList();
        Assert.Single(ids);
    }

    public static async Task Different_names_get_different_ids_concurrently(Make make)
    {
        var directory = make(new AccountOptions { AutoCreate = true });
        var results = await Task.WhenAll(Enumerable.Range(0, 40).Select(i => Login(directory, $"user{i}", (byte)i).AsTask()));
        var ids = results.Select(r => Assert.IsType<LoggedIn>(r).AccountId).ToList();
        Assert.Equal(40, ids.Distinct().Count());
        Assert.Equal(Enumerable.Range(1, 40).Select(i => (uint)i), ids.Order());
    }

    public static async Task A_ban_outranks_everything_else(Make make, Func<string, Task> ban)
    {
        var directory = make(new AccountOptions { AutoCreate = true });
        Assert.IsType<LoggedIn>(await Login(directory, "pilot", 1));
        await ban("pilot");
        Assert.Equal(new LoginRefused("ACCOUNTBANNED"), await Login(directory, "pilot", 1));
        Assert.Equal(new LoginRefused("ACCOUNTBANNED"), await Login(directory, "pilot", 2)); // even a wrong password
        var open = make(new AccountOptions { SkipPasswordCheck = true, AutoCreate = true });
        Assert.Equal(new LoginRefused("ACCOUNTBANNED"), await Login(open, "pilot", 3));
    }
}

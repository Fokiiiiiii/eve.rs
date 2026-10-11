using Npgsql;

namespace Eve.Identity.Tests;

public class PgAccountTests
{
    private static async Task With(Func<PgSchema, AccountScenarios.Make, Task> body)
    {
        await using var schema = await PgSchema.CreateAsync();
        await body(schema, options => new PgAccountDirectory(schema.Source, options, schema.Name));
    }

    [PgFact]
    public Task First_login_creates_the_account_and_then_needs_the_same_hash() => With((_, make) => AccountScenarios.First_login_creates_the_account_and_then_needs_the_same_hash(make));

    [PgFact]
    public Task New_accounts_are_stored_until_2100_and_an_operator_can_set_an_earlier_end() =>
        With((_, make) => AccountScenarios.New_accounts_are_stored_until_2100_and_an_operator_can_set_an_earlier_end(make));

    [PgFact]
    public Task An_account_can_be_looked_up_by_its_id() => With((_, make) => AccountScenarios.An_account_can_be_looked_up_by_its_id(make));

    [PgFact]
    public Task A_hash_of_another_length_is_not_the_hash() => With((_, make) => AccountScenarios.A_hash_of_another_length_is_not_the_hash(make));

    [PgFact]
    public Task Skipping_the_password_check_accepts_any_hash() => With((_, make) => AccountScenarios.Skipping_the_password_check_accepts_any_hash(make));

    [PgFact]
    public Task Unknown_users_are_refused_when_auto_creation_is_off() => With((_, make) => AccountScenarios.Unknown_users_are_refused_when_auto_creation_is_off(make));

    [PgFact]
    public Task New_accounts_get_the_configured_role_and_country() => With((_, make) => AccountScenarios.New_accounts_get_the_configured_role_and_country(make));

    [PgFact]
    public Task User_names_are_exact_not_folded() => With((_, make) => AccountScenarios.User_names_are_exact_not_folded(make));

    [PgFact]
    public Task Names_that_cannot_be_accounts_are_refused() => With((_, make) => AccountScenarios.Names_that_cannot_be_accounts_are_refused(make));

    [PgFact]
    public Task Concurrent_first_logins_of_one_name_create_one_account() => With((_, make) => AccountScenarios.Concurrent_first_logins_of_one_name_create_one_account(make));

    [PgFact]
    public Task Different_names_get_different_ids_concurrently() => With((_, make) => AccountScenarios.Different_names_get_different_ids_concurrently(make));

    [PgFact]
    public Task A_ban_outranks_everything_else() => With((schema, make) => AccountScenarios.A_ban_outranks_everything_else(
        make, user => schema.ExecuteAsync($"UPDATE {schema.Name}.accounts SET banned = true WHERE user_name = $1", user)));

    [PgFact]
    public async Task Accounts_survive_a_new_directory_over_the_same_database()
    {
        await using var schema = await PgSchema.CreateAsync();
        var first = new PgAccountDirectory(schema.Source, new AccountOptions { AutoCreate = true }, schema.Name);
        var created = await first.LoginAsync(new LoginAttempt("pilot", new byte[] { 1, 2 }, "EN", "x"), CancellationToken.None);
        var second = new PgAccountDirectory(schema.Source, new AccountOptions { AutoCreate = true }, schema.Name);
        Assert.Equal(created, await second.LoginAsync(new LoginAttempt("pilot", new byte[] { 1, 2 }, "EN", "x"), CancellationToken.None));
        Assert.IsType<LoginRefused>(await second.LoginAsync(new LoginAttempt("pilot", new byte[] { 3 }, "EN", "x"), CancellationToken.None));
    }

    [PgFact]
    public async Task A_successful_login_is_stamped_and_a_refused_one_is_not()
    {
        await using var schema = await PgSchema.CreateAsync();
        var directory = new PgAccountDirectory(schema.Source, new AccountOptions { AutoCreate = true }, schema.Name);
        await directory.LoginAsync(new LoginAttempt("pilot", new byte[] { 1 }, "EN", "x"), CancellationToken.None);
        Assert.False(await schema.ScalarAsync<bool>($"SELECT last_login_at IS NULL FROM {schema.Name}.accounts WHERE user_name = 'pilot'"));

        await schema.ExecuteAsync($"UPDATE {schema.Name}.accounts SET last_login_at = NULL");
        await directory.LoginAsync(new LoginAttempt("pilot", new byte[] { 9 }, "EN", "x"), CancellationToken.None);
        Assert.True(await schema.ScalarAsync<bool>($"SELECT last_login_at IS NULL FROM {schema.Name}.accounts WHERE user_name = 'pilot'"));
    }

    [PgFact]
    public async Task The_full_role_range_round_trips_through_a_signed_column()
    {
        await using var schema = await PgSchema.CreateAsync();
        var directory = new PgAccountDirectory(schema.Source, new AccountOptions { AutoCreate = true, NewAccountRole = ulong.MaxValue }, schema.Name);
        var login = Assert.IsType<LoggedIn>(await directory.LoginAsync(new LoginAttempt("all", new byte[] { 1 }, "EN", "x"), CancellationToken.None));
        Assert.Equal(ulong.MaxValue, login.Role);
        var again = Assert.IsType<LoggedIn>(await directory.LoginAsync(new LoginAttempt("all", new byte[] { 1 }, "EN", "x"), CancellationToken.None));
        Assert.Equal(ulong.MaxValue, again.Role);
    }

    [PgFact]
    public async Task The_database_itself_refuses_a_second_account_of_one_name_and_a_bad_country()
    {
        await using var schema = await PgSchema.CreateAsync();
        await schema.ExecuteAsync($"INSERT INTO {schema.Name}.accounts (user_name, password_hash) VALUES ('a', '\\x01')");
        await Assert.ThrowsAsync<PostgresException>(() => schema.ExecuteAsync($"INSERT INTO {schema.Name}.accounts (user_name, password_hash) VALUES ('a', '\\x02')"));
        await Assert.ThrowsAsync<PostgresException>(() => schema.ExecuteAsync($"INSERT INTO {schema.Name}.accounts (user_name, password_hash, country_code) VALUES ('b', '\\x02', 'gb')"));
    }

    [Fact]
    public void A_schema_name_that_could_carry_sql_is_refused()
    {
        using var source = NpgsqlDataSource.Create("Host=127.0.0.1");
        Assert.Throws<ArgumentException>(() => new PgAccountDirectory(source, new AccountOptions(), "identity; DROP SCHEMA x"));
        Assert.Throws<ArgumentException>(() => new PgAccountDirectory(source, new AccountOptions(), "Identity"));
    }
}

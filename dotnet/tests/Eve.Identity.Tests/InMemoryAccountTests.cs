namespace Eve.Identity.Tests;

public class InMemoryAccountTests
{
    private static InMemoryAccountDirectory Directory(AccountOptions options) => new(options);

    [Fact]
    public Task First_login_creates_the_account_and_then_needs_the_same_hash() => AccountScenarios.First_login_creates_the_account_and_then_needs_the_same_hash(Directory);

    [Fact]
    public Task New_accounts_are_stored_until_2100_and_an_operator_can_set_an_earlier_end() =>
        AccountScenarios.New_accounts_are_stored_until_2100_and_an_operator_can_set_an_earlier_end(Directory);

    [Fact]
    public Task An_account_can_be_looked_up_by_its_id() => AccountScenarios.An_account_can_be_looked_up_by_its_id(Directory);

    [Fact]
    public Task A_hash_of_another_length_is_not_the_hash() => AccountScenarios.A_hash_of_another_length_is_not_the_hash(Directory);

    [Fact]
    public Task Skipping_the_password_check_accepts_any_hash() => AccountScenarios.Skipping_the_password_check_accepts_any_hash(Directory);

    [Fact]
    public Task Unknown_users_are_refused_when_auto_creation_is_off() => AccountScenarios.Unknown_users_are_refused_when_auto_creation_is_off(Directory);

    [Fact]
    public Task New_accounts_get_the_configured_role_and_country() => AccountScenarios.New_accounts_get_the_configured_role_and_country(Directory);

    [Fact]
    public Task User_names_are_exact_not_folded() => AccountScenarios.User_names_are_exact_not_folded(Directory);

    [Fact]
    public Task Names_that_cannot_be_accounts_are_refused() => AccountScenarios.Names_that_cannot_be_accounts_are_refused(Directory);

    [Fact]
    public Task Concurrent_first_logins_of_one_name_create_one_account() => AccountScenarios.Concurrent_first_logins_of_one_name_create_one_account(Directory);

    [Fact]
    public Task Different_names_get_different_ids_concurrently() => AccountScenarios.Different_names_get_different_ids_concurrently(Directory);

    [Fact]
    public Task A_ban_outranks_everything_else()
    {
        // every directory the scenario asks for is a view of the same accounts
        InMemoryAccountDirectory? first = null;
        return AccountScenarios.A_ban_outranks_everything_else(
            options => first is null ? first = new InMemoryAccountDirectory(options) : first.WithOptions(options),
            user =>
            {
                first!.Ban(user);
                return Task.CompletedTask;
            });
    }
}

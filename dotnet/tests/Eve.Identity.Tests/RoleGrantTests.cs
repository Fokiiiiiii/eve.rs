namespace Eve.Identity.Tests;

/// <summary>The EVE_GM_ACCOUNTS grant: the role bits are added to an existing account's row, and the next login carries them.</summary>
public class RoleGrantTests
{
    // The client's account role bits (eve-logic serviceConst.py), the ones Eve.Services.GameMasterRoles names: ROLE_GML and ROLE_QA.
    private const long Gml = 1L << 54;
    private const long Qa = 1L << 52;

    [PgFact]
    public async Task A_granted_role_is_added_to_the_account_and_the_next_login_carries_it()
    {
        await using var schema = await PgSchema.CreateAsync();
        var directory = new PgAccountDirectory(schema.Source, new AccountOptions { AutoCreate = true, NewAccountRole = 1 }, schema.Name);
        var first = Assert.IsType<LoggedIn>(await directory.LoginAsync(new LoginAttempt("gm", new byte[] { 1 }, "EN", "x"), CancellationToken.None));
        Assert.Equal(0L, (long)first.Role & (Gml | Qa));

        Assert.True(await directory.GrantRolesAsync("gm", Gml | Qa));
        var after = Assert.IsType<LoggedIn>(await directory.LoginAsync(new LoginAttempt("gm", new byte[] { 1 }, "EN", "x"), CancellationToken.None));
        Assert.Equal(Gml | Qa, (long)after.Role & (Gml | Qa));
        Assert.Equal(1UL, after.Role & 1UL);

        // a second grant of the same bits changes nothing
        Assert.True(await directory.GrantRolesAsync("gm", Gml));
        var again = Assert.IsType<LoggedIn>(await directory.LoginAsync(new LoginAttempt("gm", new byte[] { 1 }, "EN", "x"), CancellationToken.None));
        Assert.Equal(after.Role, again.Role);
    }

    [PgFact]
    public async Task A_grant_to_an_account_that_does_not_exist_changes_nothing()
    {
        await using var schema = await PgSchema.CreateAsync();
        var directory = new PgAccountDirectory(schema.Source, new AccountOptions { AutoCreate = true }, schema.Name);
        Assert.False(await directory.GrantRolesAsync("nobody", Gml));
        Assert.Equal(0, await schema.ScalarAsync<long>($"SELECT count(*) FROM {schema.Name}.accounts"));
    }
}

using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.CharacterSelect.Tests;

/// <summary>The name-validation toggle (per session, by role) and the lapsed-Omega answer of the selection screen.</summary>
public class ValidationAndOmegaTests
{

    private const ulong RoleGml = 1UL << 54;

    [PgFact]
    public async Task A_session_without_toggle_account_roles_cannot_enable_validation_toggle_with_client_role()
    {
        await using var h = await Harness.CreateAsync();
        var (session, _) = await h.LoginAsync("plain");
        session.Set("role", new PyLong(unchecked((long)RoleGml)));

        Assert.Equal(PyBool.True, await h.CallAsync(session, "ToggleValidation"));
        Assert.Equal(PyBool.True, await h.CallAsync(session, "ToggleValidation"));
    }

    [PgFact]
    public async Task A_gml_session_switches_name_validation_off_and_back_on()
    {
        await using var h = await Harness.CreateAsync();
        var (session, _) = await h.LoginAsync("gml", RoleGml);

        Assert.Equal(PyBool.False, await h.CallAsync(session, "ToggleValidation"));
        Assert.Equal(PyBool.True, await h.CallAsync(session, "ToggleValidation"));
    }

    [PgFact]
    public async Task A_new_account_has_not_lapsed_and_its_selection_row_carries_the_stored_end_time()
    {
        await using var h = await Harness.CreateAsync();
        var (session, _) = await h.LoginAsync("fresh");

        Assert.Equal(PyBool.False, await h.CallAsync(session, "GetCharOmegaDowngradeStatus"));
        var account = Assert.Single(((PyList)((PyTuple)await h.CallAsync(session, "GetCharacterSelectionData")).Items[0]).Items);
        Assert.Equal(new PyLong(157_469_184_000_000_000), Recorded.Attributes(account).Single(a => a.Key == "subscriptionEndTime").Value);
    }

    [PgFact]
    public async Task An_end_time_that_has_passed_is_reported_as_lapsed_and_the_selection_row_shows_it()
    {
        await using var h = await Harness.CreateAsync();
        var (session, _) = await h.LoginAsync("lapsing");
        var accountId = checked((uint)session.UserId!.Value);
        var yesterday = DateTimeOffset.UtcNow.AddDays(-1);

        h.Accounts.SetSubscriptionEndsAt(accountId, yesterday);

        Assert.Equal(PyBool.True, await h.CallAsync(session, "GetCharOmegaDowngradeStatus"));
        var account = Assert.Single(((PyList)((PyTuple)await h.CallAsync(session, "GetCharacterSelectionData")).Items[0]).Items);
        Assert.Equal(new PyLong(Py.FileTimeFromUnixMs(yesterday.ToUnixTimeMilliseconds())), Recorded.Attributes(account).Single(a => a.Key == "subscriptionEndTime").Value);
    }
}
